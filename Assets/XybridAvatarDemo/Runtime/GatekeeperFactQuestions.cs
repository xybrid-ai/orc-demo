using System.Text.RegularExpressions;

// A deliberately narrow fallback while the model adapter is staged separately.
// These recognize questions, never extract new facts from arbitrary player text.
public static class GatekeeperFactQuestions
{
    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Normalize(string message) => (message ?? "").Replace('\u2019', '\'');
    // A question or a "tell me" request, not a statement ("My name is Bob") or an opinion ("What do you think of my business?").
    private static bool Asks(string text) => Has(text, @"\?|^\s*(?:what'?s?|who|how|where|tell me|remind me|say|do you (?:know|remember)|(?:can|could) you|(?:did|have) you)\b")
        && !Has(text, @"(?<!\bwhat )\bmy (?:name|business|trade) (?:is|was)\b|^\s*(?:just )?call me\b|\b(?:i am|i'm) called\b|\b(?:think|feel) (?:of|about) my\b");
    // Authored fact answers are for requests only; a statement about the player gets a written reply.
    public static bool AsksForFacts(string message) => Asks(Normalize(message));
    public static GatekeeperDecision Interpret(string message)
    {
        string text = Normalize(message);
        bool question = Asks(text);
        if (question && Has(text, @"\b(?:my (?:sister|brother|mother|father|family|age|birthday)|where (?:am i from|do i live))\b"))
            return new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.PlayerFacts, GatekeeperFact.Unknown);
        GatekeeperFact facts = GatekeeperFact.None;
        if (question && Has(text, @"\b(?:my name|who am i|what am i called)\b")) facts |= GatekeeperFact.PlayerName;
        if (question && Has(text, @"\b(?:my business|(?:what am i|what i am) (?:selling|doing)|why (?:am i|i am) here)\b")) facts |= GatekeeperFact.Business;
        if (facts != GatekeeperFact.None) return new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.PlayerFacts, facts);
        if (question && Has(text, @"\b(?:what (?:did i offer|have i offered|i offered|did i pay|did i give)|how (?:many|much) .{0,40}(?:did i offer|i offered)|(?:remember|recall|remind me) .{0,30}(?:offer|bribe)|what (?:was|is) my (?:offer|bribe))\b"))
            return new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.OfferRecall);
        if (question && Has(text, @"\b(?:already (?:let|allowed|admitted)|(?:can|may|could) i (?:pass|enter|go through)|(?:am i|was i) (?:allowed|admitted)|(?:did|have) you refused? me|permission to (?:pass|enter))\b"))
            return new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.AdmissionStatus);
        return null;
    }
}
