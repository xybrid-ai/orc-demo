> Archived integration snapshot. Use the project root README and current
> `Assets/XybridAvatarDemo` implementation. Do not run these historical installers
> against the current demo. See [integration history](../README.md).

# KittenTTS 2 troll gatekeeper spike

This adds a resident KittenTTS 2 rc.4 speech provider to the existing Xybrid Tavern troll demo. Kitten starts by default with the custom WarChief voice and synthesizes every dialogue line, including the intro and name prompts. Kokoro remains selectable. The authored dialogue, performance layer, F3 timing overlay and prefab restoration guards continue to work. Kokoro mode retains its prepared reactions and late Rhubarb cues; Kitten mode skips the Kokoro reaction clips. F4 selects dialogue speech: Kokoro, Kitten full line, Kitten sentence delivery, or Kitten batch with Rhubarb. Switching providers restarts the encounter and drains the previous worker before opening another engine session.

The current route is rc.4's C API, relinked into a Unity native plugin. The local Xybrid SDK route is being developed separately; both routes execute the same Kitten engine. The conditional SDK adapter is supplied for migration but needs the matching package and plugin before it can be enabled.

## Tested project and targets

- Project: `/Users/glennsonna/Dev/games/Xybrid Tavern`.
- Scene: `Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity`.
- Unity: 6000.3.6f1, installed Xybrid package 0.9.0.
- rc.4 desktop plugin: native macOS ARM64, macOS 13 or later. Use an Apple Silicon editor and player; the project's previous Intel player architecture cannot load it.
- Linux native host checks: x86-64-v3. The setup tool checks CPU instructions before loading the archive.
- Windows, iOS and WebGL have no rc.4 release archive for this spike. The Android release is outside this desktop demo's staging workflow.
- IL2CPP callbacks have a static rooted delegate and `MonoPInvokeCallback` annotation. Actual IL2CPP validation is still required; the current player uses Mono.

## Prepare assets and install

`assets.json` pins the rc.4 source commit, both release archives, the source model revisions and SHA-256 for all four prepared assets. Model binaries and native libraries are build outputs and are not committed.

Use a zzz checkout at `56385c4a3fbab8bc503341947af37e96ebb0071d`. The existing Mac reference checkout is `/Users/glennsonna/conductor/workspaces/zzz/port-vila`. Its prepared models already live in `~/.zzz/models`. For a fresh machine, fetch the pinned sources and run the release's conversion scripts from that checkout (requires `uv`, Python and NumPy only during preparation):

```sh
mkdir -p ~/.zzz/models
curl -fL -o ~/.zzz/models/model-tq2_1.gguf \
  https://huggingface.co/KittenML/kitten-tts-2/resolve/baa41e5d2c5f64be0095365672a7858542261271/cpp/model-tq2_1.gguf
uv run --script scripts/repack_kitten_tts.py \
  ~/.zzz/models/model-tq2_1.gguf ~/.zzz/models/kitten-tts-2-q2_0.gguf
curl -fL -o ~/.zzz/models/s3gen_meanflow.safetensors \
  https://huggingface.co/ResembleAI/chatterbox-turbo/resolve/749d1c1a46eb10492095d68fbcf55691ccf137cd/s3gen_meanflow.safetensors
uv run --script scripts/convert_kitten_s3_to_gguf.py \
  ~/.zzz/models/s3gen_meanflow.safetensors ~/.zzz/models/kitten-s3-meanflow-f32.gguf
```

The converters refuse existing output files and validate their pinned source. Prepared Bruno voice inputs are the checkout's `fixtures/reference/kitten-tts-2/bruno-short.json` and `fixtures/reference/kitten-s3/bruno-voice.json`; the installer stages them as `bruno.lm.json` and `bruno.s3.json`.

Run the following from this directory on the Mac, with Unity in Edit Mode. Xcode command-line tools, Python 3 and `gh` release access are required. `--archive` can select an already downloaded, checksum-matching archive.

```sh
python3 tools/setup_native.py \
  --project '/Users/glennsonna/Dev/games/Xybrid Tavern' \
  --zzz-checkout '/Users/glennsonna/conductor/workspaces/zzz/port-vila'
python3 tools/stage_voice_pair.py \
  --project '/Users/glennsonna/Dev/games/Xybrid Tavern' \
  --voice-dir ~/.zzz/voices/warchief --name warchief \
  --lm-sha256 d8bb800f8fa51bef14bfa2d39a389f9380e15bfe40dca4b20424e4cd42ba7f39 \
  --s3-sha256 b1b205e5adad0357af7ee99d7bb184eb6fdb18a47258ddb55d280eeeaecf1d3e
python3 tools/install_mac.py \
  --project '/Users/glennsonna/Dev/games/Xybrid Tavern'
```

