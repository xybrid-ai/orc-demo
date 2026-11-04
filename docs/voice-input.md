# Speech input

Hold **Left Option** to talk instead of typing. Your words are transcribed on
this Mac by Whisper through the Xybrid SDK's whisper.cpp runtime (Metal); no
audio leaves the machine.

## Where the words go

- **Trola asks your name or business:** the words fill his field and are sent
  on their own after one second. Type or delete during that second to correct
  them instead. The name reader picks the name out of a sentence ("My name is
  Malik" gives Malik), as it does for typing.
- **Vesper is listening** (her field is open): the words are your thought to
  her, sent at once. While you hold the key her field shows `...`, which also
  keeps her window from closing mid-sentence. At the name and business prompts
  Trola wins: she is telling you to answer him.
- **Trola's choice buttons** (the hub and the reckoning), while Vesper is not
  listening: d1 reads the words and presses the button they mean, or Trola
  reacts. See [Answering at the buttons](#answering-at-the-buttons).
- **Free talk after the gate:** the words fill the reply field and are sent
  after a second, or queued while Trola is still speaking.
- **Anywhere else** (while Trola speaks an authored line) nothing is sent; a
  note at the bottom left says so.

Who hears the words is fixed when the key goes down. If Vesper's field closes
before the words are ready, nothing is sent ("Vesper stopped listening"). The
hint at the bottom left names the listener at the buttons ("Hold Option to
answer Trola" or "…Vesper").

Holding the key also counts as activity, so Trola's idle taunt waits while you
speak and while the words are transcribed.

## Answering at the buttons

Say what you would click, in your own words ("That's a fine wall you built",
"I'll pay you a fair toll"). The buttons stay on screen as hints.

- **A button:** it lights up for 0.45 s and is pressed exactly like a click,
  so Trola answers it, Vesper reacts to it and patience moves as usual. The
  player line shows your words. d1 must give the button 0.6, or 0.8 for
  Threaten and Demand the gate (they cost two patience; the second ends the
  encounter).
- **Something else in the world** (d1 says none at 0.6 or more): Trola
  rebukes you, loses one patience and asks again with the same buttons. At one
  patience left his rebuke is a last warning; with none left he throws you out
  (the Demand the gate expulsion, Refused).
- **An unrelated task or a role override** (scope out_of_world or
  role_override at 0.5): the same authored rebuke and cost.
- **Anything unsure** (a low score, a near tie, a garbled transcript): Trola
  asks you to say it again, at no cost.

With `-xybridWriter gemma`, an in-world off-topic answer is worded by the
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
The option descriptions d1 reads are in `tools/npc-quality/decision-choice-options.json`;
the rebukes are `off_topic_lines`, `off_topic_last_line` and `unclear_lines`
in the story.

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
echo cancellation, so that would need headphones; speaking during his idle taunt
or Vesper's voice can transcribe them too.
