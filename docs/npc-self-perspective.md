# Trola self-perspective quality pass

The final card makes Trola's own motive concrete: he built the wall and takes
pride in keeping trouble outside. It separates NPC and player pronouns, asks
for a direct answer before banter, marks missing facts as unknown, and labels
the game's admission outcome as authoritative. Typed JSON examples show
first-person replies; neutral questions should carry patience_delta 0.
Repetitive confrontational dialogue examples were removed from the card.
The authored encounter and its voice lines are unchanged.

The per-turn prompt repeats the actual player name/business and the speaker
reference beside the new question. The model, temperature 1, top-p .95,
top-k 64, 112-token budget, grammar, parser and six-exchange history policy
are retained. This is a prompt change, not a deterministic recall fix.

The headless comparison reproduces the old card's deflection: to the job
question E2B answered “Your business is not my business. Keep moving.” A final
card sample answered “I care about my wall. It stands firm against meddling
and the rustlers.” Other samples remained weak; player identity, unknown
facts and admission contradictions still occur. See the complete
[model comparison and limits](npc-quality-2026-10-15.md), including raw
receipts and the stronger Gemma3NPC candidate. No model is certified to own
game facts or admission state.

The upstream structure (`Enter Roleplay Mode`, `You are`, `Background`,
`Location`, `Quest`, `Roleplaying Instructions`) and sampling guidance remain
in use. Sources:

- [E2B card](https://huggingface.co/chimbiwide/Gemma4NPC-E2B-GGUF/blob/b8e57bca1c8e5455178c16cd9554b2f75bb0c9fc/README.md)
- [E4B card](https://huggingface.co/chimbiwide/Gemma4NPC-E4B-GGUF/blob/5b1e65fc3ac2e10f9d3a846ed6fbf0ee25308499/README.md)
- [NPC system-prompt generator](https://github.com/chimbiwide/Gemma3NPC/blob/main/datasets/npc_dialogue_v2/create_system_prompt.py)
- [Roleplay examples](https://github.com/chimbiwide/Gemma3NPC/blob/main/demos/hf_spaces/general_demo.py)

Validation uses the actual prompt/grammar/parser and scenario C# source in the
[headless harness](../tools/npc-quality/README.md), with the demo's pinned 0.9.0
managed bindings and a locally compiled Xybrid Rust worker. The adapter tests
cover authored endings, malformed replies, reflection rejection, history
rollover and patience depletion. This is headless source validation; it does
not claim a Unity Editor, playback or lip-sync check. Nothing was run on the
Mac, and no SDK upgrade is staged in the demo.
