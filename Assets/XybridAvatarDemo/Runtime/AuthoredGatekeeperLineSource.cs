using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Owns every tree decision. The performer sees only resolved lines and prompts.
public sealed class AuthoredGatekeeperLineSource : IGatekeeperLineSource
{
    private readonly GatekeeperScenario scenario;
    private readonly GatekeeperConversationState state;
    private readonly GatekeeperStory story;
    private int lineIndex;
    private bool reaction;
    public GatekeeperPresentation Presentation { get; private set; } = new GatekeeperPresentation();
    public AuthoredGatekeeperLineSource(GatekeeperScenario scenario, GatekeeperConversationState state)
    {
        this.scenario = scenario; this.state = state; story = new GatekeeperStory(scenario, state.Encounter);
    }
    public IEnumerable<GatekeeperScenario.Line> AnticipatedLines
    {
        get
        {
            if (string.IsNullOrEmpty(state.Encounter.PlayerName)) yield break;
            foreach (var line in scenario.lines)
            {
                if (line.id == "named-1" && state.Encounter.NameWasAssigned || line.id == "assigned-reply" && !state.Encounter.NameWasAssigned) continue;
                if (GatekeeperNames.HasSlots(line.text) && (story.BusinessCaptured || !line.text.Contains("{business}")))
                    yield return GatekeeperLineSources.Resolve(line, state.Encounter);
            }
        }
    }
    public Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState context, GatekeeperPlayerInput input, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (context != state) throw new ArgumentException("Use the source's conversation state.");
        if (input.Kind == GatekeeperInputKind.Text)
        {
            state.Record("Player", input.Text);
            if (story.CurrentNode.input_slot == "name")
            {
                story.CaptureName(input.Text, scenario.nameBlocklist, scenario.fallbackNames[state.AssignedNameIndex % scenario.fallbackNames.Length]);
                if (state.Encounter.NameWasAssigned) state.AssignedNameIndex++;
            }
            else if (story.CurrentNode.input_slot == "business") story.CaptureBusiness(input.Text, scenario.nameBlocklist);
            else throw new InvalidOperationException("This checkpoint accepts choices.");
            lineIndex = 0; EndReaction();
        }
        else if (input.Kind == GatekeeperInputKind.Choice)
        {
            if (input.Choice < 0 || input.Choice >= story.CurrentNode.choices.Count) throw new ArgumentOutOfRangeException(nameof(input));
            state.Record("Player", story.CurrentNode.choices[input.Choice].label);
            state.RecordApproach(story.CurrentNode.choices[input.Choice].target);
            if (!story.Choose(input.Choice)) throw new InvalidOperationException("Choice is no longer available.");
            // A choice can answer an idle taunt (the buttons stay live during it): the taunt is over, this is a new turn.
            lineIndex = 0; EndReaction();
        }
        else if (input.Kind == GatekeeperInputKind.Idle)
        {
            reaction = true; Presentation.IsReaction = true;
            Presentation.LineGapSeconds = 0;
            // A taunt is not a turn: a click during it must never cost patience.
            Presentation.CanInterrupt = Presentation.PointerInterrupts = Presentation.ShowInterruptButton = false;
            return Task.FromResult(GatekeeperLineSources.Resolve(scenario.FindLine(story.NextIdleLine()), state.Encounter));
        }
        else if (input.Kind == GatekeeperInputKind.Interrupt)
        {
            while (string.IsNullOrEmpty(story.CurrentNode.input_slot) && story.CurrentNode.choices.Count == 0 && string.IsNullOrEmpty(story.CurrentVariant.outcome))
                if (!story.Advance()) break;
            lineIndex = story.CurrentLines.Length;
            reaction = true; Presentation.IsReaction = Presentation.ProtectedReaction = true;
            Presentation.LineGapSeconds = 0;
            return Task.FromResult(GatekeeperLineSources.Resolve(scenario.FindLine(story.Data.interruption_line), state.Encounter));
        }
        if (reaction)
        {
            reaction = false; Presentation.IsReaction = Presentation.ProtectedReaction = false;
            return WaitAtCheckpoint();
        }
        while (true)
        {
            var ids = story.CurrentLines;
            if (lineIndex < ids.Length)
            {
                // Only the Interrupt button interrupts a story line; a stray click does not.
                Presentation = new GatekeeperPresentation { IdleSeconds = story.Data.idle_seconds, PointerInterrupts = false };
                var line = GatekeeperLineSources.Resolve(scenario.FindLine(ids[lineIndex++]), state.Encounter);
                // One turn, one utterance: a reply that leads into a checkpoint also asks that checkpoint's question.
                if (lineIndex == ids.Length && string.IsNullOrEmpty(story.CurrentVariant.outcome) && story.Advance())
                {
                    lineIndex = 0;
                    if (story.WaitsForPlayer)
                        for (var prompt = story.CurrentLines; lineIndex < prompt.Length; lineIndex++)
                            line = GatekeeperLineSources.Join(line, GatekeeperLineSources.Resolve(scenario.FindLine(prompt[lineIndex]), state.Encounter));
                }
                return Task.FromResult(line);
            }
            if (!string.IsNullOrEmpty(story.CurrentVariant.outcome) || !story.Advance()) return WaitAtCheckpoint();
            lineIndex = 0;
        }
    }
    private void EndReaction() { reaction = false; Presentation.IsReaction = Presentation.ProtectedReaction = false; }
    private Task<GatekeeperScenario.Line> WaitAtCheckpoint()
    {
        Presentation = new GatekeeperPresentation { IdleSeconds = story.Data.idle_seconds };
        if (!string.IsNullOrEmpty(story.CurrentVariant.outcome))
        {
            story.Finish();
            state.GateOpenRequested = state.Encounter.Outcome == GatekeeperOutcome.Passed || state.Encounter.Outcome == GatekeeperOutcome.Liked;
            // Passage is the win. Friendship retains its original gate animation and restart.
            Presentation.AwaitGate = state.Encounter.Outcome == GatekeeperOutcome.Passed;
            Presentation.Ended = !Presentation.AwaitGate;
        }
        else if (story.CurrentNode.input_slot == "name")
        {
            Presentation.InputSlot = GatekeeperInputSlot.Name; Presentation.Placeholder = "Your name"; Presentation.CharacterLimit = GatekeeperNames.AnswerMaxLength;
        }
        else if (story.CurrentNode.input_slot == "business")
        {
            Presentation.InputSlot = GatekeeperInputSlot.Business; Presentation.Placeholder = "Your business at the gate"; Presentation.CharacterLimit = GatekeeperNames.BusinessAnswerMaxLength;
        }
        else
        {
            Presentation.Choices = story.CurrentNode.choices.ConvertAll(choice => new GatekeeperChoice(choice.label, choice.say)).AsReadOnly();
            Presentation.TauntKeepsChoices = true; // An idle taunt here keeps these buttons live; a click answers it.
        }
        return Task.FromResult<GatekeeperScenario.Line>(null);
    }
}
