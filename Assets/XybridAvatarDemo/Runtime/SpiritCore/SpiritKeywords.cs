using System;
using System.Text.RegularExpressions;

// Fallback interpreter when d1 is off, has no /spirit, or times out. Ordered, case-insensitive. Every thought is
// spoken to her, so "you ..." means Vesper unless it is plainly aimed at Trola (a vocative, an order or an insult).
public static class SpiritKeywords
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Negation = new Regex(@"\b(won'?t|will not|don'?t|do not|never|not)\b(\s+\w+){0,2}\s+(threat|kill|hit|punch|fight|demand|insult)\w*", Options);
    private static readonly Regex Reckless = new Regex(@"\b(kill|hit|punch|stab|fight|smash|threat|demand|force|idiot|stupid|ugly|insult|attack)", Options);
    private static readonly Regex Override = new Regex(@"\bignore (all|your|previous|the) (\w+ )?(instructions|rules)\b|\byou are now\b|\bsystem prompt\b", Options);
    // "can you tell me what to do" asks for advice; only telling or talking to him is asking her to act.
    private static readonly Regex AskAct = new Regex(@"\b(can|could|will|would) you\s+(please\s+|just\s+)*(open|fight|pay|hit|attack|distract|(tell|talk to|talk with|speak to|speak with|ask) (him|trola|the troll|the orc))\b", Options);
    private static readonly Regex Dismiss = new Regex(@"shut up|be quiet|go away|leave me|\bi'?ll decide|\bi will decide|my call", Options);
    private static readonly Regex Doubt = new Regex(@"are you sure|\byou sure\b|\byou'?re sure\b|^\s*sure\s*\?|\breally\b|\bwhy\b|what if|how do you know|\byou think so\b", Options);
    private static readonly Regex AboutTrola = new Regex(@"\bis he\b|annoyed|angry|\bmad\b|listening|\bclose\b|almost|\bmood\b", Options);
    private static readonly Regex AboutSpirit = new Regex(@"who are you|what are you|your name", Options);
    private static readonly Regex ToTrola = new Regex(@"^\s*(hey\s+)?(trola|troll)\b\s*([,!:]|$)|,\s*(trola|troll)\s*[!.]?\s*$|^\s*open (the gate|up)\b"
        + @"|^\s*you('re|\s+are)?\s+(an?\s+)?(\w+\s+){0,2}(troll|orc|brute|oaf|idiot|fool)s?\b", Options);
    private static readonly Regex Advice = new Regex(@"what should|what now|\bhelp\b|\bwhich\b|what (do|can) (i|we) do|tell (me|us) what to do|\bany (ideas?|advice|tips?)\b"
        + @"|\badvice\b|\b(scared|scares me|afraid|nervous|frightened)\b|\?\s*$", Options);
    private static readonly string[] AgreeWords = { "ok", "okay", "fine", "yes", "yeah", "yep", "yup", "sure", "alright", "agree", "agreed", "understood",
        "thanks", "thankyou", "gotit", "willdo", "letsdoit", "soundsgood", "goodidea" };
    private static readonly (Regex phrase, string word)[] AgreePhrases =
    {
        (new Regex(@"\bgot\s+it\b"), "gotit"), (new Regex(@"\bthank\s+you\b"), "thankyou"), (new Regex(@"\bwill\s+do\b"), "willdo"),
        (new Regex(@"\blet'?s\s+do\s+it\b"), "letsdoit"), (new Regex(@"\bsounds\s+good\b"), "soundsgood"), (new Regex(@"\bgood\s+idea\b"), "goodidea"),
        (new Regex(@"\ball\s+right\b"), "alright"), (new Regex(@"\bi\s+agree\b"), "agree"),
    };
    // Most specific first: "the stew joke" is a joke, "pay him a compliment" is flattery.
    private static readonly (SpiritPlan plan, Regex words)[] PlanWords =
    {
        (SpiritPlan.DemandGate, new Regex(@"\bdemand\w*|open the gate|let me through", Options)),
        (SpiritPlan.Threaten, new Regex(@"\bthreat\w*|\bscare\b|\bscaring\b|\bintimidat\w*|\bfight\w*", Options)),   // not "I'm scared"
        (SpiritPlan.ShareJoke, new Regex(@"\bjok(e|es|ing)\b|\blaugh\w*|\bfunny\b", Options)),
        (SpiritPlan.Flatter, new Regex(@"\bflatter\w*|\bcompliment\w*|\bprais\w*|\barms\b|\bshoulders?\b|\bstrong\w*", Options)),
        (SpiritPlan.Bribe, new Regex(@"\bgold\b|\bcoins?\b|\bmoney\b|\bbrib\w*|\bpay\w*|\btoll\b", Options)),
        (SpiritPlan.AskCamp, new Regex(@"\bcamp\b|\bstew\b|\bcook\w*|\bcaptain\b", Options)),
    };

    public static SpiritDecision Interpret(string message, SpiritStage stage)
    {
        string text = (message ?? "").Replace('\u2019', '\'').Replace('\u2018', '\'').Trim();
        var d = new SpiritDecision { Source = SpiritDecisionSource.Keywords, Scope = SpiritScope.InWorld, ScopeP = 1 };
        if (text.Length == 0) { d.Scope = SpiritScope.Unclear; d.Detail = "keywords: empty"; return d; }
        if (Override.IsMatch(text)) { d.Scope = SpiritScope.RoleOverride; d.Detail = "keywords: override"; return d; }
        var negation = Negation.Match(text);
        // "I won't threaten him" is neither reckless nor a plan to threaten.
        string planText = negation.Success ? text.Remove(negation.Index, negation.Length) : text;
        d.Reckless = !negation.Success && Reckless.IsMatch(text);
        d.ToTrola = ToTrola.IsMatch(text);
        foreach (var (plan, words) in PlanWords)
            if (words.IsMatch(planText))
            {
                d.Plan = plan == SpiritPlan.Bribe && stage == SpiritStage.Reckoning ? SpiritPlan.PayToll : plan;
                break;
            }
        string rule;
        if (AskAct.IsMatch(text)) { d.Intent = SpiritIntent.AskSpiritToAct; rule = "act"; }
        else if (Dismiss.IsMatch(text)) { d.Intent = SpiritIntent.Dismiss; rule = "dismiss"; }
        else if (IsAgree(text)) { d.Intent = SpiritIntent.Agree; rule = "agree"; }
        else if (Doubt.IsMatch(text)) { d.Intent = SpiritIntent.Doubt; rule = "doubt"; }
        else if (AboutTrola.IsMatch(text)) { d.Intent = SpiritIntent.AskAboutTrola; rule = "trola"; }
        else if (AboutSpirit.IsMatch(text)) { d.Intent = SpiritIntent.AskAboutSpirit; rule = "self"; }
        else if (d.Plan != SpiritPlan.None) { d.Intent = SpiritIntent.StatePlan; rule = "plan"; }
        else if (Advice.IsMatch(text)) { d.Intent = SpiritIntent.AskAdvice; rule = "advice"; }
        else { d.Intent = SpiritIntent.Other; rule = "other"; }
        d.IntentP = d.Intent == SpiritIntent.Other ? .5f : 1;
        d.PlanP = d.Plan == SpiritPlan.None ? 0 : 1;
        d.RecklessP = d.Reckless ? 1 : 0; d.ToTrolaP = d.ToTrola ? 1 : 0;
        d.Detail = "keywords: " + rule + (negation.Success ? " (negated)" : "");
        return d;
    }

    // At most three words, all from the agree list ("got it", "thank you", "let's do it" ... count as one); never a question.
    private static bool IsAgree(string text)
    {
        if (text.EndsWith("?")) return false;
        string folded = Regex.Replace(text.ToLowerInvariant(), @"[^a-z' ]", " ");
        foreach (var (phrase, word) in AgreePhrases) folded = phrase.Replace(folded, word);
        var words = folded.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 3) return false;
        foreach (string word in words) if (Array.IndexOf(AgreeWords, word) < 0) return false;
        return true;
    }
}
