# Entering the camp

Once Trola admits the player, by passage or by friendship, the gate opens and the conversation goes on. The
encounter now finishes when the player chooses to go in. They can say "Go in" or "Enter camp", or click the small
**Enter camp** button. Trola then gives a short send-off, the camera walks around him and through the gate, the
picture fades to black, and an end card offers **Try again**.

This is milestone 3 of the encounter UX roadmap. Refusals are unchanged: an ordinary refusal still ends calmly with
Try again, and nothing in this milestone is violent. Every ending now carries a reason, so milestone 4 can tell a
provoked expulsion from a calm refusal.

## What the player sees and hears

| When | What happens |
| --- | --- |
| Admission (passage or friendship) | Trola's ending lines, then the gate opens. Friendship used to end here; now both admissions continue. |
| Gate fully open | Trola's greeting says how to finish. Passage: "Gate's open. Go in when you're ready, or talk, if you must." Friendship: "Go in when you like. Or stay and talk, I've had worse company." The reply field and the **Enter camp** button (bottom right) appear. |
| Free talk | Typed or spoken (hold Left Option) remarks work as before, through d1's `/decide` and the authored replies. The voice hint reads "say "go in" to enter the camp". |
| Entry requested | The reply field and the button go at once, and nothing more is answered (no typing, no queued reply, no idle taunts, no interruption). If Trola is mid-line, he finishes it first. |
| Send-off | Passage: "In you go, then. Mind the stew." Friendship: "Go on in, then. Keep my seat by the fire warm." It is never interrupted. The encounter ends with the reason **Entered**. |
| Walk in (3.8 s) | 0.2 s later the camera starts exactly where the close-up is, so there is no cut. It eases into a walk past Trola's right shoulder (screen left), with the gap set by his body's width plus 0.8 m. It then runs straight through the middle of the open gate, and the gaze turns from his face to the camp. The dialogue fades out over 0.4 s, the footsteps match the road approach's, and the camp's positional sound swells. |
| Through the gate | The last 1.1 s fade to black as the walk crosses the gate. The camp sound follows the fade. |
| End card | After 0.35 s of black: "Inside the camp", a line for the outcome ("You paid Trola's toll and walked through his gate." / "Trola laughed you through his gate. His fire is waiting."), and **Try again**. |
| Try again | Replays the road approach (or, with `-xybridNoOpening`, restarts at the gate). The doors are shut again, and the camera, the dialogue and the sound are restored. |

The walk covers about 9.5 m in 3.8 s, about 2.9 m/s at full pace. That keeps it within the roadmap's 3–4 s target,
but it is a brisk walk. If it feels hurried, raise `entryTiming.walkSeconds` to 5–6 s for a normal pace (see Tuning).

## What counts as entering

Entry has to be explicit, and only after admission with both doors fully open:

- **The button.** `GatekeeperController.RequestEntry()` validates admission (Passed or Liked), the open gate, live
  free talk, patience above zero and that no opening is settling.
- **Clear words.** `GatekeeperEntry.Clear` accepts a plain statement or command to go in that stands on its own:
  "Go in.", "Enter camp.", "Right, I'll head inside.", "Okay, in I go!", "I'll go in then, goodbye." Courtesies
  around it ("thanks", "okay", "Trola", "then", "now") are allowed.
- **d1.** `/decide` asks one more yes/no question, `enter_camp` (`tools/npc-quality/decision-entry.json`). At
  P(yes) >= 0.8 (`GatekeeperDecisionClient.EntryBar`), in world, it covers wording the keywords miss, such as "Let me
  in." (0.89) or "Alright, enough chat. Let's go in." (0.87). `GatekeeperEntry.MayEnter` still vetoes questions,
  hedges, refusals and anything over 80 characters.

Everything else stays conversation and gets its normal reply:

- **Questions:** "Can I go in?", "Who's in there?", "Is it safe in there?", "Mind if I go through?". After admission
  the admission answer is now "I already let you through. Go in when you're ready."
- **Ambiguous:** "So…", "Let me in."
- **Hedges and refusals:** "Maybe I'll go in later.", "I don't want to go in yet.", "Wait, I'll go in."
- **Goodbyes:** "Goodbye.", "See you inside.". After admission a goodbye gets "Leaving? The gate's open behind me, go
  in when you're ready." and the conversation stays. Before this change, a goodbye in free talk ended the encounter.
  It still does only without admission, in the F4 debug free talk after a refusal, where it now ends as Departed.

The optional Gemma writer (`-xybridWriter gemma`) may mark a reply as ending the scene. After admission that mark is
ignored, so only entering finishes.

## Ending reasons

`GatekeeperEncounter.Ending` (`GatekeeperController.Ending`) sits beside `Outcome`:

| Ending | When |
| --- | --- |
| `None` | Still going, including free talk after admission. |
| `Entered` | The admitted player went in: the send-off has been said. Only `EnterCamp()` sets it, and only with admission. |
| `Refused` | A calm refusal at the reckoning: a toll or a joke that fell flat (`passage` or `friendship` below their patience). |
| `Expelled` | Thrown out: "Demand the gate", patience spent on provocation at a checkpoint (off-topic or out-of-world answers), or an expulsion in free talk. Trola strikes ([docs/defeat.md](defeat.md)). |
| `Departed` | The player left of their own accord: a goodbye in non-admitted free talk, or a withdrawal in the old intent demo. |

## Ownership

