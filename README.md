# Orc Demo

A standalone Unity gatekeeper encounter: local d1 selects authored replies
after the scripted encounter, with live Phonon speech using a separately
supplied cloned voice.
KittenTTS 2 / paired WarChief remains an optional provider.
The NPC Tavern remains a separate project:
https://github.com/xybrid-ai/xybrid-unity-tavern.
Trola answers each player act with one spoken line; see
[docs/trola-turns.md](docs/trola-turns.md) before editing his script.
Hold Left Option to talk instead of typing (Whisper, on device; stage it with
`python3 tools/setup_asr.py`, see [docs/voice-input.md](docs/voice-input.md)).
Subtitles appear phrase by phrase as Trola and Vesper speak; see
[docs/subtitles.md](docs/subtitles.md).
Each encounter opens on the road: a short walk up to the gate, Vesper's warning
and Trola's challenge, then the close-up (Space skips; Try again replays it).
See [docs/approach.md](docs/approach.md).

## Open locally

Use **Unity 6000.6.4f1**, URP 17.6.0 and Xybrid Unity SDK **0.16.0**
(resolved through OpenUPM by `Packages/manifest.json`). This project currently
speaks with the public **Phonon 31m** (`gradium/phonon-31m-en`) through
**zzz v0.1.0-rc.9** (pinned to `5de2a037`) on CPU/macOS ARM64, keeps
**ptts 0.6.0** selectable on the same checkpoint, and
retains the direct zzz **rc.4** Kitten bridge. The standard SDK native omits the zzz
engine, so both voices keep their own bridges.

1. Clone this repository. No Git LFS is required.
2. Restore the local dependencies below. With an existing licensed installation:
   `python3 tools/local_assets.py --from-project "/path/to/Xybrid Tavern"`.
   This copies only manifest-listed dependencies, preserves GUIDs and executable
   permissions, and never overwrites existing files. On APFS it clones file
   storage without linking the projects together.
3. Run `python3 tools/local_assets.py` to check required paths, sizes and importer
   metadata. Add `--verify-hashes` for recorded model/plugin hashes.
4. Follow [zzz Phonon setup](docs/phonon-zzz.md) to build the plugin and stage
   your matching checkpoint and cloned safetensors. These inputs stay local.
