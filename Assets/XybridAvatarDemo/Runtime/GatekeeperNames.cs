using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// How a name answer was read: only Given keeps the player's own name; the rest earn the assigned one.
public enum GatekeeperNameReading { Given, Empty, Refused, NotAName, Unsafe }

// Only the returned name, business or said text may reach dialogue, speech, the player line or diagnostics.
public static class GatekeeperNames
{
    public const int MaxLength = 16;
    public const int BusinessMaxLength = 60;
    public const int AnswerMaxLength = 48;          // the typed name field: room for "I go by the name of ..."
    public const int BusinessAnswerMaxLength = 90;  // the typed business field; the business itself keeps 60
    public const int SaidMaxLength = 60;            // the player line
    private const int TranscriptMaxLength = 160;    // a spoken answer is read from its first 160 characters
    public static readonly string[] DefaultBlocklist =
    {
        "fuck", "shit", "bitch", "cunt", "asshole", "arsehole", "dick", "cock",
        "pussy", "bastard", "motherfucker", "nigger", "nigga", "faggot"
    };

    public static bool TrySanitize(string input, string[] blocklist, out string name)
        => TrySanitize(input, blocklist, MaxLength, out name);

    public static bool TrySanitize(string input, string[] blocklist, int maxLength, out string name)
        => TrySanitize(input, blocklist, maxLength, false, out name);

    // Digits are allowed in a business ("selling 10 turnips"; the voice reads numbers), never in a name.
    private static bool TrySanitize(string input, string[] blocklist, int maxLength, bool digits, out string name)
    {
        if (maxLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLength));
        name = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string text;
        try { text = input.Trim().Replace('\u2019', '\'').Replace('\u2018', '\'').Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
        // A typed answer often ends like a sentence ("Sam.", "Buy weapons!"): drop that ending, keep rejecting the rest.
        text = text.TrimEnd('.', '!', '?', ',', ';', ':', '…', ' ');
        var clean = new StringBuilder();
        bool space = false, letter = false;
        foreach (char c in text)
        {
            if (!char.IsLetter(c) && !(digits && c >= '0' && c <= '9') && c != ' ' && c != '-' && c != '\'') return false;
            if (c == ' ')
            {
                if (space) continue;
                space = true;
            }
            else { space = false; if (char.IsLetter(c)) letter = true; }
            clean.Append(c);
        }
        if (!letter || IsBlocked(clean.ToString(), blocklist)) return false;
        string capped = clean.ToString();
        if (capped.Length > maxLength) capped = capped.Substring(0, maxLength);
        capped = capped.Trim(' ', '-', '\'');
        if (capped.Length == 0 || IsBlocked(capped, blocklist)) return false;
        name = capped;
        return true;
    }

