# Authored free talk — November 3, 2026

Glenn's blind reading selected authored replies on 15 of 19 messages, Gemma on
three, LFM2.5 on one, and LFM3 on none. These are first choices, not rejection
counts: several turns had two good answers. The page now also accepts
“also good” marks without changing the saved favorites.

The test ran the same game prompt and grammar on the Mac. The authored bank's
topics were selected by hand, so the result supports the wording approach but
does not measure d1's topic accuracy. Local raw choices, blind key and model
replies remain under `.context/gemma-compare/`.

The default now uses d1 to choose scope, intent, requested fact slots and a
closed topic in one packed pass. C# chooses the wording and every gameplay
effect. `GatekeeperAuthoredReplies.cs` holds the bank. Gemma is not instantiated
or preloaded during the intro or free talk. Its files remain local for the
explicit `-xybridWriter gemma` option and the Gemma3 trial.

The bank keeps the favored wall, stew, captain, day-off, loneliness, turnip,
beds and repeat lines. The three preferred Gemma answers were the terse wall
apology, the trees beyond the wall, and the fire invitation; the preferred
LFM2.5 answer was the wall guardian motive. These informed authored variants.
The fire answer no longer invents a three-turnip toll, and a refused or undecided
player never receives an invitation through the gate.

Facts still come from the encounter snapshot. A conversational name statement
does not overwrite the captured name. Repeat uses the last NPC line, never a
player's quoted or injected NPC line, and does not accumulate repeat prefixes.
All replies pass the existing speech sanitizer and preserve one complete clip
per line. Phonon, Vesper, Whisper, Kitten and SDK versions are unchanged.

When d1 is unavailable, explicit factual questions still use encounter state.
A narrow keyword fallback can answer neutral camp topics; other messages ask
for clarification. It does not infer patience changes. A request failure costs
one neutral clarification and keeps the existing reconnection backoff. An old
server without the topic question is detected through health; restart it after
updating.

Validation: the headless policy and real HTTP client checks cover topic mapping,
missing/unknown topics, authored wording without a model, follow-up history,
repeat limits, admission-safe wording, scope boundaries, unchanged effects,
cancellation, failure and full expulsion. The separate /spirit checks also pass.
One muted Unity 6000.6.4f1 Play Mode confirmation in an isolated Mac copy passed
23 checks. The actual d1/MPS server chose wall_motive, day_off, food (including
“Is it really that bad?”), repeat and player as expected. The next name question
used the authoritative encounter fact. No `GatekeeperNpcModel` component existed
during the scene intro or after those free-talk turns. These seven turns check
functional integration, not broader classifier accuracy or listening quality.
The Editor logged the existing Unity Search index exception and Rhubarb
loudness-jaw fallback during startup. No game compilation error was observed.
Local receipts are in `.context/authored-replies-check/` on both machines.
No new memory savings or microphone performance is claimed.
