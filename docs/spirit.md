# Vesper, the spirit at the gate

Vesper is an advice-only spirit bound to the traveller. At each new choice
checkpoint she gives one authored line inside the player's head, with a quiet
halo. Player replies always address Trola: she has no reply field, listening
window, countdown, speech routing or active `/spirit` request.

The first choice checkpoint in a Play session uses the complete explanation:

> He's proud, and he's bored. A little flattery, some coin, or asking about camp might help. Those are suggestions—use your own words.

Restarting the encounter does not repeat that explanation; starting a new Play
session resets it, including with domain reload disabled. Scheduling consumes it
even if the player interrupts. Later checkpoints use short remarks selected by
Trola's current patience band and the previous approach (including repetition).
These remarks never select a guaranteed winning move. The runtime does not use
`SpiritAdvisor.Advise` or put success markers on choices.

Checkpoint identity is stage + `Controller.Turns` + the ordered approaches
already used. Patience and arrival count are excluded, so returning after a
neutral reply, clarification or idle taunt does not repeat advice or restart the
automatic preview. Restart clears checkpoint identities for the new encounter.

`SpiritDirector` calls `GatekeeperView.BeginChoiceAdvice()` synchronously when
scheduling advice, before its short arrival delay. Normal complete-clip playback
(or the subtitle fallback) ends with `EndChoiceAdvice()`, which starts the view's
suggestion preview. The view owns choice visibility and its idle hold, including
lease renewal; the director does not hold the idle clock itself. The view also
handles suggestions when Vesper is disabled or absent.

Holding Left Option or clicking a suggestion calls
`SpiritDirector.InterruptAdvice()` before recording or selecting. Her voice fades
out in .12 seconds, the halo in `timing.snap_out` (.2 seconds), pending speech is
invalidated, and `InterruptChoicePresentation()` hides/cancels the preview. The
preview never starts from an interrupted advice line. Hints remain the view's
responsibility (Tab / Hints); they never summon a second conversation.

