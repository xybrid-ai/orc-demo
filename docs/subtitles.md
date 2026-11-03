# Subtitles, sentence by sentence

When Trola or Vesper speaks a line of several sentences, the subtitle shows each sentence as the voice starts saying it,
not the whole line at once. Nothing about the speech changes: Phonon still speaks each line as one trimmed clip
(Kitten as complete clauses), with the same text, trimming, voices and threads.

## What the player sees

- While Trola's voice is being prepared, his subtitle shows the line's first sentence.
- Once the clip plays, each later sentence appears 100 ms before he starts saying it and fades in over 150 ms. The
  whole line is laid out from the start, with the unspoken sentences transparent, so the text never moves or rewraps
  as sentences appear.
- Pausing the audio (`AudioListener.pause`, a paused clip) freezes the reveal. A pitch change does not affect it.
- When the line is no longer playing (finished, skipped, waiting for the player, ended) the whole line shows.
- An interruption replaces the line as before. Until his next line arrives, only the part he had already said stays
  on screen.
- A line shows whole at once when it has no audio (the subtitle-only fallback), when a prepared reaction clip is
  playing, when it is a single sentence, or when it contains `<` or `>`. Lines are never read as markup; player
  names and businesses are inserted into them. The loudness-jaw fallback still has audio, so it reveals normally.
- Vesper's line does the same, driven by her own clip. Her line already fades in as a whole; the unspoken sentences
  stay hidden through that fade, and a new sentence's fade multiplies with it. Her subtitle-only lines show whole.

`GatekeeperController.CurrentText`, the history, d1, the F4 dump and the tests still use the full line.
`GatekeeperView.ShownLine` is the plain line on screen; the self-test compares that.

## How the timing works

`GatekeeperSubtitleTiming` holds all of it and is pure code; `tools/npc-quality/SubtitleTimingChecks.cs` covers it.

- **Sentences.** A sentence ends at `.` `!` `?` or `…`, plus any closing quote or bracket, when whitespace and more
  text follow. Abbreviations (`Mr.`, `Dr.`, `St.`) and initials (`J. R.`) don't end a sentence.
- **Onsets.** On the voice worker, right after `GatekeeperSpeechTrim`, each Phonon whole line gets the sample in its
  clip where each sentence after the first starts (`GatekeeperLiveSpeech.Generated.sentenceOnsets`). The warm-up
  line skips this. The onset is where speech resumes after the pause before the sentence.
- **Why the text decides.** Pause length alone can't tell a sentence end from a comma. In the trimmed clips a full
  stop pauses about 420 ms and a comma about 310 ms, but an interjection's comma ("Hmph, ...") also pauses about
  420 ms. A full stop the flowing style turned into a comma pauses about 355 ms, and the voice sometimes breathes
  where there is no punctuation.
- **How the aligner matches them.** It predicts a timeline from the spoken words: each word lasts in proportion to
  its letters, and each punctuated gap expects a pause of a length typical for its punctuation. It finds the real
  pauses in the clip: quiet runs of 10 ms windows 40 dB under the loudest one, with the threshold raised over a
  noisy floor. A dynamic program then matches pauses to gaps in order, scoring how likely each pause length is at
  that punctuation and how well the speech between two matched pauses fits the predicted duration. If a sentence
  start has no matched pause, it is placed by its letters and moved to the end of a clear dip nearby.
- **Flowing style.** The aligner reads the text the voice was actually given, so a sentence end spoken as a comma is
  still found.
- **Fallbacks.** The aligner never throws. On a failure, or input too large to align quickly, it returns a
  character-share estimate.
- **Clause playback.** Kitten and Kokoro play each `SplitClauses` piece as its own clip. There, a sentence shows when
  the clause that contains its start begins playing (`ClauseStarts`).
- **Playback.** The controller and Vesper's voice compare `AudioSource.timeSamples` with the onsets of the clip that
  is actually playing (`Reached`). `Markup` writes the line with the hidden sentences in `<color=#00000000>`, and the
  view turns `supportRichText` on only while it shows markup. The scene keeps it off ("Use literal slot subtitles").

The 100 ms lead is there because the onset is where speech resumes, and the pauses before sentences last 190-440 ms.
The new sentence therefore appears just inside the silence, never after the first word has begun.

## Accuracy

The aligner (`SubtitleTiming_Final.cs`, copied verbatim; local tournament files are under `.context/subtitles/`) won a
tournament of three designs. The winner, a duration model, took robustness changes from the runners-up: a noise-floor-aware threshold, a
dip snap for unmatched sentence starts, and caps on its work.

The reference was Whisper small.en word timings. The reference onset is where the silence between the two words ends.

- **Held-out test.** Renders of the current Trola and Vesper lines with ptts 0.6.0, as written and flowing. The
  aligner picked the right pause for 187 of 187 sentence starts and was never early. 94.1% of starts were within
  150 ms of the strict reference; in the rest, Whisper stretched a word into the silence. A character-share baseline
  gets 25%.
- **Real in-game clips from the owner's Mac.** zzz Phonon for Trola and XnPtts for Vesper: 120 clips with 146
  sentence starts. The aligner picked the right pause for 145 of 146 and was never early. The miss is a clip where
  Whisper dropped "Hmph".
- **Cost.** About 0.15-0.45 ms per line on the voice worker. The worst case was 3.8 ms, in a stress test of 400
  sentences over 40 s.

The runtime file returns exactly what the tournament's file returns on every corpus clip: 658 clips and 1061 onsets,
with no difference.

## The switch

F4 has one button for both characters: **Subtitles: by sentence — show whole lines** /
**Subtitles: whole lines — reveal by sentence**.

- The default is by sentence.
- It applies at once and changes only the display.
- It is remembered in PlayerPrefs (`Trola.subtitleReveal`).
- `-xybridSubtitles sentences|whole` sets it for one run.

The F4 line list shows where each sentence was detected in the line's clip, for example `Sentences at 0.00, 1.42,
2.97 s`.

## Limits

- A line shows whole when nothing times it. That is a line with no audio, or a single clip without onsets: a Kitten
  or Kokoro line spoken as one clause, or a streamed line. It is also clause playback whose clauses can't be placed
  in the line.
- In clause playback a sentence appears when its clause starts. A sentence that starts inside a clause (after `…`,
  which `SplitClauses` doesn't split at) appears with that clause, a little early.
- The words of the displayed and spoken text must be the same, which they are for both speech styles. If the word
  counts differ, sentence starts are mapped by position, which is less precise.
- A wrong pause is still possible on lines unlike any measured: odd symbols, code, or long runs of digits. A
  mistimed sentence appears early or late at a pause; it is never dropped.
- Accuracy was measured on Phonon 31m voices only (Trola's clone on zzz, Vesper's stock voice on ptts).
