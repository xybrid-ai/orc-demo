using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Compatibility adapter for the earlier intent/name demo; no performer branches.
internal sealed class IntentGatekeeperLineSource : IGatekeeperLineSource
{
    private readonly GatekeeperScenario scenario;
    private GatekeeperConversationState state;
    private bool awaitingName;
    public GatekeeperPresentation Presentation { get; private set; } = new GatekeeperPresentation();
    public IntentGatekeeperLineSource(GatekeeperScenario scenario) { this.scenario = scenario; }
    public IEnumerable<GatekeeperScenario.Line> AnticipatedLines
    {
        get
        {
            if (state == null || string.IsNullOrEmpty(state.Encounter.PlayerName)) yield break;
            foreach (var line in scenario.lines)
                if (GatekeeperNames.HasSlots(line.text)) yield return GatekeeperLineSources.Resolve(line, state.Encounter);
        }
    }
    public Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState state, GatekeeperPlayerInput input, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); this.state = state;
        string id = null;
        if (input.Kind == GatekeeperInputKind.Start) { awaitingName = scenario.captureName; id = awaitingName ? "name-demand" : "intro"; }
        else if (input.Kind == GatekeeperInputKind.Text)
        {
            state.Record("Player", input.Text);
            if (awaitingName)
            {
                state.Encounter.CaptureName(input.Text, scenario.nameBlocklist, scenario.fallbackNames[state.AssignedNameIndex % scenario.fallbackNames.Length]);
                if (state.Encounter.NameWasAssigned) state.AssignedNameIndex++;
                awaitingName = false; id = state.Encounter.NameWasAssigned ? "name-assigned" : "intro";
            }
            else
            {
                if (!GatekeeperIntentRules.TryInterpret(input.Text, out var intent)) throw new ArgumentException("Provide a valid reply.");
                id = state.Encounter.Respond(intent)?.lineId;
            }
        }
        else if (input.Kind == GatekeeperInputKind.Choice)
        {
            string reply = state.Encounter.HasPendingOffer ? input.Choice == 0 ? "Here you go!" : "Never mind." : scenario.choices[input.Choice].reply;
            return NextLineAsync(state, new GatekeeperPlayerInput(GatekeeperInputKind.Text, reply), cancellation);
        }
        Presentation = new GatekeeperPresentation
        { IdleSeconds = 0, CanInterrupt = false, InterruptLabel = "Skip", PointerInterrupts = false, LineGapSeconds = 0, WaitForPrefetch = scenario.captureName };
        if (id != null)
        {
            var line = GatekeeperLineSources.Resolve(scenario.FindLine(id), state.Encounter, id == "name-demand");
            line.emotion = string.IsNullOrWhiteSpace(line.emotion) ? state.Encounter.Mood.ToString().ToLowerInvariant() : line.emotion;
            line.intensity = .5f; line.ends_scene = state.Encounter.Outcome != GatekeeperOutcome.Playing;
            return Task.FromResult(line);
        }
        Presentation.Ended = state.Encounter.Outcome != GatekeeperOutcome.Playing;
        state.GateOpenRequested = state.Encounter.Outcome == GatekeeperOutcome.Passed;
        if (awaitingName) { Presentation.InputSlot = GatekeeperInputSlot.Name; Presentation.CharacterLimit = 16; Presentation.Placeholder = "Your name"; }
        else if (!Presentation.Ended && !scenario.captureName) { Presentation.InputSlot = GatekeeperInputSlot.Reply; Presentation.Placeholder = "What do you say?"; }
        else if (!Presentation.Ended && state.Encounter.HasPendingOffer) Presentation.Choices = new[] { new GatekeeperChoice("Pay the toll"), new GatekeeperChoice("Walk away") };
        else if (!Presentation.Ended) Presentation.Choices = scenario.choices.ConvertAll(choice => new GatekeeperChoice(choice.label)).AsReadOnly();
        return Task.FromResult<GatekeeperScenario.Line>(null);
    }
}
