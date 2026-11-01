#include "t99.h"
#include "receiver.hpp"
#include "session.hpp"
#include "tokuder.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <deque>
#include <numbers>
#include <stdexcept>
#include <string>

using namespace t99;
using spear::cf32;

namespace {
thread_local std::string error;
// Receiver input: twice the 8 x 6.25 kHz PFB band, so that the receiver decimates by 2 in front of the PFB.
constexpr double kRate = 100000;
constexpr uint64_t kRfFrame = 8000;             // 80 ms at kRate

// Fixed phase bank, streaming windowed-sinc resampler (std-t98-receiver-sharp). Retains history and
// absolute position across arbitrary SDR# callback boundaries.
class Resampler {
    static constexpr int taps = 64, phases = 1024;
    double step, next = 32;
    uint64_t base = 0;
    std::vector<cf32> history = std::vector<cf32>(32);
    std::vector<float> coefficients;
public:
    explicit Resampler(double rate) : step(rate / kRate), coefficients(taps * phases) {
        double cutoff = std::min(0.45, 20000. / rate);
        for (int p = 0; p < phases; ++p) {
            double sum = 0;
            for (int k = 0; k < taps; ++k) {
                double x = k - 31 - double(p) / phases;
                double sinc = std::abs(x) < 1e-12 ? 2 * cutoff : std::sin(2 * std::numbers::pi * cutoff * x) / (std::numbers::pi * x);
                double w = .42 + .5 * std::cos(std::numbers::pi * x / 32) + .08 * std::cos(2 * std::numbers::pi * x / 32);
                coefficients[p * taps + k] = float(sinc * w);
                sum += sinc * w;
            }
            for (int k = 0; k < taps; ++k) coefficients[p * taps + k] /= float(sum);
        }
    }
    void process(std::span<const cf32> input, std::vector<cf32>& output) {
        history.insert(history.end(), input.begin(), input.end());
        output.clear();
        while (uint64_t(next) + 32 < base + history.size()) {
            auto center = uint64_t(next);
            int phase = std::min(phases - 1, int((next - center) * phases));
            cf32 value{};
            for (int k = 0; k < taps; ++k) value += history[size_t(center - base - 31 + k)] * coefficients[phase * taps + k];
            output.push_back(value);
            next += step;
        }
        uint64_t retain = uint64_t(next) > 32 ? uint64_t(next) - 32 : 0;
        size_t drop = size_t(std::min<uint64_t>(history.size(), retain - base));
        history.erase(history.begin(), history.begin() + drop);
        base += drop;
    }
};

void copy_string(char* out, std::size_t capacity, const std::string& s) {
    const auto n = std::min(s.size(), capacity - 1);
    std::memcpy(out, s.data(), n);
    out[n] = 0;
}

// TOKUDER voice timeline: 4 TCH slots per 80 ms RF frame, frame 0 = slot 0 of the first voice RF frame.
// FACCH halves and missed RF frames inside a transmission are erased slots (the previous frame repeats,
// as tokuder_neurodec frames.to_info48); a long gap, a synchronous burst or End call ends the stream.
class Voice {
    tokuder::Decoder decoder_{tokuder::model()};
    bool active_ = false;
    uint64_t origin_ = 0;                        // sync-word sample of the first frame of the stream
    int64_t last_frame_ = 0;                     // RF frame number relative to origin_
    std::array<uint8_t, 48> previous_{};
    static constexpr int64_t kMaxGap = 6;        // RF frames (480 ms) bridged with erased slots
    void erased(int64_t slots, std::vector<float>& pcm) { for (int64_t i = 0; i < slots; ++i) decoder_.push(previous_.data(), pcm); }
    // Frames sit at origin + 8000 k ± timing jitter; rounding relative to the stream's first frame keeps
    // the numbering away from the rounding boundary (an absolute position could sit right on it).
    int64_t number(uint64_t sample) const {
        const auto delta = static_cast<int64_t>(sample) - static_cast<int64_t>(origin_);
        return (delta >= 0 ? delta + static_cast<int64_t>(kRfFrame / 2) : delta - static_cast<int64_t>(kRfFrame / 2)) / static_cast<int64_t>(kRfFrame);
    }
public:
    void flush(std::vector<float>& pcm) {
        if (active_) decoder_.flush(pcm);
        active_ = false;
        previous_.fill(0);
    }
    // frame: a communication-channel frame inside a transmission
    void frame(const Frame& f, std::vector<float>& pcm) {
        const bool tch[2] = {f.halves[0].kind == HalfKind::tch, f.halves[1].kind == HalfKind::tch};
        if (active_) {
            const int64_t n = number(f.input_sample_index);
            if (n <= last_frame_) return;   // duplicate or out of order
            if (n - last_frame_ > kMaxGap) flush(pcm);
            else { erased((n - last_frame_ - 1) * 4, pcm); last_frame_ = n; }
        }
        if (!active_) {
            if (!tch[0] && !tch[1]) return;
            active_ = true;
            origin_ = f.input_sample_index;
            last_frame_ = 0;
        }
        for (int half = 0; half < 2; ++half) {
            if (!tch[half]) { erased(2, pcm); continue; }
            const auto& h = f.halves[static_cast<std::size_t>(half)];
            for (int slot = 0; slot < 2; ++slot) {
                tokuder::soft_decode(h.llr.data() + 72 * slot, previous_.data());
                decoder_.push(previous_.data(), pcm);
            }
        }
    }
    // Ends the stream after a pause in the received frames
    void idle(uint64_t sample, std::vector<float>& pcm) {
        if (active_ && number(sample) > last_frame_ + kMaxGap + 1) flush(pcm);
    }
};

struct Context {
    double rate, offset = 0;
    uint64_t input = 0, output = 0;
    Resampler resampler;
    Receiver receiver;
    Session session;
    Voice voice;
    std::vector<cf32> mixed, converted;
    std::deque<t99_frame> frames;
    std::deque<std::string> lines;               // Session JSON without the burst field, per queued frame
    std::string last_json;
    std::deque<std::array<float, 64>> eyes;
    std::vector<float> audio;
    uint64_t eye_count = 0, last_frame_sample = 0;
    bool have_frame = false;
    static ReceiverConfig config() {
        ReceiverConfig c;
        c.in_rate = kRate;
        c.pfb_channels = 8;
        return c;
    }
    explicit Context(double r) : rate(r), resampler(r), receiver(config()) {
        receiver.set_eye_handler([this](std::span<const float> values) {
            // 600 traces/s keep the eye dense without drawing all 2400 symbols
            if ((eye_count++ % 4) != 0 || !(receiver.metrics().locked || (have_frame && output < last_frame_sample + kRfFrame * 6))) return;
            std::array<float, 64> trace{};
            std::copy_n(values.begin(), 64, trace.begin());
            eyes.push_back(trace);
            if (eyes.size() > 256) eyes.pop_front();
        });
        receiver.set_frame_handler([this](const Frame& f) { on_frame(f); });
    }
    void on_frame(const Frame& f) {
        last_frame_sample = f.input_sample_index;
        have_frame = true;
        const auto decoded = session.update(f);
        const auto& s = session.state();
        t99_frame e{};
        e.seconds = static_cast<double>(f.input_sample_index) / kRate;
        e.sample = f.input_sample_index;
        e.kind = f.sync_burst() ? T99_KIND_SB : f.rich.f == 0b10 ? T99_KIND_SC : T99_KIND_UNKNOWN;
        e.sync_ok = f.sync_ok;
        e.crc = f.crc_ok();
        e.voice = f.voice();
        for (int i = 0; i < 2; ++i) {
            const auto& h = f.halves[static_cast<std::size_t>(i)];
            e.half[i] = h.kind == HalfKind::tch ? T99_HALF_TCH : h.kind == HalfKind::pich ? T99_HALF_PICH : T99_HALF_FACCH;
            e.half_crc[i] = h.control && h.control->crc_ok;
            if (h.kind == HalfKind::tch) {
                std::copy(h.payload.begin(), h.payload.end(), e.payload + 18 * i);
                std::copy(h.llr.begin(), h.llr.end(), e.llr + 144 * i);
            }
        }
        e.new_call = f.sync_burst() && f.halves[0].control && f.halves[0].control->crc_ok;
        if (s.caller) { e.has_caller = 1; copy_string(e.caller, sizeof e.caller, s.caller->str()); }
        if (s.called) { e.has_called = 1; copy_string(e.called, sizeof e.called, s.called->str()); }
        if (s.identification) { e.has_identification = 1; e.extension = s.identification->extension; }
        e.comm_type = s.comm_type;
        e.group = s.group;
        e.call_control = s.call_control;
        e.voice_mode = s.voice_mode;
        e.last_type = s.last_type;
        e.ended = s.ended;
        if (s.position) {
            e.has_position = 1;
            e.lat_raw = s.position->latitude;
            e.lon_raw = s.position->longitude;
            e.lat = s.position->latitude_deg();
            e.lon = s.position->longitude_deg();
            e.altitude_m = s.position->altitude_m;
            e.gps_valid = s.position->valid;
        }
        if (s.gps_time) {
            e.has_gps_time = 1;
            e.gps_time = s.gps_time->time;
            e.gps_date = s.gps_time->date;
            e.speed_01kn = s.gps_time->speed_01kn;
            e.direction_deg = s.gps_time->direction_deg;
        }
        e.name_failed = s.name_check_failed;
        copy_string(e.name, sizeof e.name, s.name);
        std::string list;
        for (const auto& d : decoded) {
            if (!list.empty()) list += ';';
            list += d.channel + ":" + (d.channel == "PICH1" ? "Identification" : message_name(d.message.type));
        }
        copy_string(e.messages, sizeof e.messages, list);
        if (frames.size() >= 256) throw std::runtime_error("Frame output queue overflow");
        frames.push_back(e);
        lines.push_back(session.json(f, decoded, e.seconds, -1));

        // Audio: a synchronous burst or End call ends the stream; communication-channel frames feed it
        bool end = f.sync_burst();
        for (const auto& d : decoded) end |= d.message.type == kEndCall;
        if (end) voice.flush(audio);   // before the frame: its FACCH halves would only repeat the last voice frame
        if (f.rich.f == 0b10) voice.frame(f, audio);
    }
};
} // namespace

