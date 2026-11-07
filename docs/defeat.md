# Provoked defeat

When the player pushes Trola too far, he strikes. This happens when they demand the gate, or when their provocations use up his patience. He winds up and swings at the viewer. The viewpoint jolts back and falls to the ground, Vesper reacts, and the screen fades to an end card with **Try again**. A calm refusal never turns violent.

This is milestone 4 of the encounter UX roadmap. It relies on the ending reasons added in milestone 3 ([docs/entry.md](entry.md)). Only `Expelled` strikes; `Refused`, `Departed` and `Entered` never do.

## What the player sees and hears

| When (defaults) | What happens |
| --- | --- |
| The provocation | **Demand the gate** at the reckoning, or an off-topic, out-of-world or insulting answer with no patience left, at a checkpoint or in free talk. Trola says "Nobody talks to me like that at my own gate. Out, before I use you to bar it!", and the encounter ends `Expelled`. That line now ends the expulsion; "Walk away, while it's still your choice." closes calm refusals only. |
| +0.25 s | He strikes. Wind-up (0.62 s): his right arm rises and cocks back, his torso turns that shoulder away and leans back, his brows drop and he snarls. The camera stays on the close-up. |
| +0.78 s | The swing (0.16 s) accelerates into the viewer, and the blow lands. At the same frame the camera jolts back and up with a shake, the picture dips to black and recovers to a dazed level, a low body thud and one heavy drum hit sound, and the camp falls quiet. The dialogue fades out. |
| +0.86 s after the blow | The viewpoint falls to the ground, landing about 0.55 m back with a smaller thud. It ends rolled 70° onto its side, about 0.3 m up, looking up at Trola. His eyes follow it down. |
| +0.5 s on the ground | Vesper reacts, the first time in a Play session with "Ow. He meant that one, stay down a moment, then we try again." and afterwards with "Down again. Maybe ask him nicely next time." |
| Once she has finished (≥ 2.2 s on the ground, ≤ 8 s) | A 1.3 s fade to black, then the end card: "Thrown out", "You pushed Trola too far.", and **Try again**, which replays the road approach. |

He follows through and recovers to his idle pose over about 1.25 s after the blow, while the viewpoint is falling.

A threat that does not end the encounter, such as **Threaten** at the hub, follows the conversation rules as before: no strike. A calm refusal (a toll or a joke that falls flat at low patience) ends with "Walk away…" and the view's own Try again, at the gate.

## Ownership and coordination

- **`GatekeeperPerformer`** owns the strike: `Strike()`, `Striking`, `StrikeImpact` and the `strike` settings. Trola's body offsets were already its job.
  - **Arm bones:** it now also drives his right upper arm, forearm and hand.
  - **Why nothing overwrites the strike:** the strike is applied last in the performer's `LateUpdate`. That is after `XybridAvatarPlayer` has sampled the idle (execution order 0) and after the performer's own mood, breathing, gaze and roar offsets.
  - **No accumulation:** these bones go through the performer's existing bookkeeping (a bone the idle did not rewrite is restored before the next frame).
  - **Rig-independent aiming:** the arm is aimed bone by bone towards directions in Trola's own axes (cocked back by his head, then straight at the viewer, then on through), so the rig's bone axes do not matter.
  - **During the strike:** gaze darts stop and his face takes the fury (brows down, eyes wide, snarl).
  - **Impact event:** `StrikeImpact` fires the frame the blow lands, after the pose is written.
- **`TrolaStrike`** is the pure timeline of the strike: wind-up, swing, follow-through, recovery, and the arm weight, swing and follow amounts, twist and lean.
- **`ExperienceDirector`** owns the defeat:
  - **Start:** when the encounter ends `Expelled` (`OwnsEnding`), it calls `Strike()` after `strikeDelay`.
  - **The fall:** the frame `StrikeImpact` arrives, it takes the camera through a `Knockdown`.
  - **Effects:** it dips the fade, plays the thuds, calls `CampSoundscape.Strike()`, cues Vesper, and shows the card.
  - **Camera ownership:** `TrolaAtmosphere` is still the single camera writer. The fall's pose (and its depth-of-field focus on Trola's face) is renewed every frame and lapses if the director stops.
  - **Fallbacks:** if the impact event is lost, a timer stands in for it (strike time + 0.4 s). If the director has failed, the view's Try again shows as usual, without the fall.
