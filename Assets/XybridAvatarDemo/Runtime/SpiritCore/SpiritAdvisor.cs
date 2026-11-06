using System;
using System.Collections.Generic;
using System.Text;

// Exhaustive search over the loaded story graph. It mirrors GatekeeperStory.Choose/Advance/Finish
// (repeat_delta, the 0..5 clamp, the hub->reckoning hand-off, patience variants), so hints stay true if the JSON is retuned.
public sealed class SpiritAdvisor
{
    public const string HubNode = "hub", ReckoningNode = "reckoning", LostKey = "lost";
    // Ties after utility, patience and delta: the gentlest, most in-character pick first.
    private static readonly string[] TieOrder = { "camp", "flatter", "bribe", "passage", "friendship", "threaten", "expelled" };
    private const int MaxDepth = 64;
    private readonly GatekeeperStory.Document story;
    private readonly Dictionary<string, GatekeeperStory.Node> nodes = new Dictionary<string, GatekeeperStory.Node>();
    public string Preference { get; }
    public int HubRounds => story.hub_rounds;

    public SpiritAdvisor(GatekeeperStory.Document story, string reckoningPreference)
    {
        this.story = story ?? throw new ArgumentNullException(nameof(story));
        foreach (var node in story.nodes ?? new List<GatekeeperStory.Node>())
            if (node != null && !string.IsNullOrEmpty(node.id) && !nodes.ContainsKey(node.id)) nodes[node.id] = node;
        Preference = reckoningPreference == "friendship" ? "friendship" : "passage";
    }

    // The node whose choice labels equal the shown labels (any order); "" when nothing matches.
    public string NodeFor(IReadOnlyList<string> choiceLabels)
    {
        if (choiceLabels == null || choiceLabels.Count == 0) return "";
        foreach (var node in nodes.Values)
        {
            if (node.choices == null || node.choices.Count != choiceLabels.Count) continue;
            bool all = true;
            foreach (string label in choiceLabels) if (node.choices.Find(c => c != null && c.label == label) == null) { all = false; break; }
            if (all) return node.id;
        }
        return "";
    }

    // Label lookup only: the advice-only runtime never asks the graph for a winning move.
    public string TargetForLabel(string nodeId, string label)
    {
        if (nodeId == null || !nodes.TryGetValue(nodeId, out var node) || node.choices == null) return "";
        return node.choices.Find(choice => choice != null && choice.label == label)?.target ?? "";
    }

    public SpiritAdvice Advise(string nodeId, int patience, int hubRoundsDone, IReadOnlyCollection<string> usedTargets)
    {
        patience = Clamp(patience);
        var advice = new SpiritAdvice { NodeId = nodeId ?? "", Patience = patience };
        if (nodeId == null || !nodes.TryGetValue(nodeId, out var node) || node.choices == null || node.choices.Count == 0) return advice;
        var used = new HashSet<string>(usedTargets ?? Array.Empty<string>());
        var options = new List<SpiritOption>();
        foreach (var choice in node.choices)
        {
            if (choice == null) continue;
            var step = Step(node, choice, patience, hubRoundsDone, used);
            options.Add(new SpiritOption
            {
                Label = choice.label ?? "", Target = choice.target ?? "", Delta = step.delta, PatienceAfter = step.after,
                BestOutcome = step.outcome, Repeat = step.repeat, Aggressive = step.delta <= -2
            });
        }
        advice.Options = options;
        advice.Hopeless = options.Count > 0 && options.TrueForAll(o => Utility(o.BestOutcome) <= 0);
        foreach (var option in options)
        {
            if (advice.Worst == null || Core(option, advice.Worst) < 0) advice.Worst = option;
            if (!advice.Hopeless && (advice.Best == null || Compare(option, advice.Best) > 0)) advice.Best = option;
        }
        foreach (var option in options)
            option.Verdict = advice.Hopeless ? SpiritVerdict.Hopeless : Utility(option.BestOutcome) <= 0 ? SpiritVerdict.Fatal
                : option == advice.Best ? SpiritVerdict.Best : option.Delta < 0 ? SpiritVerdict.Costly : SpiritVerdict.Good;
        advice.AlmostThere = !advice.Hopeless && (node.id == ReckoningNode || node.id == HubNode && hubRoundsDone >= story.hub_rounds - 1 && patience >= 3);
        advice.Trace = Trace(advice, hubRoundsDone);
        return advice;
    }

