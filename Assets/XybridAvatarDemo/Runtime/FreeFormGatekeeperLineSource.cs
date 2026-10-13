using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class FreeFormGatekeeperLineSource : IGatekeeperLineSource
{
    private readonly GatekeeperScenario scenario;
    private readonly GatekeeperNpcModel model;
    private readonly string[] expulsion;
    private int fallbackIndex, idleIndex, expulsionIndex;
    private readonly bool debugImmediate;
    private bool greetingDelivered, ended, expelling;
    public GatekeeperPresentation Presentation { get; private set; } = Prompt();
    public IEnumerable<GatekeeperScenario.Line> AnticipatedLines => Array.Empty<GatekeeperScenario.Line>();
    public FreeFormGatekeeperLineSource() : this(UnityEngine.Object.FindFirstObjectByType<GatekeeperController>().scenario) { }
    private FreeFormGatekeeperLineSource(GatekeeperScenario scenario) : this(scenario, GatekeeperNpcModel.Prepare(scenario)) { }
    public FreeFormGatekeeperLineSource(GatekeeperScenario scenario, GatekeeperNpcModel model, bool skipGreeting = false)
    {
        this.scenario = scenario; this.model = model;
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
        // Check the patience applied by the performer, including the last model
        // reply, before accepting another input. Play the complete authored ending.
        if (expelling || state.Encounter.Patience <= 0)
        {
            expelling = true; Presentation.InputSlot = GatekeeperInputSlot.None; Presentation.PersistentText = false;
            if (expulsionIndex < expulsion.Length)
            {
                var line = GatekeeperLineSources.Resolve(scenario.FindLine(expulsion[expulsionIndex++]), state.Encounter);
                line.patience_delta = 0;
                return line;
            }
            state.Encounter.FinishStory(GatekeeperOutcome.Refused);
            state.GateOpenRequested = false;
            ended = true;
        }
        if (ended) { Presentation.Ended = true; Presentation.InputSlot = GatekeeperInputSlot.None; return null; }
        if (!greetingDelivered && input.Kind == GatekeeperInputKind.Start)
        {
            greetingDelivered = true;
            return Line("free-welcome", "Gate's open. Now, what else do you want?", "interest", .5f);
        }
        if (input.Kind == GatekeeperInputKind.Text)
        {
            state.Encounter.CountStoryTurn();
            var snapshot = state.Snapshot(); // Existing exchanges, excluding this new message.
            state.Record("Player", input.Text);
            var reply = await model.ReplyAsync(snapshot, input.Text, cancellation);
            cancellation.ThrowIfCancellationRequested();
            bool fallback = reply == null;
            reply = reply ?? Fallback();
            reply.debugSource = fallback ? "LLM fallback" : "LLM";
            reply.debugRaw = model.LastRawReply ?? "";
            reply.debugModelSeconds = model.LastReplySeconds;
            reply.patience_delta = Math.Max(-1, Math.Min(1, reply.patience_delta));
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
    private GatekeeperScenario.Line Fallback()
    {
        // Generic authored replies also back the original placeholder source.
        string[] replies = { "Mm. Go on. I'm listening.", "Careful. I still have a gate to guard.", "Tell me more. Keep the heroic bits short." };
        return Line("free-fallback", replies[fallbackIndex++ % replies.Length], "interest", .45f);
    }
    private static GatekeeperScenario.Line Line(string id, string text, string emotion, float intensity, int delta = 0) => new GatekeeperScenario.Line
    { id = id, text = text, emotion = emotion, intensity = intensity, patience_delta = delta, ends_scene = false, delivery = "" };
}
