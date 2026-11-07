using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Entering the camp (docs/entry.md), headless: the entry reader, free talk after both admissions, the send-off and the
// Entered ending, goodbyes and questions that keep the conversation, d1's enter_camp with its vetoes, and ending reasons.
static class EntryChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Saved : IGatekeeperDecisionSource
    {
        public GatekeeperDecision Decision;
        public bool IsAvailable => true;
        public Task<GatekeeperDecision> DecideAsync(GatekeeperConversationSnapshot state, string text, CancellationToken cancellation) => Task.FromResult(Decision);
    }
    private static GatekeeperConversationState Admitted(GatekeeperOutcome outcome = GatekeeperOutcome.Passed, bool gateOpen = true, int patience = 3)
    {
        var encounter = new GatekeeperEncounter(patience, 5, true);
        encounter.CaptureName("Mira", GatekeeperNames.DefaultBlocklist, "Worm");
        encounter.CaptureBusiness("selling turnips", GatekeeperNames.DefaultBlocklist);
        encounter.FinishStory(outcome);
        return new GatekeeperConversationState(encounter) { GateOpenRequested = encounter.Admitted, GateOpen = gateOpen };
    }
    private static GatekeeperScenario.Line Ask(IGatekeeperLineSource source, GatekeeperConversationState state, GatekeeperPlayerInput input)
    {
        var line = source.NextLineAsync(state, input, CancellationToken.None).GetAwaiter().GetResult();
        if (line != null) { GatekeeperLineSources.ValidateOutput(line); state.Encounter.ChangePatience(line.patience_delta); }
        return line;
    }
    private static GatekeeperScenario.Line Say(IGatekeeperLineSource source, GatekeeperConversationState state, string words) =>
        Ask(source, state, new GatekeeperPlayerInput(GatekeeperInputKind.Text, words));
    private static GatekeeperScenario.Line Continue(IGatekeeperLineSource source, GatekeeperConversationState state) =>
        Ask(source, state, new GatekeeperPlayerInput(GatekeeperInputKind.Continue));

    public static string Run(GatekeeperScenario scenario)
    {
        // The reader: clear entries on their own, never questions, hedges, refusals, goodbyes or remarks.
        foreach (string words in new[] { "Go in.", "Enter camp.", "enter the camp", "I'm going in now.", "Right, I'll head inside.", "Okay, in I go!",
            "Let's go in.", "I'll go in then, goodbye.", "Go on in", "Yes, go in.", "Thanks, Trola. I'll head in now.", "Time to go inside.",
            "I\u2019m going in.", "I want to go in.", "Walking in." })
            Check(GatekeeperEntry.Clear(words), "Entry missed: " + words);
        foreach (string words in new[] { "Who's in there?", "So...", "So\u2026", "So", "Can I go in?", "can I go in", "Goodbye.", "Maybe I'll go in later.",
            "I don't want to go in yet.", "Let me in.", "See you inside.", "What's in the camp?", "Is it safe in there?", "Mind if I go through?",
            "I'll go in after one more question.", "Go in? Not yet.", "Lead the way.", "Go in, you ugly troll.", "I'm not going in.", "", null,
            "Wait, I'll go in.", "Tell me about the camp before I go in." })
            Check(!GatekeeperEntry.Clear(words), "Not an entry, but read as one: " + words);
        Check(GatekeeperEntry.MayEnter("Lead the way.") && GatekeeperEntry.MayEnter("Time to see this camp of yours."), "d1's entry vetoed a plain statement");
        foreach (string words in new[] { "Can I go in?", "Maybe I'll go in later.", "I won't go in.", "So...", "Who's in there", new string('a', 81) })
            Check(!GatekeeperEntry.MayEnter(words), "d1 could enter on: " + words);

        // Ending reasons: the story's endings, the encounter's admission gate.
        foreach (var (plan, outcome, ending) in new[] {
            (new[] { 0, 1, 0 }, GatekeeperOutcome.Passed, GatekeeperEnding.None), (new[] { 0, 3, 1 }, GatekeeperOutcome.Liked, GatekeeperEnding.None),
            (new[] { 2, 2, 2 }, GatekeeperOutcome.Refused, GatekeeperEnding.Expelled), (new[] { 2, 2, 0 }, GatekeeperOutcome.Refused, GatekeeperEnding.Refused) })
        {
            var story = new GatekeeperStory(scenario);
            story.CaptureName("Mira", GatekeeperNames.DefaultBlocklist, "Worm"); story.Advance();
            story.CaptureBusiness("selling turnips", GatekeeperNames.DefaultBlocklist); story.Advance();
            foreach (int choice in plan) { Check(story.Choose(choice), "Scripted choice refused"); while (story.Advance()) { } }
            story.Finish();
            Check(story.Encounter.Outcome == outcome && story.Encounter.Ending == ending,
                "Ending reason for " + string.Join(",", plan) + ": " + story.Encounter.Outcome + "/" + story.Encounter.Ending + ", expected " + outcome + "/" + ending);
        }
        // A provoked expulsion ends on Trola's threat, which his strike follows (docs/defeat.md); calm refusals keep
        // "Walk away, while it's still your choice."
        var data = scenario.ReadStory();
        string[] Ending(string node, int patience) => data.nodes.Find(n => n.id == node).variants.Find(v => patience >= v.min_patience && patience <= v.max_patience).line_ids;
        Check(Ending("expelled", 3).SequenceEqual(new[] { "refuse-out" }) && scenario.FindLine("refuse-out").ends_scene, "Expulsion does not end on his threat");
        Check(Ending("passage", 1).Last() == "refuse-walk" && Ending("friendship", 1).Last() == "refuse-walk", "A calm refusal lost its closer");
        var playing = new GatekeeperEncounter(3, 5, true);
        Check(!playing.EnterCamp() && playing.Ending == GatekeeperEnding.None, "Entered without admission");
        playing.FinishStory(GatekeeperOutcome.Refused, GatekeeperEnding.Refused);
        Check(!playing.EnterCamp() && playing.Ending == GatekeeperEnding.Refused, "A refusal became an entry");

        // Both admissions wait for the gate, then talk on (the authored source no longer ends friendship at once).
        foreach (var (route, outcome) in new[] { (new[] { 0, 1, 0 }, GatekeeperOutcome.Passed), (new[] { 3, 3, 1 }, GatekeeperOutcome.Liked) })
        {
            var state = new GatekeeperConversationState(new GatekeeperEncounter(3, 5, true));
            var authored = new AuthoredGatekeeperLineSource(scenario, state);
            void Drain(GatekeeperPlayerInput act) { for (var l = Ask(authored, state, act); l != null; l = Continue(authored, state)) { } }
            Drain(new GatekeeperPlayerInput(GatekeeperInputKind.Start));
            Drain(new GatekeeperPlayerInput(GatekeeperInputKind.Text, "Mira"));
            Drain(new GatekeeperPlayerInput(GatekeeperInputKind.Text, "selling turnips"));
            foreach (int choice in route) Drain(new GatekeeperPlayerInput(GatekeeperInputKind.Choice, choice: choice));
            Check(state.Encounter.Outcome == outcome && authored.Presentation.AwaitGate && !authored.Presentation.Ended && state.GateOpenRequested
                && state.Encounter.Ending == GatekeeperEnding.None, outcome + " does not wait for the gate and talk on");
        }

        // Free talk after admission: the greeting says how to finish; words or the button enter; nothing else does.
        foreach (var outcome in new[] { GatekeeperOutcome.Passed, GatekeeperOutcome.Liked })
        {
            var state = Admitted(outcome);
            var source = new FreeFormGatekeeperLineSource(scenario, null, false);
            var greeting = Ask(source, state, new GatekeeperPlayerInput(GatekeeperInputKind.Start));
            Check(greeting.text.StartsWith(outcome == GatekeeperOutcome.Liked ? "Go in when you like" : "Gate's open. Go in", StringComparison.Ordinal)
                && source.Presentation.CanEnter && !source.Presentation.Entering, "Greeting or entry offer wrong: " + greeting.text);
            Check(Continue(source, state) == null && source.Presentation.InputSlot == GatekeeperInputSlot.Reply && source.Presentation.CanEnter, "Free talk not offered");
            foreach (string words in new[] { "Who's in there?", "So...", "Can I go in?", "Goodbye.", "See you inside." })
            {
                var reply = Say(source, state, words);
                Check(reply != null && !reply.ends_scene && !source.Presentation.Entering && source.Presentation.CanEnter, "Kept talking? " + words);
                Check(Continue(source, state) == null && !source.Presentation.Ended && state.Encounter.Ending == GatekeeperEnding.None, "Ended on: " + words);
            }
            var sendOff = Say(source, state, "Right, I'll head inside.");
            Check(sendOff.id == (outcome == GatekeeperOutcome.Liked ? "free-enter-liked" : "free-enter") && sendOff.patience_delta == 0 && !sendOff.ends_scene
                && source.Presentation.Entering && !source.Presentation.CanEnter && !source.Presentation.CanInterrupt
                && source.Presentation.InputSlot == GatekeeperInputSlot.None && !source.Presentation.PersistentText && sendOff.debugDecision == "entry: keywords",
                "Send-off missing or still answering: " + sendOff.text);
            Check(Continue(source, state) == null && source.Presentation.Ended && source.Presentation.Entering && state.Encounter.Ending == GatekeeperEnding.Entered
                && state.Encounter.Outcome == outcome, "Did not end Entered");
            Check(Continue(source, state) == null && state.Encounter.Ending == GatekeeperEnding.Entered, "Entered did not stand");
        }
        // The button (Enter): the same send-off.
        {
            var state = Admitted();
            var source = new FreeFormGatekeeperLineSource(scenario, null, true);
            var line = Ask(source, state, new GatekeeperPlayerInput(GatekeeperInputKind.Enter));
            Check(line.id == "free-enter" && line.debugDecision == "Enter camp" && Continue(source, state) == null
                && state.Encounter.Ending == GatekeeperEnding.Entered, "Enter camp did not send off and end Entered");
        }
        // No entry with the gate still closed, or without admission (a refused free talk is debug only).
        {
            var shut = Admitted(gateOpen: false);
            var source = new FreeFormGatekeeperLineSource(scenario, null, true);
            Check(Say(source, shut, "Go in.").id != "free-enter" && !source.Presentation.Entering, "Entered through a shut gate");
            try { Ask(source, shut, new GatekeeperPlayerInput(GatekeeperInputKind.Enter)); throw new Exception("Enter accepted through a shut gate"); }
            catch (InvalidOperationException) { }
            var refused = Admitted(GatekeeperOutcome.Refused);
            source = new FreeFormGatekeeperLineSource(scenario, null, true);
            Check(Say(source, refused, "Go in.").id != "free-enter" && !source.Presentation.CanEnter, "Entered without admission");
        }
        // d1's enter_camp widens the phrasing, in world only, and never over a question, a hedge or a refusal.
        {
            GatekeeperScenario.Line Decided(string words, GatekeeperScope scope, bool enters, out FreeFormGatekeeperLineSource source, GatekeeperConversationState state = null)
            {
                state ??= Admitted();
                source = new FreeFormGatekeeperLineSource(scenario, null, true,
                    new Saved { Decision = new GatekeeperDecision(scope, GatekeeperDialogueIntent.Other, detail: "d1 stub", entersCamp: enters) });
                return Say(source, state, words);
            }
            Check(Decided("Lead the way.", GatekeeperScope.InWorld, true, out var led).id == "free-enter" && led.Presentation.Entering, "d1 entry ignored");
            Check(Decided("Lead the way.", GatekeeperScope.InWorld, false, out _).id != "free-enter", "Entered without d1 or keywords");
            Check(Decided("Can I go in?", GatekeeperScope.InWorld, true, out _).id != "free-enter", "d1 entered on a question");
            Check(Decided("Maybe I'll go in later.", GatekeeperScope.InWorld, true, out _).id != "free-enter", "d1 entered on a hedge");
            Check(Decided("Lead the way.", GatekeeperScope.OutOfWorld, true, out _).id != "free-enter", "d1 entered out of world");
            Check(Decided("Lead the way.", GatekeeperScope.InWorld, true, out _, Admitted(gateOpen: false)).id != "free-enter", "d1 entered a shut gate");
        }
        // A goodbye after admission is answered and stays; without admission (debug free talk) it still ends, as Departed.
        {
            var farewell = new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.Farewell);
            var state = Admitted();
            var source = new FreeFormGatekeeperLineSource(scenario, null, true, new Saved { Decision = farewell });
            var line = Say(source, state, "All right. Goodbye.");
            Check(!line.ends_scene && line.text.Contains("go in when you're ready") && Continue(source, state) == null && !source.Presentation.Ended
                && source.Presentation.CanEnter, "A goodbye ended free talk after admission: " + line.text + " / " + line.ends_scene);
            var refused = Admitted(GatekeeperOutcome.Refused);
            source = new FreeFormGatekeeperLineSource(scenario, null, true, new Saved { Decision = farewell });
            Check(Say(source, refused, "All right. Goodbye.").ends_scene && Continue(source, refused) == null && source.Presentation.Ended
                && refused.Encounter.Ending == GatekeeperEnding.Departed, "Goodbye without admission lost its ending");
            var admission = GatekeeperResponsePolicy.Create(state.Snapshot(), new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.AdmissionStatus));
            Check(admission.AuthoredText.Contains("Go in when you're ready") && !admission.EndsScene, "Admission answer does not point at the gate");
        }
        // An expulsion in free talk is Expelled, never Entered.
        {
            var state = Admitted(patience: 1);
            var source = new FreeFormGatekeeperLineSource(scenario, null, true,
                new Saved { Decision = new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.Insult) });
            for (var line = Say(source, state, "Your wall is pathetic."); line != null; line = Continue(source, state)) { }
            Check(source.Presentation.Ended && !source.Presentation.CanEnter && state.Encounter.Outcome == GatekeeperOutcome.Refused
                && state.Encounter.Ending == GatekeeperEnding.Expelled, "Free-talk expulsion: " + state.Encounter.Ending);
        }
        return "expulsion ends on his threat, calm refusals walk away; entry reader (clear entries; questions, hedges, refusals, goodbyes and remarks kept), d1 vetoes, ending reasons (Expelled, calm Refused, " +
               "Entered only after admission), both admissions wait for the gate, greetings, send-off and Entered, Enter camp, shut gate, goodbye replies, free-talk expulsion";
    }
}
