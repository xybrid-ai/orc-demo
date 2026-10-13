# Live Phonon voice

Phonon is the default voice on the integration branch. It feeds the existing
complete-clause playback, saved debug audio, Rhubarb analysis and smoothed jaw
through `IGatekeeperSpeechProvider`. Every request generates fresh audio; debug
WAVs are evidence and are never replayed as a voice cache.

The native bridge builds **ptts 0.4.0-rc.1 / xn 0.2.10**, CPU/Q8, six threads,
English, temperature 0.3 and seed 0. The external cloned voice is registered
through the checkpoint's speaker-latent projection. Config, tokenizer, model
and voice compatibility remain upstream validation responsibilities; this
does not bypass encoder checks or reinterpret older beta embeddings.

## Local setup

Use an Apple Silicon Mac, macOS 13+, Rust 1.98.0 (`rustup toolchain install 1.98.0`) and the existing Unity 6000.6.4f1 /
Xybrid 0.9.0 installation. Supply the matching checkpoint folder containing
`config.json`, `tokenizer.json`, `model.q8.gguf`, plus your cloned safetensors:

```sh
python3 tools/setup_phonon.py \
  --checkpoint /absolute/path/to/phonon-checkpoint \
  --voice /absolute/path/to/cloned-voice.safetensors
```

The script builds from the committed Cargo lock, checks ARM64 architecture and
native exports, and stages regular copies under ignored `Assets/StreamingAssets/Phonon`.
The distinct `liborc_phonon.dylib` is staged with a macOS ARM64 importer beside
the optional Kitten plugin. No engine archive, model, cloned voice, binary or
private provenance receipt belongs in Git. `--skip-build` stages a plugin
already built from this source. `tools/phonon-native/clean.sh` previews ignored
build output; `--apply` removes it.

The existing rc.4 zzz Phonon library rejects the newer checkpoint. This bridge
therefore calls the current upstream CPU runtime directly. The published Apple
`ptts-ffi` selects Core ML and expects an exported bundle, which is a different
input from this supplied raw CPU checkpoint. It is not a drop-in native slice
for this integration. No Python process or server is needed during gameplay.

## Try in Unity

Refresh assets, open `Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity`, and
enter Play mode. Press **F4**: the voice line should read
**Voice: live Phonon / cloned voice; 6 CPU threads**. Press **F5**, choose Gemma
replies if placeholders are selected, and send a short line. Listen to final
words, clause pauses and voice fidelity; watch mouth movement and queued Send.

The inspector exposes the checkpoint/voice paths and thread count. Relative
paths resolve against StreamingAssets. Startup chooses Phonon by default;
`-xybridSpeech Kitten` overrides it, or disable `startWithPhonon` to retain the
existing Kitten startup selection. Kitten's WarChief pair and settings are
preserved. Failed Phonon setup surfaces the voice error; it does not silently
switch to a slower voice.

Phonon requests use the configured preparation budget (15 seconds by default)
as a cancellation deadline, rather than Kitten's 120-second wait floor. Each
native cancellation targets the current call ID, including cancellation racing
entry. Disposal waits for synthesis and upstream stream workers to return.
Cancellation is checked between PCM packets; it cannot interrupt a tensor
operation already executing. This is not a hard wall-clock shutdown guarantee.

## Limited verification

Verified on the development Mac: the Rust 1.98.0 native release build passed,
and the complete runtime compiled using Unity 6000.6.4f1 Roslyn and its existing
assembly response file, with the new provider added and output redirected to
an ignored folder. Existing deprecated FindFirstObjectByType warnings remain.
Editor menu automation was unavailable, so this was a compiler check rather
than an Editor refresh or playback confirmation.

One fresh call through the actual managed/native provider passed with the
staged clone: "The gate is open, Mira." produced 2.16 seconds of non-silent
24 kHz mono audio in 0.204 seconds; model opening took 0.080 seconds. This is
a single smoke observation, not a latency benchmark or an in-game guarantee. Run the simple provider check with:

```sh
python3 tools/phonon_smoke.py
```

It compiles the real provider against the installed SDK and requires complete,
non-silent 24 kHz mono PCM. Its AOT annotation shim is test-only. This proves
managed/native generation, not Unity playback or IL2CPP support. Broader voice
listening, playback drain, interruptions, Play-mode exit, script reload and
standalone-player checks are left for manual testing. No new latency suite or
repeated measurements are required for this branch.
