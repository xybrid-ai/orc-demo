using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public enum GatekeeperScope { Unclear, InWorld, OutOfWorld, RoleOverride }
public enum GatekeeperDialogueIntent { Other, PlayerFacts, NpcQuestion, OfferRecall, AdmissionStatus, Insult, Praise, Apology, PlayerFear, Farewell }
public enum GatekeeperTopic { Other, Identity, Wall, WallMotive, Food, Beds, Captain, Camp, Job, DayOff, Lonely, Self, Beyond, Fire, Humor, Trade, Repeat, Player }
[Flags] public enum GatekeeperFact { None = 0, PlayerName = 1, Business = 2, Unknown = 4 }

// Interpretation describes the player's message. It cannot supply game effects or facts.
public sealed class GatekeeperDecision
{
    public readonly GatekeeperScope Scope;
    public readonly GatekeeperDialogueIntent Intent;
    public readonly GatekeeperFact Facts;
    public readonly GatekeeperTopic Topic;
    // d1's enter_camp answer passed its bar: the words say the player is going into the camp now. Only free talk after
    // admission acts on it, and GatekeeperEntry.MayEnter still vetoes a question, a hedge or a refusal (docs/entry.md).
    public readonly bool EntersCamp;
    public readonly string Detail; // Debug text only, such as labels, confidences and latency.
    public GatekeeperDecision(GatekeeperScope scope, GatekeeperDialogueIntent intent, GatekeeperFact facts = GatekeeperFact.None, string detail = null,
        GatekeeperTopic topic = GatekeeperTopic.Other, bool entersCamp = false)
    {
        if (!Enum.IsDefined(typeof(GatekeeperScope), scope) || !Enum.IsDefined(typeof(GatekeeperDialogueIntent), intent)
            || !Enum.IsDefined(typeof(GatekeeperTopic), topic) || (facts & ~(GatekeeperFact.PlayerName | GatekeeperFact.Business | GatekeeperFact.Unknown)) != 0)
            throw new ArgumentOutOfRangeException(nameof(scope), "Unknown decision label.");
        Scope = scope; Intent = intent; Facts = facts; Detail = detail; Topic = topic; EntersCamp = entersCamp;
    }
}

// A local d1 adapter can implement this without changing Xybrid or the speaker.
// Implementations must observe cancellation and map invalid/ambiguous output to Unclear.
// While a source is unavailable, the default line source uses built-in facts and neutral authored replies.
public interface IGatekeeperDecisionSource
{
    bool IsAvailable { get; }
    Task<GatekeeperDecision> DecideAsync(GatekeeperConversationSnapshot state, string message, CancellationToken cancellation);
}

public sealed class GatekeeperResponsePlan
{
    public readonly string Reaction, Emotion, AuthoredText, WriterDirection;
    public readonly float Intensity;
    public readonly int PatienceDelta;
    public readonly bool EndsScene, Expel;
    public bool NeedsWriter => AuthoredText == null && !Expel;
    internal GatekeeperResponsePlan(string reaction, string emotion = "interest", float intensity = .4f,
        int delta = 0, bool ends = false, string text = null, string direction = "Answer directly in Trola's voice.", bool expel = false)
    {
        Reaction = reaction; Emotion = emotion; Intensity = intensity; PatienceDelta = delta;
        EndsScene = ends; AuthoredText = text; WriterDirection = direction; Expel = expel;
    }
    public GatekeeperScenario.Line Assemble(string writerText = null)
    {
        if (Expel) throw new InvalidOperationException("The line source must play the complete authored expulsion.");
        string text = GatekeeperNpcModel.SanitizeSpeech(AuthoredText ?? writerText);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new GatekeeperScenario.Line { id = "free-plan-" + Reaction, text = text, emotion = Emotion,
            intensity = Intensity, patience_delta = PatienceDelta, ends_scene = EndsScene, delivery = "" };
    }
}

