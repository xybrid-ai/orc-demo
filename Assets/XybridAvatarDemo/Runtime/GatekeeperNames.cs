using System;
using System.Globalization;
using System.Text;

// Only the returned name may reach dialogue, speech, or diagnostics.
public static class GatekeeperNames
{
    public const int MaxLength = 16;
    public const int BusinessMaxLength = 60;
    public static readonly string[] DefaultBlocklist =
    {
        "fuck", "shit", "bitch", "cunt", "asshole", "arsehole", "dick", "cock",
        "pussy", "bastard", "motherfucker", "nigger", "nigga", "faggot"
    };

    public static bool TrySanitize(string input, string[] blocklist, out string name)
        => TrySanitize(input, blocklist, MaxLength, out name);

    public static bool TrySanitize(string input, string[] blocklist, int maxLength, out string name)
    {
        if (maxLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLength));
        name = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string text;
        try { text = input.Trim().Replace('\u2019', '\'').Replace('\u2018', '\'').Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
        var clean = new StringBuilder();
        bool space = false, letter = false;
        foreach (char c in text)
        {
            if (!char.IsLetter(c) && c != ' ' && c != '-' && c != '\'') return false;
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
}
