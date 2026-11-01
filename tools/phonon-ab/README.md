# Phonon A/B harness

Compares the upstream xn-ptts reference with the zzz port on the same Phonon
checkpoint and voice, and optionally the previous `7e71a02d@200` drop, on the
game's own clauses. Results from 2026-10-31 are in
[docs/phonon-31m-ab.md](../../docs/phonon-31m-ab.md). This is a measurement
suite: run it only when a comparison is requested.

Requirements (Apple Silicon Mac): a zzz v0.1.0-rc.9 checkout built with
`zig build engine-build -Doptimize=ReleaseFast`, uv, ffmpeg, whisper.cpp
(`whisper-cli`) with a ggml model, the 31m checkpoint at commit `52433c46`, a
local copy of `gradium/phonon-voices` (for the `freya` and `grub_the_ogre`
controls converted into the bundle) and the Trola clone.

```sh
ZZZ_ENGINE=/path/to/zzz-research P31=/path/to/phonon-31m-en PV=/path/to/phonon-voices \
CLONE=/path/to/clone.safetensors WHISPER_MODEL=/path/to/ggml-large-v3-turbo-q5_0.bin \
P7E=/path/to/phonon-7e71a02d.200 SEEDS="1 2 3" tools/phonon-ab/run.sh
```

`P7E` is optional. Outputs stay under the ignored `.context/phonon-ab`:

- `renders/<arm>/trola_clone/s<seed>/` with clips, zzz `tts_summary` logs and Whisper JSON
- `metrics.csv`, `per_item.csv`, `worst_cases.csv` and `summary.md` from `score.py`
- `listening/index.html`, a blind A/B page with a Reveal tally and a JSON export

The engines use different random number generators, so clips are compared as
per-item means over seeds, never paired by seed. zzz seed 0 equals seed 1.
xn-ptts runs with `XN_KAI=0`, so both engines use plain Q8_0 kernels.