    // Plan -> option, stage-normalized (bribe<->toll, demand<->threaten); null = unavailable here.
    public SpiritOption Match(SpiritAdvice advice, SpiritPlan plan, SpiritStage stage)
    {
        string target = TargetFor(plan, stage);
        if (advice == null || target == null) return null;
        foreach (var option in advice.Options) if (option.Target == target) return option;
        return null;
    }

    public static string TargetFor(SpiritPlan plan, SpiritStage stage)
    {
        if (stage == SpiritStage.Hub)
            switch (plan)
            {
                case SpiritPlan.Flatter: return "flatter";
                case SpiritPlan.Bribe: case SpiritPlan.PayToll: return "bribe";
                case SpiritPlan.Threaten: case SpiritPlan.DemandGate: return "threaten";
                case SpiritPlan.AskCamp: return "camp";
            }
        if (stage == SpiritStage.Reckoning)
            switch (plan)
            {
                case SpiritPlan.Bribe: case SpiritPlan.PayToll: return "passage";
                case SpiritPlan.Threaten: case SpiritPlan.DemandGate: return "expelled";
                case SpiritPlan.ShareJoke: return "friendship";
            }
        return null;
    }

    // Line key for "the best move": the best option's target, or "lost" when nothing works.
    public static string BestKey(SpiritAdvice advice) => advice?.Best != null ? advice.Best.Target : LostKey;

    public static SpiritBand Band(int patience) => (SpiritBand)Clamp(patience);
    public static string D1Mood(SpiritBand band)
    {
        switch (band)
        {
            case SpiritBand.Done: case SpiritBand.Furious: return "furious";
            case SpiritBand.Annoyed: return "annoyed";
            case SpiritBand.Wary: return "wary";
            case SpiritBand.Listening: return "listening";
            default: return "warm";
        }
    }

    // Every checkpoint the advisor can be asked about: hub rounds with each reachable used-target set, then the reckoning.
    // Patience spans 0..5 everywhere (interruptions reach any value), a superset of real play.
    public IEnumerable<(string node, int patience, int hubRoundsDone, string[] used)> Checkpoints()
    {
        var targets = new List<string>();
        if (nodes.TryGetValue(HubNode, out var hub) && hub.choices != null)
            foreach (var choice in hub.choices) if (choice != null && !targets.Contains(choice.target)) targets.Add(choice.target);
        for (int rounds = 0; rounds < story.hub_rounds && hub != null; rounds++)
            for (int mask = 0; mask < 1 << targets.Count; mask++)
            {
                int size = 0; for (int m = mask; m != 0; m >>= 1) size += m & 1;
                if (size > rounds || rounds > 0 && size == 0) continue;
                var used = new List<string>();
                for (int i = 0; i < targets.Count; i++) if ((mask & 1 << i) != 0) used.Add(targets[i]);
                for (int p = 0; p <= 5; p++) yield return (HubNode, p, rounds, used.ToArray());
            }
        if (nodes.ContainsKey(ReckoningNode))
            for (int p = 0; p <= 5; p++) yield return (ReckoningNode, p, story.hub_rounds, Array.Empty<string>());
    }

    public int Utility(GatekeeperOutcome outcome)
    {
        if (outcome == GatekeeperOutcome.Passed) return Preference == "passage" ? 3 : 2;
        if (outcome == GatekeeperOutcome.Liked) return Preference == "friendship" ? 3 : 2;
        return 0;
    }

