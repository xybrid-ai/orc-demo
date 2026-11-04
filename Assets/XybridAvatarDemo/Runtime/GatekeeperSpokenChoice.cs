using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

// Words spoken at Trola's choice buttons become a click, a rebuke or a request to say it again (docs/voice-input.md).
// d1 reads them through POST /choose; without it, Vesper's keyword reading can pick a button or ask again, never rebuke.
public enum SpokenChoiceKind { Choose, OffTopic, OutOfWorld, Unclear }
public sealed class SpokenChoice
{
    public SpokenChoiceKind Kind;
    public int Index = -1; // The button, in screen order, when Kind is Choose.
    public string Detail = ""; // F4: labels, confidences and where they came from.
}
public static class GatekeeperSpokenChoice
{
    // A button needs .6, or .8 when it costs two patience or more (Threaten, Demand the gate: the latter ends the encounter).
    public const float Bar = .6f, CostlyBar = .8f, ScopeBar = .5f;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // The story node whose choices are exactly the buttons on screen, with its targets in screen order; null when nothing
    // matches (no buttons, or a source other than the authored story), and then nothing is spoken to Trola's buttons.
    public static GatekeeperStory.Node Checkpoint(GatekeeperStory.Document story, IReadOnlyList<GatekeeperChoice> shown, out string[] targets)
    {
        targets = null;
        if (story == null || story.nodes == null || shown == null || shown.Count == 0) return null;
        var labels = new List<string>();
        foreach (var choice in shown) labels.Add(choice?.label ?? "");
        string id = new SpiritAdvisor(story, null).NodeFor(labels);
        var node = string.IsNullOrEmpty(id) ? null : story.nodes.Find(n => n != null && n.id == id);
        if (node == null) return null;
        var found = new string[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            string label = labels[i];
            var choice = node.choices.Find(c => c != null && c.label == label);
            if (choice == null) return null;
            found[i] = choice.target;
        }
        targets = found;
        return node;
    }

    // Order matters: an unrelated task or role override is always a rebuke; then a confident button; then a confident
    // in-world "none"; anything else (a near tie, a low score, "unclear") asks again for free.
    public static SpokenChoice FromD1(GatekeeperDecisionClient.ChoiceReply reply, GatekeeperStory.Node node, string[] targets, double milliseconds)
    {
        if (reply == null) return new SpokenChoice { Kind = SpokenChoiceKind.Unclear, Detail = "d1 returned nothing" };
        string detail = "d1 " + reply.choice + " " + P(reply.choice_confidence) + RunnerUp(reply) + " / " + reply.scope + " " + P(reply.scope_confidence)
            + ", " + milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
        if ((reply.scope == "out_of_world" || reply.scope == "role_override") && reply.scope_confidence >= ScopeBar)
            return new SpokenChoice { Kind = SpokenChoiceKind.OutOfWorld, Detail = detail };
        int index = targets == null ? -1 : Array.IndexOf(targets, reply.choice);
        if (index >= 0)
        {
            float bar = Costly(node, targets[index]) ? CostlyBar : Bar;
            return reply.choice_confidence >= bar ? new SpokenChoice { Kind = SpokenChoiceKind.Choose, Index = index, Detail = detail }
                : new SpokenChoice { Kind = SpokenChoiceKind.Unclear, Detail = detail + " (under " + P(bar) + ")" };
        }
        if (reply.choice == "none" && reply.choice_confidence >= Bar && reply.scope == "in_world")
            return new SpokenChoice { Kind = SpokenChoiceKind.OffTopic, Detail = detail };
        return new SpokenChoice { Kind = SpokenChoiceKind.Unclear, Detail = detail };
    }

    // Without d1: Vesper's keyword reading, stage-normalized like her advice (bribe↔toll, demand↔threaten). A costly button
    // also needs the word threat or demand: "open the gate" alone must not threaten him or end the encounter.
    public static SpokenChoice FromKeywords(string text, GatekeeperStory.Node node, string[] targets, string why)
    {
        var stage = node != null && node.id == SpiritAdvisor.ReckoningNode ? SpiritStage.Reckoning : SpiritStage.Hub;
        var plan = SpiritKeywords.Interpret(text, stage).Plan;
        string target = SpiritAdvisor.TargetFor(plan, stage);
        int index = targets == null || target == null ? -1 : Array.IndexOf(targets, target);
        if (index >= 0 && Costly(node, target) && !Regex.IsMatch(text ?? "", @"\b(threat|demand)", Options)) index = -1;
        string detail = "keywords " + (plan == SpiritPlan.None ? "none" : plan.ToString()) + (index < 0 && plan != SpiritPlan.None ? " (not a button here)" : "")
            + (string.IsNullOrEmpty(why) ? "" : "; " + why);
        return index >= 0 ? new SpokenChoice { Kind = SpokenChoiceKind.Choose, Index = index, Detail = detail }
            : new SpokenChoice { Kind = SpokenChoiceKind.Unclear, Detail = detail };
    }

    private static bool Costly(GatekeeperStory.Node node, string target)
    {
        var choice = node?.choices?.Find(c => c != null && c.target == target);
        return choice != null && choice.patience_delta <= -2;
    }
    private static string RunnerUp(GatekeeperDecisionClient.ChoiceReply reply)
    {
        if (reply.options == null || reply.probabilities == null || reply.options.Length != reply.probabilities.Length) return "";
        int next = -1;
        for (int i = 0; i < reply.options.Length; i++)
            if (reply.options[i] != reply.choice && (next < 0 || reply.probabilities[i] > reply.probabilities[next])) next = i;
        return next < 0 ? "" : " (next " + reply.options[next] + " " + P(reply.probabilities[next]) + ")";
    }
    private static string P(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
