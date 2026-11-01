# Phonon via zzz rc.9

The startup voice is the public **Phonon 31m**
([`gradium/phonon-31m-en`](https://huggingface.co/gradium/phonon-31m-en),
checkpoint `5f0a2aab@200`, Apache-2.0) run by
[zzz-research](https://github.com/xybrid-ai/zzz-research) **v0.1.0-rc.9**,
pinned at `5de2a037896a874f75efbdaf683888c5e3156020`, with the separately
supplied Trola clone. The [xn-ptts provider](phonon-integration.md) remains
selectable on the same checkpoint. The previous pin, PR #407 (`9524f098`) with
the private `7e71a02d@200` drop, is still convertible for rollback. The
[31m A/B](phonon-31m-ab.md) records why the model and engine changed.

## Build and stage

Apple Silicon macOS 13+, Zig **0.16.0 stable**, Python 3.11+ with NumPy (via
[uv](https://docs.astral.sh/uv/)), Git, and the existing Unity 6000.6.4f1 /
Xybrid SDK 0.9.0 installation are required.

The rc.9 converter pins 31m's original `config.json` (2,851 bytes, SHA-256
`7062ea37…`), so download the checkpoint at the Hugging Face commit it was
validated against. Later commits only add transformers configuration and change
the card's default voice; the weights are identical at main (`92741c5b`).

```sh
hf download gradium/phonon-31m-en config.json tokenizer.json model.q8.gguf \
  --revision 52433c46975bfa13144c56204c9f4ee1c5b148aa --local-dir /absolute/path/to/phonon-31m-en
uv run --no-project --python 3.12 --with numpy python tools/setup_phonon_zzz.py \
  --checkpoint /absolute/path/to/phonon-31m-en \
  --voice /absolute/path/to/cloned-voice.safetensors
```

The script fetches the exact engine revision into `.context/phonon-zzz-rc9`
(or uses `--engine-checkout`), builds the small bridge, converts the checkpoint
with that revision's converter and stages the plugin and bundle. The checkpoint
kind is detected from its config digest: 31m uses the public conversion, and the
`7e71a02d@200` drop uses the speaker-latent conversion. The converter's voice
table gets one local entry with the supplied clone's digest; model, config,
tokenizer, encoder metadata, dtype and latent shape checks remain enabled. The
clone's encoder (`ba7a5a45@300`) is the one 31m's own voices use, so no
re-cloning is needed.

`liborc_phonon_zzz.dylib` keeps its own library name and C symbol prefix,
separate from xn-ptts and Kitten. Binaries, model/voice files, converted bundle
manifests and local receipts stay ignored. There is no Python process or server
during gameplay. `--skip-build` re-stages a previously built plugin;
`tools/phonon-zzz-native/clean.sh` previews its ignored build outputs.

## Threading and behavior

rc.9's `zzz_embed_open` still opens Phonon with one thread (its embed sources
are unchanged since #407), so the bridge keeps using the revision's internal
Phonon session with an owned upstream thread pool, six threads by default. The
synthesis caller participates as thread zero. zzz output is identical for every
thread count. For rc.9 the bridge sets the build option
`pool_chunk_factor_explicit` (used only by LLM callers) and accepts both the
public 31m and the speaker-latent bundle profiles.

The bundle's temperature default is used (0.3), and each utterance resets zzz's
seed to zero. The 31m bundle uses the `xn_ptts_v040` text frontend, which adds
xn-ptts v0.4.0's rewrites for currency, times and numbers. zzz and xn-ptts use
different random number generators, so waveforms do not match between engines.

On 31m, with either engine, a bare "Hmph." clause does not reach a natural stop
and runs to its frame cap (3.36 s instead of about 1.3 s). The Phonon path plays
limited audio rather than failing, so the clause is only longer.

Managed call serialization, rooted callbacks, copied/validated PCM and
call-ID cancellation remain in the existing provider. Cancellation is checked
at emitted frame boundaries and cannot interrupt the tensor operation already
running. Frame-limit output is reported as `Limited`, never completed speech.
The existing complete-clause playback, Rhubarb and queued Send are retained.

## Manual test

Refresh assets, open GoblinSpeakingDemo and press Play. **F4** shows
**Voice: live Phonon / cloned voice / zzz rc.9; 6 CPU threads**. Use **F5**,
choose Gemma replies, and send a short sentence. Check voice fidelity, final
words, clause pauses, lips and queued delivery.

To use xn-ptts, select **XnPtts** in the `GatekeeperLiveSpeech` inspector
before Play; the label then reads **xn-ptts 0.6.0**. Do not change engines
during an active worker session. To roll back, rerun the setup script with the
`7e71a02d@200` folder.

## Verification

```sh
python3 tools/phonon_smoke.py --zzz
```

The smoke test compiles the real provider against the installed SDK and
requires completed, non-silent 24 kHz mono PCM. It saves raw PCM16 in ignored
`.context/phonon-smoke/zzz.pcm16`. This establishes real managed/native
synthesis, not Unity playback or IL2CPP.

On an M5 Max (macOS 27), the single call **"The gate is open, Mira."**
(CPU, six threads, supplied clone, seed 0) passed through both providers:

| Observation | zzz #407, 7e71a02d (previous) | zzz rc.9, 31m | xn-ptts 0.6.0, 31m |
| --- | ---: | ---: | ---: |
| Model open | 0.217 s | 0.011 s | 0.016 s |
| Synthesis, native entry to completion | 0.186 s | 0.065 s | 0.049 s |
| Generated audio | 2.24 s | 2.24 s | 2.16 s |
| First PCM callback | 0.079 s | 0.005 s | 0.014 s |

The previous column was measured on the earlier development Mac. These are
single smoke observations, not a controlled benchmark. One Editor Play session
on the M5 Max confirmed the 31m voice in game. No player build, latency suite
or IL2CPP check was run.
