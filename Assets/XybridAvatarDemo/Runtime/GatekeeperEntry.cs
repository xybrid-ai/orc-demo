using System;
using System.Text.RegularExpressions;

// Whether words said in free talk after admission ask to go into the camp (docs/entry.md). Deliberately narrow: a clear,
// present statement or command to go in, standing on its own ("Go in.", "Enter camp.", "Right, I'll head inside.").
// Questions ("Can I go in?", "Who's in there?"), hedges ("Maybe later"), refusals ("I won't go in yet"), goodbyes and
// everything else stay conversation. d1's enter_camp question widens the phrasing ("Let me in."); MayEnter still
// vetoes its questions, hedges and refusals. Pure, so the headless checks drive it.
public static class GatekeeperEntry
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    // Courtesies around the entry itself: "Okay, thanks, I'll go in then, goodbye."
    private static readonly Regex Lead = new Regex(
        @"^(?:(?:ok(?:ay)?|right|alright|all right|fine|well|so|good|great|thanks|thank you|cheers|sure|yes|yeah|now|then|please|very well|in that case|trola|troll|friend)\b[\s,.!-]*)+", Options);
    private static readonly Regex Trail = new Regex(
        @"(?:[\s,.!-]+(?:then|now|please|thanks|thank you|cheers|trola|troll|friend|goodbye|bye|see you|for now|at last|finally))+$", Options);
    // The entry: an optional "I'll" / "let's" / "time to", a verb of going and "in", "inside" or "into the camp"; or enter.
    private static readonly Regex Core = new Regex(
        @"^(?:(?:i'll|i will|i'm|i am|let's|let us|time to|i'm going to|i am going to|i want to|i'd like to|i'll just|i will just)\s+)?"
        + @"(?:go|going|head|heading|walk|walking|step|stepping|get|getting|come|coming)\s+(?:on\s+)?"
        + @"(?:in|inside|in there|through|through the gate|into (?:the |your )?camp|in(?:to)? the camp)$"
        + @"|^(?:(?:i'll|i will|i'm|let's|let us|time to)\s+)?(?:enter|entering)(?:\s+(?:the |your )?camp)?$"
        + @"|^in (?:i|we) go$", Options);
    private static readonly Regex Question = new Regex(
        @"\?|^(?:can|could|may|might|should|shall|do|does|did|is|are|am|will|would|who|who's|what|what's|where|where's|when|why|how|which|whose|mind if|any)\b", Options);
    private static readonly Regex Hedge = new Regex(
        @"\b(?:maybe|perhaps|probably|might|later|soon|tomorrow|tonight|first|before|after|if|unless|wait|yet|in a (?:bit|moment|minute|while)|one more|someday)\b|\.\.\.|\u2026", Options);
    private static readonly Regex Refusal = new Regex(
        @"\b(?:not|never|no|don't|do not|won't|will not|can't|cannot|shouldn't|wouldn't|rather not|nah)\b", Options);
    public const int MaxLength = 80;

    // Keywords alone: a clear entry standing on its own.
    public static bool Clear(string words)
    {
        string text = Normalize(words);
        if (!MayEnter(text)) return false;
        string core = Trail.Replace(Lead.Replace(text, ""), "").Trim(' ', ',', '.', '!', '-');
        return core.Length > 0 && Core.IsMatch(core);
    }

    // What d1's enter_camp answer may not override: a question, a hedge or a refusal, or a long message.
    public static bool MayEnter(string words)
    {
        string text = Normalize(words);
        return text.Length > 0 && text.Length <= MaxLength && !Question.IsMatch(text) && !Hedge.IsMatch(text) && !Refusal.IsMatch(text);
    }

    // Curly apostrophes and quotes evened out, spaces collapsed, a closing full stop or exclamation dropped (a trailing
    // ellipsis stays: it is a hedge).
    private static string Normalize(string words)
    {
        string text = Regex.Replace((words ?? "").Replace('\u2019', '\'').Replace('"', ' '), @"\s+", " ").Trim();
        return text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith("\u2026", StringComparison.Ordinal) ? text : text.TrimEnd('.', '!', ' ');
    }
}
