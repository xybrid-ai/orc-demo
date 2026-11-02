using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// The words Phonon is given for a line when it should flow. Phonon (Pocket TTS lineage, trained on single sentences)
// treats every '.' as a sentence end and resets its pitch there, even inside one prompt; on Trola's clone, commas in
// their place cut the raw gap from 660 to 355 ms and the reset from 7.7 to 5.9 st. So internal full stops become
// commas, quotes go, ellipses and dashes become commas. '?' and '!' stay (a mid-line '?' really rises) and the line
// keeps one closing mark. Words never change apart from quotes and a common word's capital after a former stop
// ("Walk, while", but "Hmph, Mira"): names keep theirs. Speech only: subtitles, history and d1 keep the authored text.
// Pure; tools/npc-quality/SpeechTextChecks.cs pins it to speech-text-cases.json.
public static class GatekeeperSpeechText
{
    // The rules were prototyped in Python; these are its Unicode \w and \s.
    private const string W = @"\p{L}\p{N}_", S = @"\s\x1c-\x1f";
    private static readonly Regex Quotes = new Regex("[\"“”«»]");
    private static readonly Regex Ellipsis = new Regex("[" + S + @"]*(\.\.\.|…)[" + S + "]*(?=[^" + S + "])");
    private static readonly Regex Dash = new Regex("[" + S + "]*[—–][" + S + "]*|[" + S + "]+--?[" + S + "]+");
    private static readonly Regex Stop = new Regex("([" + W + @"]+)\.([" + S + "]+)(?=([A-Za-z][" + W + "'-]*))");
    private static readonly Regex MarkRun = new Regex("([?!])[?!]+");
    private static readonly Regex MarkComma = new Regex("([?!]),");
    private static readonly Regex Commas = new Regex("[" + S + "]*,([" + S + "]*,)+");
    private static readonly Regex SpaceComma = new Regex("[" + S + "]+,");
    private static readonly Regex Spaces = new Regex("[" + S + "]+");
    private static readonly Regex OpenEnd = new Regex("[,;:]+$");
    private static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.Ordinal) { "mr", "mrs", "ms", "dr", "st", "jr", "sr", "mt", "vs" };
    // Words lowercased when a former full stop no longer starts a sentence: common words and the ones Trola's and
    // Vesper's lines start sentences with. Anything else (a name) keeps its capital; "I" and its contractions always do.
    private static readonly HashSet<string> CommonWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "a", "an", "the", "this", "that", "that's", "these", "those", "it", "it's", "its", "he", "he's", "he'd", "he'll", "she", "she's",
        "we", "we're", "they", "they're", "you", "you're", "you'll", "your", "my", "our", "his", "her", "their", "there", "here", "now",
        "then", "so", "and", "but", "or", "yet", "still", "just", "only", "even", "not", "no", "nobody", "nothing", "one", "once", "too",
        "well", "right", "fine", "last", "before", "after", "while", "when", "if", "because", "from", "up", "out", "back", "again", "much",
        "more", "fewer", "gold", "coin", "coins", "cold", "excellent", "cook", "careful", "surprise", "pay", "tell", "be", "don't", "do",
        "choose", "give", "make", "keep", "ask", "flatter", "trust", "begging", "finally", "shame", "remember", "leave", "fair", "go",
        "act", "show", "earn", "walk", "gently", "come", "see", "stop", "spare", "mind", "look", "listen", "wait", "why", "what", "how",
        "who", "where", "hmph", "hah", "ha", "oi", "huh", "good", "bad", "never", "always", "maybe", "perhaps", "yes", "away", "through",
        "should", "could", "would", "can", "must", "let", "let's", "there's", "here's", "what's", "who's", "where's", "how's", "we'll",
        "we've", "they'll", "you've", "you'd", "she'll", "she'd", "it'll", "isn't", "aren't", "wasn't", "won't", "can't", "didn't",
        "doesn't", "every", "all", "some", "any", "each", "many", "few", "two", "three", "first", "next", "someone", "something",
        "everyone", "gate", "gate's", "proud", "breathe", "focus", "sit", "stand", "run", "hold", "take", "bring", "put", "try", "think",
        "know", "say", "speak", "talk", "hear", "quick", "quickly", "slowly", "softly", "easy", "easily", "steady", "almost", "very",
        "really", "okay", "ok", "sorry", "thanks", "please"
    };

    // A spoken line is at most a few hundred characters; anything far longer is left as written.
    private const int MaxLength = 4000;

    // Never throws: on any failure, or an empty result, the line is spoken as written.
    public static string Flow(string text)
    {
        if (string.IsNullOrEmpty(text) || IsBlank(text) || text.Length > MaxLength) return text;
        try
        {
            // Real lines settle in one pass; a stray "? ," or "-- --" needs another, so Flow(Flow(x)) == Flow(x).
            string t = Once(text);
            for (int pass = 0; pass < 4; pass++) { string again = Once(t); if (again == t) break; t = again; }
            return string.IsNullOrEmpty(t) ? text : t;
        }
        catch (Exception) { return text; }
    }

    private static string Once(string text)
    {
        if (string.IsNullOrEmpty(text) || IsBlank(text)) return text;
        // Whitespace first, so no rule below scans a long run of it. Quotes are not spoken (an echoed business is quoted
        // in the subtitle).
        string t = Quotes.Replace(Spaces.Replace(text, " "), "");
        // The frontend makes "..." a full stop and deletes dashes, running the words together; both become commas.
        t = Ellipsis.Replace(t, ", ");
        t = Dash.Replace(t, ", ");
        // "word. Next" becomes "word, next", except after an initial or a short abbreviation ("J. R.", "Mr. Rook").
        var chars = new StringBuilder(t);
        var stops = Stop.Matches(t);
        for (int k = stops.Count - 1; k >= 0; k--)
        {
            Match m = stops[k];
            string word = m.Groups[1].Value;
            if (word.Length == 1 && IsUpper(word) || Abbreviations.Contains(word.ToLowerInvariant())) continue;
            int dot = m.Groups[2].Index - 1, end = m.Index + m.Length;
            if (CommonWords.Contains(m.Groups[3].Value.Replace('’', '\'').ToLowerInvariant())) chars[end] = char.ToLowerInvariant(chars[end]);
            chars.Remove(dot, end - dot).Insert(dot, ", ");
        }
        // One mark per run of ? and !, no comma after one (the frontend would drop the mark), no doubled commas.
        t = MarkRun.Replace(chars.ToString(), "$1");
        t = MarkComma.Replace(t, "$1");
        t = Commas.Replace(t, ",");
        t = SpaceComma.Replace(t, ",");
        t = Spaces.Replace(t, " ").Trim(' ');
        if (t.Length > 0) t = char.ToUpperInvariant(t[0]) + t.Substring(1);
        // One closing mark: a trailing , ; : becomes '.', a bare word end gets one.
        t = OpenEnd.Replace(t, ".");
        if (t.Length > 0 && EndsInWord(t)) t += ".";
        return t;
    }

    private static bool IsBlank(string text)
    {
        foreach (char c in text) if (!char.IsWhiteSpace(c) && (c < '\x1c' || c > '\x1f')) return false;
        return true;
    }

    // Python's str.isupper: no lowercase or titlecase letter, and at least one uppercase one.
    private static bool IsUpper(string s)
    {
        bool cased = false;
        foreach (char c in s)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.LowercaseLetter || category == UnicodeCategory.TitlecaseLetter) return false;
            cased |= category == UnicodeCategory.UppercaseLetter;
        }
        return cased;
    }

    private static bool EndsInWord(string t)
    {
        int at = t.Length - 1;
        if (at > 0 && char.IsLowSurrogate(t[at]) && char.IsHighSurrogate(t[at - 1])) at--;
        switch (CharUnicodeInfo.GetUnicodeCategory(t, at))
        {
            case UnicodeCategory.UppercaseLetter: case UnicodeCategory.LowercaseLetter: case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter: case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber: case UnicodeCategory.LetterNumber: case UnicodeCategory.OtherNumber: return true;
            default: return false;
        }
    }
}