    private static bool IsBlocked(string name, string[] blocklist)
    {
        string folded = Fold(name);
        foreach (string word in blocklist ?? DefaultBlocklist)
        {
            string key = Fold(word ?? "");
            if (key.Length > 0 && folded.IndexOf(key, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    private static string Fold(string name)
    {
        var result = new StringBuilder();
        foreach (char c in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetter(c))
                result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }

    // First word, punctuation removed, with a pronounceable extra vowel.
    public static string Mangle(string safeName)
    {
        if (string.IsNullOrEmpty(safeName)) throw new ArgumentException("Capture a name first.");
        string first = safeName.Split(' ')[0].Replace("-", "").Replace("'", "");
        return first + (first.EndsWith("o", StringComparison.OrdinalIgnoreCase) ? "a" : "o");
    }

    public static bool HasSlots(string text) =>
        text != null && (text.Contains("{name}") || text.Contains("{name_mangled}") || text.Contains("{business}"));

    public static string Resolve(string text, string name, string mangled, string business = "")
    {
        if (HasSlots(text) && string.IsNullOrEmpty(name)) throw new InvalidOperationException("Capture a name before resolving dialogue.");
        return text.Replace("{name_mangled}", mangled ?? "").Replace("{name}", name ?? "").Replace("{business}", business ?? "");
    }

    // ---- Reading a typed or transcribed answer ("I am Malik" is Malik). Interpreted regexes: one read per answer.
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static Regex R(string pattern) => new Regex(pattern, Options);
    // Says outright what follows is a name; may appear anywhere ("I'm a traveller, but my name is Malik").
    private static readonly Regex StrongLeadIn = R(@"\b(?:my\s+name\s+is|my\s+name's|my\s+names|the\s+name\s+is|the\s+name's|the\s+names|name's|name\s+is|calls?\s+me|(?:i\s+)?go\s+by(?:\s+the\s+name(?:\s+of)?)?|(?:i\s+am|i'm|im)\s+(?:called|named|known\s+as)|known\s+as|name\s*:)(?=\s|[.,:;!?…]|$)");
    // Usually precedes a name, only at the start of a clause ("I'm Malik", "It's Malik").
    private static readonly Regex WeakLeadIn = R(@"^(?:(?:i\s+am|i'm|im)(?:\s+just)?|it's|it\s+is|its|this\s+is|here's|here\s+is|that's|that\s+is|mine\s+is|mine's)(?:\s+|$)");
    // "Malik is my name", "Malik's the name", "Malik is what they call me".
    private static readonly Regex LeadOut = R(@"^(.+?)(?:'s|\s+is)\s+(?:my|the)\s+name$|^(.+?)\s+is\s+what\s+\w+\s+calls?\s+me$");
    private static readonly Regex Filler = R(@"^(?:hi|hello|hey|hiya|howdy|yo|sup|ahoy|hail|salutations|well\s+met|greetings|ugh|meh|pfft|sigh|eh|huh|gah|argh|fine|just|simply|good\s+(?:morning|evening|day|afternoon|night)|well|um+|uh+|uhm|erm?|hmm+|ah+|oh+|ok|okay|alright|all\s+right|so|sure|yes|yeah|yep|right|ahem|there|me|who\s+me|oi|listen|look|honestly|actually|of\s+course|certainly|please|then|"
        + @"troll|trola|(?:mister|mr|master)\.?\s+troll|big\s+guy|my\s+friend|friend|gatekeeper|"
        // A title is only politeness when it stands alone: "Sir, I'm Malik" (not "Sir Lancelot").
        + @"(?:good\s+)?(?:sir|madam|ma'am|my\s+lord|my\s+lady)(?=\s*[,.!?]|\s*$|\s+(?:i\b|i'm|im\b|my\b|the\s+name|call|it's|this\s+is|they\b|people\b)))(?=[\s,.!?]|$)");
    private static readonly Regex EchoQuestion = R(@"^(?:my\s+name|name|your\s+name|who\s+am\s+i|(?:you\s+)?want\s+(?:to\s+know\s+)?my\s+name|what(?:\s+is|'s)\s+my\s+name|who|what|huh)$");
    private static readonly Regex Refusal = R(@"^(?:no|nope|nah|never|nothing|nobody|no\s+one|none|pass|skip|whatever|secret|guess|forget\s+it|not\s+(?:telling|saying)|not\s+(?:gonna|going\s+to)\s+(?:tell|say)|"
        + @"i\s+(?:won't|wont|will\s+not|shan't|can't|cannot|refuse|don't\s+want|do\s+not\s+want|would\s+rather\s+not|'d\s+rather\s+not)|i'd\s+rather\s+not|rather\s+not|"
        + @"(?:i\s+)?(?:don't|do\s+not)\s+(?:have|know|remember|need)|i\s+have\s+no|no\s+name|i\s+forgot|you\s+tell\s+me|(?:it\s+)?(?:doesn't|does\s+not)\s+matter|"
        + @"what's\s+it\s+to\s+you|(?:that's|it's|that\s+is|it\s+is)\s+(?:a\s+secret|private|personal|none\s+of\s+your)|none\s+of\s+your|mind\s+your\s+own|unimportant|irrelevant|not\s+important|private)\b");
    private static readonly Regex Interrogative = R(@"^(?:who|what|what's|why|how|where|when|which|may|can|could|will|would|shall|should|do|does|did|are|is|am|have|has|isn't|aren't|don't)\b");
    // Words that end a name: "Malik and you", "Malik from the east", "Malik the Swift".
    private static readonly HashSet<string> Stops = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "or", "from", "of", "the", "a", "an", "who", "here", "there", "to", "at", "in", "on", "with", "by", "for", "if", "so",
        "because", "since", "as", "is", "am", "are", "was", "i", "i'm", "im", "my", "me", "you", "your", "it", "it's", "this", "that",
        "sir", "madam", "troll", "trola", "friend", "please", "thanks", "thank", "now", "too", "though", "nice", "pleased", "son", "daughter",
        "what", "what's", "how", "why", "whats", "speaking", "just", "only", "then", "good", "glad"
    };
    // A first word that is never someone's own name in this scene.
    private static readonly HashSet<string> NonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "i", "i'm", "me", "my", "mine", "myself", "you", "your", "yours", "he", "she", "it", "it's", "we", "they", "a", "an", "the", "this", "that",
        "here", "there", "no", "nope", "nah", "none", "nothing", "nobody", "noone", "never", "not", "yes", "yeah", "yep", "ok", "okay", "sure",
        "fine", "maybe", "hello", "hi", "hey", "greetings", "well", "um", "uh", "why", "what", "what's", "who", "where", "when", "how", "which",
        "let", "open", "give", "go", "get", "take", "want", "need", "please", "thanks", "thank", "sorry", "just", "only", "really", "very",
        "traveller", "traveler", "stranger", "wanderer", "human", "person", "someone", "somebody", "anyone", "anybody", "everyone",
        "nameless", "anonymous", "unknown", "secret", "whatever", "idk", "dunno", "pass", "skip", "guess",
        "troll", "trola", "vesper", "spirit", "gatekeeper", "lost", "tired", "hungry", "afraid", "scared", "from", "to", "for", "of",
        "is", "am", "are", "was", "do", "does", "did", "can", "could", "would", "should", "have", "has", "had", "be", "name", "names", "pleased", "nice", "glad",
        "beautiful", "anything", "something", "everything", "tomorrow", "today", "later", "soon", "now", "unimportant", "irrelevant", "private",
        "yo", "sup", "ugh", "meh", "ask", "tell", "say", "come", "wait", "stop", "move", "step", "try", "see", "think", "know", "leave", "great"
    };
    private static readonly HashSet<string> Titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sir", "lady", "lord", "dame", "master", "mistress", "madam", "captain", "capt", "dr", "doctor", "mr", "mrs", "ms", "miss", "prof", "professor", "general", "st", "saint" };
    private static readonly HashSet<string> Pronouns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "my", "me", "you", "your", "yours", "it", "us", "him", "her", "them", "this", "that", "mine" };
    private static readonly HashSet<string> Particles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "da", "de", "del", "della", "der", "di", "du", "la", "le", "van", "von", "den", "bin", "ibn", "al", "el", "ben", "st" };
    private static readonly Regex Spelled = new Regex(@"^(?:[A-Za-z][-.]){1,}[A-Za-z]\.?$", RegexOptions.CultureInvariant);