Setup relinks the static archive, signs the dylib, writes platform-specific Unity plugin import settings, verifies/copies the four assets under `Assets/StreamingAssets/KittenTTS2`, and records provenance. Install validates the source baseline, compiles runtime replacements using Unity's own compiler, then backs up overwritten files under `Library/XybridAvatarDemo/Backups`. A baseline mismatch stops installation: review and merge the newer demo changes before updating the expected hash. Refresh Assets and start a fresh Play session. No scene save is needed.

Read `~/.zzz/voices/warchief/README.md` before staging the private clone. Its speech-LM and waveform files are a matched pair: `warchief.lm.json` and `warchief.s3.json`. The staging tool checks both hashes and schemas, copies both files, and updates `language_voice_file` and `decoder_voice_file` together in `model_metadata.json`. It leaves both GGUF models untouched and records the pair in `voice_pair.json`. Always stop Play and retire the resident session before changing a voice. The direct provider derives both paths from the single `kittenVoice` prefix, which defaults to `warchief`; the panel displays **Kitten / WarChief**. For stock Bruno, explicitly set that prefix to `bruno` before opening a new session.

WarChief is a private character-test asset. Keep the clone JSONs out of the public repository and public build distribution. The voices remain local to the game project's StreamingAssets folder.

The installed WarChief pair compiled in the actual Unity editor and opened successfully during gameplay. The NPC line “Before you lose yours, give me your name.” completed with 24 kHz mono audio lasting 3.16 seconds. The playback buffer detected samples above amplitude 0.01 starting at 0.028 seconds, confirming non-silent synthesized PCM. This is a short-line Editor check; duration depends on the text and is not a fixed 5.8 seconds.

Keep upstream code attribution and each model/voice license with any distributed assets. zzz's code and model licenses are separate; this example does not redistribute models or provide additional voice rights.

## Playback and cancellation

Each provider opens and synthesizes on the existing serialized voice worker. The direct callback copies borrowed float32 into owned PCM16 little-endian packets before returning. Format/offset checks reject unexpected packets. The audio thread drains a bounded 180-second PCM ring; buffer gaps produce silence and are measured. Unity clips and scene objects are created/accessed on the main thread.

Full-line delivery receives audio after a whole line is synthesized. Sentence delivery receives each completed sentence earlier, but later sentences can leave playback gaps and add total work. Streaming lip sync follows loudness immediately. Batch mode starts playback when PCM is ready and attaches Rhubarb cues when analysis finishes, retaining the earlier latency improvement.

Settings start at six threads, `max_tokens=0`, seed zero, Accelerate on macOS only, streaming playback and sentence delivery enabled. Each sentence can play as soon as its waveform is ready, while the worker generates the rest of the line. The silent "Hrm. Listen." warm-up remains in `Start` after session open. F4's Kitten full-line option still selects whole-line delivery for comparison. Cancellation reaches native computation independently of the synthesis lock. Queued cancellation is checked before starting; interrupted packets are discarded. The provider closes only after the active call returns. Limited output is retained and displayed as partial speech rather than silently reported as complete. Kitten gets up to 120 seconds to prepare a line; its slower synthesis does not inherit Kokoro's short retry budget. Set `startWithKitten=false` to honor the saved provider at startup, or use the `-xybridSpeech Kokoro` launch argument. The default startup switch requires no scene save.

## Repeat validation

The sentence-delivery default was installed and compiled in the Mac project. A clean scene reload confirmed `sentenceChunks=true` and `streamKitten=true`; Unity had preserved the old value in memory across recompilation. No scene save was needed. The silent warm-up completed before the following three lines, each measured once through the actual controller, avatar and AudioSource with WarChief, Accelerate and six threads:

| Sentences | Text | First packet | Total synthesis | Audio | Playback underrun |
|---|---|---|---|---|---|
| 1 | Your name sounds like trouble. | 7.37 s | 7.39 s | 2.68 s | 0 s |
| 2 | State your business. The village gate stays closed until I know who you are. | 6.40 s | 13.06 s | 5.04 s | 5.64 s |
| 3 | Tell me your name. Then explain your business. The village gate stays closed until I know who you are and what you want here today. | 4.04 s | 18.26 s | 8.16 s | 11.16 s |

All completed at 24 kHz mono, with one/two/three engine chunks respectively and no limited chunks. These are single warm Editor observations, not medians or standalone-player benchmarks. Sentence delivery emitted audio before the multi-sentence lines finished synthesis, but generation was slower than playback in this run. Machine load average was 12.11 and the Unity process consumed approximately 502% CPU during the final load snapshot. The source of the slowdown was not isolated, and approximately two-second first packets from a separate native test were not reproduced here. Evidence is in `.context/kitten/sentence-gameplay-report.json` and `sentence-gameplay-load.log`; the normal encounter was resumed after the probe.

