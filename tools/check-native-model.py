"""Native TOKUDER decoder (t99_native.dll) against the PyTorch reference (tokuder-neurodec).

  python tools/check-native-model.py --neurodec PATH/TO/tokuder-neurodec

1. FEC: soft ML Golay decoding gives the same 48 info bits as fec.soft_decode + info48.
2. Frontend: per-frame log-mel and periodicity head match the batch model.
3. Waveform: with the same excitation noise injected on both sides, the streaming C++ decoder gives the
   batch model's samples (float32 vs float64 phase accumulation is the only intended difference, so the
   reference accumulates the phase in float64 too).
"""
import argparse
import ctypes
import os
import sys
import time
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--neurodec", type=Path, default=ROOT.parent / "references" / "tokuder-neurodec")
    ap.add_argument("--frames", type=int, default=250)
    a = ap.parse_args()
    sys.path.insert(0, str(a.neurodec))
    import torch
    from tokuder_neurodec import fec, frames, load
    from tokuder_neurodec import excitation

    os.add_dll_directory(str(ROOT / "build" / "native"))
    dll = ctypes.CDLL(str(ROOT / "build" / "native" / "t99_native.dll"))
    dll.t99_voice_create.restype = ctypes.c_void_p
    dll.t99_voice_destroy.argtypes = [ctypes.c_void_p]
    dll.t99_voice_fec.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    dll.t99_voice_decode.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
    dll.t99_voice_probe.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_int]
    dll.t99_error.restype = ctypes.c_char_p
    ptr = lambda x: x.ctypes.data_as(ctypes.c_void_p)

    G = load(a.neurodec / "models" / "tokuder48_causal_v1.pt")
    failures = 0
    for sample in sorted((a.neurodec / "samples").glob("*.bin")):
        bits, llr, erased = frames.read_bin(sample, sample.with_suffix(".llr"))
        llr8 = np.fromfile(sample.with_suffix(".llr"), np.int8).reshape(-1, 72)
        ref_info, _ = frames.to_info48(bits, llr, erased)
        info = np.zeros((len(llr8), 48), np.uint8)
        for i in range(len(llr8)):
            dll.t99_voice_fec(ptr(np.ascontiguousarray(llr8[i])), ptr(info[i:]))
        fec_ok = np.array_equal(info, ref_info)

        T = min(a.frames, len(info))
        x = np.ascontiguousarray(info[:T])
        v = dll.t99_voice_create()
        feats = np.zeros(T * 87, np.float32)
        n = dll.t99_voice_probe(v, ptr(x), T, ptr(feats), feats.size)
        if n != T * 87:
            raise SystemExit(f"probe failed: {dll.t99_error()}")
        with torch.no_grad():
            _, mel, h = G.frontend.forward_with_aux(torch.from_numpy(x.astype(np.float32))[None], with_f0=True)
        ref = torch.cat([mel, h], 1)[0].T.numpy()
        front = np.abs(feats.reshape(T, 87) - ref).max()

        # waveform with injected noise; reference phase accumulated in float64
        rng = np.random.default_rng(1)
        noise = rng.standard_normal(T * 160).astype(np.float32)
        orig_randn, orig_cumsum = torch.randn, torch.cumsum
        torch.randn = lambda *s, **k: torch.from_numpy(noise.copy()).reshape(1, -1)
        torch.cumsum = lambda t, dim: orig_cumsum(t.double(), dim=dim).remainder(2 * np.pi).float()
        try:
            with torch.no_grad():
                wav_ref = G(torch.from_numpy(x.astype(np.float32))[None])[0].numpy()
        finally:
            torch.randn, torch.cumsum = orig_randn, orig_cumsum
        wav_ref = np.clip(wav_ref, -1, 1)
        pcm = np.zeros(T * 160 + 1024, np.float32)
        start = time.perf_counter()
        n = dll.t99_voice_decode(v, ptr(x), T, 1, ptr(np.concatenate([noise, np.zeros(160, np.float32)])), ptr(pcm), pcm.size)
        elapsed = time.perf_counter() - start
        dll.t99_voice_destroy(v)
        if n != T * 160:
            raise SystemExit(f"decode returned {n} samples for {T} frames: {dll.t99_error()}")
        err = pcm[:n] - wav_ref
        snr = 10 * np.log10((wav_ref ** 2).sum() / max((err ** 2).sum(), 1e-20))
        ok = fec_ok and front < 2e-3 and snr > 40
        failures += not ok
        print(f"{sample.stem}: FEC {'same' if fec_ok else 'DIFFERENT'}, frontend max diff {front:.2e}, "
              f"waveform SNR {snr:.1f} dB, {T * 0.02:.1f} s decoded in {elapsed * 1000:.0f} ms -> {'ok' if ok else 'FAILED'}")
    if failures:
        raise SystemExit(f"{failures} sample(s) failed")
    print("Native TOKUDER decoder matches the PyTorch reference.")


if __name__ == "__main__":
    main()