- `GatekeeperController` owns the entry action and its validation: `CanEnter`, `RequestEntry()`, `EntryRequested`,
  `IsEntering` and `Ending`. `GateOpen` is set by `NotifyGateOpened`, which `GatekeeperBarrier` now calls every
  frame the doors stand fully open. A request made during a line is taken at the next wait. Restart, Suspend,
  `UseLineSource` and a halt clear it.
- `FreeFormGatekeeperLineSource` reads the words, gives the outcome's greeting and send-off, and ends Entered.
  `GatekeeperPresentation.CanEnter` and `Entering` tell the controller and the view. The input kind
  `GatekeeperInputKind.Enter` is the button, or a request taken after his line.
- `AuthoredGatekeeperLineSource` now waits for the gate on both admissions (`AwaitGate`), so friendship also goes
  on into free talk.
- `ExperienceDirector` owns the walk in and the end card. It now installs even with `-xybridNoOpening`, which skips
  only the approach. When the encounter ends Entered (`OwnsEnding`), it builds a `CampEntry` route, renews the
  camera pose every frame (`TrolaAtmosphere` stays the single camera writer), fades the dialogue canvas and then
  the picture, and shows the card. `GatekeeperView` hides its own Try again while the director owns the ending.
  If the director has failed, the view's Try again shows as before, without the walk.
- `CampEntry` is the pure timeline and path: a Catmull-Rom curve through the start, beside Trola, before the gate
  and through it, walked by arc length so the pace stays even. Stages are Walking, Black and Card.
- `GatekeeperView` builds the **Enter camp** button (a copy of Send, like Hints and Skip opening) and hides the
  reply field once entry starts.

## Tuning

All on the runtime `ExperienceDirector` (Inspector in Play Mode). The route is rebuilt each time from the camera,
Trola's renderers and the gate's hinges.

| Setting | Default | Meaning |
| --- | --- | --- |
| `entryTiming.walkSeconds`, `accelerateSeconds` | 3.8, 0.9 s | The walk and its eased start. It keeps full pace to the end; the fade covers the arrival. |
| `entryTiming.fadeSeconds`, `cardDelay` | 1.1, 0.35 s | Fade to black over the end of the walk; black before the card. |
| `entryTiming.lookTurn` | 0.3 | Share of the path over which the gaze turns from Trola to the camp. |
| `entryTiming.strideMeters`, `bobMeters`, `swayMeters`, `rollDegrees` | 0.8, 0.016, 0.008, 0.2 | Walking motion, as on the road. |
| `passSide`, `passClearance` | 1 (his right), 0.8 m | Which side of Trola, and the gap beyond his body. |
| `entryEyeHeight`, `beforeGate`, `throughGate` | 1.45, 1.6, 1 m | Eye height; the straight run before and after the gate's centre. |
| `campLookDistance`, `campLookHeight` | 7, 1.3 m | Where the gaze settles inside. |
| `entryFieldOfView` | 0 (close-up lens) | A wider lens for the walk in. |
| `entryDelay`, `interfaceFadeSeconds` | 0.2, 0.4 s | Beat after the send-off; dialogue fade. |
| `cardTitle`, `cardPassed`, `cardLiked` | see above | End card text. |

F4 shows "Entering the camp: stage, seconds, metres at m/s" during the walk.

## d1

`serve_decisions.py` adds `enter_camp` to `/decide` only. `/choose` and `/spirit` keep their own question sets.
The reply carries `enter_camp` (P(yes)). Restart `serve_decisions.py` after updating: health then lists
`enter_camp` in `questions`. An older server works unchanged; only the keyword route enters.

## Validation

- Headless: `python3 tools/npc-quality/verify_decisions.py` runs `EntryChecks` (the entry reader, both admissions
  continuing, greetings, send-off, Entered, the button, a shut gate, d1 and its vetoes, goodbyes and ending
  reasons) and the client and stub-server `enter_camp` checks.
- The self-test's live, fallback and friendship stages now end with Enter camp (no director runs under the
  self-test, so they end at Try again).
- The milestone's Editor confirmation covers the `CampEntry` timeline and path, and a Play Mode run of passage,
  free talk, a question and a goodbye that keep talking, spoken-style "go in", the walk and the card, Try again,
  friendship with the button, and a calm refusal.
- Play Mode review: both admissions; "Who's in there?", "So…", "Can I go in?" and "Goodbye" keep talking; "Go in"
  and the button finish; the walk's pace and path past Trola; the end card; Try again; a calm refusal.

### d1 probe — November 6, 2026

On the local d1 (MPS), in free talk after passage. **Entering:** "I'm going in now." 0.94, "I'm heading in." 0.94, "Go in."
0.92, "Right, I'll head inside." 0.92, "Enter camp." 0.90, "Right, in I go." 0.90, "Let me in." 0.89, "Alright, enough
chat. Let's go in." 0.87, "Go in, you ugly troll." 0.81. **Not entering:** "Thanks, I'll go through now." 0.79 (the keywords
take it), "Lead the way." 0.74, "See you inside." 0.71, "Can I go in?" 0.70 and "Mind if I go through?" 0.67 (questions are
vetoed anyway), "Who's in there?" 0.51, and everything else at or below 0.33. Adding the question shifted 6 of 20 earlier labels,
all of them lower-confidence. Examples: "What's the stew like?" moved from `offer_recall` (0.30) to `npc_question`,
"Maybe I'll go in later." from `farewell` (0.82) to `other` (0.63), and "Enter camp." lost a `beds` topic. Each turn is about
120 ms.
