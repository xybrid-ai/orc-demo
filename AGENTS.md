# Orc Demo agent guide

This is the standalone orc encounter; keep NPC Tavern separate.
Paid Synty/imported art and customized Sidekick meshes are local dependencies.
Preserve .meta GUIDs. No LFS, model/native binaries or full-project ZIPs.
Run python3 tools/check_repository.py before commits.
Run python3 tools/local_assets.py after local dependency restoration.
Use Unity 6000.6.4f1 and Xybrid 0.9.0. Phonon is the selected startup voice:
public Phonon 31m (gradium/phonon-31m-en, 5f0a2aab@200) on pinned zzz
v0.1.0-rc.9 (5de2a037) with a separately supplied clone.
The ptts 0.6.0 CPU bridge remains selectable as XnPtts on the same checkpoint.
Preserve complete-clip playback (Phonon: one trimmed clip per line; Kitten:
complete clauses) and six Phonon threads. Keep the optional Kitten rc.4
provider, paired WarChief voice, sentenceChunks=true, Accelerate and six
Kitten threads. Do not upgrade the SDK as part of voice integration.
See docs/phonon-zzz.md and docs/phonon-integration.md for local staging and the native build boundary.
Vesper, the spirit, speaks through a second XnPtts engine on the same checkpoint
with a stock Apache-2.0 voice: stage it with python3 tools/setup_spirit_voice.py.
Her lines are authored, chosen by rules from d1 /spirit labels; see docs/spirit.md.
Default validation: one Editor confirmation; no builds or measurement suites
unless explicitly requested. See README.md for external dependency limits.
