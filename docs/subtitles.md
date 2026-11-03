# Subtitles, phrase by phrase

When Trola or Vesper speaks, the subtitle shows each phrase of the line as the voice starts saying it, not the whole
line at once. A phrase ends at any mark the voice may pause at: a full stop, `?`, `!`, `…`, a comma, `;`, `:` or a
dash. Nothing about the speech changes: Phonon still speaks each line as one trimmed clip (Kitten as complete
clauses), with the same text, trimming, voices and threads.

The reveal was first sentence by sentence. The owner's listening picks then turned most full stops into commas, so
most lines became one or two long sentences and still appeared nearly whole. "Stop there, this gate has teeth, so
before you lose yours, give me your name." is one sentence; the first sentence of the name reply is 80% of the line.
He chose to reveal at every pause, commas included:
"Stop there, → this gate has teeth, → so before you lose yours, → give me your name."

## What the player sees

- While Trola's voice is being prepared, his subtitle shows the line's first phrase.
- Once the clip plays, each later phrase appears 100 ms before he starts saying it and fades in over 150 ms. The
  whole line is laid out from the start, with the unspoken phrases transparent, so the text never moves or rewraps
  as phrases appear.
- Pausing the audio (`AudioListener.pause`, a paused clip) freezes the reveal. A pitch change does not affect it.
- When the line is no longer playing (finished, skipped, waiting for the player, ended) the whole line shows.
- An interruption replaces the line as before. Until his next line arrives, only the part he had already said stays
  on screen.
- A line shows whole at once when it has no audio (the subtitle-only fallback), when a prepared reaction clip is
  playing, when it is a single phrase, or when it contains `<` or `>`. Lines are never read as markup; player
  names and businesses are inserted into them. The loudness-jaw fallback still has audio, so it reveals normally.
- Vesper's line does the same, driven by her own clip. Her line already fades in as a whole; the unspoken phrases
  stay hidden through that fade, and a new phrase's fade multiplies with it. Her subtitle-only lines show whole.

`GatekeeperController.CurrentText`, the history, d1, the F4 dump and the tests still use the full line.
`GatekeeperView.ShownLine` is the plain line on screen; the self-test compares that.

## How the timing works

`GatekeeperSubtitleTiming` holds all of it and is pure code; `tools/npc-quality/SubtitleTimingChecks.cs` covers it.

- **Phrases.** A phrase ends at a run of `.` `!` `?` `…`, or at `,` `;` `:` `—` `–` or a hyphen with spaces on both
  sides (` - `), plus any closing quote or bracket, when whitespace and more text follow. `1,000`, `3:30`, `a—b` and
  `well-known` don't split. Abbreviations (`Mr.`, `Dr.`, `St.`) and initials (`J. R.`) don't end a phrase.
- **Sentences.** A sentence ends at `.` `!` `?` or `…` by the same rules. Every sentence end is also a phrase end.
  Sentences no longer split the display; they steer the aligner (below).
- **Onsets.** On the voice worker, right after `GatekeeperSpeechTrim`, each Phonon whole line gets the sample in its
  clip where each phrase after the first starts (`GatekeeperLiveSpeech.Generated.phraseOnsets`, from
  `PhraseOnsets`). The warm-up line skips this. The onset is where speech resumes after the pause before the phrase.
- **Why the text decides.** Pause length alone can't tell a sentence end from a comma. In the trimmed clips a full
  stop pauses about 420 ms and a comma about 310 ms, but an interjection's comma ("Hmph, ...") also pauses about
  420 ms. A full stop the flowing style turned into a comma pauses about 355 ms, and the voice sometimes breathes
  where there is no punctuation.
- **How the aligner matches them.** It predicts a timeline from the spoken words: each word lasts in proportion to
  its letters, and each punctuated gap expects a pause of a length typical for its punctuation. It finds the real
  pauses in the clip: quiet runs of 10 ms windows 40 dB under the loudest one, with the threshold raised over a
  noisy floor. A dynamic program then matches pauses to gaps in order, scoring how likely each pause length is at
  that punctuation and how well the speech between two matched pauses fits the predicted duration. A phrase starts
  where its matched pause ends. A phrase start with no matched pause (a comma the voice ran through) is placed by its
  letters between the matched pauses around it and moved to the end of a clear dip nearby.
- **One alignment, sentence-aware.** The gap classes come from sentence ends: a displayed sentence end almost always
  pauses, and one the flowing style turned into a comma gets its own pause prior. Commas inside a sentence keep the
  comma prior. One solve serves both readings: `Onsets` reads it at sentence starts, `PhraseOnsets` at phrase starts.
  A phrase that starts a sentence gets exactly the sentence's onset, and a phrase inside a sentence stays between
  its sentence's onset and the next one, so the finer reveal never moves a sentence start.
- **Flowing style.** The aligner reads the text the voice was actually given, so a sentence end spoken as a comma is
  still found.
- **Fallbacks.** The aligner never throws. On a failure, or input too large to align quickly, it returns a
  character-share estimate.
- **Clause playback.** Kitten and Kokoro play each `SplitClauses` piece as its own clip. Those pieces end at the
  same marks as phrases, so each phrase shows when the clause that contains its start begins playing
  (`ClauseStarts`, given the phrases).