void* t99_create(double rate) {
    try {
        if (!std::isfinite(rate) || rate < 12000 || rate > 3200000) throw std::invalid_argument("IQ rate must be 12 kHz..3.2 MHz");
        tokuder::model();   // load the voice model up front
        return new Context(rate);
    } catch (const std::exception& e) { error = e.what(); return nullptr; }
}

void t99_destroy(void* h) { delete static_cast<Context*>(h); }
const char* t99_error() { return error.c_str(); }

int t99_process(void* h, const float* iq, int count) {
    try {
        if (!h || !iq || count < 0 || count > 1 << 20) throw std::invalid_argument("Invalid IQ block");
        auto& c = *static_cast<Context*>(h);
        c.mixed.resize(static_cast<std::size_t>(count));
        for (int i = 0; i < count; ++i) {
            if (!std::isfinite(iq[2 * i]) || !std::isfinite(iq[2 * i + 1])) throw std::invalid_argument("Non-finite IQ sample");
            c.mixed[static_cast<std::size_t>(i)] = cf32(iq[2 * i], iq[2 * i + 1]);
        }
        c.resampler.process(c.mixed, c.converted);
        c.receiver.process(c.converted, c.output);
        c.output += c.converted.size();
        c.input += static_cast<uint64_t>(count);
        // The input is not rotated: the receiver removes the carrier offset of every frame itself (sync-word DC,
        // about ±700 Hz). The offset measured while locked is kept for display.
        const auto& m = c.receiver.metrics();
        if (m.locked && m.have_offset) c.offset = m.freq_offset_hz;
        c.voice.idle(c.output, c.audio);
        return 0;
    } catch (const std::exception& e) { error = e.what(); return -1; }
}