    private (int delta, int after, bool repeat, GatekeeperOutcome outcome) Step(GatekeeperStory.Node node, GatekeeperStory.Choice choice, int patience, int rounds, HashSet<string> used)
    {
        // GatekeeperStory.Choose: only the hub records approaches, so only the hub knows repeats.
        bool hub = node.id == HubNode;
        bool repeat = hub && used.Contains(choice.target);
        int delta = repeat ? choice.repeat_delta : choice.patience_delta;
        int after = Clamp(patience + delta);
        var nextUsed = used;
        if (hub) { nextUsed = new HashSet<string>(used); nextUsed.Add(choice.target); }
        return (delta, after, repeat, Settle(choice.target, after, rounds + (hub ? 1 : 0), nextUsed, 0));
    }

    // Enter(node) at this patience, then play on: ending variant, next checkpoint (best follow-up), or Advance.
    private GatekeeperOutcome Settle(string nodeId, int patience, int rounds, HashSet<string> used, int depth)
    {
        if (depth > MaxDepth || nodeId == null || !nodes.TryGetValue(nodeId, out var node)) return GatekeeperOutcome.Playing;
        var variant = node.variants?.Find(v => v != null && patience >= v.min_patience && patience <= v.max_patience);
        if (variant == null) return GatekeeperOutcome.Playing;
        if (!string.IsNullOrEmpty(variant.outcome))
            return Enum.TryParse<GatekeeperOutcome>(variant.outcome, out var ending) ? ending : GatekeeperOutcome.Playing;
        if (node.choices != null && node.choices.Count > 0)
        {
            var best = GatekeeperOutcome.Playing; int top = -1;
            foreach (var choice in node.choices)
            {
                if (choice == null) continue;
                var outcome = Step(node, choice, patience, rounds, used).outcome;
                if (Utility(outcome) > top) { top = Utility(outcome); best = outcome; }
            }
            return best;
        }
        if (!string.IsNullOrEmpty(node.input_slot)) return GatekeeperOutcome.Playing;
        string next = node.next;
        if (next == HubNode && rounds >= story.hub_rounds) next = ReckoningNode;
        return string.IsNullOrEmpty(next) ? GatekeeperOutcome.Playing : Settle(next, patience, rounds, used, depth + 1);
    }

    // Best = argmax (utility, PatienceAfter, Delta, unused before repeated, tie order); Worst = first argmin of the first three.
    private int Compare(SpiritOption a, SpiritOption b)
    {
        int c = Core(a, b);
        if (c == 0) c = b.Repeat.CompareTo(a.Repeat);
        return c != 0 ? c : Rank(b.Target).CompareTo(Rank(a.Target));
    }
    private int Core(SpiritOption a, SpiritOption b)
    {
        int c = Utility(a.BestOutcome).CompareTo(Utility(b.BestOutcome));
        if (c == 0) c = a.PatienceAfter.CompareTo(b.PatienceAfter);
        return c != 0 ? c : a.Delta.CompareTo(b.Delta);
    }

    private static int Rank(string target) { int i = Array.IndexOf(TieOrder, target); return i < 0 ? TieOrder.Length : i; }
    private static int Clamp(int patience) => Math.Max(0, Math.Min(5, patience));

    private static string Trace(SpiritAdvice advice, int rounds)
    {
        var text = new StringBuilder();
        text.Append(advice.NodeId).Append(advice.NodeId == HubNode ? "-" + (rounds + 1) : "").Append(" p").Append(advice.Patience).Append(": ");
        for (int i = 0; i < advice.Options.Count; i++)
        {
            var o = advice.Options[i];
            if (i > 0) text.Append(" | ");
            text.Append(o.Label).Append(' ').Append(o.Delta >= 0 ? "+" : "").Append(o.Delta).Append("→p").Append(o.PatienceAfter).Append(' ').Append(o.BestOutcome);
            if (o.Verdict != SpiritVerdict.Good) text.Append(' ').Append(o.Verdict.ToString().ToUpperInvariant());
        }
        return text.ToString();
    }
}