- **Playback.** The controller and Vesper's voice compare `AudioSource.timeSamples` with the onsets of the clip that
  is actually playing (`Reached`). `Markup` writes the line with the hidden phrases in `<color=#00000000>`, and the
  view turns `supportRichText` on only while it shows markup. The scene keeps it off ("Use literal slot subtitles").

The 100 ms lead is there because the onset is where speech resumes, and the pauses before sentences and commas last
about 190-500 ms. The new phrase therefore appears just inside the silence, never after the first word has begun.
After a comma spoken through there is no silence; the phrase appears about 100 ms before its first word.

## Accuracy

The aligner (`SubtitleTiming_Final.cs`, copied verbatim; local tournament files are under `.context/subtitles/`) won a
tournament of three designs. The winner, a duration model, took robustness changes from the runners-up: a
noise-floor-aware threshold, a dip snap for unmatched starts, and caps on its work. Phrases reuse it unchanged.

The reference was Whisper small.en word timings. For a start with a pause, the reference onset is where the silence
between the two words ends: the end of the whole quiet run, because Whisper often stretches a word into the silence
(the stricter reference stops at the midpoint of Whisper's next word). "Right" (the right pause) means within 150 ms
of it. For a start with no pause, the reference is Whisper's start of the next word.

- **Sentence starts (unchanged).** Held-out test: renders of the current Trola and Vesper lines with ptts 0.6.0, as
  written and flowing. The aligner picked the right pause for 187 of 187 sentence starts and was never early. 94.1%
  were within 150 ms of the stricter reference. A character-share baseline gets 25%. Real in-game clips from the
  owner's Mac (zzz Phonon for Trola, XnPtts for Vesper; 120 clips, 146 sentence starts): the right pause for 145 of
  146, never early; the miss is a clip where Whisper dropped "Hmph".
- **Phrase starts.** The same renders, 686 clips with phrase ground truth (`tools/ground_truth.py` given the phrase
  spans). Dev was used for every decision; test was held out.

| Phrase start | Dev | Held-out test |
| --- | --- | --- |
| Sentence start, with a pause | 840 of 842 right, never early | 187 of 187 right, never early |
| Comma, with a pause | 536 of 543 right, 1 early | 97 of 106 right, 3 early |
| `;` or `:`, with a pause | 8 of 12 right | 13 of 18 right, 2 early |
| Any mark, no pause | 25: 7 early, 2 late | 5: none early, 3 late |

  "Early" is more than 150 ms before the reference; "late" (no pause) is more than 300 ms after it. The median error
  of every row with a pause is 0 ms. The corpus also holds eight code fragments its collector picked up by mistake;
  they are every `;` and `:` start, 8 of the 9 test comma misses and 4 of the 5 test starts with no pause. Without
  them, test commas with a pause are 88 of 89 right. That miss, and the one early dev comma, are clips where Whisper
  dropped or misplaced the player's name. The other dev comma misses are commas before the name that the voice nearly
  runs through ("Careful, Mira."): the phrase shows 170-280 ms late. Six of the seven early starts with no pause are
  "Fine, Mira.", where the phrase lands on a dip in the speech 160-240 ms before Whisper's start of "Mira". A
  character-share baseline gets 25 of 106 test commas.
- **Cost.** `PhraseOnsets` takes about 0.13 ms per line (median; 90th percentile 0.28 ms, worst 0.51 ms on a real
  line, 1.7 ms on a 30 s code fragment). A 40 s line of 800 phrases hits the work caps and takes the character-share
  estimate at once.

The runtime's `Onsets` returns exactly what the tournament's file returns on every corpus clip: 658 clips and 1061
onsets, with no difference. On every one of those clips, `PhraseOnsets` at sentence starts equals `Onsets`. A wider
dip snap for unmatched phrase starts was tried and left out: 200 ms gained 3 of 562 dev starts and nothing on test,
and 250 ms added an early one.

## The switch

F4 has one button for both characters: **Subtitles: by phrase — show whole lines** /
**Subtitles: whole lines — reveal by phrase**.

- The default is by phrase.
- It applies at once and changes only the display.
- It is remembered in PlayerPrefs (`Trola.subtitleReveal`: `phrases` or `whole`; a stored `sentences` from before
  reads as by phrase).
- `-xybridSubtitles phrases|whole` sets it for one run (`sentences` also means by phrase).

The F4 line list shows where each phrase was detected in the line's clip, for example `Phrases at 0.00, 0.62, 1.40 s`.

## Limits

- A line shows whole when nothing times it. That is a line with no audio, or a single clip without onsets: a Kitten
  or Kokoro line spoken as one clause, or a streamed line. It is also clause playback whose clauses can't be placed
  in the line.
- In clause playback a phrase appears when its clause starts. A phrase that starts inside a clause (after `…` or a
  spaced hyphen, which `SplitClauses` doesn't split at) appears with that clause, a little early.
- A comma the voice runs through has no pause to find. Its phrase is placed by its letters and the nearest dip, which
  is less exact than a matched pause (see Accuracy).
- The words of the displayed and spoken text must be the same, which they are for both speech styles. If the word
  counts differ, phrase starts are mapped by position, which is less precise.
- A wrong pause is still possible on lines unlike any measured: odd symbols, code, or long runs of digits. A
  mistimed phrase appears early or late at a pause; it is never dropped.
- Accuracy was measured on Phonon 31m voices only (Trola's clone on zzz, Vesper's stock voice on ptts).
