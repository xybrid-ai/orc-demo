# The road approach

Each encounter now begins on the road, about 12 m short of the gate. The camera walks up to Trola, Vesper gives one
short warning on the way, Trola challenges the traveller as they arrive, and a 1.2-second move settles into the
close-up before the first answer. Restarts replay it; **Space** or **Skip opening** goes straight to his challenge.

This is milestone 2 of the encounter UX roadmap. Dialogue is unchanged: the same authored lines, d1 interpretation and
game rules run once Trola speaks.

## What the player sees and hears

| When (defaults) | What happens |
| --- | --- |
| Play / Try again | Fade from black (1 s) onto the camp ahead, 12 m down the road. Speech is prepared; nothing moves yet. |
| after 1.2–6 s | The walk starts once Trola's opening line and Vesper's warning are voiced (at least 1.2 s, at most 6 s). |
| walk + 0.8 s | Vesper's warning, with a light halo that leaves the road in focus. |
| 3 m before the halt | Trola: "Stop there, this gate has teeth…" once Vesper has finished (and a short breath); never later than the halt. The walker slows to a halt as he speaks. |
| walk + 9.6 s | A 1.2-second ease into the existing close-up; answers go live once it completes. |

With speech already prepared (any replay) the opening lasts about 12 seconds. The walk is a gently eased first-person
path at about 1.45 m/s on the close-up's 60 mm lens, with a 1.6 cm head bob, 0.8 cm sway and 0.2° roll per stride
(the long lens magnifies roll), all fading as the walker slows. Procedural footfalls play once per 0.8 m stride,
a little quieter under Vesper's voice. The camp loops are positional, so the hearth, torches and campfire swell as
the gate approaches; the tension drone and drum wait for Trola's challenge, and the whole camp follows the fades.
A roadside hearth now shows where the warm light on Trola's face comes from.

No conversation input is accepted during the opening: there is no prompt, Left Option reports "Nothing to answer
yet", and the choice and Hints controls do not appear. Trola's challenge plays as any line does (phrase-by-phrase
subtitle), but while the camera is still settling (`GatekeeperController.IsSettling`) the Interrupt button, typing,
choices and idle taunts wait; the name prompt goes live in the close-up.

**Vesper's warning.** The first approach of a Play session uses `approach.first`; replays use the shorter
`approach.again` (`Resources/Spirit/SpiritLines.json`). Her name tag appears with her first line of the session.
Trola's challenge waits for her to finish; if her line is still going when the walk halts, she fades out before he
speaks. Without her (F7, `-xybridNoSpirit`, missing voice files) the approach runs unchanged without the warning, or
with her subtitle only.

## Skip, replay and restart

- **Space** or the **Skip opening (Space)** button, available until Trola's challenge starts: stops the footsteps and
  Vesper, dips to black for 0.35 s and lands in the close-up as Trola starts his challenge. After the challenge Space
  does nothing (the settle is never cut while he speaks), and the opening deselects UI buttons so a stray Enter or
  Submit cannot interrupt him.
- **Try again** after an ending (or after an error) replays the approach. The previous encounter's speech, d1 and
  Whisper results are invalidated as before (the controller raises `Restarted` when the approach begins), the gate
  snaps to the new encounter's state, and the camera returns to the road.
- The F4 panel's restart button, F5 jumps and suspension still start Trola immediately; an approach in progress then
  stands down at the close-up.

## Speech before movement, and failures

`GatekeeperController.PrepareOpening()` resets the encounter exactly as `Restart()` does and starts synthesizing the
opening line (read from a throwaway authored source, so the text always matches what will be said). The encounter
then waits with no prompt until `StartOpening()` speaks that line, consuming the prepared voice once and only for the
same text. Vesper's warning is prefetched as soon as Trola's worker is idle.

The walk waits for both, for at most `maximumHold` (6 s). If they are not ready by then the walk starts anyway:
Trola's line waits for its voice as any line does (its first phrase on screen) and falls back to a subtitle; Vesper's
line falls back to a subtitle after her 2-second voice wait. Neither can hold the opening: every stage is on the
approach's own clock. Should the director itself fail, it logs once, returns the camera home and starts Trola
directly for the rest of the session.

