# Orc Demo agent guide

This is the standalone orc encounter; keep NPC Tavern separate.
Paid Synty/imported art and customized Sidekick meshes are local dependencies.
Preserve .meta GUIDs. No LFS, model/native binaries or full-project ZIPs.
Run python3 tools/check_repository.py before commits.
Run python3 tools/local_assets.py after local dependency restoration.
Use Unity 6000.6.4f1 and Xybrid 0.9.0. Phonon is the selected startup voice,
using the pinned ptts 0.4.0-rc.1 CPU bridge and separately supplied clone.
Preserve complete-clause playback and six Phonon threads. Keep the optional
Kitten rc.4 provider, paired WarChief voice, sentenceChunks=true, Accelerate
and six Kitten threads. Do not upgrade the SDK as part of voice integration.
See docs/phonon-integration.md for local staging and the native build boundary.
Default validation: one Editor confirmation; no builds or measurement suites
unless explicitly requested. See README.md for external dependency limits.
