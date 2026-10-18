# Live speech debugging

In `GoblinSpeakingDemo`, F5 jumps straight to free-form input, without synthesizing a welcome or starting idle taunts during this debug test. F4 shows or hides the runtime debug panel. Its editable handoff defaults to Mira, selling turnips, a bribe attempt, passage granted, and patience 3. Change the fields and press **Jump / apply summary** to start a fresh turn with those values.

The panel keeps each delivered line's source (authored, LLM, LLM fallback, placeholder), raw model output, parsed emotion/intensity, requested patience delta and resulting patience, model time and total voice synthesis time. It shows RTF as synthesis seconds divided by generated audio seconds; below 1 means faster than playback. The model timer includes any wait for preload and the serial model worker. The voice timer sums synthesis across clauses and excludes Rhubarb.

Every delivered voice line is freshly synthesized. Authored text and fallback text use the same live voice path. There is no waveform reuse, prerecorded reaction, or debug-file playback. Kitten's matched WarChief files, rc.4 bridge, Accelerate, and unlimited token setting are retained.

Text splits into clauses at commas, semicolons, colons, sentence punctuation and dashes. A complete nonstreaming PCM clip is generated for each clause. The first starts playing while the voice worker prepares subsequent clauses. If the next is still being generated, playback waits at the clause boundary. A clause never plays from a partially generated waveform; its AudioSource plays the whole clip before the next starts.

Each fully generated line is saved as an original 24 kHz mono PCM16 WAV with a text sidecar under `Application.persistentDataPath/TrolaSpeechDebug`. On this Mac that is `~/Library/Application Support/DefaultCompany/Orc Demo/TrolaSpeechDebug`. These are diagnostic output only. Rhubarb receives the clause text through `--dialogFile` and a separate 120 Hz high-pass, peak-normalized waveform with the same number of samples. Playback uses the original. Its cues attach to their matching clause; the loudness jaw covers pending or failed analysis, with a 120 Hz high-pass, an adaptive rumble floor, a raised relative noise threshold and smoothed attack/release.

The installed Xybrid 0.9.0 loader rejects Kitten's `ZzzEmbed` execution template. The direct Kitten rc.4 ABI is CPU-only, uses greedy token decoding, and exposes no stable/expressive preset or Metal selector. Performance comparisons must distinguish unsupported controls from measured results; no SDK/native upgrade is implied by this debug pass.

See `voice-performance.md` for the single-observation measurements and their limitations.

Rhubarb 1.14.0 for macOS is Intel-only. Apple Silicon requires Apple’s Rosetta 2 runtime. The initial lip analysis failed with exit 255 / Bad CPU type because Rosetta was absent; after installing Rosetta, the existing binary produced cues successfully and the live Editor confirmation passed. No Rhubarb/native rebuild or SDK upgrade was needed.

Free-form Send accepts one queued reply while the current live voice is being generated or played. The button shows **Queued**, and the reply is submitted only after the current line finishes; speech is never interrupted. A nonempty draft keeps idle taunts from starting while the player reads or thinks. The debug panel shows the conversation phase and whether Send is available, waiting or queued. Restarting, applying a new summary, or ending the encounter clears a queued reply.

The input distinguishes Thinking from Preparing voice and shows elapsed stage time. `TrolaSpeechDebug/session-state.txt` is updated during Play with the current phase, queued text, audio sample position/pause/loop state, model readiness, player summary, exchanges and raw model outputs. It is diagnostic output only. Ask **What is my name?** to test player-name recall; **What is your name?** asks for Trola.

A functional Editor check confirmed the actual Send raycast, Mira/turnip recall, and delivery of a second message queued during live speech. The second generated reply requested `ends_scene=true`, so the final state was Ended with an empty queue. Bribe recall failed in that reply (it said turnips). Returning to input after an ordinary Gemma reply was not confirmed in this check. The earlier placeholder queue check did return to input. Voice latency remains unresolved.
