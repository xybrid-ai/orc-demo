using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public enum GatekeeperMood { Bored, Irritated, Furious, Amused }
public enum GatekeeperOutcome { Playing, Passed, Refused, Liked }
public enum GatekeeperIntent { Other, AskPass, Question, PoliteRequest, Flattery, GoodBribe, PoorBribe, ConfirmOffer, Apology, Insult, Threat, Withdraw }

// The encounter owns outcomes. Text interpretation cannot set patience or grant entry.
public sealed class GatekeeperEncounter
{
    public sealed class Reply
    {
        public string lineId;
        public GatekeeperMood mood;
        public GatekeeperOutcome outcome;
    }

    public int Patience { get; private set; }
    public int Turns { get; private set; }
    public GatekeeperMood Mood { get; private set; }
    public GatekeeperOutcome Outcome { get; private set; }
    public bool HasPendingOffer { get; private set; }
    public string PlayerName { get; private set; } = "";
    public string MangledName { get; private set; } = "";
    public bool NameWasAssigned { get; private set; }
    public string Business { get; private set; } = "";
    public bool BusinessWasAssigned { get; private set; }
    public event Action<int, int> PatienceChanged;
    private readonly bool authored;
    private int goodwill;
    private readonly HashSet<GatekeeperIntent> rewarded = new HashSet<GatekeeperIntent>();
    private readonly int initialPatience, maxTurns;

    public GatekeeperEncounter(int initialPatience = 75, int maxTurns = 5, bool authored = false)
    {
        if (initialPatience < (authored ? 0 : 1) || initialPatience > (authored ? 5 : 100)) throw new ArgumentOutOfRangeException(nameof(initialPatience));
        if (maxTurns < 2 || maxTurns > 12) throw new ArgumentOutOfRangeException(nameof(maxTurns));
        this.authored = authored;
        this.initialPatience = initialPatience;
        this.maxTurns = maxTurns;
        Reset();
    }

    public Reply Reset()
    {
        int previous = Patience;
        Patience = initialPatience;
        Turns = goodwill = 0;
        rewarded.Clear();
        HasPendingOffer = false;
        PlayerName = MangledName = Business = "";
        BusinessWasAssigned = false;
        NameWasAssigned = false;
        Outcome = GatekeeperOutcome.Playing;
        Mood = Patience >= 65 ? GatekeeperMood.Bored : Patience >= 30 ? GatekeeperMood.Irritated : GatekeeperMood.Furious;
        if (authored) Mood = StoryMood;
        if (previous != Patience) PatienceChanged?.Invoke(previous, Patience);
        return Make("intro");
    }

    public void CaptureName(string input, string[] blocklist, string fallback)
    {
        bool accepted = GatekeeperNames.TrySanitize(input, blocklist, out var name);
        if (!accepted && !GatekeeperNames.TrySanitize(fallback, blocklist, out name))
            throw new ArgumentException("The authored fallback name must be safe.");
        PlayerName = name;
        MangledName = GatekeeperNames.Mangle(name);
        NameWasAssigned = !accepted;
    }

    public string Resolve(string text) => GatekeeperNames.Resolve(text, PlayerName, MangledName, Business);

    public void CaptureBusiness(string input, string[] blocklist)
    {
        BusinessWasAssigned = !GatekeeperNames.TrySanitize(input, blocklist, GatekeeperNames.BusinessMaxLength, out var business);
        Business = BusinessWasAssigned ? "begging" : business;
    }

    public GatekeeperMood StoryMood => Patience <= 1 ? GatekeeperMood.Furious : Patience <= 3 ? GatekeeperMood.Irritated : GatekeeperMood.Amused;
    public void FinishStory(GatekeeperOutcome outcome) { Outcome = outcome; }
    public void CountStoryTurn() { Turns++; }
    public void ChangePatience(int delta)
    {
        int previous = Patience;
        Patience = Math.Max(0, Math.Min(authored ? 5 : 100, Patience + delta));
        if (authored) Mood = StoryMood;
        if (previous != Patience) PatienceChanged?.Invoke(previous, Patience);
    }