int t99_read(void* h, t99_frame* out) {
    if (!h || !out) return -1;
    auto& c = *static_cast<Context*>(h);
    if (c.frames.empty()) return 0;
    *out = c.frames.front();
    c.frames.pop_front();
    c.last_json = std::move(c.lines.front());
    c.lines.pop_front();
    return 1;
}

int t99_json(void* h, int64_t burst, char* text, int capacity) {
    if (!h || !text || capacity <= 0) return -1;
    auto json = static_cast<Context*>(h)->last_json;
    const std::string none = ",\"burst\":null,";
    if (const auto at = json.find(none); at != std::string::npos && burst >= 0)
        json.replace(at, none.size(), ",\"burst\":" + std::to_string(burst) + ",");
    if (static_cast<int>(json.size()) >= capacity) return -1;
    std::memcpy(text, json.data(), json.size());
    text[json.size()] = 0;
    return static_cast<int>(json.size());
}

int t99_status(void* h, t99_stats* s) {
    if (!h || !s) return -1;
    auto& c = *static_cast<Context*>(h);
    const auto& m = c.receiver.metrics();
    *s = {c.input, m.frames, m.crc_frames, m.power_db, c.offset,
          int(m.locked), int(m.locked || !c.frames.empty())};
    return 0;
}

int t99_eye_read(void* h, float* eye, int capacity) {
    if (!h || !eye || capacity < 64) return -1;
    auto& q = static_cast<Context*>(h)->eyes;
    int n = std::min(int(q.size()), capacity / 64);
    for (int i = 0; i < n; ++i) { std::copy(q.front().begin(), q.front().end(), eye + i * 64); q.pop_front(); }
    return n * 64;
}

