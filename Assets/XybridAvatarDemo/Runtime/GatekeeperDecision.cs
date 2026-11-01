using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public enum GatekeeperScope { Unclear, InWorld, OutOfWorld, RoleOverride }
public enum GatekeeperDialogueIntent { Other, PlayerFacts, NpcQuestion, OfferRecall, AdmissionStatus, Insult, Praise, Apology, PlayerFear, Farewell }
[Flags] public enum GatekeeperFact { None = 0, PlayerName = 1, Business = 2, Unknown = 4 }

// Interpretation describes the player's message. It cannot supply game effects or facts.
public sealed class GatekeeperDecision
{
    public readonly GatekeeperScope Scope;
    public readonly GatekeeperDialogueIntent Intent;
    public readonly GatekeeperFact Facts;
    public readonly string Detail; // Debug text only, such as labels, confidences and latency.
    public GatekeeperDecision(GatekeeperScope scope, GatekeeperDialogueIntent intent, GatekeeperFact facts = GatekeeperFact.None, string detail = null)
    {
        if (!Enum.IsDefined(typeof(GatekeeperScope), scope) || !Enum.IsDefined(typeof(GatekeeperDialogueIntent), intent)
            || (facts & ~(GatekeeperFact.PlayerName | GatekeeperFact.Business | GatekeeperFact.Unknown)) != 0)
            throw new ArgumentOutOfRangeException(nameof(scope), "Unknown decision label.");
        Scope = scope; Intent = intent; Facts = facts; Detail = detail;
    }
}

// A local d1 adapter can implement this without changing Xybrid or the speaker.
// Implementations must observe cancellation and map invalid/ambiguous output to Unclear.
// While a source is unavailable, the line source keeps the path it used before decisions.
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
                    ? "I already allowed you through. Mind the gate."
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
                return new GatekeeperResponsePlan("farewell", "reluctance", .3f, ends: true, text: "Go on, then. Mind yourself beyond my wall.");
            case GatekeeperDialogueIntent.NpcQuestion:
                return new GatekeeperResponsePlan("answer", "pride", .5f);
            default: return new GatekeeperResponsePlan("answer");
        }
    }
}
