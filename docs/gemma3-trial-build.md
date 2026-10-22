# Gemma3NPC Mac trial build

`GatekeeperTrialBuild.BuildMac` builds an Apple Silicon player of the standalone
orc encounter with the grounded Trola prompt, Gemma3NPC-it Q4_K_M and the existing
Phonon startup voice. The trial player uses the `XYBRID_GEMMA3_TRIAL` scripting
define; the Editor and ordinary builds continue to select E2B.

Requires the licensed local assets, Unity 6000.6.4f1, the installed Xybrid 0.9.0
native bundle, and the existing Phonon zzz #407 plugin/model/clone. No native
rebuild or SDK upgrade is needed.

Stage the verified Gemma3NPC directory under
`Assets/StreamingAssets/XybridModels/gemma3npc-it-q4/`. It must contain
`model_metadata.json` and `gemma-3npc-it-q4_k_m.gguf` (SHA-256
`8d1d1a7f693237fcc6515274d2a6665fe867471a1d95005a39e21ec1a3c8c384`).
Keep the demo's 8192-token context. Models remain ignored local inputs.
Unity copies StreamingAssets into the app; omit the unused E2B directory from
trial packaging and restore it afterward to avoid carrying both model files.
Keep the Phonon bundles, Rhubarb and any desired debug voice providers.

With the Editor closed and the local dependency paths prepared:

```sh
ORC_TRIAL_BUILD_PATH="$PWD/Builds/Trola-Gemma3NPC.app" \
  /Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -quit -projectPath "$PWD" -buildTarget OSXUniversal \
  -executeMethod GatekeeperTrialBuild.BuildMac \
  -logFile "$PWD/.context/gemma3-trial-build.log"
```

The helper selects ARM64 for the build, then restores the architecture setting.
It writes a sibling `.app.build.json` receipt. A successful startup logs
`[Trola] NPC ready: gemma3npc-it-q4` after local loading and prefix priming.
The player can be opened directly in Finder. F4 exposes the existing debugging
controls; no automated dialogue run is necessary to try the character.

This is a local playable trial. No app, purchased art, clone or model files are
published by this workflow. The [Mac comparison](npc-mac-quality-2026-10-18.md)
documents the latency/quality tradeoff and remaining factual failures.

## Local build — 22 October 2026

Built and launched on the Mac at
`~/Dev/games/orc-demo/Builds/Trola-Gemma3NPC-2026-10-22.app` (5.1 GiB).
Unity reported success with zero errors and 16 warnings; ARM64 architecture and
strict deep code-sign verification passed. Gemma3NPC readiness was confirmed in
the player log and live session snapshot. Phonon generated eight fresh speech
WAVs during the startup check. Two clauses fell back during initial warmup;
subsequent audio generated successfully. Listening and gameplay quality remain
for the manual trial. The player also reported an unavailable PaniniProjection
postprocessing shader; no visual review was performed in this smoke check.

The original Mac project source, model folders and build-induced settings
changes were restored, preserving the three pre-existing ProjectSettings edits.
The app was left open and revealed in Finder. F5 jumps to free-form conversation;
F4 toggles the debug panel. Build logs and smoke receipts are under
`.context/gemma3-trial-build-2026-10-22/` on the Mac.

## Initial playtest feedback — 22 October 2026

The player's first feedback was roughly three seconds of thinking with
consistent answers. This is a qualitative observation from the trial, not an
instrumented latency average: turn count, input set and first-audible-output
timing were not recorded. It supports continuing the Gemma3NPC trial without
establishing long-conversation reliability or resolving the initial clause
fallbacks.

The next useful acceptance check is 10–15 exchanges covering player-name and
offer recall after history rollover, identity-changing instructions, missing
facts and the game's Refused state. Listen for missing speech and clause gaps,
check mouth/voice alignment, and record model and voice preparation separately
from first audible output. The Mac comparison already shows that neither
candidate should own authoritative game facts.
