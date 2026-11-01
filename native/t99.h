#pragma once
// C ABI of t99_native.dll for the SDR# plugin (SDRSharp.T99).
// One selected STD-T99 Annex 2 channel: IQ -> frames, decoded call information, and TOKUDER audio.
#include <stdint.h>
#ifdef _WIN32
#define API __declspec(dllexport)
#else
#define API
#endif
#ifdef __cplusplus
extern "C" {
#endif

enum { T99_HALF_TCH = 0, T99_HALF_FACCH = 1, T99_HALF_PICH = 2 };
enum { T99_KIND_UNKNOWN = 0, T99_KIND_SB = 1, T99_KIND_SC = 2 };

// One received frame (80 ms) and the call state after it. Strings are NUL-terminated UTF-8.
typedef struct {
    double seconds;                 // input time of the sync word
    uint64_t sample;                // receiver samples (100 kHz) from the start
    int32_t kind;                   // T99_KIND_*
    int32_t sync_ok;                // 0 = flywheel frame
    int32_t crc;                    // any CRC (SACCH / PICH / FACCH) passed
    int32_t voice;                  // both halves TCH (written to .burst)
    int32_t half[2];                // T99_HALF_*
    int32_t half_crc[2];            // control halves: CRC-12 passed
    int32_t new_call;               // synchronous burst with a valid PICH1: a new transmission
    int32_t has_caller, has_called, has_identification;
    char caller[16], called[16];    // "MMM-IIIIIII"
    uint32_t extension;             // identification code extension (20 bit)
    int32_t comm_type;              // 1 group, 4 individual, -1 unknown
    int32_t group;                  // SACCH group number, 0 = simultaneous call, -1 unknown
    int32_t call_control;           // 0 normal, 1 emergency 1, 2 emergency 2, 3 response, -1 unknown
    int32_t voice_mode;             // 0 TOKUDER, -1 unknown
    int32_t last_type;              // last message type (5 bit), -1 none
    int32_t ended;                  // End call received
    int32_t has_position;
    uint32_t lat_raw, lon_raw;      // ddmmmmmm, dddmmmmmm
    double lat, lon;                // degrees
    int32_t altitude_m, gps_valid;
    int32_t has_gps_time;
    uint32_t gps_time, gps_date;    // hhmmss, ddmmyy (JST)
    int32_t speed_01kn, direction_deg;
    int32_t name_failed;            // caller name CRC-32 mismatch
    char name[64];                  // caller name (Shift_JIS -> UTF-8), empty until verified
    char messages[256];             // messages of this frame: "PICH1:Identification;FACCH1:GPS data 2"
    uint8_t payload[36];            // TCH: 4 x 72 bit, MSB first (zero for control halves)
    int8_t llr[288];                // TCH soft decisions, positive = 1
} t99_frame;

typedef struct {
    uint64_t samples, frames, crc_ok;
    double power, frequency_error;  // dB, Hz (carrier offset measured during the last lock)
    int32_t locked, active;
} t99_stats;

API void* t99_create(double rate);
API void t99_destroy(void* handle);
API const char* t99_error(void);
API int t99_process(void* handle, const float* iq, int count);
// 1 = frame copied, 0 = none
API int t99_read(void* handle, t99_frame* frame);
// JSON line of the frame returned by the last t99_read (see FORMAT.md); burst = .burst record index or -1
API int t99_json(void* handle, int64_t burst, char* text, int capacity);
API int t99_status(void* handle, t99_stats* stats);
API int t99_eye_read(void* handle, float* traces, int capacity);
// Decoded 8 kHz audio of the TCH frames (float, -1..1), in order
API int t99_audio_read(void* handle, float* pcm, int capacity);
// Ends the current voice stream (outputs its tail)
API void t99_audio_flush(void* handle);

// TOKUDER decoder alone (tests): info bits [frames][48] -> pcm [frames * 160], returns samples written
API void* t99_voice_create(void);
API void t99_voice_destroy(void* voice);
API int t99_voice_fec(const int8_t* llr72, uint8_t* info48);
API int t99_voice_decode(void* voice, const uint8_t* info48, int frames, int flush, const float* noise, float* pcm, int capacity);
API int t99_voice_probe(void* voice, const uint8_t* info48, int frames, float* features, int capacity);

#ifdef __cplusplus
}
#endif
