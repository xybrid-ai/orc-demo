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
- **Free talk after the gate:** the words fill the reply field and are sent
  after a second, or queued while Trola is still speaking.
- **Anywhere else** (the choice hubs, while Trola speaks an authored line)
  nothing is sent; a note at the bottom left says so. Choices are still
  clicked.

Holding the key also counts as activity, so Trola's idle taunt waits while you
speak and while the words are transcribed.

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
choosing hub options by voice, and talking over Trola. Unity's microphone has no
echo cancellation, so that would need headphones.
