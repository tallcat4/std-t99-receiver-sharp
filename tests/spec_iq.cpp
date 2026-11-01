// Annex 2 7.7.2 test signals as cf32 IQ for the native and SDR# host checks (test fixture, not a transmitter).
//   spec-iq OUT.cf32 RATE OFFSET_HZ [TESTS...]   (default tests: 1-1 6-2 2-1 6-1, 0.5 s of noise between them)
// Modulator from std-t99-receiver tests/native/test_main.cpp: impulses through H(f)·P(f), ±315/±945 Hz deviation.
#include "spec_vectors.hpp"
#include "dsp/rrc.hpp"
#include "spear/core/types.hpp"
#include <cmath>
#include <cstdio>
#include <fstream>
#include <numbers>
#include <random>
#include <string>
#include <vector>

namespace {
std::vector<spear::cf32> modulate(const std::vector<int8_t>& symbols, double rate, double offset_hz, double noise, uint32_t seed) {
    const double fs = 48000, sps = fs / 2400;
    const auto taps = spear::std_t98::make_tx_taps(fs, 2400, 0.2, static_cast<int>(sps) * 24 + 1);
    std::vector<double> level(static_cast<std::size_t>(symbols.size() * sps + taps.size()), 0.0);
    for (std::size_t k = 0; k < symbols.size(); ++k)
        for (std::size_t t = 0; t < taps.size(); ++t) level[static_cast<std::size_t>(k * sps) + t] += symbols[k] * sps * taps[t];
    std::mt19937 rng(seed);
    std::normal_distribution<float> gauss(0.f, static_cast<float>(noise));
    std::vector<spear::cf32> iq(static_cast<std::size_t>(static_cast<double>(level.size()) / fs * rate));
    double phase = 0;
    for (std::size_t i = 0; i < iq.size(); ++i) {
        const double x = static_cast<double>(i) * fs / rate;
        const auto j = static_cast<std::size_t>(x);
        const double mu = x - static_cast<double>(j);
        const double f = (j + 1 < level.size() ? level[j] + mu * (level[j + 1] - level[j]) : 0.0) * 315.0 + offset_hz;
        phase += 2 * std::numbers::pi * f / rate;
        iq[i] = spear::cf32(0.5f * static_cast<float>(std::cos(phase)) + gauss(rng), 0.5f * static_cast<float>(std::sin(phase)) + gauss(rng));
    }
    return iq;
}
std::vector<int8_t> transmission(const std::string& test) {
    std::vector<int8_t> s(48, 0);
    for (int8_t p : {3, 3, 3, -3, 3, -3, 3, 3, -3, -3, -3, 3}) s.push_back(p);   // preamble 5775FDh
    for (const auto& v : t99::spec::vectors()) if (test == v.test) s.insert(s.end(), v.symbols.begin(), v.symbols.end());
    s.insert(s.end(), 48, 0);
    return s;
}
}

int main(int argc, char** argv) {
    if (argc < 4) { std::fprintf(stderr, "usage: spec-iq OUT.cf32 RATE OFFSET_HZ [TESTS...]\n"); return 2; }
    const double rate = std::stod(argv[2]), offset = std::stod(argv[3]);
    std::vector<std::string> tests;
    for (int i = 4; i < argc; ++i) tests.emplace_back(argv[i]);
    if (tests.empty()) tests = {"1-1", "6-2", "2-1", "6-1"};
    std::ofstream out(argv[1], std::ios::binary);
    std::mt19937 rng(5);
    std::normal_distribution<float> gauss(0.f, 0.03f);
    std::vector<spear::cf32> gap(static_cast<std::size_t>(rate / 2));
    uint32_t seed = 1;
    std::size_t total = 0;
    auto write = [&](const std::vector<spear::cf32>& v) { out.write(reinterpret_cast<const char*>(v.data()), static_cast<std::streamsize>(v.size() * sizeof(spear::cf32))); total += v.size(); };
    for (const auto& test : tests) {
        for (auto& x : gap) x = {gauss(rng), gauss(rng)};
        write(gap);
        write(modulate(transmission(test), rate, offset, 0.03, seed++));
    }
    for (auto& x : gap) x = {gauss(rng), gauss(rng)};
    write(gap);
    std::printf("%zu samples at %.0f Hz written\n", total, rate);
    return out ? 0 : 1;
}
