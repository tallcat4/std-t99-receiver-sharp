"""Convert a tokuder-neurodec checkpoint (models/<name>.pt, a PyTorch state dict) to the flat
TKD1 file embedded in t99_native.dll. Needs PyTorch only for this conversion; the build uses the .bin.

TKD1: "TKD1", uint32 count, per tensor (uint16 name length, name, uint8 ndim, int32 dims, uint64 offset
in floats), then all tensors as little-endian float32."""
import argparse
import struct
from pathlib import Path

import numpy as np
import torch


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("checkpoint", type=Path)
    ap.add_argument("output", type=Path)
    a = ap.parse_args()
    state = torch.load(a.checkpoint, map_location="cpu", weights_only=True)
    header, payload, offset = [b"TKD1", struct.pack("<I", len(state))], [], 0
    for name, tensor in state.items():
        data = tensor.detach().float().contiguous().numpy().astype("<f4")
        key = name.encode("utf-8")
        header.append(struct.pack("<H", len(key)) + key + struct.pack("<B", data.ndim)
                      + struct.pack(f"<{data.ndim}i", *data.shape) + struct.pack("<Q", offset))
        payload.append(data.tobytes())
        offset += data.size
    a.output.write_bytes(b"".join(header) + b"".join(payload))
    print(f"{len(state)} tensors, {offset} parameters -> {a.output}")


if __name__ == "__main__":
    main()