int t99_audio_read(void* h, float* pcm, int capacity) {
    if (!h || !pcm || capacity < 0) return -1;
    auto& a = static_cast<Context*>(h)->audio;
    const int n = std::min(capacity, static_cast<int>(a.size()));
    std::copy_n(a.begin(), n, pcm);
    a.erase(a.begin(), a.begin() + n);
    return n;
}

void t99_audio_flush(void* h) {
    if (!h) return;
    try { auto& c = *static_cast<Context*>(h); c.voice.flush(c.audio); } catch (...) {}
}

// ---------------------------------------------------------------- decoder tests
void* t99_voice_create() {
    try { return new tokuder::Decoder(tokuder::model()); } catch (const std::exception& e) { error = e.what(); return nullptr; }
}
void t99_voice_destroy(void* v) { delete static_cast<tokuder::Decoder*>(v); }
int t99_voice_fec(const int8_t* llr, uint8_t* info) { return llr && info ? tokuder::soft_decode(llr, info) : -1; }

int t99_voice_decode(void* v, const uint8_t* info, int frames, int flush, const float* noise, float* pcm, int capacity) {
    try {
        if (!v || (!info && frames) || frames < 0 || !pcm) return -1;
        auto& d = *static_cast<tokuder::Decoder*>(v);
        if (noise) d.set_noise(noise, static_cast<std::size_t>(frames + 1) * tokuder::kFrameSamples);
        std::vector<float> out;
        for (int i = 0; i < frames; ++i) d.push(info + 48 * i, out);
        if (flush) d.flush(out);
        if (static_cast<int>(out.size()) > capacity) return -1;
        std::copy(out.begin(), out.end(), pcm);
        return static_cast<int>(out.size());
    } catch (const std::exception& e) { error = e.what(); return -1; }
}

int t99_voice_probe(void* v, const uint8_t* info, int frames, float* features, int capacity) {
    try {
        if (!v || !info || frames <= 0 || !features) return -1;
        auto& d = *static_cast<tokuder::Decoder*>(v);
        d.keep_probe = true;
        d.probe.clear();
        std::vector<float> out;
        for (int i = 0; i < frames; ++i) d.push(info + 48 * i, out);
        d.flush(out);
        d.keep_probe = false;
        if (static_cast<int>(d.probe.size()) > capacity) return -1;
        std::copy(d.probe.begin(), d.probe.end(), features);
        return static_cast<int>(d.probe.size());
    } catch (const std::exception& e) { error = e.what(); return -1; }
}