    public static bool TryReadName(string answer, string[] blocklist, out string name) => ReadName(answer, blocklist, out name) == GatekeeperNameReading.Given;

    // The name in the answer, never the whole sentence; TrySanitize stays the last gate, so nothing it blocks gets through.
    public static GatekeeperNameReading ReadName(string answer, string[] blocklist, out string name)
    {
        name = null;
        string text = NormalizeAnswer(answer);
        if (text == null) return GatekeeperNameReading.Empty;
        if (HasBlockedWord(text, blocklist)) return GatekeeperNameReading.Unsafe; // rudeness anywhere earns the assigned name
        var clauses = Clauses(text);
        if (clauses.Count == 0) return GatekeeperNameReading.Empty;
        // One word: nothing to strip ("Im", "Malik?", "no").
        if (clauses.Count == 1 && clauses[0].text.IndexOf(' ') < 0)
            return Refusal.IsMatch(clauses[0].text) ? GatekeeperNameReading.Refused : Finish(Cut(clauses[0].text, text, true), blocklist, out name);
        var strong = StrongLeadIn.Match(text);
        if (strong.Success)
        {
            foreach (var c in Clauses(text.Substring(strong.Index + strong.Length)))
            {
                string rest = StripFillers(c.text);
                if (rest.Length == 0) continue;
                if (Refusal.IsMatch(rest)) return GatekeeperNameReading.Refused; // "my name is none of your business"
                if (rest.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(4); // "they call me the Butcher"
                return Finish(Cut(rest, text, false), blocklist, out name);
            }
            return GatekeeperNameReading.NotAName;
        }
        var result = GatekeeperNameReading.NotAName;
        bool lead = false; int read = 0;
        foreach (var c in clauses)
        {
            string clause = StripFillers(c.text);
            if (clause.Length == 0 || EchoQuestion.IsMatch(clause)) continue;
            // A question back at Trola is not an answer; a one-word "Malik?" is.
            if (c.question && !lead) continue;
            if (Refusal.IsMatch(clause)) return GatekeeperNameReading.Refused;
            if (++read > 4) break;
            var outro = LeadOut.Match(clause);
            if (outro.Success) return Finish(Cut(outro.Groups[1].Success ? outro.Groups[1].Value : outro.Groups[2].Value, text, false), blocklist, out name);
            var weak = WeakLeadIn.Match(clause);
            if (weak.Success)
            {
                clause = StripFillers(clause.Substring(weak.Length));
                lead = true;
                // "I'm, uh, Malik" and "It is I, Malik": the name is in a later clause.
                if (clause.Length == 0 || clause.Equals("i", StringComparison.OrdinalIgnoreCase) || clause.Equals("me", StringComparison.OrdinalIgnoreCase)) continue;
                if (Refusal.IsMatch(clause)) return GatekeeperNameReading.Refused; // "it's a secret"
            }
            result = Finish(Cut(clause, text, false), blocklist, out name);
            if (result != GatekeeperNameReading.NotAName) return result;
            if (lead) return result; // "I'm a traveller": a description, not a name
        }
        return result;
    }

    // Business stays in the player's own words (Trola quotes it back): fillers off the front, the first sentence,
    // commas kept between sanitized pieces, whole words within 60 characters. Rudeness anywhere still means "begging".
    public static bool TryReadBusiness(string answer, string[] blocklist, out string business)
    {
        business = null;
        string text = NormalizeAnswer(answer);
        if (text == null || HasBlockedWord(text, blocklist)) return false;
        // The first sentence that says something: "Hello! I'm a merchant." reads "I'm a merchant".
        string first = "";
        foreach (string sentence in text.Split('.', '!', '?', ';', '\u2026'))
            if ((first = StripFillers(sentence)).Length > 0) break;
        var pieces = new List<string>();
        foreach (string raw in first.Split(','))
        {
            if (raw.Trim().Length == 0) continue;
            if (!TrySanitize(raw, blocklist, 200, true, out var piece)) break; // "I sell salt, 3 sacks": keep what reads
            string joined = pieces.Count == 0 ? piece : string.Join(", ", pieces) + ", " + piece;
            if (joined.Length > BusinessMaxLength)
            {
                if (pieces.Count > 0) break;
                int cut = piece.LastIndexOf(' ', BusinessMaxLength);
                piece = cut > 0 ? piece.Substring(0, cut) : piece.Substring(0, BusinessMaxLength);
            }
            pieces.Add(piece);
        }
        if (pieces.Count == 0) return false;
        business = string.Join(", ", pieces);
        return true;
    }

    // The player line: what they said, not what Trola read out of it. Letters, digits, spaces and . , ! ? ' - only,
    // whole words within 60 characters; nothing when it is empty or any word is blocked.
    public static bool TryReadSaid(string answer, string[] blocklist, out string said)
    {
        said = null;
        if (string.IsNullOrWhiteSpace(answer)) return false;
        string text;
        try
        {
            text = answer.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"')
                .Replace('\u2014', '-').Replace('\u2013', '-').Replace("\u2026", "...").Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException) { return false; }
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { if (clean.Length > 0 && clean[clean.Length - 1] != ' ') clean.Append(' '); }
            else if (char.IsLetterOrDigit(c) || ".,!?'-".IndexOf(c) >= 0) clean.Append(c);
        }
        string result = clean.ToString().Trim();
        if (result.Length > SaidMaxLength)
        {
            int cut = result.LastIndexOf(' ', SaidMaxLength);
            result = (cut > 0 ? result.Substring(0, cut) : result.Substring(0, SaidMaxLength)).TrimEnd(' ', ',', '-', '\'');
        }
        bool readable = false;
        foreach (char c in result) if (char.IsLetterOrDigit(c)) { readable = true; break; }
        if (!readable || HasBlockedWord(text, blocklist)) return false;
        // Dropped characters must not assemble a blocked word ("fu*ck"), and punctuation inside a word ("F.U.C.K") hides none.
        foreach (string word in result.Split(' ', ',', '!', '?'))
            if (word.Length > 0 && IsBlocked(word, blocklist)) return false;
        said = result;
        return true;
    }