5. Optional, for Vesper's voice: stage the [xn-ptts bridge](docs/phonon-integration.md)
   with `tools/setup_phonon.py`, then run `python3 tools/setup_spirit_voice.py`
   ([spirit setup](docs/spirit.md#setup)). Without them she shows subtitles only.
6. Open in Unity, then open
   `Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity` and press Play.

The orc scene is the sole scene in Build Settings. Only this encounter's source,
authored data, material settings and scene references belong in Git. Tavern scenes and conversation systems are independent. The small PCM-to-AudioClip
utility is retained locally in this project for the speech path.

## Local dependencies

`local-assets.json` records exact required paths, sizes and importer metadata
hashes. Imported/derived assets stay local even outside `Assets/Synty`.

- Synty **POLYGON Goblin War Camp**, **Sidekick Characters** (GoblinFighter_03),
  **Animation Base Locomotion**, and **PolygonGeneric** shared dependencies.
  Obtain/import your own licensed packages under `Assets/Synty`. Preserve `.meta`
  GUIDs. The working Sidekick prefab and avatar can include local customizations;
  a stock pack import alone is not guaranteed to reconstruct those. Restore the
  manifest-listed customized files from your licensed installation. No pack
  binaries, extracted meshes, textures or shaders are redistributed here.
- Cinzel font and locally generated authored/reaction audio at their original
  paths. These are also external pending explicit provenance/license packaging.
- `Assets/Xybrid/Plugins/macOS/libxybrid_bolt.dylib` from the **0.16.0** native
  bundle, preserving plugin importer metadata and the version marker. The SDK
  downloads it (SHA-256 checked) the first time the project opens with a
  different marker; that first Play can pause while it does.
  The obsolete `libxybrid_ffi.dylib` is not part of this project's dependency set.
- `Assets/XybridAvatarDemo/Plugins/macos-arm64/libzzz_embed.dylib`, the direct
  rc.4 bridge. Keep its provenance with the model bundle. This demo's validated
  platform is macOS Apple Silicon; other native platforms need their own setup.
- `Assets/StreamingAssets/KittenTTS2/`: `kitten-tts-2-q2_0.gguf`,
  `kitten-s3-meanflow-f32.gguf`, **both** `warchief.lm.json` and `warchief.s3.json`,
  metadata/provenance. Never mix voice pairs. The optional Kitten/WarChief path
  uses sentence chunks, Accelerate and six threads. Startup uses Phonon.
- Historical writer, optional for play but retained in the restoration manifest:
  `Assets/StreamingAssets/XybridModels/gemma4npc-e2b-q4/`: metadata and
  `Gemma4NPC-E2B-Q4_K_M.gguf`, from
  https://huggingface.co/chimbiwide/Gemma4NPC-E2B-GGUF.
  For M1 Metal, only auxiliary `per_layer_model_proj.weight` was converted
  BF16 to FP16; quantized tensors are unchanged. Prepared SHA256:
  `2a6f2d10f8ca60f357048a8ec9dd52c7d2f9f81d777ccd8c1a86373a89295846`.
  See `tools/prepare_mac_weights.py`; acquire the upstream model under its own
  terms, then prepare it locally. Upstream SHA256:
  `d808fcac6160f69139cca6583e3d66470f2b21fb3b6a1895ee01bf47a29f721e`.
- The Kokoro `universal.xyb` fallback and Rhubarb Lip Sync 1.14.0 macOS tool
  and resources under `StreamingAssets`, for the retained debug/speech paths.
- Optional and not in `local-assets.json`: Vesper's pinned Apache-2.0 stock
  voice, `Assets/StreamingAssets/Phonon/spirit.safetensors` with its
  provenance, staged by `tools/setup_spirit_voice.py`, and your own camp laugh
  clips under `Assets/XybridAvatarDemo/Resources/CampAmbience/`.

Free talk uses a bank of authored replies selected by d1's topic and intent.
Gemma no longer preloads. The game owns facts, patience, admission and endings;
without d1 it uses narrow factual routes and neutral authored fallbacks.
F4 shows the writer, topic and decision status. To compare the historical
Gemma writer explicitly, launch Unity with `-xybridWriter gemma`; it then
preloads during the intro and writes planned replies with the grounded context that won the
[context experiment](docs/gemma-context-experiment-2026-11-03.md) (same parser and policy).
See the [blind reading and implementation](docs/authored-replies-2026-11-03.md).

## Headless dialogue quality

Use the [NPC quality harness](tools/npc-quality/README.md) to exercise Trola's
actual prompts, six-exchange history, structured reply parser and authored
encounter rules without launching Unity. It logs local model replies, first-token
and completion timing, speech-ready JSON and state transitions. See the
[model comparison and limits](docs/npc-quality-2026-10-15.md) before choosing a
checkpoint. The demo's installed model and Phonon startup voice remain the
local setup described above.

Free-form name, business, offer-recall and admission questions have a narrow
deterministic route backed by encounter state. Unknown personal facts receive
an authored reply. Offer details must be recorded by a trusted game action;
the `bribe` approach alone supplies no amount.

The optional decision layer separates player intent from a game-owned response
plan and the authored reply bank. In Play Mode, a local d1 server on the Apple
GPU classifies each free-form message when it is running; otherwise messages
use built-in factual routes and neutral authored replies. See
[decision-layer staging and checks](docs/decision-layer.md)
for the server, the contract, recorded-decision replay and the focused Apple GPU probe.

Vesper, an optional spirit in the player's head, advises at Trola's
choice checkpoints with authored advice. All player answers address Trola;
temporary suggestions can be recalled with Tab or the Hints button. A procedural camp soundscape and
tension music play with or without her. See [Vesper and the camp](docs/spirit.md)
for setup, the F6 lens, F7 spirit, F8 ambience, Tab and Esc keys, and the
`-xybridNoSpirit`, `-xybridNoAmbience` and `-xybridLens` flags.

For a playable Apple Silicon Gemma3NPC trial, see the
[trial build instructions](docs/gemma3-trial-build.md). Its build-only model
selection explicitly keeps the generative writer for that trial; the ordinary
Editor defaults to authored replies.

## Repository and release policy

Run `python3 tools/check_repository.py` before committing or packaging source.
It checks tracked and untracked source candidates for external assets, LFS,
large binaries and embedded mesh/texture data. `.gitattributes` disables inherited
LFS filters. Models, natives, imported packs and caches are ignored.

Distribute this source repository with local import instructions. Do not upload
full-project ZIPs, `Assets/` archives or `.unitypackage` exports containing paid
art. A player release is a separate action requiring the relevant asset/model
terms to be checked; this split does not create or authorize one.

Current restoration is from a licensed working project, not a fully automated
fresh-machine downloader. WarChief, customized Sidekick assets and the direct
native bundle are local dependencies with no public provisioning endpoint yet.

## Extraction confirmation

One Editor check passed on macOS Apple Silicon: scene loaded with zero missing
scripts, live Kitten/WarChief audio played, and Gemma's intro preload completed.
No player/native builds or measurement runs were performed. Unity logged a
streamed-clip PCM-analysis warning (the performer uses its live-output fallback)
and a Search index startup exception; these are separate follow-ups.

Historical Gemma and Kitten integration sources are retained in
[docs/integration-history](docs/integration-history/README.md). Active code stays
in `Assets/XybridAvatarDemo`; the historical installers should not be used.
