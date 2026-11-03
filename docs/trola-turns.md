# Trola's turns: one reply per player act

Each thing the player does gets one answer from Trola: a single line, synthesized
as one Phonon clip and shown as one subtitle. The answer responds to what the
player said, then hands the turn back with the next question.

## How a turn is built

`AuthoredGatekeeperLineSource` speaks a node's line. When that node leads straight
into a checkpoint (the name or business prompt, the hub, or the reckoning), it
appends the checkpoint's line with `GatekeeperLineSources.Join`, so the reply and
the question are spoken together. The reply line sets the emotion, intensity and
delivery for the whole joined line. Ending variants (with an `outcome`) are never
joined; an ending can play a second line when its delivery changes, for example a
roar followed by a quiet threat.

`TrolaAuthoredConversation.json` adds two fields to the story graph:

- `Variant.revisit_line_ids` replaces `line_ids` when the story comes back to a
  node: the hub in round two, or an approach chosen twice. An empty list means
  the node uses `line_ids`.
- `Choice.say` holds the player's words for a choice. The view shows them as
  `You: "..."` above Trola's subtitle while he answers; they are never voiced.
  The button text, conversation history, Vesper's advice and the tests still use
  `label`.

Write each line so that it answers the `say` of the choice that leads to it. Keep
a joined reply and question within 135 characters with sample names, so the
subtitle fits on two lines. `verify.py` checks the length budget, one line per
turn, a hand-back after every turn, and that the JSON matches the asset.

## Writing for the voice

Phonon hears each full stop as the end of a sentence, so Trola's voice falls and restarts at every one. The game
speaks a line with its internal full stops turned into commas (see `docs/live-speech-debug.md`, Speech text), but
writing helps more: one or two full sentences joined by commas and small words ("so", "but", "and") flow better than
a chain of fragments ("Right. Go on. So talk."). Keep `?` where a question really is one (it rises mid-line; at the end
of a line it falls in this voice), end each line with one mark, and avoid dashes and ellipses: the text frontend
deletes dashes and turns ellipses into full stops.

## Waiting, taunts and interruptions

- An idle taunt at a choice checkpoint keeps the buttons live. Clicking a choice
  during the taunt makes that choice, with no "Oi" and no patience cost. The taunt
  itself cannot be interrupted.
- While a story line plays, clicking elsewhere on the screen does nothing. Only
  the Interrupt button interrupts, and it ignores presses in the first 0.5 s of a
  line, so the second click of a double-click on a choice doesn't count.
  Interrupting costs one patience and returns to the same checkpoint.
- A typed name or business may end with sentence punctuation ("Sam.",
  "Buy weapons!"); `GatekeeperNames.TrySanitize` drops it before checking.

## Editing the script

Unity speaks the serialized `lines` in `Scenarios/TrolaGatekeeper.asset`, while
the headless checks read the JSON. After editing the JSON, open
`GoblinSpeakingDemo` and run **Xybrid › Avatar Demo › Configure Authored
Gatekeeper** to rewrite the asset, then run `python3 tools/npc-quality/verify.py`.
Configure also saves the scene, which Unity may reserialize; commit only the JSON
and the asset unless you meant to change the scene.
Give a line a new id when its text changes, so Configure doesn't reattach a baked
clip of the old words. Baked clips are never played (every line is synthesized
live), but `Validate` still checks them.
