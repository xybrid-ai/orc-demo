# Speech input

Hold **Left Option** to talk instead of typing. Your words are transcribed on
this Mac by Whisper through the Xybrid SDK's whisper.cpp runtime (Metal); no
audio leaves the machine.

## Where the words go

- **Trola asks your name or business:** the words fill his field and are sent
  on their own after one second. Type or delete during that second to correct
  them instead. The name reader picks the name out of a sentence ("My name is
  Malik" gives Malik), as it does for typing.
- **Trola's choice checkpoints** (the hub and the reckoning): d1 reads the
  words and selects the approach they mean, or Trola replies to the remark.
  This works during Vesper's advice, while hints fade, and after they disappear.
  Pressing Left Option fades Vesper out immediately; all answers address Trola.
- **Free talk after the gate:** the words fill the reply field and are sent
  after a second, or queued while Trola is still speaking. Clear words such as
  "Go in" or "Enter camp" go into the camp ([docs/entry.md](entry.md)).
- **Anywhere else** (while Trola speaks an authored line) nothing is sent; a
  note at the bottom left says so.

The input slot and checkpoint are fixed when the key goes down. A restart,
click, or another player action invalidates pending recordings, transcriptions
and classifications. No late result can answer the new conversation.

Trola's idle impatience pauses during advice, the hint preview, hint recall,
recording, transcription, classification and acknowledgement. Holding Tab for
an extended time keeps that pause active.

## Answering at the buttons

Say what you would click, in your own words ("That's a fine wall you built",
"I'll pay you a fair toll"). Suggestions appear after Vesper's advice: **Flatter**, **Bribe** and
**Talk about camp**, staggered by 0.15 s, held together for two seconds, then
faded out. Hold **Tab** or toggle **Hints** to recall them; clicking interrupts
Vesper. Later checkpoints show their available nonaggressive approaches.
Threats and demands remain valid spoken actions, but are never suggested.
Hint visibility has no effect on the accepted actions.
Personal compliments such as "I like your tusks" count as Flatter, and offers
phrased as questions, such as "Do you want money?", count as Bribe. The choice
prompt distinguishes those from neutral questions about his features or money,
refusals and references to past actions; history only helps resolve references.

- **A button:** it reappears in its original position, lights up for 0.45 s,
  then executes exactly once as a click,
  so Trola answers it, Vesper reacts to it and patience moves as usual. The
  player line shows your words. d1 must give the button 0.6, or 0.8 for
  Threaten and Demand the gate (they cost two patience; the second ends the
  encounter).
- **An in-world remark** (confident `none`, intent, topic and fact labels): a
  brief authored reply, then the same checkpoint. It consumes no choice,
  patience or progress; incidental praise and apologies grant no extra progress.
  This return does not repeat advice or the preview; hints remain available.
- **An insult:** the existing rebuke and one-patience cost. At one patience
  left his rebuke is a last warning; with none left he throws you out.
- **An unrelated task or a role override** (scope out_of_world or
  role_override at 0.5): the same authored rebuke and cost.
- **Anything unsure** (a low score, a near tie, a garbled transcript): Trola
  asks what you mean, at no cost. This is uncertainty about the intended choice,
  so his clarification does not assume you spoke too quietly or mumbled.

With `-xybridWriter gemma`, an in-world insult is worded by the
planned Gemma writer (reaction `off-topic`, the cost stays one patience). The
last warning, the expulsion, out-of-world answers and fact questions ("What is
my name?") stay authored, and so does any rebuke Gemma fails or takes more than
5 s to write. F4 shows the line's source (LLM, LLM fallback or authored).

d1 answers through `POST /choose` on `serve_decisions.py`; restart the server
after updating (see [decision-layer.md](decision-layer.md#spoken-answers-at-the-buttons-post-choose)).
Without it (server off, d1 off in F4, an older server or a request over 2.5 s),
Vesper's keyword reading picks a button or asks again, and never costs patience;
Threaten and Demand the gate then need the word "threat" or "demand". F4's
voice line shows the last spoken answer, what it became and why
(`"nice wall" → Flatter (d1 flatter 0.91 (next none 0.05) / in_world 0.95, 312 ms)`).
Missing or invalid intent/topic/fact fields on a `none` response use the same
conservative keyword-or-clarification fallback. `/choose` obtains these labels
in its existing single d1 pass, without another `/decide` request.
The option descriptions d1 reads are in `tools/npc-quality/decision-choice-options.json`;
the rebukes are `off_topic_lines`, `off_topic_last_line` and `unclear_lines`
in the story.

## Milestone 1 validation

Milestone 1 passed a focused confirmation in Unity 6000.6.4f1: the existing C#
decision regressions, sequential preview/fade, Tab and mouse recall, original-slot
acknowledgement, duplicate prevention, neutral checkpoint return, restart
cancellation, view re-enable ordering and transcription-task recovery. The
scene had zero missing scripts. Runtime input checks used a controlled
checkpoint and keyword answers; microphone recognition and the new voice mix
still need Play Mode review. The local d1 service was restarted with the new
contract. No player/native builds or measurement runs were performed.

## Setup

```sh
python3 tools/setup_asr.py                  # Whisper base.en, 60 MB (default)
python3 tools/setup_asr.py --model small-en # 190 MB: better on names, about 3x the GPU time
```

The script downloads the pinned `ggerganov/whisper.cpp` file (MIT), checks its
size and SHA-256, and writes `Assets/StreamingAssets/XybridModels/whisper-<model>/`
with `model.bin`, `model_metadata.json` (Xybrid's `GgmlWhisper` template,
`audio_ctx` 0) and `provenance.json`. Like Vesper's voices it is optional and
not in `local-assets.json`: without it the game has no speech input and typing
works as before. `PlayerVoiceInput.ModelKey` selects the folder (`base-en`).

The first press opens the microphone (macOS asks Unity for permission once)
and keeps it open for the session, so each later press gets 0.3 s of audio from
before the key went down.

## How it works

`PlayerVoiceInput` installs itself next to `GatekeeperController`, loads the
model two seconds after the scene starts and warms it on a second of silence.
On release it waits 0.25 s, then `PlayerSpeechAudio`:

1. downmixes to mono and resamples to 16 kHz (windowed-sinc low-pass, so
   content above 8 kHz is filtered rather than folded into speech);
2. finds the speech: 20 ms frames 10 dB above the clip's quietest fifth and
   above -50 dBFS. Less than 0.3 s of speech, or a peak under -45 dBFS, is
   "Didn't catch that";
3. trims to the speech with a 160 ms margin and encodes a PCM16 WAV.

Whisper's transcript loses bracketed tags, notes and its silence fillers ("you",
"Thank you.") when the clip was quiet. `tools/npc-quality/verify.py` runs
`PlayerSpeechChecks` over the audio helpers. F4 shows the state, the last
transcript and its time.

Not yet: live partial transcripts while you talk (the SDK's `Model.Stream`),
typed answers at the buttons, and talking over Trola. Unity's microphone has no
echo cancellation, so that would need headphones; his idle taunt can still be picked up; Vesper fades when recording begins.