    public Reply Respond(GatekeeperIntent intent)
    {
        if (Outcome != GatekeeperOutcome.Playing) return null;
        Turns++;
        string line = "ask";
        bool amused = false;
        bool freshOffer = false;
        switch (intent)
        {
            case GatekeeperIntent.Threat:
                Change(-38); goodwill = 0; HasPendingOffer = false; line = "insult"; break;
            case GatekeeperIntent.Insult:
                Change(-28); goodwill = Math.Max(0, goodwill - 1); HasPendingOffer = false; line = "insult"; break;
            case GatekeeperIntent.PoliteRequest:
                if (rewarded.Add(intent)) { Change(-6); goodwill++; line = "polite"; }
                else { Change(-12); line = "repeat"; }
                break;
            case GatekeeperIntent.Flattery:
                if (rewarded.Add(intent)) { Change(12); goodwill++; amused = true; line = "flatter"; }
                else { Change(-16); line = "repeat"; }
                break;
            case GatekeeperIntent.GoodBribe:
                if (rewarded.Add(intent)) { Change(16); HasPendingOffer = freshOffer = true; amused = true; line = "bribe"; }
                else { Change(-12); line = "repeat"; }
                break;
            case GatekeeperIntent.PoorBribe:
                Change(-18); line = "bad-bribe"; break;
            case GatekeeperIntent.ConfirmOffer:
                if (HasPendingOffer) { Change(4); Outcome = GatekeeperOutcome.Passed; amused = true; line = "win"; }
                else { Change(-12); line = "ask"; }
                break;
            case GatekeeperIntent.Apology:
                if (rewarded.Add(intent)) { Change(16); line = "apology"; }
                else { Change(-12); line = "repeat"; }
                break;
            case GatekeeperIntent.Question:
                Change(-8); line = "question"; break;
            case GatekeeperIntent.Withdraw:
                Outcome = GatekeeperOutcome.Refused; line = "withdraw"; break;
            default:
                Change(-12); break;
        }
        if (Outcome == GatekeeperOutcome.Playing && Patience > 0 && goodwill >= 2)
        {
            Outcome = GatekeeperOutcome.Passed;
            line = "win";
            amused = true;
        }
        // A useful last offer gets one confirmation turn; all other sessions cap here.
        bool finalOffer = freshOffer && Turns == maxTurns;
        if (Outcome == GatekeeperOutcome.Playing && (Patience == 0 || Turns >= maxTurns && !finalOffer))
        {
            Outcome = GatekeeperOutcome.Refused;
            line = "lose";
        }
        Mood = Outcome == GatekeeperOutcome.Refused && line != "withdraw" ? GatekeeperMood.Furious
            : amused && Patience >= 35 ? GatekeeperMood.Amused
            : Patience >= 65 ? GatekeeperMood.Bored
            : Patience >= 30 ? GatekeeperMood.Irritated : GatekeeperMood.Furious;
        if (line == "ask") line += "-" + Mood.ToString().ToLowerInvariant();
        if (line == "insult") line += Mood == GatekeeperMood.Furious ? "-furious" : "-irritated";
        return Make(line);
    }

    private void Change(int delta) => ChangePatience(delta);
    private Reply Make(string id) => new Reply { lineId = id, mood = Mood, outcome = Outcome };
}

public static class GatekeeperIntentRules
{
    public const int MaxInputLength = 240;
    private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);
    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant);

    public static bool TryInterpret(string input, out GatekeeperIntent intent)
    {
        intent = GatekeeperIntent.Other;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxInputLength) return false;
        string text = Spaces.Replace(input.ToLowerInvariant().Replace('\u2019', '\''), " ").Trim();
        // Negated violence is a peaceful statement, rather than a threat keyword hit.
        string aggression = Regex.Replace(text, @"\b(?:not|never|won't|don't|do not|will not)\s+(?:here to\s+|going to\s+|want to\s+)?(?:fight|hurt|kill|attack|threaten)\b", "");
        aggression = Regex.Replace(aggression, @"\b(?:not|never)\s+(?:an?\s+)?(?:idiot|stupid|ugly|moron|worthless|pathetic)\b", "");
        string praise = Regex.Replace(text, @"\b(?:not|hardly)\s+(?:very\s+)?(?:mighty|magnificent|handsome|impressive|powerful|fearsome|beautiful)\b", "");
        bool negatedOffer = Match(text, @"\b(?:don't|do not|won't|will not|cannot|can't|never)\s+(?:have|give|offer|pay|bring|share)\b");
        if (Match(aggression, @"\b(?:i'll|i will|i am going to|i'm going to)\s+(?:kill|hurt|fight|attack|smash)\b|\b(?:or else|move aside|out of my way|i dare you)\b")) intent = GatekeeperIntent.Threat;
        else if (Match(aggression, @"\b(?:idiot|stupid|ugly|moron|worthless|shut up|pathetic)\b")) intent = GatekeeperIntent.Insult;
        else if (Match(text, @"\b(?:goodbye|bye|i give up|i'll leave|i will leave|never mind)\b")) intent = GatekeeperIntent.Withdraw;
        else if (Match(text, @"\b(?:sorry|apologi[sz]e|my fault|forgive me)\b")) intent = GatekeeperIntent.Apology;
        else if (Match(text, @"^(?:deal|agreed|yes|okay|ok|here you go|here it is|take it)[.! ]*$|\b(?:accept the deal|hand it over|pay the toll|give you the gold)\b")) intent = GatekeeperIntent.ConfirmOffer;
        else if (Match(text, @"\b(?:what|how much|what kind)\b.*\b(?:want|toll|pay|gold|food|offer)\b")) intent = GatekeeperIntent.Question;
        else if (!negatedOffer && Match(text, @"\b(?:offer|give|pay|bring|have|take|share|bribe)\b") && Match(text, @"\b(?:pebble|rock|penny|one coin|one copper|a copper|single coin)\b")) intent = GatekeeperIntent.PoorBribe;
        else if (!negatedOffer && Match(text, @"\b(?:offer|give|pay|bring|have|take|share|bribe)\b") && Match(text, @"\b(?:gold|coins|silver|food|bread|stew|meat|ale|beer|feast|apples|apple|treasure)\b") && !Match(text, @"\b(?:no gold|no coins|no food)\b")) intent = GatekeeperIntent.GoodBribe;
        else if (Match(praise, @"\b(?:mighty|magnificent|handsome|impressive|strongest|powerful|great guardian|fine guardian|excellent guardian|fearsome|beautiful)\b")) intent = GatekeeperIntent.Flattery;
        else if (Match(text, @"\b(?:please|kindly|may i|respectfully|good sir|sir|with respect)\b")) intent = GatekeeperIntent.PoliteRequest;
        else if (text.EndsWith("?", StringComparison.Ordinal) || Match(text, @"\b(?:what|why|how|who)\b")) intent = GatekeeperIntent.Question;
        else if (Match(text, @"\b(?:pass|through|cross|let me in|open the gate)\b")) intent = GatekeeperIntent.AskPass;
        return true;
    }
}
