using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// The composition root is the only place selecting or switching concrete sources.
public static class GatekeeperLineSources
{
    public static bool ValidateScenario(GatekeeperScenario scenario, out string error)
    {
        if (!scenario.debugStartInFreeForm) return scenario.Validate(out error);
        foreach (GatekeeperMood mood in Enum.GetValues(typeof(GatekeeperMood)))
            if (scenario.Voice(mood) == null) { error = "Provide all existing voice profiles."; return false; }
        error = null; return true;
    }
    public static IGatekeeperLineSource Create(GatekeeperScenario scenario, int assignedNameIndex, out GatekeeperConversationState state)
    {
        bool bounded = scenario.authoredConversation || scenario.debugStartInFreeForm;
        state = new GatekeeperConversationState(new GatekeeperEncounter(bounded ? 3 : scenario.initialPatience, bounded ? 5 : scenario.maxTurns, bounded), assignedNameIndex);
        var model = scenario.debugUsePlaceholderReplies ? null : GatekeeperNpcModel.Prepare(scenario);
        if (scenario.debugStartInFreeForm) { state.GateOpenRequested = true; return FreeForm(scenario, model); }
        IGatekeeperLineSource first = scenario.authoredConversation
            ? new AuthoredGatekeeperLineSource(scenario, state)
            : new IntentGatekeeperLineSource(scenario);
        return new Session(first, scenario, model);
    }
    private sealed class Session : IGatekeeperLineSource
    {
        private IGatekeeperLineSource active;
        private readonly GatekeeperScenario scenario;
        private readonly GatekeeperNpcModel model;
        public Session(IGatekeeperLineSource first, GatekeeperScenario scenario, GatekeeperNpcModel model) { active = first; this.scenario = scenario; this.model = model; }
        public GatekeeperPresentation Presentation => active.Presentation;
        public IEnumerable<GatekeeperScenario.Line> AnticipatedLines => active.AnticipatedLines;
        public Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState state, GatekeeperPlayerInput input, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (input.Kind == GatekeeperInputKind.GateOpened && active.Presentation.AwaitGate)
            {
                active = FreeForm(scenario, model);
                input = new GatekeeperPlayerInput(GatekeeperInputKind.Start);
            }
            return active.NextLineAsync(state, input, cancellation);
        }
    }
    private static IGatekeeperLineSource FreeForm(GatekeeperScenario scenario, GatekeeperNpcModel model) => scenario.debugUsePlaceholderReplies
        ? (IGatekeeperLineSource)new PlaceholderGatekeeperLineSource() : new FreeFormGatekeeperLineSource(scenario, model);
    public static GatekeeperScenario.Line Resolve(GatekeeperScenario.Line original, GatekeeperEncounter encounter, bool forceLive = false)
    {
        bool live = forceLive || GatekeeperNames.HasSlots(original.text);
        return new GatekeeperScenario.Line
        {
            id = original.id, text = encounter.Resolve(original.text), emotion = original.emotion,
            intensity = original.intensity, patience_delta = original.patience_delta, ends_scene = original.ends_scene,
            recordedMood = original.recordedMood, delivery = original.delivery,
            audio = live ? null : original.audio, mouthCues = live ? null : original.mouthCues
        };
    }
    public static void ValidateOutput(GatekeeperScenario.Line line)
    {
        if (line == null || string.IsNullOrWhiteSpace(line.text) || string.IsNullOrWhiteSpace(line.emotion)
            || float.IsNaN(line.intensity) || float.IsInfinity(line.intensity) || line.intensity < 0 || line.intensity > 1 || line.patience_delta < -5 || line.patience_delta > 5)
            throw new InvalidOperationException("Line source returned invalid text, emotion, intensity or patience_delta.");
    }
}
