using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Phrase-by-phrase subtitles (docs/subtitles.md): each phrase of a line appears when the voice starts saying it.
// Phrases splits a displayed line at every pause mark (Sentences at its sentence ends, which steer the aligner);
// PhraseOnsets finds where each phrase starts in the line's trimmed Phonon clip (on the voice worker, after
// GatekeeperSpeechTrim; Onsets reads the same alignment at sentence starts); ClauseStarts, Reached and Markup decide
// what Trola's and Vesper's subtitle shows while the clip plays. Pure (Color32 is the only Unity type);
// tools/npc-quality/SubtitleTimingChecks.cs covers it.
public static class GatekeeperSubtitleTiming
{
    // ---- Sentences ----

    // A sentence ends at a run of . ! ? or … (and any closing quotes or brackets right after it) that is followed by
    // whitespace and more text. Not after a short abbreviation ("Mr. Rook") or a single capital initial ("J. R. Tolk").
    // Spans cover the sentence's characters (punctuation and closing quotes included), never the whitespace between.
    private static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "mr", "mrs", "ms", "dr", "st", "jr", "sr", "mt", "vs" };
    private static bool Terminal(char c) => c == '.' || c == '!' || c == '?' || c == '…';
    private static bool Closer(char c) => c == '"' || c == '”' || c == '’' || c == '\'' || c == ')' || c == ']' || c == '»';

    public static List<(int start, int length)> Sentences(string text)
    {
        var spans = new List<(int start, int length)>();
        if (string.IsNullOrEmpty(text)) return spans;
        int n = text.Length, start = 0;
        while (start < n && char.IsWhiteSpace(text[start])) start++;
        int i = start;
        while (i < n)
        {
            if (!Terminal(text[i])) { i++; continue; }
            int markStart = i;
            while (i < n && Terminal(text[i])) i++;
            while (i < n && Closer(text[i])) i++;
            int end = i; // exclusive end of the candidate sentence
            int next = i;
            while (next < n && char.IsWhiteSpace(text[next])) next++;
            if (next == i || next >= n) continue; // no whitespace after the mark, or end of text
            if (text[markStart] == '.' && markStart + 1 < n && text[markStart + 1] != '.' && IsAbbreviation(text, markStart)) continue;
            spans.Add((start, end - start));
            start = next; i = next;
        }
        int last = n;
        while (last > start && char.IsWhiteSpace(text[last - 1])) last--;
        if (last > start) spans.Add((start, last - start));
        return spans;
    }

    // The word before a single '.' is a known short abbreviation or one capital letter.
    private static bool IsAbbreviation(string text, int dot)
    {
        int b = dot;
        while (b > 0 && char.IsLetter(text[b - 1])) b--;
        string word = text.Substring(b, dot - b);
        if (word.Length == 1 && char.IsUpper(word[0])) return true;
        return Abbreviations.Contains(word);
    }

    // ---- Phrases ----

    // The letters before a '.' are the whole word: "21st." is not the abbreviation "st." (the voice and the aligner treat
    // it as a full stop). Phrases only; Sentences keeps the tournament's rule, so it splits no more often.
    private static bool WholeWord(string text, int dot)
    {
        int b = dot;
        while (b > 0 && char.IsLetter(text[b - 1])) b--;
        return b == 0 || !char.IsLetterOrDigit(text[b - 1]);
    }

    // The reveal unit: a phrase ends after any mark the voice may pause at, followed by whitespace and more text: a run
    // of . ! ? … (not after an abbreviation or initial, as for sentences), , ; : or a dash (— –, or a hyphen with spaces
    // on both sides: "well-known" is one word), with any closing quotes or brackets right after. "1,000", "3:30" and
    // "a—b" don't split. Every sentence end is a phrase end. Spans exclude the whitespace between, like Sentences.
    public static List<(int start, int length)> Phrases(string text)
    {
        var spans = new List<(int start, int length)>();
        if (string.IsNullOrEmpty(text)) return spans;
        int n = text.Length, start = 0;
        while (start < n && char.IsWhiteSpace(text[start])) start++;
        int i = start;
        while (i < n)
        {
            if (!PhraseMark(text, i)) { i++; continue; }
            int marks = 0, dot = i;
            while (i < n && (PhraseMark(text, i) || Closer(text[i]))) { if (PhraseMark(text, i)) marks++; i++; }
            int end = i, next = i;
            while (next < n && char.IsWhiteSpace(text[next])) next++;
            if (next == i || next >= n) continue;
            if (marks == 1 && text[dot] == '.' && IsAbbreviation(text, dot) && WholeWord(text, dot)) continue;
            spans.Add((start, end - start));
            start = next; i = next;
        }
        int last = n;
        while (last > start && char.IsWhiteSpace(text[last - 1])) last--;
        if (last > start) spans.Add((start, last - start));
        return spans;
    }

    private static bool PhraseMark(string text, int i)
    {
        char c = text[i];
        if (Terminal(c) || c == ',' || c == ';' || c == ':' || c == '—' || c == '–') return true;
        if (c != '-') return false;
        int a = i, b = i;
        while (a > 0 && text[a - 1] == '-') a--;
        while (b + 1 < text.Length && text[b + 1] == '-') b++;
        return a > 0 && char.IsWhiteSpace(text[a - 1]) && b + 1 < text.Length && char.IsWhiteSpace(text[b + 1]);
    }

    // ---- Onsets ----

    // When each sentence (Onsets) or phrase (PhraseOnsets) of a displayed line starts in its trimmed Phonon clip (sample
    // index per sentence or phrase after the first); both read one alignment of the line. The spoken text predicts a
    // timeline: each word lasts in proportion to its letters, plus phrase-final lengthening, and each punctuated gap
    // expects a pause whose length depends on its punctuation. The clip gives the measured pauses: quiet runs of 10 ms
    // windows 40 dB under the loudest window (the threshold rises over a noisy floor). A dynamic program matches pauses to
    // punctuated gaps in order. It scores how likely each pause length is at that punctuation (a sentence end almost always
    // pauses, a full stop about 400 ms, a comma 200-500 ms, a word rarely over 100 ms) and how well the speech between two
    // matched pauses fits the predicted duration of the words between them. A sentence or phrase starts where its matched
    // pause ends; an unmatched one is placed by its letters between matched neighbours, then moved to the end of a clear
    // dip within 80 ms. Gap classes come from sentence ends only, so phrases read the sentence alignment at their own
    // starts and never move a sentence start. Never throws: on any failure, or on input too large to align quickly, it
    // returns the character-share estimate.

    // Gap classes from the spoken punctuation between two words.
    private const int None = 0, Weak = 1, Comma = 2, Stop = 3, FlowComma = 4;

    // Quiet: windows more than 40 dB under the loudest one; a pause has a quiet run of at least 40 ms (the ground
    // truth's definition). Runs split by a blip of at most 20 ms count as one pause, which ends where its longest run ends.
    private const double QuietDb = -40;
    private const int MinQuietWindows = 4, MergeWindows = 2;
    // Over a noisy floor the threshold rises to 10 dB above the 5th-percentile window, never above -30 dB.
    private const double FloorMarginDb = 10, MaxQuietDb = -30;

    // Pause length bins (10 ms windows): tiny < 100 ms, short < 200 ms, mid < 370 ms, long.
    private static int Bin(int windows) => windows < 10 ? 0 : windows < 20 ? 1 : windows < 37 ? 2 : 3;

    // P(no pause, tiny, short, mid, long | gap class), rounded from dev clips.
    private static readonly double[][] PausePrior =
    {
        new[] { 0.80, 0.17, 0.02, 0.005, 0.005 }, // None: not an anchor (listed for reference)
        new[] { 0.40, 0.20, 0.20, 0.10, 0.10 },   // Weak: colon, semicolon, dash, bracket, quote, abbreviation
        new[] { 0.05, 0.025, 0.03, 0.50, 0.39 },  // Comma
        new[] { 0.02, 0.002, 0.005, 0.02, 0.95 }, // Stop: . ? ! … and an interjection's comma ("Hmph, ...")
        new[] { 0.02, 0.005, 0.06, 0.40, 0.53 },  // FlowComma: a displayed sentence end the voice read as a comma
    };
    // A displayed sentence end almost always has a measurable pause (842 of 843 on dev).
    private const double BoundaryNoPause = 0.005;
    // A pause at no anchor (inside a word or at an unpunctuated gap): tiny, short, mid, long.
    private static readonly double[] NullPrior = { 0.80, 0.12, 0.03, 0.05 };

    // Word duration: letters (a digit counts 3) plus phrase-final lengthening before a comma or full stop (fitted on dev).
    private const double FinalBonus = 3;
    // Timing noise of the speech between two matched pauses: variance floor^2 + perMs * predicted ms (fitted on dev),
    // with Student-t tails (Nu) so one badly predicted stretch (digits, symbols, odd names) cannot dominate.
    private const double SigmaFloorMs = 80, SigmaPerMs = 15, Nu = 4;
    // Unanchored pauses of 150 ms or more are breaths, not speech time.
    private const int BreathWindows = 15;
    // Search band: a pause is only matched to a gap within 2 s + 10% of the speech time of its predicted position
    // (never binds on typical lines; keeps long ones fast).
    private const double BandMs = 2000, BandShare = 0.10;
    // An unmatched start moves to the end of the deepest dip within 80 ms of its letter position, if that dip
    // is 20 dB under the loudest window (a closure between words); otherwise the letter position stands.
    private const int SnapWindows = 8;
    private const double SnapDb = -20;
    // Work caps: a 40 s line uses about 2 500 table cells and 35 000 search steps. Anything far larger (a text much
    // longer than its clip) takes the character-share estimate instead of stalling the voice worker.
    private const long MaxCells = 50000, MaxSteps = 200000;

    private static readonly HashSet<string> Interjections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "hmph", "hah", "ha", "oi", "shh", "pfft", "heh", "bah" };

    public static int[] Onsets(string displayed, string spoken, byte[] pcm16Mono, int sampleRate)
    {
        var spans = Sentences(displayed ?? "");
        int count = Math.Max(0, spans.Count - 1);
        if (count == 0) return new int[0];
        int samples = pcm16Mono == null ? 0 : pcm16Mono.Length / 2;
        try
        {
            var alignment = Align(displayed, string.IsNullOrEmpty(spoken) ? displayed : spoken, spans, pcm16Mono, sampleRate);
            var result = alignment == null ? null : SentenceStarts(alignment, spans);
            if (Valid(result, count, samples)) return result;
        }
        catch (Exception) { }
        return Proportional(displayed, spans, samples);
    }

    // The reveal's onsets: one per phrase after the first. A phrase that starts a sentence gets exactly that sentence's
    // onset; one inside a sentence stays between its sentence's onset and the next one.
    public static int[] PhraseOnsets(string displayed, string spoken, byte[] pcm16Mono, int sampleRate)
    {
        var phrases = Phrases(displayed ?? "");
        int count = Math.Max(0, phrases.Count - 1);
        if (count == 0) return new int[0];
        int samples = pcm16Mono == null ? 0 : pcm16Mono.Length / 2;
        try
        {
            var sentences = Sentences(displayed);
            var alignment = Align(displayed, string.IsNullOrEmpty(spoken) ? displayed : spoken, sentences, pcm16Mono, sampleRate);
            var result = alignment == null ? null : PhraseStarts(alignment, sentences, phrases);
            if (Valid(result, count, samples)) return result;
        }
        catch (Exception) { }
        return Proportional(displayed, phrases, samples);
    }

    private static bool Valid(int[] result, int count, int samples)
    {
        if (result == null || result.Length != count) return false;
        for (int k = 0; k < count; k++)
            if (result[k] < 0 || result[k] > samples || (k > 0 && result[k] < result[k - 1])) return false;
        return true;
    }

    private static int[] Proportional(string displayed, List<(int start, int length)> spans, int samples)
    {
        int total = Math.Max(1, displayed.Length);
        var result = new int[spans.Count - 1];
        for (int k = 1; k < spans.Count; k++) result[k - 1] = (int)((long)samples * spans[k].start / total);
        return result;
    }

    private struct Word { public int Start, End, Letters; }

    // Letters and digits, with inner apostrophes ("I've", "fool's").
    private static List<Word> Words(string text)
    {
        var words = new List<Word>();
        int n = text.Length, i = 0;
        while (i < n)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int s = i, letters = 0;
            while (i < n)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c)) { letters += char.IsDigit(c) ? 3 : 1; i++; }
                else if ((c == '\'' || c == '’') && i + 1 < n && char.IsLetterOrDigit(text[i + 1])) i++;
                else break;
            }
            words.Add(new Word { Start = s, End = i, Letters = letters });
        }
        return words;
    }

    // Exclamations that take a full-stop pause even before a comma; any hum spelled with h and m ("Hmm", "Mmmm") counts.
    private static bool IsInterjection(string word)
    {
        if (Interjections.Contains(word)) return true;
        if (word.Length < 2) return false;
        foreach (char c in word) if (c != 'h' && c != 'm' && c != 'H' && c != 'M') return false;
        return true;
    }

    private static int GapClass(string text, Word a, Word b, bool sentenceEnd)
    {
        bool stop = false, comma = false, weak = false;
        string word = text.Substring(a.Start, a.End - a.Start);
        for (int i = a.End; i < b.Start; i++)
        {
            char c = text[i];
            // A full stop after an abbreviation or an initial is not a sentence end (the voice does stop at "a.b").
            if (c == '.' && (Abbreviations.Contains(word) || (word.Length == 1 && char.IsUpper(word[0])))) weak = true;
            else if (c == '.' || c == '?' || c == '!' || c == '…') stop = true;
            else if (c == ',') comma = true;
            else if (c == ';' || c == ':' || c == '—' || c == '–' || c == '-' || c == '(' || c == ')' || c == '"' || c == '“' || c == '”') weak = true;
        }
        if (stop) return Stop;
        if (comma && IsInterjection(word)) return Stop;
        if (sentenceEnd) return FlowComma;
        if (comma) return Comma;
        return weak ? Weak : None;
    }

    // One solve of a line: the measured pauses, the anchors (punctuated gaps of the spoken text) and which pause each
    // anchor matched. Onsets and PhraseOnsets read it at their own starts (OnsetAt).
    private sealed class Alignment
    {
        public double[] Db, TS, TE, AnchorPos, Cum;
        public double Quiet;
        public int Lead, Tail, Win, Samples, Words, J, G;
        public List<int> POn, AnchorWord;
        public List<Word> Displayed;
        public int[] MatchOf;
    }

    // `sentences` are the displayed line's sentences: their ends set the gap classes (a displayed sentence end the voice
    // read as a comma is a FlowComma, and a sentence end almost always pauses).
    private static Alignment Align(string displayed, string spoken, List<(int start, int length)> sentences, byte[] pcm, int rate)
    {
        int count = sentences.Count - 1, samples = pcm.Length / 2;
        if (rate < 1000 || samples < rate / 10) return null;
        int win = rate / 100, nw = samples / win;

        // Envelope in dB under the loudest 10 ms window.
        var db = new double[nw];
        double max = 1e-6;
        for (int w = 0, at = 0; w < nw; w++)
        {
            long sum = 0;
            for (int i = 0; i < win; i++, at += 2) { int x = (short)(pcm[at] | pcm[at + 1] << 8); sum += x * x; }
            db[w] = (double)sum / win;
            if (db[w] > max) max = db[w];
        }
        for (int w = 0; w < nw; w++) db[w] = 10 * Math.Log10(Math.Max(db[w], 1e-6) / max);
        // Noise floor: the 5th-percentile window, from a 1 dB histogram (bin b holds windows b to b + 1 dB down).
        var hist = new int[121];
        for (int w = 0; w < nw; w++) hist[Math.Min(120, (int)-db[w])]++;
        int floorBin = 120, seen = hist[120];
        while (seen <= nw / 20 && floorBin > 0) seen += hist[--floorBin];
        double quiet = Math.Max(QuietDb, Math.Min(FloorMarginDb - floorBin, MaxQuietDb));

        // Speech runs from lead to tail; pauses inside it (start, end, onset = end of the longest quiet run).
        int lead = 0, tail = nw;
        while (lead < nw && db[lead] < quiet) lead++;
        while (tail > lead && db[tail - 1] < quiet) tail--;
        if (tail - lead < 10) return null;
        var pS = new List<int>(); var pE = new List<int>(); var pOn = new List<int>();
        {
            int w = lead, curS = -1, curE = -1, bestLen = 0, bestEnd = -1;
            while (w < tail)
            {
                if (db[w] >= quiet) { w++; continue; }
                int s = w;
                while (w < tail && db[w] < quiet) w++;
                if (curS >= 0 && s - curE <= MergeWindows) curE = w;
                else
                {
                    if (curS >= 0 && bestLen >= MinQuietWindows) { pS.Add(curS); pE.Add(curE); pOn.Add(bestEnd); }
                    curS = s; curE = w; bestLen = 0;
                }
                if (w - s > bestLen) { bestLen = w - s; bestEnd = w; }
            }
            if (curS >= 0 && bestLen >= MinQuietWindows) { pS.Add(curS); pE.Add(curE); pOn.Add(bestEnd); }
        }

        // Words of the spoken line; sentence starts mapped from the displayed line by word index (same word sequence).
        var dWords = Words(displayed);
        var sWords = Words(spoken);
        int nWords = sWords.Count;
        if (nWords < 2 || dWords.Count < 1) return null;
        var isBoundary = new bool[nWords + 1];
        for (int k = 0; k < count; k++) isBoundary[WordAt(dWords, nWords, sentences[k + 1].start)] = true;

        // Predicted speech position (letters) before each word; anchors are the punctuated gaps.
        var gapCls = new int[nWords]; // gap before word i (i >= 1)
        var cum = new double[nWords + 1];
        for (int i = 1; i < nWords; i++) gapCls[i] = GapClass(spoken, sWords[i - 1], sWords[i], isBoundary[i]);
        for (int i = 0; i < nWords; i++)
        {
            bool final = i == nWords - 1 || gapCls[i + 1] >= Comma;
            cum[i + 1] = cum[i] + sWords[i].Letters + (final ? FinalBonus : 0);
        }
        var anchorWord = new List<int> { 0 };
        for (int i = 1; i < nWords; i++) if (gapCls[i] != None) anchorWord.Add(i);
        anchorWord.Add(nWords);
        int G = anchorWord.Count - 2, J = pS.Count;
        if ((long)(J + 2) * (G + 2) > MaxCells) return null;

        // Index 0 is the speech start, J + 1 / G + 1 the speech end.
        var tS = new double[J + 2]; var tE = new double[J + 2]; var bin = new int[J + 2];
        tE[0] = lead; tS[J + 1] = tail;
        for (int j = 1; j <= J; j++) { tS[j] = pS[j - 1]; tE[j] = pE[j - 1]; bin[j] = Bin(pE[j - 1] - pS[j - 1]); }
        var anchorCls = new int[G + 2]; var anchorPos = new double[G + 2]; var anchorSkip = new double[G + 2];
        for (int g = 0; g <= G + 1; g++)
        {
            int wi = anchorWord[g];
            anchorPos[g] = cum[wi];
            if (g == 0 || g == G + 1) continue;
            anchorCls[g] = gapCls[wi];
            anchorSkip[g] = -Math.Log(isBoundary[wi] ? BoundaryNoPause : PausePrior[gapCls[wi]][0]);
        }

        // Prefix sums over pauses: cost of leaving them unanchored, and their breath time.
        var nullPre = new double[J + 2]; var breathPre = new double[J + 2];
        for (int j = 1; j <= J + 1; j++)
        {
            int len = j <= J ? pE[j - 1] - pS[j - 1] : 0;
            nullPre[j] = nullPre[j - 1] + (j <= J ? -Math.Log(NullPrior[bin[j]]) : 0);
            breathPre[j] = breathPre[j - 1] + (len >= BreathWindows ? len : 0);
        }
        double speechMs = Math.Max(10, tail - lead - breathPre[J]) * 10;
        double msPerLetter = speechMs / Math.Max(1, cum[nWords]);

        // Band: the speech time before each pause against the predicted speech time before each anchor.
        var lo = new int[G + 2]; var hi = new int[G + 2];
        double band = BandMs + BandShare * speechMs;
        for (int g = 1; g <= G; g++)
        {
            double expect = anchorPos[g] * msPerLetter;
            lo[g] = J + 1; hi[g] = 0;
            for (int j = 1; j <= J; j++)
            {
                double at = (tS[j] - lead - breathPre[j - 1]) * 10;
                if (Math.Abs(at - expect) > band) continue;
                if (j < lo[g]) lo[g] = j;
                hi[g] = j;
            }
        }
        lo[0] = hi[0] = 0; lo[G + 1] = hi[G + 1] = J + 1;

        var matchOf = Solve(J, G, anchorCls, anchorPos, anchorSkip, lo, hi, tS, tE, bin, nullPre, breathPre, msPerLetter);
        if (matchOf == null) return null;

        return new Alignment
        {
            Db = db, TS = tS, TE = tE, AnchorPos = anchorPos, Cum = cum, Quiet = quiet, Lead = lead, Tail = tail, Win = win,
            Samples = samples, Words = nWords, J = J, G = G, POn = pOn, AnchorWord = anchorWord, Displayed = dWords, MatchOf = matchOf,
        };
    }

    // The spoken word a displayed character position starts at: the same word index (the word sequences match), or the
    // same share of the words if the counts differ.
    private static int WordAt(List<Word> displayed, int spokenWords, int start)
    {
        int wi = 0;
        while (wi < displayed.Count && displayed[wi].Start < start) wi++;
        if (displayed.Count != spokenWords) wi = (int)Math.Round((double)wi * spokenWords / displayed.Count);
        return wi;
    }

    // Sentence onsets, made non-decreasing.
    private static int[] SentenceStarts(Alignment a, List<(int start, int length)> sentences)
    {
        var result = new int[sentences.Count - 1];
        for (int k = 0; k < result.Length; k++) result[k] = OnsetAt(a, WordAt(a.Displayed, a.Words, sentences[k + 1].start));
        for (int k = 1; k < result.Length; k++) if (result[k] < result[k - 1]) result[k] = result[k - 1];
        return result;
    }

    // Phrase onsets: a sentence's first phrase takes the sentence onset; any other phrase is read from the same alignment
    // and kept between its sentence's onset and the next sentence's, after the phrase before it.
    private static int[] PhraseStarts(Alignment a, List<(int start, int length)> sentences, List<(int start, int length)> phrases)
    {
        var sentence = SentenceStarts(a, sentences);
        var result = new int[phrases.Count - 1];
        for (int k = 0, s = 0, previous = 0; k < result.Length; k++)
        {
            int start = phrases[k + 1].start;
            while (s < sentence.Length && sentences[s + 1].start < start) s++; // sentence s + 1 is the next to start
            if (s < sentence.Length && sentences[s + 1].start == start) result[k] = sentence[s];
            else
            {
                int from = s > 0 ? sentence[s - 1] : 0, to = s < sentence.Length ? sentence[s] : a.Samples;
                result[k] = Math.Max(previous, Math.Max(from, Math.Min(to, OnsetAt(a, WordAt(a.Displayed, a.Words, start)))));
            }
            previous = result[k];
        }
        return result;
    }

    // Where the speech before spoken word `bw` resumes (a sample index). A matched anchor: the end of its pause. Otherwise
    // interpolated by letters between the matched neighbours and snapped to the end of the deepest dip near there.
    private static int OnsetAt(Alignment a, int bw)
    {
        var db = a.Db; var matchOf = a.MatchOf; var anchorWord = a.AnchorWord; var anchorPos = a.AnchorPos;
        int G = a.G, J = a.J, g = anchorWord.IndexOf(bw);
        double t;
        if (bw <= 0) t = a.Lead;
        else if (bw >= a.Words) t = a.Tail;
        else if (g > 0 && g <= G && matchOf[g] > 0) t = a.POn[matchOf[g] - 1];
        else
        {
            int ga = 0, gb = G + 1;
            for (int h = 1; h <= G; h++)
            {
                if (matchOf[h] == 0) continue;
                if (anchorWord[h] < bw) ga = h;
                else if (anchorWord[h] > bw && gb == G + 1) gb = h;
            }
            double ta = a.TE[matchOf[ga]], tb = a.TS[gb == G + 1 ? J + 1 : matchOf[gb]];
            double pa = anchorPos[ga], pb = anchorPos[gb];
            t = pb > pa ? ta + (tb - ta) * (a.Cum[bw] - pa) / (pb - pa) : ta;
            int guess = (int)Math.Round(t), dip = -1;
            for (int w = Math.Max((int)ta, guess - SnapWindows), z = Math.Min((int)tb - 1, guess + SnapWindows); w <= z; w++)
                if (dip < 0 || db[w] < db[dip]) dip = w;
            if (dip >= 0 && db[dip] < SnapDb)
            {
                while (dip + 1 < a.Tail && db[dip + 1] < a.Quiet) dip++;
                t = dip + 1;
            }
        }
        return (int)Math.Max(0, Math.Min(a.Samples, Math.Round(t * a.Win)));
    }

    // Monotone matching of pauses to anchors. State (j, g): pause j ends at anchor g. A step from (pj, pg) leaves the
    // pauses and anchors in between unmatched and pays for how far the measured speech between them is from the
    // prediction. Every cost term is non-negative, so a predecessor whose cheapest state plus its skip costs already
    // exceeds the best step found is not examined (exact pruning; the result is the full search's).
    private static int[] Solve(int J, int G, int[] anchorCls, double[] anchorPos, double[] anchorSkip, int[] lo, int[] hi,
        double[] tS, double[] tE, int[] bin, double[] nullPre, double[] breathPre, double msPerLetter)
    {
        var cost = new double[J + 2, G + 2];
        var from = new int[J + 2, G + 2];
        for (int j = 0; j <= J + 1; j++) for (int g = 0; g <= G + 1; g++) cost[j, g] = double.PositiveInfinity;
        cost[0, 0] = 0;
        var skipPre = new double[G + 2];
        for (int g = 1; g <= G + 1; g++) skipPre[g] = skipPre[g - 1] + anchorSkip[g];
        var minCost = new double[G + 2]; // cheapest state of each anchor
        for (int g = 1; g <= G + 1; g++) minCost[g] = double.PositiveInfinity;
        long steps = 0;
        for (int g = 1; g <= G + 1; g++)
        {
            for (int j = lo[g]; j <= hi[g]; j++)
            {
                double best = double.PositiveInfinity; int arg = -1;
                for (int pg = g - 1; pg >= 0; pg--)
                {
                    if (++steps > MaxSteps) return null;
                    double skips = skipPre[g - 1] - skipPre[pg];
                    if (minCost[pg] + skips >= best) continue;
                    double pred = (anchorPos[g] - anchorPos[pg]) * msPerLetter;
                    double var = SigmaFloorMs * SigmaFloorMs + SigmaPerMs * pred;
                    for (int pj = Math.Min(hi[pg], j - 1); pj >= lo[pg]; pj--)
                    {
                        if (++steps > MaxSteps) return null;
                        double partial = skips + (nullPre[j - 1] - nullPre[pj]);
                        if (minCost[pg] + partial >= best) break; // skipped pauses only add cost further back
                        partial += cost[pj, pg];
                        if (partial >= best) continue;
                        double d = (tS[j] - tE[pj] - (breathPre[j - 1] - breathPre[pj])) * 10 - pred;
                        double c = partial + (Nu + 1) / 2 * Math.Log(1 + d * d / (Nu * var));
                        if (c < best) { best = c; arg = pj * (G + 2) + pg; }
                    }
                }
                if (arg < 0) continue;
                cost[j, g] = best + (g <= G ? -Math.Log(PausePrior[anchorCls[g]][1 + bin[j]]) : 0);
                from[j, g] = arg;
                if (cost[j, g] < minCost[g]) minCost[g] = cost[j, g];
            }
        }
        if (double.IsPositiveInfinity(cost[J + 1, G + 1])) return null;
        var matchOf = new int[G + 2];
        int cj = J + 1, cg = G + 1;
        while (cg > 0)
        {
            int a = from[cj, cg];
            cj = a / (G + 2); cg = a % (G + 2);
            if (cg > 0) matchOf[cg] = cj;
        }
        return matchOf;
    }

    // ---- What the subtitle shows ----

    // Clause playback (Kitten, Kokoro: GatekeeperLiveSpeech.SplitClauses pieces, one clip after another): how many of
    // `spans` (the phrases of `displayed`) show while clause i plays, those starting before the next clause does (one
    // that starts inside a clause shows with it). Clauses are found in order; one not found starts where the last one ended.
    public static int[] ClauseStarts(string displayed, string[] clauses, IReadOnlyList<(int start, int length)> spans)
    {
        displayed ??= "";
        int n = clauses?.Length ?? 0;
        var starts = new int[n + 1];
        for (int i = 0, cursor = 0; i < n; i++)
        {
            int found = string.IsNullOrEmpty(clauses[i]) ? -1 : displayed.IndexOf(clauses[i], cursor, StringComparison.Ordinal);
            starts[i] = found < 0 ? cursor : found;
            if (found >= 0) cursor = found + clauses[i].Length;
        }
        starts[n] = int.MaxValue;
        var visible = new int[n];
        for (int i = 0; i < n; i++)
        {
            int count = 0;
            if (spans != null) foreach (var span in spans) if (span.start < starts[i + 1]) count++;
            visible[i] = Math.Max(1, count);
        }
        return visible;
    }

    // Phrases a whole-line clip has reached at clip sample `played`: the first, plus each one whose onset is at most
    // `lead` samples ahead. The newest one fades in (0-1) over `fade` samples from that point.
    public static int Reached(int[] onsets, long played, int lead, int fade, out float newestAlpha)
    {
        int count = 1; long at = 0;
        if (onsets != null) foreach (int onset in onsets) if (played >= (long)onset - lead) { count++; at = (long)onset - lead; }
        newestAlpha = count == 1 || fade <= 0 ? 1 : (float)Math.Max(0, Math.Min(1, (played - at) / (double)fade));
        return count;
    }

    // The line with spans (phrases) >= visible hidden in a fully transparent colour, so the whole line keeps its layout
    // and nothing reflows as phrases appear; the newest visible one at newestAlpha times the colour's alpha. Line text is
    // never read as tags: a line with '<' or '>' (a model-written one could have them) comes back as is, with plain set.
    // Show the result with supportRichText on only when it differs from the text.
    public static string Markup(string text, IReadOnlyList<(int start, int length)> spans, int visible, float newestAlpha, Color32 color, out bool plain)
    {
        text ??= "";
        plain = text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0;
        int count = spans?.Count ?? 0;
        if (plain || count == 0) return text;
        visible = Math.Max(0, Math.Min(visible, count));
        float fade = newestAlpha >= 0 ? Math.Min(1, newestAlpha) : 0;
        bool fading = visible > 0 && fade < 1;
        if (visible == count && !fading) return text;
        int hidden = visible < count ? Math.Max(0, Math.Min(text.Length, spans[visible].start)) : text.Length;
        var shown = new StringBuilder(text.Length + 48);
        if (fading)
        {
            var newest = spans[visible - 1];
            int from = Math.Max(0, Math.Min(hidden, newest.start)), to = Math.Max(from, Math.Min(hidden, newest.start + newest.length));
            shown.Append(text, 0, from).Append("<color=#").Append(Hex(color.r)).Append(Hex(color.g)).Append(Hex(color.b))
                .Append(Hex((byte)Math.Round(fade * color.a))).Append('>').Append(text, from, to - from).Append("</color>").Append(text, to, hidden - to);
        }
        else shown.Append(text, 0, hidden);
        if (hidden < text.Length) shown.Append("<color=#00000000>").Append(text, hidden, text.Length - hidden).Append("</color>");
        return shown.ToString();
    }

    private static string Hex(byte value) => value.ToString("x2");
}
