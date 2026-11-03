// Headless checks for GatekeeperSubtitleTiming: the sentence and phrase splitters (every sentence end a phrase end, over
// every authored and spirit line and the local subtitle corpus when present), the onset aligner on synthetic Phonon-like
// clips (noise-burst words, trimmed pauses: a full stop ~420 ms, a comma ~280 ms, an interjection's comma as long as a
// full stop, a comma spoken through), sentence and phrase onsets from one alignment, flowing speech text, hostile input,
// the clause mapping, the reveal clock and the subtitle markup. No WAV files.
// Headless only: the guard keeps it (and its Color32 stub) out of a Unity compile of changed files.
#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityEngine { public struct Color32 { public byte r, g, b, a; public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; } } }

public static class SubtitleTimingChecks
{
    private const int Rate = 24000;
    private const string LinesFile = "Assets/XybridAvatarDemo/Resources/Spirit/SpiritLines.json", CasesFile = "tools/npc-quality/speech-text-cases.json";
    private static readonly string[] CorpusFiles = { ".context/subtitles/corpus/texts_flow.jsonl", ".context/subtitles/corpus/exp_texts.jsonl" };
    private static readonly Regex Word = new Regex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*");

    public static object Run(JsonNode request, GatekeeperScenario scenario, GatekeeperStory.Document story)
    {
        var failures = new List<string>();
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) failures.Add(what); }

        // ---- Sentences ----
        void Split(string text, params string[] expected)
        {
            var got = GatekeeperSubtitleTiming.Sentences(text).Select(s => text.Substring(s.start, s.length)).ToArray();
            Check(got.SequenceEqual(expected), "split '" + text + "': [" + string.Join(" | ", got) + "]");
        }
        Split("Stop there. This gate has teeth. Before you lose yours, give me your name.",
            "Stop there.", "This gate has teeth.", "Before you lose yours, give me your name.");
        Split("Mr. Rook and Dr. Venn wait. St. Agnes too.", "Mr. Rook and Dr. Venn wait.", "St. Agnes too.");
        Split("J. R. Tolk wrote it. I read it.", "J. R. Tolk wrote it.", "I read it.");
        Split("\"WIFI\"? Up since dawn. \"Get out!\" he said.", "\"WIFI\"?", "Up since dawn.", "\"Get out!\"", "he said.");
        Split("You what?! Say it again.", "You what?!", "Say it again.");
        Split("Wait… then go. Well... fine.", "Wait…", "then go.", "Well...", "fine.");
        Split("  Hmph.   Go.  \n", "Hmph.", "Go.");
        Split("Just one sentence, with a comma", "Just one sentence, with a comma");
        Split("Version 1.5 is out.", "Version 1.5 is out.");
        Split("", new string[0]);
        Split("   ", new string[0]);

        // ---- Phrases: the reveal unit, split at every pause mark followed by whitespace ----
        void Phrase(string text, params string[] expected)
        {
            var got = GatekeeperSubtitleTiming.Phrases(text).Select(s => text.Substring(s.start, s.length)).ToArray();
            Check(got.SequenceEqual(expected), "phrases '" + text + "': [" + string.Join(" | ", got) + "]");
        }
        Phrase("Stop there, this gate has teeth, so before you lose yours, give me your name.",
            "Stop there,", "this gate has teeth,", "so before you lose yours,", "give me your name.");
        Phrase("Hmph, fine. You may pass, but mind the gate.", "Hmph,", "fine.", "You may pass,", "but mind the gate.");
        Phrase("Listen: the gate opens at dawn; not before.", "Listen:", "the gate opens at dawn;", "not before.");
        Phrase("Wait — no. Then – nothing.", "Wait —", "no.", "Then –", "nothing.");
        Phrase("Wait—no, go.", "Wait—no,", "go.");
        Phrase("Go - now. Go -- now. A well-known spot - there.", "Go -", "now.", "Go --", "now.", "A well-known spot -", "there.");
        Phrase("Go -now, or go- later.", "Go -now,", "or go- later.");
        Phrase("That's 1,000 coins, by 3:30 sharp.", "That's 1,000 coins,", "by 3:30 sharp.");
        Phrase("Mr. Rook, Dr. Venn: J. R. Tolk wrote it.", "Mr. Rook,", "Dr. Venn:", "J. R. Tolk wrote it.");
        // An abbreviation is a whole word: "21st." ends a phrase (the voice and the aligner take it as a full stop).
        Phrase("We meet on the 21st. Then we go, Mr. Rook.", "We meet on the 21st.", "Then we go,", "Mr. Rook.");
        // A run of marks after an initial or abbreviation always ends a phrase, as it ends a sentence.
        Phrase("I... I can't. Plan B?! Go.", "I...", "I can't.", "Plan B?!", "Go.");
        Phrase("\"Go,\" he said. “Fine.”, she said, (or so).", "\"Go,\"", "he said.", "“Fine.”,", "she said,", "(or so).");
        Phrase("\"WIFI\"? Up since dawn, and now this.", "\"WIFI\"?", "Up since dawn,", "and now this.");
        Phrase("Wait… then go. Well... fine.", "Wait…", "then go.", "Well...", "fine.");
        Phrase("  Hmph,   go.  \n", "Hmph,", "go.");
        Phrase("Just one phrase", "Just one phrase");
        Phrase("Trailing comma, ", "Trailing comma,");
        Phrase("", new string[0]);
        Phrase("   ", new string[0]);
        Check(GatekeeperSubtitleTiming.Phrases(null).Count == 0, "phrases of null: none");

        // Every sentence end is a phrase end, so each sentence start is a phrase start and takes the sentence's onset.
        int supersetTexts = 0, corpusTexts = 0;
        void Superset(string where, string text)
        {
            supersetTexts++;
            var sentences = GatekeeperSubtitleTiming.Sentences(text);
            var phrases = GatekeeperSubtitleTiming.Phrases(text);
            var starts = new HashSet<int>(phrases.Select(p => p.start));
            var ends = new HashSet<int>(phrases.Select(p => p.start + p.length));
            Check(sentences.All(x => starts.Contains(x.start) && ends.Contains(x.start + x.length)) && phrases.Count >= sentences.Count,
                where + ": a sentence end is not a phrase end: '" + text + "'");
        }
        foreach (bool longSlots in new[] { false, true })
        {
            string name = longSlots ? "Bartholomew Rook" : "Mira", business = longSlots ? "delivering a cartload of pickled turnips to my old aunt Ysol" : "selling turnips";
            foreach (var line in story.lines)
            {
                string text = GatekeeperNames.Resolve(line.text, name, GatekeeperNames.Mangle(name), business);
                Superset(line.id, text); Superset(line.id + " (flowing)", GatekeeperSpeechText.Flow(text));
            }
        }
        foreach (var line in SpiritLines.Parse(File.ReadAllText(Find(LinesFile))).lines)
            foreach (string name in SpiritLines.TestNames)
                Superset(line.id, SpiritLines.Resolve(line.text, name, name == SpiritLines.Traveller ? "Turnip" : name, true));
        foreach (var row in JsonNode.Parse(File.ReadAllText(Find(CasesFile))).AsArray())
        {
            Superset("speech-text case", row["text"].GetValue<string>()); Superset("speech-text case (speech)", row["speech"].GetValue<string>());
        }
        // The local subtitle corpus (.context, not committed), when present: every line it measured.
        foreach (string file in CorpusFiles)
        {
            string path = TryFind(file);
            if (path == null) continue;
            foreach (string row in File.ReadAllLines(path).Where(l => l.Trim().Length > 0))
            {
                var o = JsonNode.Parse(row).AsObject();
                Superset(file, o["text"].GetValue<string>()); corpusTexts++;
                if (o["flowing"] != null) { Superset(file + " (flowing)", o["flowing"].GetValue<string>()); corpusTexts++; }
            }
        }

        // ---- Onsets and PhraseOnsets: contract and choice on synthetic clips ----
        // Each sentence start and each phrase start within 30 ms, and the phrase onsets at sentence starts are exactly the
        // sentence onsets (one alignment).
        void Aligned(string label, string displayed, string spoken, Clip clip, int rate, double toleranceMs = 30)
        {
            var spans = GatekeeperSubtitleTiming.Sentences(displayed);
            var onsets = GatekeeperSubtitleTiming.Onsets(displayed, spoken, clip.pcm, rate);
            Check(Contract(onsets, spans.Count - 1, clip.pcm.Length / 2), label + ": contract (" + Show(onsets) + ")");
            var truth = Truth(displayed, spans, clip);
            for (int k = 0; k < truth.Count && onsets.Length == truth.Count; k++)
            {
                double error = (onsets[k] - truth[k]) * 1000.0 / rate;
                Check(Math.Abs(error) <= toleranceMs, label + ": sentence " + (k + 2) + " starts at " + Ms(truth[k], rate) + " ms, got " + Ms(onsets[k], rate) + " ms");
            }
            var phrases = GatekeeperSubtitleTiming.Phrases(displayed);
            var phraseOnsets = GatekeeperSubtitleTiming.PhraseOnsets(displayed, spoken, clip.pcm, rate);
            Check(Contract(phraseOnsets, phrases.Count - 1, clip.pcm.Length / 2), label + ": phrase contract (" + Show(phraseOnsets) + ")");
            if (phraseOnsets.Length != phrases.Count - 1) return;
            var phraseTruth = Truth(displayed, phrases, clip);
            for (int k = 0; k < phraseTruth.Count; k++)
            {
                double error = (phraseOnsets[k] - phraseTruth[k]) * 1000.0 / rate;
                Check(Math.Abs(error) <= toleranceMs, label + ": phrase " + (k + 2) + " starts at " + Ms(phraseTruth[k], rate) + " ms, got " + Ms(phraseOnsets[k], rate) + " ms");
            }
            Check(AtSentences(spans, phrases, phraseOnsets).SequenceEqual(onsets), label + ": phrase onsets at sentence starts are the sentence onsets");
        }
        string written = "Stop there. This gate has teeth. Before you lose yours, give me your name.";
        Aligned("as written", written, written, Speak(written, Rate, 1), Rate);
        // "Hmph," pauses as long as a full stop; the text decides which pauses end sentences.
        string grunt = "Hmph, fine. You may pass, but mind the gate, traveller. Go on, then.";
        Aligned("interjection", grunt, grunt, Speak(grunt, Rate, 2), Rate);
        string question = "Oi, you. What do you want, and why now? Speak up, or leave.";
        Aligned("question", question, question, Speak(question, Rate, 3), Rate);
        // Flowing: full stops spoken as commas (pauses ~360 ms, the original commas ~300 ms).
        string flowing = "Walk away. Before you lose yours, give me your name. Now.";
        string flowed = GatekeeperSpeechText.Flow(flowing);
        Check(flowed != flowing && !Regex.IsMatch(flowed, @"\w\.\s"), "the flowing text has no internal full stop: " + flowed);
        Aligned("flowing", flowing, flowed, Speak(flowing, Rate, 4, stopMs: 360, commaMs: 300), Rate);
        // 48 kHz: the same line, the same choice, sample indices at the clip's own rate.
        Aligned("48 kHz", written, written, Speak(written, 48000, 5), 48000);
        // Many lines: always the contract, always the right pause.
        string[] lines =
        {
            "Name. Business. Now.", "Right, last chance. Surprise me, then. Or walk.", "Mira, is it? Hmph. I've heard worse, so, what brings you here?",
            "The stew is mutton, mostly. Don't ask about the rest. Eat it or don't.", "Heh. Gold talks, traveller. Mine listens. Go through, before I change my mind.",
        };
        for (int i = 0; i < lines.Length; i++) Aligned("line " + (i + 1), lines[i], lines[i], Speak(lines[i], Rate, (uint)(10 + i)), Rate);
        // One sentence of commas (the owner's picks joined most lines this way): each comma's pause starts a phrase.
        string commas = "Stop there, this gate has teeth, so before you lose yours, give me your name.";
        Aligned("commas", commas, commas, Speak(commas, Rate, 20), Rate);
        Aligned("commas, longer pauses", commas, commas, Speak(commas, Rate, 21, commaMs: 330), Rate);
        // Gap classes come from sentences, not phrases: a short comma pause inside a short sentence must not take the
        // near-certain pause of a sentence end (that would pull the next sentence's start onto it).
        string begging = "Can't make that out. Begging, then. Up since dawn on burnt stew, and now this.";
        Aligned("a short comma pause in a short sentence", begging, begging, Speak(begging, Rate, 270, stopMs: 220, commaMs: 50), Rate);
        Aligned("; : and a dash", "Listen: the gate opens at dawn; not before — so wait.", "Listen: the gate opens at dawn; not before — so wait.",
            Speak("Listen: the gate opens at dawn; not before — so wait.", Rate, 22, commaMs: 260), Rate);
        // A comma spoken through (no pause): placed by its letters between the pauses around it, then snapped to the end of
        // the word gap (within 5 ms here; without the snap it lands up to 45 ms off).
        Aligned("a comma spoken through", "Fine, Mira. Gate's open, and you can sit by my fire tonight.", "Fine, Mira. Gate's open, and you can sit by my fire tonight.",
            Speak("Fine, Mira. Gate's open, and you can sit by my fire tonight.", Rate, 23, through: new[] { "Fine" }), Rate);
        Aligned("two commas spoken through", commas, commas, Speak(commas, Rate, 24, through: new[] { "teeth", "yours" }), Rate);
        Aligned("a comma spoken through, 48 kHz", grunt, grunt, Speak(grunt, 48000, 25, through: new[] { "pass" }), 48000);

        // ---- Onsets: hostile input never throws and keeps the contract ----
        var clip = Speak(written, Rate, 6);
        Check(GatekeeperSubtitleTiming.Onsets("", "", clip.pcm, Rate).Length == 0, "empty text: no onsets");
        Check(GatekeeperSubtitleTiming.Onsets(null, null, null, Rate).Length == 0, "null text: no onsets");
        Check(GatekeeperSubtitleTiming.Onsets("Just one, no more.", null, clip.pcm, Rate).Length == 0, "one sentence: no onsets");
        Check(Contract(GatekeeperSubtitleTiming.Onsets(written, written, null, Rate), 2, 0), "no audio: the contract holds");
        Check(Contract(GatekeeperSubtitleTiming.Onsets(written, written, new byte[0], Rate), 2, 0), "empty audio: the contract holds");
        var odd = new byte[4801]; Array.Copy(clip.pcm, odd, odd.Length);
        Check(Contract(GatekeeperSubtitleTiming.Onsets(written, written, odd, Rate), 2, 2400), "odd byte count: the contract holds");
        var silence = new byte[Rate * 4];
        Check(Contract(GatekeeperSubtitleTiming.Onsets(written, written, silence, Rate), 2, silence.Length / 2), "silence: the contract holds");
        Check(Contract(GatekeeperSubtitleTiming.Onsets(written, written, clip.pcm, 0), 2, clip.pcm.Length / 2), "no sample rate: the contract holds");
        Check(Contract(GatekeeperSubtitleTiming.Onsets(written, "Something else entirely, said by nobody at all, twice over.", clip.pcm, Rate), 2, clip.pcm.Length / 2),
            "different spoken words: the contract holds");
        Check(Contract(GatekeeperSubtitleTiming.Onsets("Use <b>this</b>. Then go.", "Use <b>this</b>. Then go.", clip.pcm, Rate), 1, clip.pcm.Length / 2), "tags in the text: the contract holds");
        Check(GatekeeperSubtitleTiming.PhraseOnsets("", "", clip.pcm, Rate).Length == 0 && GatekeeperSubtitleTiming.PhraseOnsets(null, null, null, Rate).Length == 0,
            "phrases: empty or null text, no onsets");
        Check(GatekeeperSubtitleTiming.PhraseOnsets("Just one phrase.", null, clip.pcm, Rate).Length == 0, "one phrase: no onsets");
        string oneSentence = "Stop there, traveller, then go.";
        Check(Contract(GatekeeperSubtitleTiming.PhraseOnsets(oneSentence, oneSentence, clip.pcm, Rate), 2, clip.pcm.Length / 2), "one sentence of phrases: the contract holds");
        var unaligned = GatekeeperSubtitleTiming.Phrases(oneSentence).Skip(1).Select(x => (int)((long)(odd.Length / 2) * x.start / oneSentence.Length));
        Check(GatekeeperSubtitleTiming.PhraseOnsets(oneSentence, oneSentence, odd, Rate).SequenceEqual(unaligned), "phrases in a clip too short to align: the character share of the phrase starts");
        foreach (var (label, pcm, rate, samples) in new[] { ("no audio", (byte[])null, Rate, 0), ("empty audio", new byte[0], Rate, 0), ("odd byte count", odd, Rate, 2400),
                     ("silence", silence, Rate, silence.Length / 2), ("no sample rate", clip.pcm, 0, clip.pcm.Length / 2) })
        {
            var phrased = GatekeeperSubtitleTiming.PhraseOnsets(written, written, pcm, rate);
            Check(Contract(phrased, GatekeeperSubtitleTiming.Phrases(written).Count - 1, samples), "phrases, " + label + ": the contract holds (" + Show(phrased) + ")");
            Check(AtSentences(GatekeeperSubtitleTiming.Sentences(written), GatekeeperSubtitleTiming.Phrases(written), phrased)
                .SequenceEqual(GatekeeperSubtitleTiming.Onsets(written, written, pcm, rate)), "phrases, " + label + ": sentence starts match Onsets");
        }
        Check(Contract(GatekeeperSubtitleTiming.PhraseOnsets(written, "Something else entirely, said by nobody at all, twice over.", clip.pcm, Rate), 3, clip.pcm.Length / 2),
            "phrases, different spoken words: the contract holds");
        Check(Contract(GatekeeperSubtitleTiming.PhraseOnsets("Use <b>this</b>, now. Then go.", null, clip.pcm, Rate), 2, clip.pcm.Length / 2), "phrases, tags in the text: the contract holds");
        // 400 sentences against 40 s of speech: far over the search-step cap, so the character-share estimate (this fails
        // if the cap stops binding). Against a 1 s clip with no pause: nothing to search, each start placed by its letters.
        string stress = string.Concat(Enumerable.Repeat("Go now. ", 400)).Trim();
        var forty = Speak(string.Concat(Enumerable.Repeat("Go now. ", 48)).Trim(), Rate, 7).pcm;
        var timer = Stopwatch.StartNew();
        var many = GatekeeperSubtitleTiming.Onsets(stress, stress, forty, Rate);
        double stressMs = timer.Elapsed.TotalMilliseconds;
        var stressSpans = GatekeeperSubtitleTiming.Sentences(stress);
        var share = stressSpans.Skip(1).Select(s => (int)((long)(forty.Length / 2) * s.start / stress.Length));
        Check(many.Length == 399 && many.SequenceEqual(share), "400 sentences in 40 s: the work caps give the character-share estimate");
        Check(stressMs < 500, "400 sentences align in well under a second: " + stressMs.ToString("0.0") + " ms");
        timer.Restart();
        var unpaused = GatekeeperSubtitleTiming.Onsets(stress, stress, clip.pcm.Take(Rate * 2).ToArray(), Rate);
        Check(Contract(unpaused, 399, Rate) && timer.Elapsed.TotalMilliseconds < 500, "400 sentences in a 1 s clip with no pause: 399 onsets at once");
        string stressPhrases = string.Concat(Enumerable.Repeat("Go, now. ", 400)).Trim();
        timer.Restart();
        var manyPhrases = GatekeeperSubtitleTiming.PhraseOnsets(stressPhrases, stressPhrases, forty, Rate);
        double stressPhraseMs = timer.Elapsed.TotalMilliseconds;
        var phraseShare = GatekeeperSubtitleTiming.Phrases(stressPhrases).Skip(1).Select(x => (int)((long)(forty.Length / 2) * x.start / stressPhrases.Length));
        Check(manyPhrases.Length == 799 && manyPhrases.SequenceEqual(phraseShare), "800 phrases in 40 s: the work caps give the character share of the phrase starts");
        Check(stressPhraseMs < 500, "800 phrases align in well under a second: " + stressPhraseMs.ToString("0.0") + " ms");

        // ---- ClauseStarts: clause playback ----
        string clauses = "Stop there, traveller. This gate has teeth; mind them. Go!";
        Check(GatekeeperSubtitleTiming.ClauseStarts(clauses, new[] { "Stop there,", "traveller.", "This gate has teeth;", "mind them.", "Go!" }, GatekeeperSubtitleTiming.Sentences(clauses))
            .SequenceEqual(new[] { 1, 1, 2, 2, 3 }), "clauses: each sentence shows with the clause that starts it");
        Check(GatekeeperSubtitleTiming.ClauseStarts("Wait… then go.", new[] { "Wait… then go." }, GatekeeperSubtitleTiming.Sentences("Wait… then go.")).SequenceEqual(new[] { 2 }),
            "a sentence starting inside a clause shows with that clause");
        Check(GatekeeperSubtitleTiming.ClauseStarts("Wait!\" he said. Go.", new[] { "Wait!", "\" he said.", "Go." }, GatekeeperSubtitleTiming.Sentences("Wait!\" he said. Go.")).SequenceEqual(new[] { 1, 2, 3 }),
            "a closing quote after a clause belongs to the sentence before");
        Check(GatekeeperSubtitleTiming.ClauseStarts("One. Two. Three.", new[] { "One.", "missing", "Three." }, GatekeeperSubtitleTiming.Sentences("One. Two. Three.")).SequenceEqual(new[] { 1, 2, 3 }),
            "a clause that is not found shows no less than its predecessor");
        Check(GatekeeperSubtitleTiming.ClauseStarts("Hmph.", new[] { "Hmph." }, GatekeeperSubtitleTiming.Sentences("Hmph.")).SequenceEqual(new[] { 1 }) && GatekeeperSubtitleTiming.ClauseStarts("", null, null).Length == 0,
            "one clause, or none");
        // Over phrases, as the game maps them: SplitClauses pieces end at the same marks, so each phrase shows with its clause.
        // (GatekeeperLiveSpeech needs the SDK; its SplitClauses regex is repeated here, and checked against the game's source.)
        const string ClausePattern = @"[^,;:.!?—–]+[,;:.!?—–]*";
        string liveSpeech = File.ReadAllText(Find("Assets/XybridAvatarDemo/Runtime/GatekeeperLiveSpeech.cs"));
        int splitAt = liveSpeech.IndexOf("SplitClauses(string text)", StringComparison.Ordinal);
        Check(splitAt >= 0 && liveSpeech.IndexOf("Regex.Matches(text, @\"" + ClausePattern + "\")", splitAt, StringComparison.Ordinal) > splitAt,
            "the clause regex here is GatekeeperLiveSpeech.SplitClauses's");
        string[] SplitClauses(string text) => Regex.Matches(text, ClausePattern).Select(m => m.Value.Trim()).Where(t => t.Length > 0).ToArray();
        int[] ClausePhrases(string text) => GatekeeperSubtitleTiming.ClauseStarts(text, SplitClauses(text), GatekeeperSubtitleTiming.Phrases(text));
        Check(ClausePhrases(clauses).SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "clauses over phrases: one phrase per clause");
        Check(ClausePhrases("Stop there, this gate has teeth, so give me your name.").SequenceEqual(new[] { 1, 2, 3 }), "one sentence of commas: a phrase per clause");
        Check(ClausePhrases("Go - now, then wait… and go.").SequenceEqual(new[] { 2, 4 }), "phrases starting inside a clause (spaced hyphen, ellipsis) show with it");
        Check(ClausePhrases("That's 1,000 coins, then go.").SequenceEqual(new[] { 1, 1, 2 }), "a clause split inside \"1,000\" shows no new phrase");
        Check(GatekeeperSubtitleTiming.ClauseStarts("Go, now.", new[] { "Go,", "now." }, null).SequenceEqual(new[] { 1, 1 }), "no spans: one phrase shows");

        // ---- Reached: the reveal clock (clip samples; 100 ms lead, 150 ms fade at 24 kHz) ----
        int[] at = { 24000, 48000 };
        int Count(long played, out float alpha) => GatekeeperSubtitleTiming.Reached(at, played, 2400, 3600, out alpha);
        Check(Count(0, out float a0) == 1 && a0 == 1, "before the first onset: one phrase, fully shown");
        Check(Count(21599, out _) == 1 && Count(21600, out float a1) == 2 && a1 == 0, "the next phrase shows 100 ms before its onset, transparent");
        Check(Count(23400, out float a2) == 2 && Math.Abs(a2 - .5f) < 1e-6, "half way through its 150 ms fade: alpha .5");
        Check(Count(30000, out float a3) == 2 && a3 == 1, "after the fade: alpha 1");
        Check(Count(60000, out _) == 3 && GatekeeperSubtitleTiming.Reached(null, 99999, 2400, 3600, out _) == 1, "the last phrase; no onsets keep one");

        // ---- Markup ----
        var white = new Color32(255, 255, 255, 255);
        string three = "One. Two. Three.";
        var spans3 = GatekeeperSubtitleTiming.Sentences(three);
        string first = GatekeeperSubtitleTiming.Markup(three, spans3, 1, 1, white, out bool plain1);
        Check(!plain1 && first == "One. <color=#00000000>Two. Three.</color>", "one of three: the tail hidden in place: " + first);
        Check(Strip(first) == three, "hidden text keeps the whole line's layout");
        string fading = GatekeeperSubtitleTiming.Markup(three, spans3, 2, .5f, new Color32(200, 100, 50, 200), out _);
        Check(fading == "One. <color=#c8643264>Two.</color> <color=#00000000>Three.</color>", "the newest sentence at half the base alpha: " + fading);
        Check(Strip(fading) == three, "a fading sentence keeps the layout too");
        string all = GatekeeperSubtitleTiming.Markup(three, spans3, 3, 1, white, out bool plain3);
        Check(!plain3 && all == three, "all shown: the plain line, no hidden tag");
        string last = GatekeeperSubtitleTiming.Markup(three, spans3, 3, 0, white, out _);
        Check(last == "One. Two. <color=#ffffff00>Three.</color>" && !last.Contains("#00000000"), "the last sentence fading in: no hidden tag: " + last);
        Check(GatekeeperSubtitleTiming.Markup(three, spans3, 9, 1, white, out _) == three, "more than all: the plain line");
        Check(GatekeeperSubtitleTiming.Markup(three, spans3, 0, 1, white, out _) == "<color=#00000000>One. Two. Three.</color>", "none: all hidden");
        string tagged = "Use <b>this</b>. Then go.";
        Check(GatekeeperSubtitleTiming.Markup(tagged, GatekeeperSubtitleTiming.Sentences(tagged), 1, 1, white, out bool plainTag) == tagged && plainTag,
            "a line with '<' is never read as tags: plain, whole");
        string arrow = "Go -> there. Now.";
        Check(GatekeeperSubtitleTiming.Markup(arrow, GatekeeperSubtitleTiming.Sentences(arrow), 1, 1, white, out bool plainArrow) == arrow && plainArrow, "a line with '>' too");
        string one = "Hmph, fine.";
        Check(GatekeeperSubtitleTiming.Markup(one, GatekeeperSubtitleTiming.Sentences(one), 1, 1, white, out bool plainOne) == one && !plainOne, "a single sentence: the plain line");
        Check(GatekeeperSubtitleTiming.Markup("", GatekeeperSubtitleTiming.Sentences(""), 1, 1, white, out _) == "" && GatekeeperSubtitleTiming.Markup(null, null, 1, 1, white, out _) == "",
            "empty and null lines");
        string commaLine = "Stop there, this gate has teeth, give me your name.";
        var commaSpans = GatekeeperSubtitleTiming.Phrases(commaLine);
        Check(GatekeeperSubtitleTiming.Markup(commaLine, commaSpans, 1, 1, white, out _) == "Stop there, <color=#00000000>this gate has teeth, give me your name.</color>",
            "phrases: one of three shown, the rest hidden in place");
        Check(GatekeeperSubtitleTiming.Markup(commaLine, commaSpans, 2, .5f, white, out _)
            == "Stop there, <color=#ffffff80>this gate has teeth,</color> <color=#00000000>give me your name.</color>", "phrases: the newest one fading in");
        string padded = "  Mira, is it?  Hmph.  ";
        Check(Strip(GatekeeperSubtitleTiming.Markup(padded, GatekeeperSubtitleTiming.Sentences(padded), 1, .3f, white, out _)) == padded, "padding survives the markup");
        Check(Strip(GatekeeperSubtitleTiming.Markup(padded, GatekeeperSubtitleTiming.Phrases(padded), 2, .3f, white, out _)) == padded, "padding survives the phrase markup");

        return new
        {
            passed = failures.Count == 0,
            checks = checks + " checks; every sentence end a phrase end over " + supersetTexts + " lines (" + corpusTexts + " from the local corpus)",
            failed = failures
        };
    }

    private static bool Contract(int[] onsets, int count, int samples)
    {
        if (onsets == null || onsets.Length != count) return false;
        for (int k = 0; k < count; k++) if (onsets[k] < 0 || onsets[k] > samples || (k > 0 && onsets[k] < onsets[k - 1])) return false;
        return true;
    }

    // The phrase onsets at the phrases that start sentences (-1 where none does).
    private static int[] AtSentences(List<(int start, int length)> sentences, List<(int start, int length)> phrases, int[] onsets)
    {
        var result = new List<int>();
        for (int k = 1; k < sentences.Count; k++)
        {
            int i = phrases.FindIndex(p => p.start == sentences[k].start);
            result.Add(i >= 1 && i - 1 < onsets.Length ? onsets[i - 1] : -1);
        }
        return result.ToArray();
    }

    private static string Find(string relative) => TryFind(relative) ?? throw new FileNotFoundException("Not found from the working or adapter folder: " + relative);
    private static string TryFind(string relative)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }

    private static string Strip(string markup) => Regex.Replace(markup, "</?color(=#[0-9a-f]{8})?>", "");
    private static string Show(int[] onsets) => onsets == null ? "null" : string.Join(", ", onsets);
    private static long Ms(int sample, int rate) => (long)Math.Round(sample * 1000.0 / rate);

    // The sample where each sentence or phrase after the first starts speaking: its first word's burst.
    private static List<int> Truth(string displayed, List<(int start, int length)> spans, Clip clip)
    {
        var starts = Word.Matches(displayed).Select(m => m.Index).ToList();
        var truth = new List<int>();
        for (int k = 1; k < spans.Count; k++) truth.Add(clip.words[starts.Count(s => s < spans[k].start)]);
        return truth;
    }

    private sealed class Clip { public byte[] pcm; public List<int> words = new List<int>(); }

    // A trimmed Phonon-like clip of `text`: each word a noise burst of 65 ms per letter (a digit counts 3) plus 60 ms
    // before punctuation, 10 ms ramps, 20 ms between words; pauses after a sentence end `stopMs`, after an interjection's
    // comma 420 ms, after a comma or other phrase mark (; : —) `commaMs`, none after a word in `through` (a comma spoken
    // through); 25 ms lead, 100 ms tail. The floor is +-3 LSB of noise (about -66 dB).
    private static Clip Speak(string text, int rate, uint seed, int stopMs = 420, int commaMs = 280, string[] through = null)
    {
        var samples = new List<short>();
        uint state = 2463534242u ^ seed;
        double Noise() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state / (double)uint.MaxValue - .5; }
        void Quiet(int ms) { for (int i = 0; i < rate * ms / 1000; i++) samples.Add((short)Math.Round(Noise() * 6)); }
        var clip = new Clip();
        var words = Word.Matches(text);
        Quiet(25);
        for (int w = 0; w < words.Count; w++)
        {
            var m = words[w];
            string after = w + 1 < words.Count ? text.Substring(m.Index + m.Length, words[w + 1].Index - m.Index - m.Length) : "";
            bool stop = after.IndexOfAny(new[] { '.', '!', '?', '…' }) >= 0;
            bool comma = after.IndexOfAny(new[] { ',', ';', ':', '—', '–' }) >= 0 && (through == null || !through.Contains(m.Value));
            int letters = m.Value.Sum(c => char.IsDigit(c) ? 3 : char.IsLetter(c) ? 1 : 0);
            int body = rate * (letters * 65 + (stop || comma ? 60 : 0)) / 1000, ramp = rate / 100;
            double gain = (.55 + .45 * (Noise() + .5)) * 9000;
            clip.words.Add(samples.Count);
            for (int i = 0; i < body; i++)
            {
                double level = Math.Min(1, Math.Min(i, body - 1 - i) / (double)ramp);
                samples.Add((short)Math.Round(Noise() * 2 * gain * level + Noise() * 6));
            }
            if (w + 1 == words.Count) break;
            bool interjection = comma && Regex.IsMatch(m.Value, "^(hmph|hah|oi|heh)$", RegexOptions.IgnoreCase);
            Quiet(stop || interjection ? stopMs : comma ? commaMs : 20);
        }
        Quiet(100);
        clip.pcm = new byte[samples.Count * 2];
        for (int i = 0; i < samples.Count; i++) { clip.pcm[2 * i] = (byte)samples[i]; clip.pcm[2 * i + 1] = (byte)(samples[i] >> 8); }
        return clip;
    }
}
#endif