- **`Knockdown`** is the pure timeline of the camera: waiting through the wind-up, recoil, fall, ground, black, card. It waits for Vesper (quiet), never longer than `maxGround`.
- **`SpiritDirector`** says her reaction (`PrepareReaction`, `SayReaction`, `IsReacting`, `EndReaction`). It is chosen and prefetched as he winds up and said from the ground. While it is active, no checkpoint logic can stop her voice. The lines are in `Resources/Spirit/SpiritLines.json` (`defeat.first`, `defeat.again`) and carry no name.
- **`CampSoundscape`** plays one heavy drum hit on `Strike()` instead of the three-hit refusal, and the camp goes quiet.
- **Story data:** the `expelled` node in `TrolaAuthoredConversation.json` ends on `refuse-out` alone (now `ends_scene`, mirrored in `TrolaGatekeeper.asset`). Free talk plays the same line before its own expulsion.

Restart, Try again, a debug restart or a jump clear everything: the performer drops the strike (`Restarted`), the director drops the fall, the black and the card, and Vesper drops her reaction.

## Tuning

On the runtime `GatekeeperPerformer` (`strike`) and `ExperienceDirector` (`defeatTiming` and the fields below), in Play Mode.

| Setting | Default | Meaning |
| --- | --- | --- |
| `strike.windupSeconds`, `swingSeconds`, `followSeconds`, `recoverSeconds` | 0.62, 0.16, 0.3, 0.95 s | The strike's phases; the blow lands at wind-up + swing. |
| `strike.windupTwist`, `impactTwist` | 24, −20° | Torso turn about his up axis (positive takes the striking shoulder back). |
| `strike.windupLean`, `impactLean` | −8, 16° | Lean towards the viewer. |
| `strike.followDrop` | 22° | How far the arm carries on down after the blow. |
| `strike.browDown`, `eyeWide`, `snarl` | 0.9, 0.7, 1 | Fury on his face at full strike. |
| `strikeDelay` | 0.25 s | Beat after his last line. |
| `defeatTiming.recoilSeconds`, `fallSeconds` | 0.14, 0.72 s | The jolt and the fall. |
| `defeatTiming.recoilBack`, `recoilRise`, `recoilPitch`, `recoilRoll` | 0.22 m, 0.05 m, 9°, 8° | The jolt. |
| `defeatTiming.groundHeight`, `groundBack`, `groundSide`, `groundRoll` | 0.3, 0.55, 0.25 m, 70° | Where the viewpoint lands. |
| `defeatTiming.shakeMeters`, `shakeDegrees`, `shakeSeconds`, `bounceMeters` | 0.035 m, 2.5°, 0.4 s, 0.035 m | Impact shake and landing bounce. |
| `defeatTiming.reactAfter`, `groundSeconds`, `maxGround` | 0.5, 2.2, 8 s | Vesper's cue and the hold on the ground. |
| `defeatTiming.fadeSeconds`, `cardDelay` | 1.3, 0.4 s | The fade to black and the card. |
| `impactDip`, `dazed`, `impactRecover` | 0.55, 0.18, 0.7 s | The picture's dip at the blow and the daze it settles to. |
| `impactVolume` | 1 | The body thud (the landing plays at half). |
| `cardDefeatTitle`, `cardDefeat` | see above | End card text. |

F4 shows "Knocked down: stage, seconds" during the fall.

## Validation

- **Headless:** `python3 tools/npc-quality/verify_decisions.py` checks that the expulsion ends on his threat and calm refusals walk away. `verify.py` keeps the asset and the JSON in lockstep, and `verify_spirit.py` validates Vesper's lines.
- **Editor confirmation (this milestone):**
  - **Edit Mode:** the `TrolaStrike` and `Knockdown` timelines, Vesper's defeat lines, and the story's endings.
  - **Play Mode:** Threaten without a strike, then Demand the gate (wind-up, impact, fall, ground, Vesper, card, Try again); a calm refusal; and an expulsion in free talk.
- **Play Mode review:**
  - the look of the strike on his rig and whether his fist reads as reaching you;
  - the recoil and fall, and the dazed ground view;
  - the sound of the blow;
  - Vesper's lines;
  - a calm refusal staying calm.
