using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class FreeFormGatekeeperLineSource : IGatekeeperLineSource
{
    private readonly GatekeeperScenario scenario;
    private readonly GatekeeperNpcModel model;
    private readonly IGatekeeperDecisionSource decisions;
    private readonly string[] expulsion;
    private int fallbackIndex, idleIndex, expulsionIndex;
    private readonly bool debugImmediate;
    private bool greetingDelivered, ended, expelling, entering;
    public GatekeeperPresentation Presentation { get; private set; } = Prompt();
    public IEnumerable<GatekeeperScenario.Line> AnticipatedLines => Array.Empty<GatekeeperScenario.Line>();
    public FreeFormGatekeeperLineSource() : this(UnityEngine.Object.FindFirstObjectByType<GatekeeperController>().scenario) { }
    private FreeFormGatekeeperLineSource(GatekeeperScenario scenario) : this(scenario, GatekeeperLineSources.PrepareWriter(scenario)) { }
    public FreeFormGatekeeperLineSource(GatekeeperScenario scenario, GatekeeperNpcModel model, bool skipGreeting = false, IGatekeeperDecisionSource decisions = null)
    {
        this.scenario = scenario; this.model = model; this.decisions = decisions;
        debugImmediate = skipGreeting; greetingDelivered = skipGreeting;
        Presentation = Prompt(debugImmediate ? 0 : 8);
        expulsion = scenario.ReadStory().nodes.Find(n => n.id == "expelled").variants[0].line_ids;
    }
    private static GatekeeperPresentation Prompt(float idleSeconds = 8) => new GatekeeperPresentation
    {
        InputSlot = GatekeeperInputSlot.Reply, Placeholder = "Say anything", CharacterLimit = 240,
        IdleSeconds = idleSeconds, PersistentText = true, PointerInterrupts = false, ShowInterruptButton = false
    };
    public async Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState state, GatekeeperPlayerInput input, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Presentation = Prompt(debugImmediate ? 0 : 8);
        // The send-off has been said: the player walks in (docs/entry.md).
        if (entering)
        {
            state.Encounter.EnterCamp();
            Presentation.InputSlot = GatekeeperInputSlot.None; Presentation.PersistentText = false;
            Presentation.Entering = Presentation.Ended = true;
            return null;
        }
        // Check the patience applied by the performer, including the last model
        // reply, before accepting another input. Play the complete authored ending.
        if (expelling || state.Encounter.Patience <= 0)
        {
            return ExpulsionLine(state);
        }
        if (ended)
        {
            state.Encounter.End(GatekeeperEnding.Departed);
            Presentation.Ended = true; Presentation.InputSlot = GatekeeperInputSlot.None; return null;
        }
        Presentation.CanEnter = state.Encounter.Admitted;
        if (!greetingDelivered && input.Kind == GatekeeperInputKind.Start)
        {
            greetingDelivered = true;
            // After admission the greeting says how to finish: go in, or talk on.
            return state.Encounter.Outcome == GatekeeperOutcome.Liked
                ? Line("free-welcome-liked", "Go in when you like. Or stay and talk, I've had worse company.", "fondness", .5f)
                : state.Encounter.Outcome == GatekeeperOutcome.Passed
                ? Line("free-welcome", "Gate's open. Go in when you're ready, or talk, if you must.", "interest", .5f)
                : Line("free-welcome", "Gate's open. Now, what else do you want?", "interest", .5f);
        }
        if (input.Kind == GatekeeperInputKind.Enter)
        {
            if (!CanEnter(state)) throw new InvalidOperationException("Entry needs admission and an open gate.");
            return SendOff(state, "Enter camp");
        }
        if (input.Kind == GatekeeperInputKind.Text)
        {
            state.Encounter.CountStoryTurn();
            var snapshot = state.Snapshot(); // Existing exchanges, excluding this new message.
            state.Record("Player", input.Text);
            // A clear "go in" needs no classifier; d1's enter_camp widens the phrasing below.
            if (CanEnter(state) && GatekeeperEntry.Clear(input.Text)) return SendOff(state, "entry: keywords");
            // Facts remain authoritative. Without d1, the default path uses neutral authored topic fallbacks.
            var source = decisions != null && decisions.IsAvailable ? decisions : null;
            GatekeeperDecision decision = GatekeeperFactQuestions.Interpret(input.Text);
            if (source != null)
            {
                try { decision = await source.DecideAsync(snapshot, input.Text, cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception) { decision = null; } // Adapter failure becomes a clarification, with no effects.
            }
            cancellation.ThrowIfCancellationRequested();
            if (source == null && decision == null && model == null) decision = GatekeeperAuthoredReplies.Offline(input.Text);
            var plan = source != null || decision != null ? GatekeeperResponsePolicy.Create(snapshot, decision) : null;
            string decided = source != null ? (decision == null ? "decision failed" : decision.Detail ?? "decision source")
                : (decisions != null ? "d1 offline; " : "") + (decision != null ? "built-in route" : "model line");
            if (CanEnter(state) && decision != null && decision.EntersCamp && decision.Scope == GatekeeperScope.InWorld
                && GatekeeperEntry.MayEnter(input.Text))
                return SendOff(state, "entry: " + decided);
            if (plan != null) decided += " → " + plan.Reaction;
            if (plan != null && plan.Expel)
            {
                var ending = ExpulsionLine(state, plan.PatienceDelta);
                if (ending != null) ending.debugDecision = decided;
                return ending;
            }
            bool authored = model == null || plan != null && !plan.NeedsWriter;
            var reply = authored ? GatekeeperAuthoredReplies.Reply(snapshot, decision, plan, input.Text)
                : await model.ReplyAsync(snapshot, input.Text, cancellation, plan);
            cancellation.ThrowIfCancellationRequested();
            bool fallback = reply == null;
            reply = reply ?? (plan == null ? Fallback() : plan.Assemble(Fallback().text));
            reply.debugSource = authored ? (plan.NeedsWriter ? "authored reply" : "response plan") : fallback ? "LLM fallback" : "LLM";
            reply.debugDecision = decided;
            reply.debugRaw = authored ? "" : model.LastRawReply ?? "";
            reply.debugModelSeconds = authored ? 0 : model.LastReplySeconds;
            reply.patience_delta = Math.Max(-1, Math.Min(1, reply.patience_delta));
            // After admission a goodbye gets its reply and the conversation stays: only entering the camp finishes it.
            if (state.Encounter.Admitted) reply.ends_scene = false;
            GatekeeperLineSources.ValidateOutput(reply);
            // Expulsion takes priority on the next Continue after patience is applied.
            if (reply.ends_scene && state.Encounter.Patience + reply.patience_delta > 0) ended = true;
            return reply;
        }
        if (input.Kind == GatekeeperInputKind.Idle)
        {
            Presentation.IsReaction = true;
            var ids = scenario.ReadStory().idle_lines;
            return GatekeeperLineSources.Resolve(scenario.FindLine(ids[idleIndex++ % ids.Length]), state.Encounter);
        }
        if (input.Kind == GatekeeperInputKind.Interrupt)
        {
            Presentation.IsReaction = Presentation.ProtectedReaction = true;
            return Line("free-interrupted", "Oi. One thought at a time.", "annoyance", .6f, -1);
        }
        return null;
    }
    private bool CanEnter(GatekeeperConversationState state) => state.Encounter.Admitted && state.GateOpen && !expelling && !ended && !entering;
    // Trola's one-line send-off; once it has been said the encounter ends Entered. Nothing is answered meanwhile, and it
    // is never interrupted.
    private GatekeeperScenario.Line SendOff(GatekeeperConversationState state, string decided)
    {
        entering = true;
        Presentation.InputSlot = GatekeeperInputSlot.None; Presentation.PersistentText = false; Presentation.IdleSeconds = 0;
        Presentation.CanEnter = false; Presentation.Entering = true;
        Presentation.CanInterrupt = false; Presentation.LineGapSeconds = 0;
        var line = state.Encounter.Outcome == GatekeeperOutcome.Liked
            ? Line("free-enter-liked", "Go on in, then. Keep my seat by the fire warm.", "fondness", .5f)
            : Line("free-enter", "In you go, then. Mind the stew.", "reluctance", .4f);
        line.debugSource = "authored send-off"; line.debugDecision = decided;
        return line;
    }
    private GatekeeperScenario.Line ExpulsionLine(GatekeeperConversationState state, int delta = 0)
    {
        expelling = true; Presentation.InputSlot = GatekeeperInputSlot.None; Presentation.PersistentText = false; Presentation.CanEnter = false;
        if (expulsionIndex < expulsion.Length)
        {
            var line = GatekeeperLineSources.Resolve(scenario.FindLine(expulsion[expulsionIndex++]), state.Encounter);
            line.patience_delta = delta;
            return line;
        }
        state.Encounter.FinishStory(GatekeeperOutcome.Refused, GatekeeperEnding.Expelled);
        state.GateOpenRequested = false;
        ended = Presentation.Ended = true;
        return null;
    }
    private GatekeeperScenario.Line Fallback()
    {
        // Generic authored replies also back the original placeholder source.
        string[] replies = { "Mm. Go on. I'm listening.", "Careful. I still have a gate to guard.", "Tell me more. Keep the heroic bits short." };
        return Line("free-fallback", replies[fallbackIndex++ % replies.Length], "interest", .45f);
    }
    private static GatekeeperScenario.Line Line(string id, string text, string emotion, float intensity, int delta = 0) => new GatekeeperScenario.Line
    { id = id, text = text, emotion = emotion, intensity = intensity, patience_delta = delta, ends_scene = false, delivery = "" };
}
