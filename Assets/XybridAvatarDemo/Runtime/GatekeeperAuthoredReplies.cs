using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// d1 selects closed labels; the game selects words and owns every effect. No writer model is loaded here.
// Wording follows the November blind reading, including the sharper generated replies Glenn preferred.
public static class GatekeeperAuthoredReplies
{
    private static readonly Dictionary<GatekeeperTopic, string[]> Topics = new Dictionary<GatekeeperTopic, string[]>
    {
        [GatekeeperTopic.Identity] = new[] { "I'm Trola. I built this wall, and I guard this gate." },
        [GatekeeperTopic.Wall] = new[] {
            "I built every stone of that wall myself, and nothing's come over it since.",
            "Those stones are my work. The captain can keep his orders, that wall's mine." },
        [GatekeeperTopic.WallMotive] = new[] { "I'm a wall guardian, not your friend. Keeping trouble outside is what I do." },
        [GatekeeperTopic.Job] = new[] {
            "It's honest work, keeping trouble outside, and somebody has to do it properly.",
            "I keep trouble outside the camp. It's a living, and I'm good at it." },
        [GatekeeperTopic.DayOff] = new[] { "A day off? The gate doesn't guard itself, and the captain wouldn't know how." },
        [GatekeeperTopic.Food] = new[] {
            "Supper's burnt stew again, the cook calls it seasoning, I call it a weapon.",
            "The stew's so bad the rats bring their own. I'd stick to your own provisions." },
        [GatekeeperTopic.Beds] = new[] { "The beds are cold and lumpy, I'd sooner sleep against my wall." },
        [GatekeeperTopic.Captain] = new[] {
            "The captain keeps the keys and does the shouting, and he still buys me helmets that never fit.",
            "He's in charge. I'm the one keeping the gate standing while he shouts about it." },
        [GatekeeperTopic.Camp] = new[] { "Cold beds, burnt stew and a captain who never stops shouting. Excellent wall, though, I built it." },
        [GatekeeperTopic.Lonely] = new[] { "Lonely? I've got the wall, the fire and a camp full of fools, that's company enough." },
        [GatekeeperTopic.Self] = new[] { "I keep my past to myself. My wall and my work are what you need to know about." },
        [GatekeeperTopic.Beyond] = new[] { "Nothing but the trees. Get out of my way or get out now." },
        [GatekeeperTopic.Fire] = new[] {
            "You can sit, but keep your turnips out of my fire, or I'll let the cold bits freeze you.",
            "Sit if you like. Just don't mistake a warm fire for a soft gatekeeper." },
        [GatekeeperTopic.Humor] = new[] { "Funny? The cook calls his stew food, that's the best joke in this camp." },
        [GatekeeperTopic.Trade] = new[] { "I'm the gatekeeper, not a merchant. Ask in camp, and keep your coin in your purse." },
        [GatekeeperTopic.Player] = new[] { "I'll remember you, {name}. Hard to forget a traveller who talks this much." },
        [GatekeeperTopic.Other] = new[] { "What are you asking of me? Speak plainly." }
    };

    public static GatekeeperScenario.Line Reply(GatekeeperConversationSnapshot state, GatekeeperDecision decision,
        GatekeeperResponsePlan plan, string message)
    {
        if (!plan.NeedsWriter) return plan.Assemble();
        var topic = decision?.Topic ?? GatekeeperTopic.Other;
        string text;
        switch (plan.Reaction)
        {
            case "accept-praise":
                text = topic == GatekeeperTopic.Wall || topic == GatekeeperTopic.WallMotive
                    ? "Hmph, you've got eyes at least, every stone of that wall is mine."
                    : "Hmph, you've got eyes at least. Somebody has to keep this camp in order.";
                break;
            case "rebuke":
                text = topic == GatekeeperTopic.Wall || topic == GatekeeperTopic.WallMotive
                    ? "Pathetic, is it? It's kept out bigger fools than you, so mind your tongue at my gate."
                    : "Hmph, I've heard worse from smaller fools. Mind your tongue at my gate.";
                break;
            case "reassure":
                text = state.Outcome == GatekeeperOutcome.Passed || state.Outcome == GatekeeperOutcome.Liked
                    ? "Hurt you? You're through already, I only bite the ones who climb my wall."
                    : "Calm down, I'm guarding a gate. Keep your hands where I can see them.";
                break;
            case "accept-apology":
                text = Has(message, @"\bwall\b") ? "Hmph. Waste of time."
                    : "Hmph, fine, apology taken, just leave my wall out of it next time.";
                break;
            default:
                string last = LastReply(state);
                if (topic == GatekeeperTopic.Repeat)
                    text = string.IsNullOrEmpty(last) ? "I've nothing to repeat yet. What do you want to know?"
                        : "I said, " + (last.StartsWith("I said, ", StringComparison.Ordinal) ? last.Substring(8) : last);
                else if (topic == GatekeeperTopic.Food && Has(last, @"\bstew\b") && Has(message, @"\b(?:really|that bad|worse)\b"))
                    text = "Worse, the rats won't touch it, and they'll eat my boots.";
                else if (topic == GatekeeperTopic.Trade && Has(message, @"\bturnips?\b"))
                    text = "A turnip? Keep it, I've had enough of the cook's turnip stew to last a winter.";
                else if (topic == GatekeeperTopic.Fire && state.Outcome != GatekeeperOutcome.Passed && state.Outcome != GatekeeperOutcome.Liked)
                    text = "Don't take a warm fire for permission to pass. There's still a gate between us.";
                else
                    text = Pick(state, Topics.TryGetValue(topic, out var lines) ? lines : Topics[GatekeeperTopic.Other]);
                break;
        }
        text = GatekeeperNames.Resolve(text, state.Name, GatekeeperNames.Mangle(state.Name), state.Business);
        return plan.Assemble(text);
    }

    private static string Pick(GatekeeperConversationSnapshot state, string[] choices) =>
        choices.FirstOrDefault(text => !state.History.Any(turn => turn == "Troll: " + text || turn == "Trola: " + text))
        ?? choices[(state.History.Count / 2) % choices.Length];
    private static string LastReply(GatekeeperConversationSnapshot state)
    {
        for (int i = state.History.Count - 1; i >= 0; i--)
            foreach (string prefix in new[] { "Troll: ", "Trola: " })
                if (state.History[i].StartsWith(prefix, StringComparison.Ordinal)) return state.History[i].Substring(prefix.Length);
        return "";
    }
    private static bool Has(string text, string pattern) => Regex.IsMatch(text ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Narrow, neutral topic fallback when d1 is unavailable. It never guesses patience changes or new facts.
    public static GatekeeperDecision Offline(string message)
    {
        var topic = Has(message, @"\b(?:repeat|say that again|what did you (?:just )?say)\b") ? GatekeeperTopic.Repeat
            : Has(message, @"\b(?:stew|food|supper|cook)\b") ? GatekeeperTopic.Food
            : Has(message, @"\b(?:beds?|sleep|rest)\b") ? GatekeeperTopic.Beds
            : Has(message, @"\b(?:captain|in charge)\b") ? GatekeeperTopic.Captain
            : Has(message, @"\bday off\b") ? GatekeeperTopic.DayOff
            : Has(message, @"\blonely\b") ? GatekeeperTopic.Lonely
            : Has(message, @"\b(?:wall|gate)\b") ? GatekeeperTopic.Wall
            : GatekeeperTopic.Other;
        return new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.Other, topic: topic);
    }
}
