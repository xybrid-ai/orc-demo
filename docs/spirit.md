# Vesper, the spirit at the gate

Vesper is a spirit bound to the traveller. She speaks only inside the player's
head, at Trola's checkpoints: a halo closes in, the camp recedes and she gives
one hedged line of advice, or a short counsel the player can answer by
thinking (typing) to her. The local d1 model classifies each thought on a
separate `/spirit` route; the story graph decides what is true; authored lines
decide what she says. **No spirit line is written by an LLM**, and Gemma and
Trola's d1 never see her conversation.

She never acts and never holds Trola's line pipeline. She is audible only while
Trola waits for the player (or has ended). While she speaks or d1 is thinking,
Trola's idle clock is frozen; while the player reads or types, it runs, so a
long mind-conversation costs visible impatience and ends in his idle taunt.
F7 turns her off: no halo, voice, holds or flinch, and Trola plays as he did
before her (her voice engine stays loaded, idle). The
[camp soundscape](#camp-soundscape) is separate and F8 mutes it, so F7 plus
F8, or launching with `-xybridNoSpirit -xybridNoAmbience`, plays like the game
before Vesper.

## Voice

Vesper uses a stock voice from
[`gradium/phonon-voices`](https://huggingface.co/gradium/phonon-voices)
(**Apache-2.0**), pinned at `da6c9ec0d47be8df5c87c730a3aa97187f3229c3`, the
revision zzz rc.9's voice catalogue pins. Every file is 256,136 bytes.

| `--voice` | Voice | File | SHA-256 |
| --- | --- | --- | --- |
| `bryony` (default) | Bryony, en-gb | `voices/gfP44RlHwr2kEwk7.safetensors` | `bcc207a7…` |
| `delia` | Delia, en-us | `voices/nGzMHMPAWGaJFHOo.safetensors` | `a0c3dcb9…` |
| `fairy_godmother` | Fairy Godmother | `voices/QXNr0SvkukLB4wPq.safetensors` | `f390c09f…` |
| `morwenna` | Morwenna of the Deep | `voices/nOykTmcH9ydsGGIc.safetensors` | `2ff2db91…` |
| `snow_queen` | The Snow Queen | `voices/NIWoEurJHMJjyUTe.safetensors` | `bc8a8fcb…` |
| `hadley` | Hadley, en-us | `voices/bXwrkHhURL1VFJdG.safetensors` | `a879938a…` |

Full digests live in `tools/setup_spirit_voice.py`. Bryony is close-mic and
brisk, which suits an in-head voice and the short reply window. Pick the final
voice by ear: stage several and swap them in Play Mode (below). The character
voices (Fairy Godmother, Morwenna, The Snow Queen) were made with Gradium's
voice design; every voice has samples on the
[voice catalogue](https://huggingface.co/gradium/phonon-voices).

She runs on the [xn-ptts bridge](phonon-integration.md) (ptts 0.6.0,
`liborc_phonon`) with the same Phonon 31m checkpoint as Trola, as a **second
`GatekeeperLiveSpeech`** on its own GameObject with its own handle and worker.
Like Trola's, her lines reach the voice as flowing speech text (internal full
stops as commas; see [live speech debugging](live-speech-debug.md)), while her
subtitles keep the authored punctuation.
Trola stays on [zzz rc.9](phonon-zzz.md) (`liborc_phonon_zzz`) with
`trola_clone`, 6 threads and one trimmed clip per line, so by default
`liborc_phonon` is hers alone; no SDK, scene or bundle changes. xn sizes its
worker pool once per process, at first use, and the Editor keeps it across
Play sessions, so she opens at Trola's thread count (6): if he is switched to
XnPtts, both voices share that one library and its pool, each with its own
handle and worker. Her engine opens at the first wait after Trola's warm-up and
synthesizes only while he waits and his worker is idle. She skips lip sync
(her Rhubarb path does not exist on purpose, so no process starts) and saves
her WAVs to `persistentDataPath/SpiritSpeechDebug`.

Each of her lines plays as one trimmed clip on a 2D source: high-pass 180 Hz, low-pass 7.5 kHz,
a faint chorus and a small user reverb, drifting ±0.15 in the stereo field.
Trola's AudioSource, `AudioListener.pause` and mixers are never touched.

## Setup

Her voice needs the [xn-ptts bridge](phonon-integration.md#local-setup) and
the Phonon checkpoint that `tools/setup_phonon.py` stages
(`liborc_phonon.dylib` and `StreamingAssets/Phonon`); Trola's zzz setup
stages neither. Then:

```sh
python3 tools/setup_spirit_voice.py --voice all   # or e.g. --voice bryony,fairy_godmother
python3 tools/local_assets.py
```

The script uses only the standard library. It downloads each pinned file to
ignored `.context/phonon-voices/voices/`, checks size and SHA-256, and copies it
to `Assets/StreamingAssets/Phonon/spirit-voices/<key>.safetensors` with a
`<key>.json` provenance file (repo, revision, file, SHA, name, license). The
first voice listed is also her default, `Phonon/spirit.safetensors` with
`Phonon/spirit-provenance.json`. `--from PATH` stages one local copy (still
checked), `--dry-run` verifies and prints the plan without copying, and
`--project-root` targets another checkout.

In Play Mode, F4 lists every staged voice: click one to switch and hear her hub
line in it, or press F9 to cycle. The line in progress stops, audio prepared in
the old voice is dropped, and her engine reopens on the new file at once. A
preview only plays between her moments while Trola waits, so it never cuts
either of them. The choice is remembered between sessions (PlayerPrefs
`Vesper.voice`) for as long as that voice stays staged. Nothing staged belongs in Git. `local_assets.py` checks the
required dependencies only: the spirit voice is optional and not in
`local-assets.json`, so the script's own size and SHA-256 check covers it.

For d1 answers, restart `serve_decisions.py` so `/health` lists
`spirit_questions` ([decision layer](decision-layer.md)).

## Controls

| Key | Action |
| --- | --- |
| F3 / F4 / F5 | Unchanged; F4 adds a `Spirit:` line |
| F6 | Lens: labels, graph, timings and the run receipt (also `-xybridLens`) |
| F7 | Spirit on/off (off: no halo, voice, holds or flinch; the camp ambience stays) |
| F8 | Mute ambience and music; also lets an open moment go |
| F9 | Next staged voice for her, and a preview of it (F4 lists them all) |
| Tab | Summon a counsel at the hub or reckoning |
| Enter | Think the typed reply to her |
| Esc | Let her go (dismiss the moment); it never clears what you are typing to Trola |

`-xybridNoSpirit` and `-xybridNoAmbience` skip installation; the self-test and
speech-test flags skip both.

## Camp soundscape

`CampSoundscape` installs on its own, so it plays with Vesper off: hearth,
torches, campfire, wind, crickets, camp life (clanks, hammering, thuds), orc
laughter and the gate creak, plus tension music, a drone and a war drum whose
tempo rises as Trola's patience falls. Her moments muffle the world and stop
the drum; a flinch is one drum hit. Apart from the laughter, every layer is
rendered in code at startup. It only reads the gatekeeper and never touches
Trola's AudioSource, `AudioListener.pause` or mixers. F8 mutes it;
`-xybridNoAmbience` skips it.

Laughter prefers clips named `laugh_*` in
`Assets/XybridAvatarDemo/Resources/CampAmbience/`; without them it pitches
down Trola's local `heh` reaction, and with neither the camp does not laugh.
That folder is git-ignored: supply your own clips under their own licence and
keep their provenance with them.

## Answer rules

d1 (or, when it is away, the keyword patterns in `SpiritKeywords.Interpret`)
only labels a thought. `SpiritPolicy.Rule` then picks the family of her
answer; the first match wins, and the lens shows it as `rule N family.key`. A
plan counts only at confidence 0.6 or more, `{best}` is the option the story
graph favours, and a family with no fresh line falls back to `answer.repeat`.

| Rule | Thought | Family |
| --- | --- | --- |
| 1 | scope out_of_world, role_override or unclear | `answer.offworld`, `answer.override`, `answer.unclear.{best}` |
| 2 | addressed to Trola, nothing hostile | `answer.to_trola` |
| 4 | asks her to act (not reckless, not to Trola) | `answer.cannot_act` |
| 3 | reckless (unless agree or dismiss), or a threaten or demand_gate plan | that plan's `plan.*` line, or `plan.reckless`, with the alarm |
| 5 | any other plan | `plan.best`, `.good`, `.costly`, `.fatal`, `.hopeless` or `.unavailable` |
| 6 | agree or dismiss | `answer.agree`, `answer.dismiss` (both close the moment) |
| 7 | doubt | `answer.doubt.{best}` |
| 8 | ask_advice | `answer.advice.{best}` |
| 9 | ask_about_trola | `answer.almost` (almost through while he is wary), else `answer.read.{band}` |
| 10 | ask_about_spirit | `answer.self` |
| 11 | anything else | `answer.unclear.{best}` |

Rule 4 is checked before rule 3, so "can you open the gate for me" is answered
as a request to her unless d1 also calls it reckless.

## Fallbacks

- **No voice file, no checkpoint or a provider error:** her lines show as
  subtitles with a short breath, held for 1.2 s + 1 s per 15 characters (2 to
  4.5 s). F4 names the missing file and the setup command.
- **Voice not ready within 2 s, or Trola's worker busy:** that line is
  subtitle-only.
- **d1 off, an old server (404) or no `spirit_questions`:** a keyword
  interpreter answers; the lens labels the source `keywords`.
- **Any spirit error:** logged; after repeated errors she switches herself off
  for the session and Trola carries on.
- **`SpiritLines.json` missing or invalid:** she stays off for the session,
  with one Console warning and F4 reading `Spirit: off (<reason>)`; Trola is
  unaffected. See [tuning](#tuning-spiritlinesjson).

## Tuning `SpiritLines.json`

`Assets/XybridAvatarDemo/Resources/Spirit/SpiritLines.json` holds her
authored lines and the `timing`, `rules`, `voice` and `visual` blocks; a
missing block keeps the code defaults. Play validates it and stops at the
first problem, which keeps her off (above). `SpiritLines.Check` has the full
rules:

- **Lines:** at most 2 sentences, 16 words and 100 characters, checked with
  the names Mira, traveller and the 16-character Bartholomew-Rook, with and
  without the vocative; unchanged by `SanitizeSpeech`; only the `{name}` and
  `{assigned}` slots; `{name}` only as a leading or trailing vocative
  (`{name}, …` or `…, {name}.`) unless `always_name`; a unique `id`; a
  patience range within 0–5.
- **Coverage:** every family, key and patience that
  `SpiritPolicy.RequiredLines()` derives from the story graph needs a line,
  so a retune of `TrolaAuthoredConversation.json` can require new keyed
  `advice`, `recover`, `reckon`, `answer` or `plan` lines.
- **Blocks:** common traps are `long_draft` ≤ `reply_chars` ≤ 240,
  `max_replies` 0–4, `appearances_per_encounter` ≥ `counsels_per_encounter`,
  `distraction_patience_delta` 0 or −1, and `whisper_weight` and
  `slow_motion` within 0–1. `voice.threads` applies only when Trola has no
  live speech; otherwise she takes his count (see [Voice](#voice)).

After editing the file or retuning the story, build the
[harness](../tools/npc-quality/README.md) and run:

```sh
python3 tools/npc-quality/verify_spirit.py --dotnet <dotnet> [--lines draft.json]
```

It validates the file for both reckoning preferences. Read the first
`Real SpiritLines.json` failure; the fixture failures after it follow from
the same problem. The `Real JSON tuning` check pins the shipped tuning
(`listen_seconds` 6, `max_replies` 2, Bryony, `threads` 4, passage), so
update it with an intentional retune.

## Talking points

- "d1 decides what I meant; the story graph decides what's true; Gemma only
  writes Trola's words."
- "Watch my thought dissolve into a label: d1 reads it, the rules answer."
- "All on this laptop, no cloud calls. Two voice engines, one process."
- "Stop d1 and she falls back to keywords."

The label always waits for a 0.25 s dissolve, so the lens's `shown` time is at
least 250 ms; d1's own time is the `d1` figure beside it (`keywords` when the
fallback answered). d1's time on the spirit questions has not been measured
on the Mac, so point at the lens rather than quoting a number.

## Limits

- Both Phonon bridges in one process were checked outside Unity only (below),
  with Trola on zzz and her at 4 threads. xn's thread count is process-wide
  (`xn::set_num_threads` also sets `RAYON_NUM_THREADS`) and its pool is sized
  once, at first use, and kept across Play sessions in the Editor, so Vesper
  opens xn-ptts at Trola's thread count; no other rayon library loads after it
  today.
- The halo is a vignette, depth of field and a generated edge glow, not a real
  edge blur.
- d1 accuracy and Mac latency on the spirit questions are unmeasured; the 0.6
  plan threshold and the keyword fallback cover misreads. Her label is held at
  least 0.25 s; for scale, Trola's own d1 decisions took 218–851 ms (median
  257 ms) in Play Mode.
- Apple Silicon macOS only, like the bridges; elsewhere she is subtitles-only.

## Verification

On an M5 Max (macOS 27), outside the project, one .NET process opened the zzz
rc.9 bridge (`trola_clone`, 6 threads) and then the xn-ptts bridge (Bryony at
the pinned revision, 4 threads) through the real provider, and synthesized 21
lines: alternating with 0.3 s gaps, back to back, one concurrent pair (which
the game never does), and zzz again after closing xn. Every call completed
with non-silent 24 kHz audio and no crash.

| Observation | zzz rc.9, Trola | xn-ptts 0.6.0, Bryony |
| --- | ---: | ---: |
| Open (second engine opened after the first) | 0.032 s | 0.030 s |
| Synthesis, 2.2-4.8 s lines | 0.059-0.107 s | 0.070-0.098 s |
| First PCM callback after warm-up | 0.003-0.010 s | 0.005-0.016 s |
| "Stop there. This gate has teeth." before / after the spirit opened | 0.092 / 0.088 s | |

These are single smoke observations, not a benchmark. The one Editor
confirmation (Trola's F3 first sound with both engines resident, the halo,
labels, clock freeze and cut-off, F7 off with F8 muted) is still to be run.