Editor menu: **Xybrid → Avatar Demo → Validate Kokoro And Kitten**. This runs four delivery modes with 5-word, 12-word and three-sentence lines, four repetitions each; only the last three of each length count as warm measurements. It uses the actual controller, avatar and AudioSource, then checks cancellation, reuse, active shutdown and missing assets. The report is `Library/XybridAvatarDemo/kitten-editor-report.json`.

The build helper can be called through the editor:

```csharp
XybridKittenChecks.Build("Builds/KittenTroll/KittenTroll.app",
    "Library/XybridAvatarDemo/kitten-build-report.txt");
```

It temporarily selects Apple Silicon and macOS 14 via `PlayerSettings.macOS.targetOSVersion`, builds the existing scene without saving it, and restores the original project settings. The higher minimum accommodates the complete local SDK artifact; the direct engine alone supports macOS 13. Run the resulting executable:

```sh
'Builds/KittenTroll/KittenTroll.app/Contents/MacOS/Xybrid Tavern' \
  -xybridSpeechTest -xybridReport /tmp/kitten-player-report.json
```

For manual play: `-xybridSpeech Kitten`, optionally `-xybridKittenSentenceChunks` or `-xybridKittenBatch`. F3 shows per-line latency; F4 selects the provider. Validation reports distinguish first reply audio from the earlier prepared reaction sound; mixer timestamps exclude physical output-device latency. All raw runs, including outliers, should be kept alongside reported medians.

Native host checks (not Unity gameplay timings):

```sh
LD_LIBRARY_PATH=build/linux-x86_64 dotnet run --project Tests/SpikeChecks.csproj \
  -- ~/.zzz/models /tmp/kitten-native-report.json
```

## SDK migration

Install a package/plugin built with rc.4 and Bazel `--//:tts_zzz=true`. The SDK workspace has prepared package version 0.10.1 under `.context/kitten-rc4/delivery/Package`, the ARM64 plugin at `.context/kitten-rc4/delivery/Plugins/macOS/libxybrid_bolt.dylib`, and Mac metadata at `.context/kitten-rc4/delivery/model_metadata.macos.json`. The reported plugin SHA-256 is `fae9bd89bf9a8280416224bd6bc675ac9900287dd00379ed7911ff54eca0a13b`. Its inspected Mach-O deployment minimum is macOS 14.0. These are paths in the SDK workspace; no copied Mac path or Unity execution is verified yet. Follow `delivery/Package/Samples~/KittenTts/README.md` there for the full source recipe. Published replacement natives do not enable Kitten.

The finished cross-built artifact is [kitten-sdk-macos-arm64-local-0.10.1.tar.gz](https://glenn-cloud-w15.conductor.show/kitten-sdk-macos-arm64-local-0.10.1.tar.gz), available while the SDK workspace is awake and requiring Conductor sign-in/read access. Verify archive SHA-256 `0251e0e9192ea3636821d29fc4c6cff578183abb7123221b04ed49a457ec2fd1` before extraction. It contains Package, Plugins with importer metadata and the hidden version marker, Mac model metadata and instructions; weights and voices remain the existing verified assets. Cross-building and Mach-O inspection do not establish Unity playback or M1 performance.

Close the Editor before replacing the SDK. Park old duplicate SDK plugins outside both Assets and the package, copy the complete staged plugin tree with its importer metadata, and write `0.10.1` to `Assets/Xybrid/Plugins/.xybrid-native-macos-version`. The local Mac plugin includes ONNX Runtime statically. Copy the matching Mac metadata as `model_metadata.json` alongside the four prepared files. It sets six threads, unlimited default tokens (`max_tokens=0`), seed zero, sentence delivery and Accelerate; the Linux version disables Accelerate.

Enable `XYBRID_KITTEN_RC4_SDK` only after verifying the new C# surface, then select `Provider.KittenSdk`. The adapter uses `ModelLoader.FromDirectory(...).Load()` and disposes each `Envelope.Text(text)` after `RunTtsStreaming(input, callback, cancellationToken)` returns. It preserves owned packet offsets and completed/cancelled/limited outcomes. Batch uses `Run(input, cancellationToken: token)`, keeping partial audio rather than `RunTts(string)`, which throws on limited output. Dialogue and playback need no SDK-specific changes.

SDK loading is lazy: the first synthesis call also opens the native engine. Reports therefore separate managed loader time and the first synthesis call, and mark standalone native-open timing as measured only for the direct provider. A short `Load()` call must not be reported as cold native setup.

SDK source is [PR #638](https://github.com/xybrid-ai/xybrid/pull/638), commit `6ccba71e7d99dec5a892aff9fc2a104a83c9bb45`. The game's enabled adapter compiles against that commit's actual managed API and generated Bolt sources under C# 9/netstandard2.1. The SDK team also completed .NET 8 native-host Kitten and Kokoro regression suites against its delivered Linux plugin. These checks do not establish Mac import, Unity audio playback, M1 performance, Mono player or IL2CPP execution; those remain pending the latency-agent handoff.

See the measurement report for the recommendation and validation status.
