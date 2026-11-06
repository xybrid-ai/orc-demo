using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Content and interaction contract. No scene components, node IDs or source modes.
public interface IGatekeeperLineSource
{
    Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState state, GatekeeperPlayerInput input, CancellationToken cancellation);
    GatekeeperPresentation Presentation { get; }
    IEnumerable<GatekeeperScenario.Line> AnticipatedLines { get; }
}

// Spoken at a choice checkpoint (PlayerVoiceInput, d1 POST /choose) without picking a button: OffTopic is an in-world
// insult, OutOfWorld an unrelated task or role override; both cost one patience, and with none
// left Trola throws the player out. Unclear (a mumble, a near tie) asks again for free. Only OffTopic may be worded
// by the optional writer; the others are always authored. Neutral carries a trusted classification and reuses the
// authored reply bank at the same checkpoint with zero patience/progress, including incidental praise or apology.
public enum GatekeeperInputKind { Start, Continue, Text, Choice, Idle, Interrupt, GateOpened, Skip, OffTopic, OutOfWorld, Unclear, Neutral }
public enum GatekeeperInputSlot { None, Name, Business, Reply }
public readonly struct GatekeeperPlayerInput
{
    public readonly GatekeeperInputKind Kind;
    public readonly string Text;
    public readonly int Choice;
    public readonly GatekeeperDecision Decision;
    public GatekeeperPlayerInput(GatekeeperInputKind kind, string text = null, int choice = -1, GatekeeperDecision decision = null)
    { Kind = kind; Text = text; Choice = choice; Decision = decision; }
}
public sealed class GatekeeperChoice
{
    public readonly string label, say;
    public GatekeeperChoice(string label, string say = null) { this.label = label; this.say = say; }
}
public sealed class GatekeeperPresentation
{
    public GatekeeperInputSlot InputSlot;
    public string Placeholder = "";
    public int CharacterLimit = 240;
    public IReadOnlyList<GatekeeperChoice> Choices = Array.Empty<GatekeeperChoice>();
    public bool PersistentText, Ended, AwaitGate;
    public bool CanInterrupt = true, PointerInterrupts = true, ShowInterruptButton = true;
    public bool IsReaction, ProtectedReaction, WaitForPrefetch;
    // An idle taunt at this checkpoint keeps its choices on screen and clickable; a click during it is the choice.
    public bool TauntKeepsChoices;
    public string InterruptLabel = "Interrupt";
    public float IdleSeconds = 8, LineGapSeconds = .28f;
}
public sealed class GatekeeperConversationState
{
    public GatekeeperEncounter Encounter { get; }
    public bool GateOpenRequested { get; internal set; }
    public int AssignedNameIndex { get; internal set; }
    private readonly List<string> approaches = new List<string>();
    public void RecordApproach(string approach) { if (!approaches.Contains(approach)) approaches.Add(approach); }
    internal string[] Approaches => approaches.ToArray();
    private readonly List<string> history = new List<string>();
    private string offer = "";
    // Only trusted game actions may record an offer; a bribe approach contains no amount.
    public void RecordOffer(string description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 80
            || GatekeeperNpcModel.SanitizeSpeech(description) != description)
            throw new ArgumentException("Provide a short, speech-safe offer from game state.", nameof(description));
        offer = description;
    }
    internal string Offer => offer;
    public GatekeeperConversationState(GatekeeperEncounter encounter, int assignedNameIndex = 0) { Encounter = encounter; AssignedNameIndex = assignedNameIndex; }
    public void Record(string speaker, string text)
    {
        history.Add(speaker + ": " + text);
        if (history.Count > 128) history.RemoveAt(0);
    }
    public GatekeeperConversationSnapshot Snapshot() => new GatekeeperConversationSnapshot(this, history.ToArray());
}
// Immutable context for an asynchronous model call; safe to retain after restart.
public sealed class GatekeeperConversationSnapshot
{
    public readonly string Name, MangledName, Business, Offer;
    public readonly int Patience, Turns;
    public readonly GatekeeperOutcome Outcome;
    public readonly IReadOnlyList<string> History, Approaches;
    internal GatekeeperConversationSnapshot(GatekeeperConversationState state, string[] history)
    {
        var encounter = state.Encounter;
        Name = encounter.PlayerName; MangledName = encounter.MangledName; Business = encounter.Business;
        Offer = state.Offer;
        Patience = encounter.Patience; Turns = encounter.Turns; Outcome = encounter.Outcome; History = Array.AsReadOnly(history); Approaches = Array.AsReadOnly(state.Approaches);
    }
}
