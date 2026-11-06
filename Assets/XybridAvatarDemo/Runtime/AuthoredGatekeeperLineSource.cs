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
    // The optional writer (-xybridWriter gemma), null by default. Here it only words in-world off-topic answers at the buttons.
    private readonly GatekeeperNpcModel writer;
    private const double WriterSeconds = 5;
    private int lineIndex;
    private bool reaction;
    public GatekeeperPresentation Presentation { get; private set; } = new GatekeeperPresentation();
    public AuthoredGatekeeperLineSource(GatekeeperScenario scenario, GatekeeperConversationState state, GatekeeperNpcModel writer = null)
    {
        this.scenario = scenario; this.state = state; this.writer = writer; story = new GatekeeperStory(scenario, state.Encounter);
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
        else if (input.Kind == GatekeeperInputKind.OffTopic || input.Kind == GatekeeperInputKind.OutOfWorld || input.Kind == GatekeeperInputKind.Unclear
            || input.Kind == GatekeeperInputKind.Neutral)
        {
            // Spoken at the buttons without picking one (see GatekeeperInputKind). Never a story turn or an approach.
            if (story.CurrentNode.choices == null || story.CurrentNode.choices.Count == 0 || state.Encounter.Outcome != GatekeeperOutcome.Playing || string.IsNullOrWhiteSpace(input.Text))
                throw new InvalidOperationException("Spoken answers need a choice checkpoint.");
            var snapshot = state.Snapshot(); // Without the new words, as the writer expects.
            state.Record("Player", input.Text);
            int patience = state.Encounter.Patience;
            var kind = input.Kind;
            // A caller cannot launder an insult or scope rejection into a free remark by tagging the input Neutral.
            if (kind == GatekeeperInputKind.Neutral)
            {
                if (input.Decision?.Scope == GatekeeperScope.OutOfWorld || input.Decision?.Scope == GatekeeperScope.RoleOverride)
                    kind = GatekeeperInputKind.OutOfWorld;
                else if (input.Decision?.Scope != GatekeeperScope.InWorld) kind = GatekeeperInputKind.Unclear;
                else if (input.Decision.Intent == GatekeeperDialogueIntent.Insult) kind = GatekeeperInputKind.OffTopic;
            }
            if (kind == GatekeeperInputKind.Neutral)
            {
                reaction = true; Presentation.IsReaction = Presentation.ProtectedReaction = true;
                Presentation.CanInterrupt = Presentation.PointerInterrupts = Presentation.ShowInterruptButton = false;
                Presentation.LineGapSeconds = 0;
                var plan = GatekeeperResponsePolicy.AtCheckpoint(snapshot, input.Decision);
                var authored = GatekeeperAuthoredReplies.Reply(snapshot, input.Decision, plan, input.Text);
                authored.debugSource = "authored reply";
                authored.debugDecision = "spoken answer: Neutral / " + input.Decision.Detail + " → " + plan.Reaction;
                return Task.FromResult(authored);
            }
            if (kind != GatekeeperInputKind.Unclear && patience <= 0)
            {
                // No patience left: the complete authored expulsion, then Refused. EndReaction also clears an idle taunt's
                // reaction, which would otherwise skip the second ending line.
                story.Enter("expelled"); lineIndex = 0; EndReaction();
            }
            else
            {
                // Like an interruption: a protected reaction (its cost applied as it plays), then the same buttons again.
                reaction = true; Presentation.IsReaction = Presentation.ProtectedReaction = true;
                Presentation.LineGapSeconds = 0;
                var authored = GatekeeperLineSources.Resolve(scenario.FindLine(kind == GatekeeperInputKind.Unclear
                    ? story.NextUnclearLine() : story.NextOffTopicLine(patience)), state.Encounter);
                authored.debugDecision = "spoken answer: " + kind;
                // Facts stay in code (docs/gemma-context-experiment-2026-11-03.md), and the warning before the expulsion stays authored.
                if (kind == GatekeeperInputKind.OffTopic && patience > 1 && writer != null && writer.IsReady
                    && GatekeeperFactQuestions.Interpret(input.Text) == null)
                    return WriteOffTopic(snapshot, input.Text, patience, authored, cancellation);
                return Task.FromResult(authored);
            }
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
    // LLM mode: the planned writer words the rebuke to what was said; the game keeps its one-patience cost. The authored
    // rebuke stands in when the writer fails or takes longer than WriterSeconds.
    private async Task<GatekeeperScenario.Line> WriteOffTopic(GatekeeperConversationSnapshot snapshot, string words, int patience,
        GatekeeperScenario.Line authored, CancellationToken cancellation)
    {
        var plan = new GatekeeperResponsePlan("off-topic", patience >= 4 ? "impatience" : "irritation", patience >= 4 ? .5f : .6f, -1,
            direction: "The player's answer is not one of the choices you offered. Answer it in one short gruff line, let your patience show, and tell them to choose.");
        GatekeeperScenario.Line line = null;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(WriterSeconds));
            try { line = await writer.ReplyAsync(snapshot, words, limit.Token, plan); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { line = null; }
        }
        cancellation.ThrowIfCancellationRequested();
        var spoken = line ?? authored;
        spoken.debugSource = line != null ? "LLM" : "LLM fallback";
        spoken.debugDecision = authored.debugDecision;
        spoken.debugRaw = writer.LastRawReply ?? "";
        spoken.debugModelSeconds = writer.LastReplySeconds;
        return spoken;
    }
    private Task<GatekeeperScenario.Line> WaitAtCheckpoint()
    {
        Presentation = new GatekeeperPresentation { IdleSeconds = story.Data.idle_seconds };
        if (!string.IsNullOrEmpty(story.CurrentVariant.outcome))
        {
            story.Finish();
            state.GateOpenRequested = state.Encounter.Outcome == GatekeeperOutcome.Passed || state.Encounter.Outcome == GatekeeperOutcome.Liked;
            // Both admissions wait for the gate, then talk on until the player enters the camp (docs/entry.md).
            Presentation.AwaitGate = state.Encounter.Admitted;
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