F7 turns her off: no halo, voice or input hold. The
[camp soundscape](#camp-soundscape) is separate and F8 mutes it. Launching with
`-xybridNoSpirit -xybridNoAmbience` disables both optional layers.

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
brisk, which suits the short in-head advice lines. Pick the final
voice by ear: stage several and swap them in Play Mode (below). The character
voices (Fairy Godmother, Morwenna, The Snow Queen) were made with Gradium's
voice design; every voice has samples on the
[voice catalogue](https://huggingface.co/gradium/phonon-voices).

She runs on the [xn-ptts bridge](phonon-integration.md) (ptts 0.6.0,
`liborc_phonon`) with the same Phonon 31m checkpoint as Trola, as a **second
`GatekeeperLiveSpeech`** on its own GameObject with its own handle and worker.
Her lines follow Trola's speech-text style (as written by default; F4 can switch
both to flowing, see [live speech debugging](live-speech-debug.md)).
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

Each of her lines, including the full explanation, plays as one trimmed clip on
a 2D source at an initial `voice.gain` of **0.6**: high-pass 180 Hz, low-pass 7.5 kHz,
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

In Play Mode, F4 lists every staged voice: click one to switch and hear a short mood
remark in it, or press F9 to cycle. The line in progress stops, audio prepared in
the old voice is dropped, and her engine reopens on the new file at once. A
voice preview only plays between advice lines while Trola waits, so it never cuts
either of them. The choice is remembered between sessions (PlayerPrefs
`Vesper.voice`) for as long as that voice stays staged. Nothing staged belongs in Git. `local_assets.py` checks the
required dependencies only: the spirit voice is optional and not in
`local-assets.json`, so the script's own size and SHA-256 check covers it.

## Controls

| Key | Action |
| --- | --- |
| F3 / F4 / F5 | Existing debugging; F4 includes Spirit status and staged voice selection |
| F6 | Read-only advice lens and run receipt (also `-xybridLens`) |
| F7 | Spirit on/off; the camp ambience stays |
| F8 | Mute ambience and music |
| F9 | Next staged voice and a short voice preview when Trola is waiting |
| Left Option | Interrupt advice / suggestions and record an answer to Trola |
| Tab / Hints | Recall clickable suggestions through GatekeeperView |

`-xybridNoSpirit` and `-xybridNoAmbience` skip installation; the self-test and
speech-test flags skip both. Enter and Esc have no Vesper-specific input routing.

## Camp soundscape

`CampSoundscape` installs on its own, so it plays with Vesper off: hearth,
torches, campfire, wind, crickets, camp life (clanks, hammering, thuds), orc
laughter and the gate creak, plus tension music, a drone and a war drum whose
tempo rises as Trola's patience falls. Her advice muffles the world in proportion to `visual.effects_strength` and
stops new drum hits while the halo is present. Apart from the laughter, every layer is
rendered in code at startup. It only reads the gatekeeper and never touches
Trola's AudioSource, `AudioListener.pause` or mixers. F8 mutes it;
`-xybridNoAmbience` skips it.

Laughter prefers clips named `laugh_*` in
`Assets/XybridAvatarDemo/Resources/CampAmbience/`; without them it pitches
down Trola's local `heh` reaction, and with neither the camp does not laugh.
That folder is git-ignored: supply your own clips under their own licence and
keep their provenance with them.

## Fallbacks

- **Missing voice, checkpoint or provider error:** advice appears as subtitles;
  the full explanation stays readable for up to 12 seconds, short remarks for
  2–4.5 seconds. F4 names the missing voice and setup command.
- **Voice not ready within `timing.voice_wait` (2 seconds), or Trola's worker
  busy:** that line uses subtitles. Advice completion still starts the preview.
- **Stopped voice or failed playback coroutine:** the director releases advice
  presentation; a 30-second watchdog covers a stuck line without clipping normal
  complete-clip playback.
- **Repeated spirit errors or invalid catalogue:** she switches off or stays
  uninstalled, leaving Trola and the view's independent suggestions available.

## Tuning `SpiritLines.json`

`Assets/XybridAvatarDemo/Resources/Spirit/SpiritLines.json` contains authored
text and tuning. Two initial milestone 1 settings are exposed both in JSON and
on the runtime components for Play Mode adjustment:

- `voice.gain = 0.6` / `SpiritVoice.gain`: voice source gain, replacing 0.9.
- `visual.effects_strength = 0.5` / `SpiritPresence.effectsStrength`: multiplies
  halo and post-effect weights, world/ambience signal, subtitle dimming and
  particle slowdown. Vesper never writes choice alpha or camera focal length.

Both accept 0–1. Subtitles keep normal readability at reduced effect strength.
`voice.threads` defaults to six and only applies without Trola's live speech;
otherwise Vesper inherits his Phonon thread count. Native engines, checkpoint,
SDK version and complete-clip playback are unchanged.

Active line families are `adviser.intro` and `adviser.remark`. Runtime coverage
validation requires onboarding at every patience level and remarks for `none`,
`flatter`, `bribe`, `camp`, `threaten` and `repeat` at patience 0–5. Each short
remark retains the existing two-sentence, 16-word, 100-character limit. Only
`adviser.intro` allows three sentences, 40 words and 240 characters; each
sentence must survive the speech sanitizer unchanged. The full authored line
is sent directly to synthesis so its third sentence is never truncated.

Legacy `SpiritPolicy`, `SpiritWindow`, `/spirit` contracts, decision client and
old answer families remain available to optional diagnostic tools. They are not
wired to the encounter, and legacy listening/reply tuning has no runtime effect.
The old policy fixture tuning (four fallback threads and reply-window behavior)
is historical and does not describe milestone 1. No d1 server is needed for
Vesper's authored advice. Trola's speech interpretation remains on `/choose`.

## Limits and validation

Apple Silicon macOS is required for the local native voice bridges; elsewhere
Vesper uses the subtitle fallback. The halo remains a vignette, depth of field
and generated edge glow. Static checks cannot judge the quieter mix by ear.

Milestone 1 still needs the owner's requested Play review: interrupt during the
full explanation and during preview, return from neutral/clarification/idle,
restart after onboarding, disable Vesper, and confirm hints plus Trola's speech
routing continue. No build, measurement suite or Editor call is required for
this scoped code change.

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
confirmation of the milestone 1 advice/presentation flow and quieter mix is
still to be run by the owner.