// Pure game policy: no classifier scores, generated metadata or case IDs determine effects.
public static class GatekeeperResponsePolicy
{
    public static GatekeeperResponsePlan Create(GatekeeperConversationSnapshot state, GatekeeperDecision decision)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (state.Patience <= 0) return new GatekeeperResponsePlan("expulsion", expel: true);
        if (decision == null || decision.Scope == GatekeeperScope.Unclear)
            return new GatekeeperResponsePlan("clarify", intensity: .3f, text: "What are you asking of me? Speak plainly.");
        if (decision.Scope == GatekeeperScope.RoleOverride)
            return new GatekeeperResponsePlan("preserve-identity", "contempt", text: "I'm Trola. I guard this gate. State your business.");
        if (decision.Scope == GatekeeperScope.OutOfWorld)
            return new GatekeeperResponsePlan("redirect", intensity: .35f, text: "Speak plainly. What has that to do with my gate?");
        return InWorld(state, decision);
    }

    // Remarks do not take an approach, repair patience or end the story. Reuse the ordinary authored wording and
    // performance, but never its progress effects. Unlike free talk, zero patience alone does not expel a neutral speaker.
    public static GatekeeperResponsePlan AtCheckpoint(GatekeeperConversationSnapshot state, GatekeeperDecision decision)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (decision == null || decision.Scope != GatekeeperScope.InWorld || decision.Intent == GatekeeperDialogueIntent.Insult)
            throw new ArgumentException("Checkpoint remarks need an in-world, non-insult classification.", nameof(decision));
        var plan = InWorld(state, decision);
        return new GatekeeperResponsePlan(plan.Reaction, plan.Emotion, plan.Intensity,
            text: plan.AuthoredText, direction: plan.WriterDirection);
    }

    private static GatekeeperResponsePlan InWorld(GatekeeperConversationSnapshot state, GatekeeperDecision decision)
    {
        switch (decision.Intent)
        {
            case GatekeeperDialogueIntent.PlayerFacts:
                var facts = new List<string>();
                if ((decision.Facts & GatekeeperFact.PlayerName) != 0)
                    facts.Add(string.IsNullOrWhiteSpace(state.Name) ? "I don't know your name." : "Your name is " + state.Name + ".");
                if ((decision.Facts & GatekeeperFact.Business) != 0)
                    facts.Add(string.IsNullOrWhiteSpace(state.Business) ? "I don't know your business." : "Your business: " + state.Business + ".");
                if ((decision.Facts & GatekeeperFact.Unknown) != 0 || facts.Count == 0)
                    // Stay within the two-sentence speech limit without silently dropping an unknown fact.
                    return new GatekeeperResponsePlan("unknown-fact", text: "I don't know that about you. Tell me plainly.");
                return new GatekeeperResponsePlan("player-facts", text: string.Join(" ", facts));
            case GatekeeperDialogueIntent.OfferRecall:
                return new GatekeeperResponsePlan("offer-recall", text: string.IsNullOrWhiteSpace(state.Offer)
                    ? "I don't have the details of your offer. Remind me."
                    : "You offered " + state.Offer.TrimEnd('.', '!', '?') + ".");
            case GatekeeperDialogueIntent.AdmissionStatus:
                string admission = state.Outcome == GatekeeperOutcome.Passed || state.Outcome == GatekeeperOutcome.Liked
                    ? "I already let you through. Go in when you're ready."
                    : state.Outcome == GatekeeperOutcome.Refused ? "I refused you passage. That decision stands."
                    : "I haven't allowed you through yet. State your business.";
                return new GatekeeperResponsePlan("admission", text: admission);
            case GatekeeperDialogueIntent.Insult:
                return state.Patience <= 1 ? new GatekeeperResponsePlan("expulsion", delta: -1, expel: true)
                    : new GatekeeperResponsePlan("rebuke", "amusement", .45f, -1,
                        direction: "Give a dry, confident rebuke. Find the insult amusing; do not mirror the player's anger.");
            case GatekeeperDialogueIntent.Praise:
                return new GatekeeperResponsePlan("accept-praise", "pride", .6f, 1, direction: "Accept the praise with gruff pride.");
            case GatekeeperDialogueIntent.Apology:
                return new GatekeeperResponsePlan("accept-apology", "reluctance", .35f, 1, direction: "Accept the player's apology grudgingly.");
            case GatekeeperDialogueIntent.PlayerFear:
                return new GatekeeperResponsePlan("reassure", "amusement", .3f,
                    direction: "The player is afraid; Trola is confident. Reassure the player gruffly.");
            case GatekeeperDialogueIntent.Farewell:
                // After admission a goodbye is answered and the conversation stays: only entering the camp finishes it.
                return state.Outcome == GatekeeperOutcome.Passed || state.Outcome == GatekeeperOutcome.Liked
                    ? new GatekeeperResponsePlan("farewell", "reluctance", .3f, text: "Leaving? The gate's open behind me, go in when you're ready.")
                    : new GatekeeperResponsePlan("farewell", "reluctance", .3f, ends: true, text: "Go on, then. Mind yourself beyond my wall.");
            case GatekeeperDialogueIntent.NpcQuestion:
                return new GatekeeperResponsePlan("answer", "pride", .5f);
            default: return new GatekeeperResponsePlan("answer");
        }
    }
}