    private static GatekeeperNameReading Finish(string candidate, string[] blocklist, out string name)
    {
        name = null;
        if (string.IsNullOrEmpty(candidate)) return GatekeeperNameReading.NotAName;
        var words = new List<string>(candidate.Split(' '));
        if (NonNames.Contains(words[0])) return GatekeeperNameReading.NotAName;
        // Cap at whole words where possible; one long word keeps the 16-character cut.
        while (words.Count > 2 && Titles.Contains(words[0]) && string.Join(" ", words).Length > MaxLength) words.RemoveAt(0);
        while (words.Count > 1 && string.Join(" ", words).Length > MaxLength) words.RemoveAt(words.Count - 1);
        while (words.Count > 1 && Particles.Contains(words[words.Count - 1])) words.RemoveAt(words.Count - 1);
        string joined = string.Join(" ", words);
        foreach (char ch in joined) if (char.IsLetter(ch) && !IsLatin(ch)) return GatekeeperNameReading.NotAName; // the English voice cannot say it
        if (!TrySanitize(CaseName(joined), blocklist, out name)) { name = null; return GatekeeperNameReading.Unsafe; }
        return GatekeeperNameReading.Given;
    }

    // Up to three words, ending at a stop word, an -ing word, or (when the player capitalises) a lowercase word.
    private static string Cut(string clause, string whole, bool single)
    {
        bool capitalises = false; foreach (char ch in whole) if (char.IsUpper(ch)) { capitalises = true; break; }
        var tokens = clause.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "";
        if (Spelled.IsMatch(tokens[0]))
        {
            string letters = Regex.Replace(tokens[0], @"[-.]", "");
            return letters.Length <= 3 ? letters.ToUpperInvariant() : letters.ToLowerInvariant(); // "J.J." -> JJ, "M-A-L-I-K" -> malik
        }
        if (single) return Titles.Contains(tokens[0]) ? "" : tokens[0];
        var kept = new List<string>(); int words = 0, i = 0;
        for (; i < tokens.Length && words < 3; i++)
        {
            string t = tokens[i];
            if (i == 0) { if (Stops.Contains(t) && !Titles.Contains(t)) return ""; kept.Add(t); words++; continue; }
            if (Stops.Contains(t) || (t.EndsWith("ing", StringComparison.OrdinalIgnoreCase) && char.IsLower(t[0]))) break;
            bool particle = Particles.Contains(t);
            if (capitalises && char.IsLower(t[0]) && !particle) break;
            kept.Add(t); if (!particle) words++;
        }
        // "Ask my mother", "Tell me yours": a verb, not a name.
        if (i == 1 && i < tokens.Length && Pronouns.Contains(tokens[1])) return "";
        // A bare sentence that did not end at a stop word is not a name: "Beautiful day isn't it".
        if (i < tokens.Length && !Stops.Contains(tokens[i]) && tokens.Length > 3) return "";
        if (kept.Count == 1 && Titles.Contains(kept[0])) return ""; // "Sir" alone
        string last = kept[kept.Count - 1];
        if (last.Length > 3 && last.EndsWith("'s", StringComparison.OrdinalIgnoreCase)) kept[kept.Count - 1] = last.Substring(0, last.Length - 2); // "Malik's here"
        return string.Join(" ", kept);
    }

