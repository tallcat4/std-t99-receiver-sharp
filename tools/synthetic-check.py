"""Native receiver chain (t99_native.dll) on the Annex 2 7.7.2 test signals synthesized as IQ (build/spec-iq.exe).

The signals are fed at SDR#-like IQ rates with a carrier offset, in uneven blocks, like the plugin does:
every frame of the four transmissions must come out with valid CRCs and the call information of the standard.
"""
import ctypes as C
import json
import os
import subprocess
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]


class Frame(C.Structure):
    _fields_ = [("seconds", C.c_double), ("sample", C.c_uint64)] + \
        [(n, C.c_int32) for n in ("kind", "sync_ok", "crc", "voice")] + \
        [("half", C.c_int32 * 2), ("half_crc", C.c_int32 * 2)] + \
        [(n, C.c_int32) for n in ("new_call", "has_caller", "has_called", "has_identification")] + \
        [("caller", C.c_char * 16), ("called", C.c_char * 16), ("extension", C.c_uint32)] + \
        [(n, C.c_int32) for n in ("comm_type", "group", "call_control", "voice_mode", "last_type", "ended", "has_position")] + \
        [("lat_raw", C.c_uint32), ("lon_raw", C.c_uint32), ("lat", C.c_double), ("lon", C.c_double)] + \
        [(n, C.c_int32) for n in ("altitude_m", "gps_valid", "has_gps_time")] + \
        [("gps_time", C.c_uint32), ("gps_date", C.c_uint32)] + \
        [(n, C.c_int32) for n in ("speed_01kn", "direction_deg", "name_failed")] + \
        [("name", C.c_char * 64), ("messages", C.c_char * 256), ("payload", C.c_uint8 * 36), ("llr", C.c_int8 * 288)]


class Stats(C.Structure):
    _fields_ = [(n, C.c_uint64) for n in ("samples", "frames", "crc_ok")] + [(n, C.c_double) for n in ("power", "frequency_error")] + \
        [("locked", C.c_int32), ("active", C.c_int32)]