## Ownership

- `ExperienceDirector` (runtime-installed, like Vesper) owns the approach: when the encounter starts, the cues, the
  fade, the skip button and the footsteps. It turns off the controller's auto-start. It also owns the walk into the
  camp and its end card ([docs/entry.md](entry.md)) and the provoked defeat ([docs/defeat.md](defeat.md)); with `-xybridNoOpening` it still installs for that, without
  the approach.
- `RoadApproach` is the pure timeline and camera path (stages, eased progress, walking motion, cues, skip).
- `TrolaAtmosphere` stays the camera's single writer. `SetCinematicPose(position, rotation, fieldOfView, weight)`
  blends the director's pose with the drifting close-up; shake still applies on top, and the field of view is written
  only while a pose blends (and once more to restore it).
- `GatekeeperController` owns the encounter: `IsOpening`, `PrepareOpening`, `StartOpening`, `OpeningVoiceSettled`,
  and `IsSettling` / `FinishOpening` for the settle. `Restart`, `UseLineSource`, `Suspend` and a halt all end an
  opening. A pose that is not renewed every frame lapses in `TrolaAtmosphere`, so a stopped director can never leave
  the camera on the road.
- `SpiritDirector` owns Vesper's warning (`PrepareApproach`, `ApproachReady`, `SayApproach`, `EndApproach`); her voice
  may be heard while Trola waits, has ended, or has not started yet.
- `XybridVillageGateScene.BuildRoadApproach` (**Xybrid ▸ Avatar Demo ▸ Build Road Approach**) dresses only the
  foreground the walk exposes: six more planks continuing the path towards the road and roadside grass and rocks,
  from the same Goblin War Camp prefabs as the rest of the set. It rebuilds only its own "Road approach" group, and
  nothing in it can enter the close-up.

## Tuning

All on the runtime `ExperienceDirector` (Inspector in Play Mode) and its `timing` block:

| Setting | Default | Meaning |
| --- | --- | --- |
| `minimumHold` / `maximumHold` | 1.2 / 6 s | Distant view before walking, waiting for speech. |
| `walkSeconds`, `accelerateSeconds`, `decelerateSeconds` | 9.6, 1.4, 2.4 s | The walk and its eased start and stop. |
| `warningAt` | 0.8 s | Vesper's warning, into the walk. |
| `challengeMeters` | 3 m | Trola's challenge, before the halt (after Vesper finishes). |
| `settleSeconds` | 1.2 s | Move into the close-up. |
| `strideMeters`, `bobMeters`, `swayMeters`, `rollDegrees` | 0.8, 0.016, 0.008, 0.2 | Walking motion at full pace. |
| `startBack`, `startRise`, `startSide` | 12.25, 0.21, −0.3 m | Start, from the close-up home along the road. |
| `arrivalBack`, `arrivalRise` | 0.95, 0.13 m | Where the walk halts. |
| `startLookRise` | 0.5 m | The walk first looks above Trola's eyes, towards the camp. |
| `walkFieldOfView` | 0 (close-up lens) | A wider lens shows the ends of the palisade. |
| `fadeInSeconds`, `skipFadeSeconds` | 1, 0.35 s | Fades. |
| `footstepVolume` | 0.32 | Footfall level. |

`-xybridNoOpening` keeps the original immediate start (the walk into the camp still runs). The self-test and speech validation never run the approach.
F4 shows the opening's stage and whether each voice is ready.

## Validation

- `RoadApproach` timeline, the opening text, Vesper's approach lines and the footfall are covered by the milestone's
  Editor confirmation; `DecisionChecks` checks the opening text and `SpiritAudioChecks` the footfall headlessly.
- Play Mode review: first approach (voices ready, warning heard, challenge as the walk slows, settle before the name
  prompt), Space and the button, Try again after each ending, F7 off, and a missing voice.
