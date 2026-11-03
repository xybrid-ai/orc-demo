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

Phonon hears each full stop as the end of a sentence, so Trola's voice falls and restarts at every one. A blanket
comma rewrite lost to the written line in blind listening (see `docs/live-speech-debug.md`, Speech text), so it is
off by default; writing is the lever. Where a short tag would stand alone ("Walk.", "Stop there."), join it with a
word ("Walk away, while...", "...teeth, so before..."); a deliberate beat ("Right. Last chance. Surprise me.",
"Oi. I was speaking.") can keep its full stops. Keep `?` where a question really is one (it rises mid-line; at the end
of a line it falls in this voice), end each line with one mark, and avoid dashes and ellipses: the text frontend
deletes dashes and turns ellipses into full stops.

The two comma-preferred turns (the given-name reply and the friendly bribe reply) are authored with commas too.
Their reply fragments end with a comma so `Join` continues into the checkpoint's lowercased "so," or "right,";
the completed turn still ends in a full stop. Shared checkpoint prompts keep their normal punctuation elsewhere.

## Waiting, taunts and interruptions

- An idle taunt at a choice checkpoint keeps the buttons live. Clicking a choice
  during the taunt makes that choice, with no "Oi" and no patience cost. The taunt
  itself cannot be interrupted.
- While a story line plays, clicking elsewhere on the screen does nothing. Only
  the Interrupt button interrupts, and it ignores presses in the first 0.5 s of a
  line, so the second click of a double-click on a choice doesn't count.
  Interrupting costs one patience and returns to the same checkpoint.
- The name field takes 48 characters, so the player can answer in a sentence.
  `GatekeeperNames.ReadName` reads the name out of it: "I am Malik", "Hello,
  I'm Malik" and "I go by the name of Anna Maria" give Malik and Anna Maria,
  in whole words within 16 characters, title-cased when typed all lowercase or
  all caps. A refusal ("None of your business", "My name is Nobody"), an
  answer with no name in it ("I'm a traveller", a non-Latin script) or a
  blocked word anywhere in the answer gets the assigned name, Worm first: "No
  proper name? Then you're Worm." Trola's lines, `{name_mangled}`, Vesper, d1
  and Gemma all read that one `GatekeeperEncounter.PlayerName`; the F4 dump
  shows the reading next to it.
- The business field takes 90 characters. `TryReadBusiness` keeps the player's
  own words, because Trola quotes them back: the first sentence, without
  leading fillers or the ending punctuation ("Buy weapons!"), digits allowed
  ("selling 10 turnips"), in whole words within 60 characters. A blocked word, or an answer it can't read, is
  "begging".
- The player line (`You: "..."`) shows what the player said, not what Trola
  read from it. `TryReadSaid` keeps letters, digits, spaces and `. , ! ? ' -`,
  up to 60 characters on a word boundary, and shows nothing for a blocked
  answer. `verify.py` runs `tools/npc-quality/name-cases.json` and
  `business-cases.json` through every consumer.

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
