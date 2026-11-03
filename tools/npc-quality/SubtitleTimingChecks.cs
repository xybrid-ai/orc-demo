// Headless checks for GatekeeperSubtitleTiming: the sentence splitter, the onset aligner on synthetic Phonon-like clips
// (noise-burst words, trimmed pauses: a full stop ~420 ms, a comma ~280 ms, an interjection's comma as long as a full
// stop), flowing speech text, hostile input, the clause mapping, the reveal clock and the subtitle markup. No WAV files.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UnityEngine;

public static class SubtitleTimingChecks
{
    private const int Rate = 24000;
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

        // ---- Onsets: contract and choice on synthetic clips ----
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
        // 400 sentences against 40 s of speech, and against a 1 s clip (far over the work caps): both answer at once.
        string stress = string.Concat(Enumerable.Repeat("Go now. ", 400)).Trim();
        var forty = Speak(string.Concat(Enumerable.Repeat("Go now. ", 48)).Trim(), Rate, 7).pcm;
        var timer = Stopwatch.StartNew();
        var many = GatekeeperSubtitleTiming.Onsets(stress, stress, forty, Rate);
        double stressMs = timer.Elapsed.TotalMilliseconds;
        Check(many.Length == 399 && many.Zip(many.Skip(1), (a, b) => a <= b).All(x => x), "400 sentences: 399 ordered onsets");
        Check(stressMs < 500, "400 sentences align in well under a second: " + stressMs.ToString("0.0") + " ms");
        timer.Restart();
        var capped = GatekeeperSubtitleTiming.Onsets(stress, stress, clip.pcm.Take(Rate * 2).ToArray(), Rate);
        Check(Contract(capped, 399, Rate) && timer.Elapsed.TotalMilliseconds < 500, "400 sentences in a 1 s clip: the character-share estimate at once");

        // ---- ClauseStarts: clause playback ----
        string clauses = "Stop there, traveller. This gate has teeth; mind them. Go!";
        Check(GatekeeperSubtitleTiming.ClauseStarts(clauses, new[] { "Stop there,", "traveller.", "This gate has teeth;", "mind them.", "Go!" })
            .SequenceEqual(new[] { 1, 1, 2, 2, 3 }), "clauses: each sentence shows with the clause that starts it");
        Check(GatekeeperSubtitleTiming.ClauseStarts("Wait… then go.", new[] { "Wait… then go." }).SequenceEqual(new[] { 2 }),
            "a sentence starting inside a clause shows with that clause");
        Check(GatekeeperSubtitleTiming.ClauseStarts("Wait!\" he said. Go.", new[] { "Wait!", "\" he said.", "Go." }).SequenceEqual(new[] { 1, 2, 3 }),
            "a closing quote after a clause belongs to the sentence before");
        Check(GatekeeperSubtitleTiming.ClauseStarts("One. Two. Three.", new[] { "One.", "missing", "Three." }).SequenceEqual(new[] { 1, 2, 3 }),
            "a clause that is not found shows no less than its predecessor");
        Check(GatekeeperSubtitleTiming.ClauseStarts("Hmph.", new[] { "Hmph." }).SequenceEqual(new[] { 1 }) && GatekeeperSubtitleTiming.ClauseStarts("", null).Length == 0,
            "one clause, or none");

        // ---- Reached: the reveal clock (clip samples; 100 ms lead, 150 ms fade at 24 kHz) ----
        int[] at = { 24000, 48000 };
        int Count(long played, out float alpha) => GatekeeperSubtitleTiming.Reached(at, played, 2400, 3600, out alpha);
        Check(Count(0, out float a0) == 1 && a0 == 1, "before the first onset: one sentence, fully shown");
        Check(Count(21599, out _) == 1 && Count(21600, out float a1) == 2 && a1 == 0, "the next sentence shows 100 ms before its onset, transparent");
        Check(Count(23400, out float a2) == 2 && Math.Abs(a2 - .5f) < 1e-6, "half way through its 150 ms fade: alpha .5");
        Check(Count(30000, out float a3) == 2 && a3 == 1, "after the fade: alpha 1");
        Check(Count(60000, out _) == 3 && GatekeeperSubtitleTiming.Reached(null, 99999, 2400, 3600, out _) == 1, "the last sentence; no onsets keep one");

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
        string padded = "  Mira, is it?  Hmph.  ";
        Check(Strip(GatekeeperSubtitleTiming.Markup(padded, GatekeeperSubtitleTiming.Sentences(padded), 1, .3f, white, out _)) == padded, "padding survives the markup");

        return new { passed = failures.Count == 0, checks = checks + " checks", failed = failures };
    }

    private static bool Contract(int[] onsets, int count, int samples)
    {
        if (onsets == null || onsets.Length != count) return false;
        for (int k = 0; k < count; k++) if (onsets[k] < 0 || onsets[k] > samples || (k > 0 && onsets[k] < onsets[k - 1])) return false;
        return true;
    }

    private static string Strip(string markup) => Regex.Replace(markup, "</?color(=#[0-9a-f]{8})?>", "");
    private static string Show(int[] onsets) => onsets == null ? "null" : string.Join(", ", onsets);
    private static long Ms(int sample, int rate) => (long)Math.Round(sample * 1000.0 / rate);

    // The sample where each sentence after the first starts speaking: its first word's burst.
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
    // comma 420 ms, after a comma `commaMs`; 25 ms lead, 100 ms tail. The floor is +-3 LSB of noise (about -66 dB).
    private static Clip Speak(string text, int rate, uint seed, int stopMs = 420, int commaMs = 280)
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
            bool comma = after.Contains(",");
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