def run(dll, rate, offset, lead=0):
    path = ROOT / "build" / f"spec-{int(rate)}.cf32"
    subprocess.run([str(ROOT / "build" / "spec-iq.exe"), str(path), str(rate), str(offset)], check=True, capture_output=True)
    iq = np.fromfile(path, np.float32)
    if lead:   # shifts every frame against the receiver's sample grid
        iq = np.concatenate([(np.random.default_rng(lead).standard_normal(2 * lead) * 0.03).astype(np.float32), iq])
    h = dll.t99_create(C.c_double(rate))
    if not h:
        raise SystemExit(dll.t99_error().decode())
    frames, lines, audio = [], [], []
    rng = np.random.default_rng(3)
    pos = 0
    buf = np.zeros(1 << 16, np.float32)
    text = C.create_string_buffer(65536)
    def drain():
        f = Frame()
        while dll.t99_read(h, C.byref(f)) == 1:
            frames.append(Frame.from_buffer_copy(f))
            if dll.t99_json(h, C.c_int64(-1), text, len(text)) < 0:
                raise SystemExit("json failed")
            lines.append(json.loads(text.value.decode("utf-8")))
        while (n := dll.t99_audio_read(h, buf.ctypes.data_as(C.c_void_p), buf.size)) > 0:
            audio.append(buf[:n].copy())
    while pos < iq.size:
        n = min(int(rng.integers(100, 4000)) * 2, iq.size - pos)
        block = np.ascontiguousarray(iq[pos:pos + n])
        if dll.t99_process(h, block.ctypes.data_as(C.c_void_p), n // 2) != 0:
            raise SystemExit(dll.t99_error().decode())
        pos += n
        drain()
    dll.t99_audio_flush(h)
    drain()
    stats = Stats()
    dll.t99_status(h, C.byref(stats))
    dll.t99_destroy(h)
    return frames, lines, np.concatenate(audio) if audio else np.zeros(0, np.float32), stats


def check(ok, message):
    if not ok:
        raise SystemExit("FAILED: " + message)


def main():
    os.add_dll_directory(str(ROOT / "build" / "native"))
    dll = C.CDLL(str(ROOT / "build" / "native" / "t99_native.dll"))
    dll.t99_create.restype = C.c_void_p
    dll.t99_create.argtypes = [C.c_double]
    for name in ("t99_destroy", "t99_audio_flush"):
        getattr(dll, name).argtypes = [C.c_void_p]
    dll.t99_process.argtypes = [C.c_void_p, C.c_void_p, C.c_int]
    dll.t99_read.argtypes = [C.c_void_p, C.c_void_p]
    dll.t99_json.argtypes = [C.c_void_p, C.c_int64, C.c_char_p, C.c_int]
    dll.t99_status.argtypes = [C.c_void_p, C.c_void_p]
    dll.t99_audio_read.argtypes = [C.c_void_p, C.c_void_p, C.c_int]
    dll.t99_error.restype = C.c_char_p
    for rate, offset in ((48000, 180), (37500, -350), (96000, 420), (192000, -600), (250000, 0)):
        frames, lines, audio, stats = run(dll, rate, offset)
        ok = [f for f in frames if f.crc]
        check(len(frames) == 29 and len(ok) == 29, f"{rate} Hz: {len(ok)}/{len(frames)} frames with CRC, expected 29")
        calls = [i for i, f in enumerate(frames) if f.new_call]
        check(len(calls) == 4, f"{rate} Hz: {len(calls)} synchronous bursts")
        by_call = [frames[a:b] for a, b in zip(calls, calls[1:] + [len(frames)])]
        last = [c[-1] for c in by_call]
        for f in last:
            check(f.caller == b"127-0123456" and f.has_position and f.lat_raw == 35402565 and f.lon_raw == 139450235, "caller / position")
            check(f.altitude_m == 7 and f.gps_valid == 1, "altitude / A/V")
        check(last[0].comm_type == 4 and last[0].called == b"127-0000654" and last[0].ended == 1, "1-1 individual call")
        check(last[0].has_gps_time and last[0].gps_time == 235120 and last[0].gps_date == 260718 and last[0].speed_01kn == 27 and last[0].direction_deg == 267, "GPS data 2")
        check(last[1].name.decode("utf-8") == "無線試験No.9" and last[3].name.decode("utf-8") == "無線試験No.9", "caller name")
        check(last[2].call_control == 1, "2-1 emergency 1")
        check(last[3].comm_type == 1 and last[3].group == 3, "6-1 group call")
        voice = sum(f.voice for f in frames)
        # TOKUDER audio of every voice slot (the 1031 Hz test tone is not speech; only its presence is checked)
        # TOKUDER audio of every voice slot, from the first TCH frame to the frame before End call
        # (the 1031 Hz test tone is not speech; only its presence is checked)
        check(audio.size == voice * 640 and np.isfinite(audio).all() and np.abs(audio).max() > 0.01, f"audio {audio.size} samples for {voice} voice frames")
        check(all(l["burst"] is None and "raw" in l and len(l["halves"]) == 2 for l in lines), "JSON lines")
        print(f"{rate} Hz, offset {offset:+} Hz: 29/29 frames, 4 calls, name/position/GPS/emergency ok, "
              f"{audio.size / 8000:.2f} s audio, frequency error {stats.frequency_error:+.0f} Hz")
    # Frame positions all around the 80 ms grid: every frame and exactly the voice audio, whatever the start
    for lead in range(0, 4000, 200):
        frames, _, audio, _ = run(dll, 48000, 180, lead)
        voice = sum(f.voice for f in frames)
        check(len(frames) == 29 and all(f.crc for f in frames) and voice == 20 and audio.size == voice * 640,
              f"start offset {lead}: {len(frames)} frames, {voice} voice frames, {audio.size} audio samples")
    print("20 start offsets across the 80 ms frame grid: 29/29 frames and 20 x 640 audio samples each")
    # Receiver noise only (60 s at 48 kHz): no frame may come out
    rng = np.random.default_rng(11)
    h = dll.t99_create(C.c_double(48000))
    f = Frame()
    false_frames = 0
    for _ in range(600):
        block = (rng.standard_normal(9600) * 0.1).astype(np.float32)
        dll.t99_process(h, block.ctypes.data_as(C.c_void_p), 4800)
        while dll.t99_read(h, C.byref(f)) == 1:
            false_frames += 1
    dll.t99_destroy(h)
    check(false_frames == 0, f"{false_frames} frames from noise")
    print("60 s of noise: no frames")
    print("Native receiver chain passed.")


if __name__ == "__main__":
    main()
