# Sources and licenses

- **std-t99-receiver** (GPL-3.0): STD-T99 Annex 2 receiver, frame synchronization, channel decoding, messages and test vectors in `third_party/std_t99_receiver` and `tests`. https://github.com/tallcat4/std-t99-receiver
- **SPEAR** (GPL-3.0): FIR, polyphase channelizer, RRC design and Gardner symbol synchronization in `third_party/spear`. https://github.com/tallcat4/SPEAR
- **std-t98-receiver-sharp** (GPL-3.0): SDR# plugin structure, selected-channel IQ resampler, eye view, audio output and WAV writer. https://github.com/tallcat4/std-t98-receiver-sharp
- **tokuder-neurodec** (GPL-3.0-or-later): TOKUDER channel code and the `tokuder48_causal_v1` decoder, ported to C++ in `native/tokuder.*`; model weights in `models`. https://github.com/tallcat4/tokuder-neurodec .
  The weights were trained with VCTK (CC BY 4.0), JVS (personal / non-commercial research only) and JSUT (terms of the distributor); see the tokuder-neurodec README for the notes on the training data and the weights.
- **Natural Earth** (public domain): 1:10m cultural and physical vectors for the offline map of Japan (`src/SDRSharp.T99/japan-map.bin`, built by `tools/make-map.py`). https://www.naturalearthdata.com
- **FFTW 3.3.5**: single-precision Windows x64 runtime. See `licenses/fftw.txt` and https://www.fftw.org . Its runtime is bundled; SDR# is not bundled.
- **Python, NumPy and PyTorch** are development/test dependencies only. No Python runtime, CUDA or external model download is required or bundled in the plugin.

TOKUDER is a registered trademark of Icom Incorporated. This project is not affiliated with or endorsed by Icom.
