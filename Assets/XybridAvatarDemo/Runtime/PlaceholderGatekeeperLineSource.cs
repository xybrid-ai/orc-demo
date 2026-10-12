using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public sealed class PlaceholderGatekeeperLineSource : IGatekeeperLineSource
{
    private int replyIndex, idleIndex;
    private bool greetingDelivered, ended;
    public GatekeeperPresentation Presentation { get; private set; } = Prompt();
    public IEnumerable<GatekeeperScenario.Line> AnticipatedLines => Array.Empty<GatekeeperScenario.Line>();
    private static GatekeeperPresentation Prompt() => new GatekeeperPresentation
    {
        InputSlot = GatekeeperInputSlot.Reply, Placeholder = "Say anything", CharacterLimit = 240,
        PersistentText = true, PointerInterrupts = false, ShowInterruptButton = false
    };
    public async Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState state, GatekeeperPlayerInput input, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Presentation = Prompt();
        if (ended) { Presentation.Ended = true; Presentation.InputSlot = GatekeeperInputSlot.None; return null; }
        if (!greetingDelivered && input.Kind == GatekeeperInputKind.Start)
        {
            greetingDelivered = true;
            return Line("free-welcome", "Gate's open. Now, what else do you want?", "interest", .5f);
        }
        if (input.Kind == GatekeeperInputKind.Text)
        {
            state.Record("Player", input.Text);
            state.Encounter.CountStoryTurn();
            var line = await GenerateReplyAsync(state.Snapshot(), input.Text, cancellation);
            cancellation.ThrowIfCancellationRequested();
            GatekeeperLineSources.ValidateOutput(line);
            if (line.ends_scene) { ended = true; Presentation.Ended = true; Presentation.InputSlot = GatekeeperInputSlot.None; }
            return line;
        }
        if (input.Kind == GatekeeperInputKind.Idle)
        {
            Presentation.IsReaction = true;
            return Line("free-idle-" + idleIndex, idleIndex++ % 2 == 0 ? "Go on. You finally have an audience." : "Don't waste the quiet. The captain will be back.", "impatience", .4f);
        }
        if (input.Kind == GatekeeperInputKind.Interrupt)
        {
            Presentation.IsReaction = Presentation.ProtectedReaction = true;
            return Line("free-interrupted", "Oi. One thought at a time.", "annoyance", .6f, -1);
        }
        return null;
    }
    private Task<GatekeeperScenario.Line> GenerateReplyAsync(GatekeeperConversationSnapshot state, string latestInput, CancellationToken cancellation)
    {
        // MODEL INTEGRATION POINT: replace this method with the model call.
        // Use immutable state + latestInput; return the same Line and honor cancellation.
        // Voice, subtitles, lip cues, UI, patience and gate code stay unchanged.
        cancellation.ThrowIfCancellationRequested();
        int index = replyIndex++ % 4;
        var lines = new[]
        {
            Line("free-reply-0", "Mm. Go on. I'm listening.", "interest", .45f),
            Line("free-reply-1", "Ha! Better company than the captain.", "amusement", .65f, 1),
            Line("free-reply-2", "Careful. I still have a gate to guard.", "suspicion", .55f, -1),
            Line("free-reply-3", "Tell me more. Keep the heroic bits short.", "interest", .5f)
        };
        return Task.FromResult(lines[index]);
    }
    private static GatekeeperScenario.Line Line(string id, string text, string emotion, float intensity, int delta = 0) => new GatekeeperScenario.Line
    { id = id, text = text, emotion = emotion, intensity = intensity, patience_delta = delta, ends_scene = false, delivery = "" };
}