    // Title Case only when typed all lowercase or all caps; "DeShawn" and "da Vinci" stay as typed.
    private static string CaseName(string name)
    {
        bool upper = false, lower = false;
        foreach (char ch in name) { if (char.IsUpper(ch)) upper = true; else if (char.IsLower(ch)) lower = true; }
        if (upper && lower) return name;
        if (upper && name.Length <= 2) return name; // "JJ"
        var result = new StringBuilder(name.Length);
        string[] words = name.ToLowerInvariant().Split(' ');
        for (int w = 0; w < words.Length; w++)
        {
            if (w > 0) result.Append(' ');
            string word = words[w];
            if (w > 0 && Particles.Contains(word)) { result.Append(word); continue; }
            for (int i = 0; i < word.Length; i++)
            {
                bool start = i == 0 || word[i - 1] == '-' || (word[i - 1] == '\'' && i == 2); // Jean-Luc, O'Brien, D'Arcy
                result.Append(start ? char.ToUpperInvariant(word[i]) : word[i]);
            }
        }
        return result.ToString();
    }

    private struct Clause { public string text; public bool question; }
    private static List<Clause> Clauses(string text)
    {
        var list = new List<Clause>(); var current = new StringBuilder(); int sentence = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            char ch = i < text.Length ? text[i] : '.';
            bool dash = ch == '\u2014' || ch == '\u2013' || (ch == '-' && i > 0 && text[i - 1] == ' ');
            if (",.;:!?\u2026()[]\"".IndexOf(ch) >= 0 || dash)
            {
                string piece = current.ToString().Trim();
                if (piece.Length > 0) list.Add(new Clause { text = piece, question = ch == '?' && piece.IndexOf(' ') >= 0 });
                current.Clear();
                if (ch == '?') for (int k = sentence; k < list.Count; k++) if (Interrogative.IsMatch(list[k].text)) list[k] = new Clause { text = list[k].text, question = true };
                if (".!?\u2026".IndexOf(ch) >= 0) sentence = list.Count;
            }
            else current.Append(ch);
        }
        return list;
    }

    private static string StripFillers(string clause)
    {
        string text = clause.Trim();
        for (int guard = 0; guard < 8; guard++)
        {
            var m = Filler.Match(text);
            if (!m.Success || m.Length == 0) break;
            text = text.Substring(m.Length).TrimStart(' ', ',', '-');
        }
        return text;
    }

    private static string NormalizeAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        string text;
        try { text = answer.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"').Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return null; }
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('"', '\'', '*', '_', '`', ' ');
        text = Regex.Replace(text, @"\b(Mr|Mrs|Ms|Dr|St|Capt|Prof|Jr|Sr)\.(?=\s)", "$1", Options); // "Dr. Strange" is one clause
        text = Regex.Replace(text, @"\b(?:[A-Za-z]\.){2,}", m => string.Join("-", m.Value.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))); // "J.J." is spelled, not two sentences
        if (text.Length > TranscriptMaxLength) text = text.Substring(0, TranscriptMaxLength);
        return text.Length == 0 ? null : text;
    }

    // Whole words only for the full answer, so a long sentence cannot join two words into a blocked one.
    private static bool HasBlockedWord(string text, string[] blocklist)
    {
        foreach (string word in Regex.Split(text, @"[^\p{L}]+"))
            if (word.Length > 0 && IsBlocked(word, blocklist)) return true;
        return false;
    }

    private static bool IsLatin(char ch) => ch < '\u0250' || (ch >= '\u1E00' && ch <= '\u1EFF');
}
