# Phonon via zzz PR #407

This branch selects the CPU Phonon engine from
[zzz-research PR #407](https://github.com/xybrid-ai/zzz-research/pull/407), pinned
at `9524f098f1d6fab3ce5a115d2ccb3e1861c4540c`. It uses the same supplied
7e71a02d@200 Q8 checkpoint and cloned speaker latents as the branch below it.
The original [xn-ptts provider](phonon-integration.md) remains selectable.

## Build and stage

Apple Silicon macOS 13+, Zig **0.16.0 stable**, Python 3.11+, Git, and the
existing Unity 6000.6.4f1 / Xybrid SDK 0.9.0 installation are required.

```sh
python3 tools/setup_phonon_zzz.py \
  --checkpoint /absolute/path/to/phonon-checkpoint \
  --voice /absolute/path/to/cloned-voice.safetensors
```

The script fetches the exact engine revision into `.context/phonon-zzz407`,
builds the small bridge, converts the checkpoint using that revision's
converter, and stages the plugin and bundle. The converter's voice catalogue
gets one local entry with the supplied clone's digest; model/config/tokenizer
pins, encoder metadata, dtype and latent shape checks remain enabled. This
supports the custom clone without editing the upstream tracked source.

`liborc_phonon_zzz.dylib` has its own library name and C symbol prefix, keeping
it separate from xn-ptts and Kitten. Binaries, model/voice files, converted
bundle manifests and local receipts stay ignored. There is no Python process
or server during gameplay. `--skip-build` re-stages a previously built plugin;
`tools/phonon-zzz-native/clean.sh` previews its ignored build outputs.

## Threading and behavior

PR #407's `zzz_embed_open` does not supply a pool and therefore still opens
Phonon with one thread. This bridge uses that exact revision's internal
Phonon session with an owned upstream thread pool, six threads by default.
The synthesis caller participates as thread zero; five workers are owned by
the session. macOS does not use the Android per-core affinity placement.
Upstream hot-region behavior and numerical kernels are unchanged.

The bundle's temperature default is used (0.3 for this drop), and each
utterance resets zzz's seed to zero. zzz and xn-ptts use different random
number generators; their waveform, duration and stop behavior need not match.
The PR's bit-identical claim concerns zzz across thread counts, not equality
between engines. Its OnePlus throughput claim is not a Mac benchmark.

Managed call serialization, rooted callbacks, copied/validated PCM and
call-ID cancellation remain in the existing provider. Cancellation is checked
at emitted frame boundaries and cannot interrupt the tensor operation already
running. Frame-limit output is reported as `Limited`, never completed speech.
The existing complete-clause playback, Rhubarb and queued Send are retained.

## Manual test

Refresh assets, open GoblinSpeakingDemo and press Play. **F4** should show
**Voice: live Phonon / cloned voice / zzz #407; 6 CPU threads**. Use **F5**,
choose Gemma replies, and send a short sentence. Check voice fidelity, final
words, clause pauses, lips and queued delivery. Broader interruptions,
Play-mode exit, script reload and player support remain manual checks.

To return to xn-ptts, select **XnPtts** in the `GatekeeperLiveSpeech` inspector
before Play; the original checkpoint and safetensors paths are retained.
Do not change engines during an active worker session.

## Verification

The requested verification is the native build, runtime C# compilation with
Unity's existing compiler inputs, and one fresh managed/native call:

```sh
python3 tools/phonon_smoke.py --zzz
```

The smoke test requires completed, non-silent 24 kHz mono PCM. It saves raw
PCM16 in ignored `.context/phonon-smoke/zzz407.pcm16` for optional listening.
This establishes real managed/native synthesis, not Unity playback or IL2CPP.
Native ReleaseFast build with Zig 0.16.0 and the complete runtime C# compiler
check passed on the development Mac. C# used Unity 6000.6.4f1 Roslyn and its
existing assembly response file, redirecting output to an ignored folder;
existing deprecated FindFirstObjectByType warnings remain. The .NET provider
harness compiled with zero warnings/errors.

The single call was **"The gate is open, Mira."**, CPU/six threads, supplied
clone, seed 0 and bundle-default temperature 0.3:

| Observation | xn-ptts (previous branch) | zzz #407 (this branch) |
| --- | ---: | ---: |
| Model open | 0.080 s | 0.217 s |
| Synthesis, native entry to completion | 0.204 s | 0.186 s |
| Generated audio | 2.16 s | 2.24 s |
| Audio duration / synthesis duration | 10.6x | 12.0x |
| First PCM callback | not recorded in prior smoke | 0.079 s |

Both observations produced completed non-silent 24 kHz mono PCM. These are
single smoke observations from different moments, not a controlled A/B or
proof of a Mac performance gain. The new run synthesized slightly faster but
opened more slowly. First callback is not first audible output: the demo
still waits for the complete clause before playback. No additional latency
suite, Unity playback, lifecycle or player test was run.
