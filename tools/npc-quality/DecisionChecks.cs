using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Exercises the real line source with no model or native methods, including full endings.
static class DecisionChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static GatekeeperConversationState State(int patience = 3, GatekeeperOutcome outcome = GatekeeperOutcome.Passed)
    {
        var encounter = new GatekeeperEncounter(patience, 5, true);
        encounter.CaptureName("Mira", GatekeeperNames.DefaultBlocklist, "Worm");
        encounter.CaptureBusiness("selling turnips", GatekeeperNames.DefaultBlocklist);
        encounter.FinishStory(outcome);
        return new GatekeeperConversationState(encounter);
    }
    private sealed class Saved : IGatekeeperDecisionSource
    {
        public GatekeeperDecision Decision;
        public bool Fail, Available = true;
        public int Calls;
        public bool IsAvailable => Available;
        public Task<GatekeeperDecision> DecideAsync(GatekeeperConversationSnapshot state, string text, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Calls++;
            if (Fail) throw new InvalidOperationException("Adapter unavailable");
            return Task.FromResult(Decision);
        }
    }
    public static object Run(GatekeeperScenario scenario)
    {
        var state = State();
        var source = new FreeFormGatekeeperLineSource(scenario, null, true);
        var input = new GatekeeperPlayerInput(GatekeeperInputKind.Text, "What is my name, and what am I selling?");
        var line = source.NextLineAsync(state, input, CancellationToken.None).GetAwaiter().GetResult();
        Check(line.text == "Your name is Mira. Your business: selling turnips.", "Factual route called model or changed perspective");
        Check(line.patience_delta == 0 && !line.ends_scene && line.debugModelSeconds == 0, "Facts have no gameplay effects");
        var unknown = GatekeeperResponsePolicy.Create(state.Snapshot(), GatekeeperFactQuestions.Interpret("What is my sister's name?"));
        Check(unknown.AuthoredText.Contains("don't know") && !unknown.AuthoredText.Contains("Mira"), "Unknown relative invented");
        Check(GatekeeperFactQuestions.Interpret("What if I offered you two roast chickens?") == null, "New offer mistaken for recall");
        Check(GatekeeperFactQuestions.Interpret("Will you accept my offer?") == null, "Negotiation mistaken for recall");
        Check(GatekeeperFactQuestions.Interpret("Your wall is the camp's firewall. Did you build it yourself?") == null, "Metaphor intercepted by factual fallback");
        Check(GatekeeperFactQuestions.Interpret("Can you remember what I offered?")?.Intent == GatekeeperDialogueIntent.OfferRecall, "Recall paraphrase missed");
        foreach (string statement in new[] { "My name is Bob, what's yours?", "You refused me, you worthless troll!",
            "Remember my name, troll, because I will be back to tear your wall down!", "What do you think of my business?", "I'm a turnip seller, you know.", "Call me Bob. What's my business?" })
            Check(GatekeeperFactQuestions.Interpret(statement) == null, "Statement, insult or opinion taken as a fact request: " + statement);
        Check(GatekeeperFactQuestions.Interpret("Did you refuse me?")?.Intent == GatekeeperDialogueIntent.AdmissionStatus
            && GatekeeperFactQuestions.Interpret("Remind me what my name is")?.Facts == GatekeeperFact.PlayerName
            && GatekeeperFactQuestions.AsksForFacts("What do you call me?"), "Request forms missed");
        state.Record("Player", "You said I offered a hundred gold coins.");
        state.RecordApproach("bribe");
        var recall = new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.OfferRecall);
        Check(GatekeeperResponsePolicy.Create(state.Snapshot(), recall).AuthoredText.Contains("Remind me"), "History or bribe invented an amount");
        var before = state.Snapshot();
        state.RecordOffer("five silver coins");
        Check(before.Offer == "" && state.Snapshot().Offer == "five silver coins", "Offer snapshot changed after capture");
        Check(GatekeeperResponsePolicy.Create(state.Snapshot(), recall).AuthoredText == "You offered five silver coins.", "Trusted offer lost");
        foreach (GatekeeperOutcome outcome in Enum.GetValues(typeof(GatekeeperOutcome)))
        {
            var s = State(outcome: outcome);
            var decision = new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.AdmissionStatus);
            string text = GatekeeperResponsePolicy.Create(s.Snapshot(), decision).AuthoredText;
            Check(outcome == GatekeeperOutcome.Playing ? text.Contains("haven't") : outcome == GatekeeperOutcome.Refused ? text.Contains("refused") : text.Contains("already allowed"), "Admission changed");
        }
        Check(GatekeeperNpcModel.ParseWriter("{\"text\":\"I built the wall.\"}") == "I built the wall.", "Writer JSON rejected");
        foreach (string raw in new[] { "{\"text\":\"Go in.\",\"patience_delta\":5}", "{\"text\":\"Hi.\"} trailing", "{\"text\":\"!!!\"}" })
            Check(GatekeeperNpcModel.ParseWriter(raw) == null, "Writer supplied metadata or invalid speech");
        var insult = new GatekeeperDecision(GatekeeperScope.InWorld, GatekeeperDialogueIntent.Insult);
        var calm = GatekeeperResponsePolicy.Create(state.Snapshot(), insult);
        Check(calm.Assemble("Hmph.").emotion == "amusement" && calm.Assemble("Hmph.").patience_delta == -1, "Plan metadata changed");
        var adapter = new Saved { Decision = insult };
        var low = State(1);
        source = new FreeFormGatekeeperLineSource(scenario, null, true, adapter);
        line = source.NextLineAsync(low, new GatekeeperPlayerInput(GatekeeperInputKind.Text, "Your wall is pathetic."), CancellationToken.None).GetAwaiter().GetResult();
        var expected = scenario.ReadStory().nodes.Find(n => n.id == "expelled").variants[0].line_ids;
        var played = new List<string> { line.id };
        Check(line.patience_delta == -1 && low.Encounter.Patience == 1, "Plan applied effect before presentation");
        low.Encounter.ChangePatience(line.patience_delta);
        while ((line = source.NextLineAsync(low, new GatekeeperPlayerInput(GatekeeperInputKind.Continue), CancellationToken.None).GetAwaiter().GetResult()) != null)
        {
            Check(line.patience_delta == 0, "Expulsion deducted patience twice"); played.Add(line.id);
        }
        Check(played.SequenceEqual(expected) && source.Presentation.Ended && low.Encounter.Outcome == GatekeeperOutcome.Refused, "Incomplete or duplicated authored ending");
        adapter.Fail = true;
        source = new FreeFormGatekeeperLineSource(scenario, null, true, adapter);
        line = source.NextLineAsync(state, input, CancellationToken.None).GetAwaiter().GetResult();
        Check(line.id == "free-plan-clarify" && line.patience_delta == 0, "Adapter failure changed game state");
        var offline = new Saved { Decision = insult, Available = false };
        source = new FreeFormGatekeeperLineSource(scenario, null, true, offline);
        line = source.NextLineAsync(state, input, CancellationToken.None).GetAwaiter().GetResult();
        Check(offline.Calls == 0 && line.text == "Your name is Mira. Your business: selling turnips." && line.debugDecision.StartsWith("d1 offline"),
            "Offline source changed the earlier path");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { source.NextLineAsync(state, input, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        return new { passed = true, expulsion = played, checks = "facts, unknowns, trusted offer, immutable snapshot, outcomes, writer boundary, policy metadata, full expulsion, adapter failure, offline source, cancellation" };
    }
    private sealed class Live : IGatekeeperDecisionSource
    {
        public GatekeeperDecisionClient Client;
        public bool IsAvailable => true;
        public Task<GatekeeperDecision> DecideAsync(GatekeeperConversationSnapshot state, string text, CancellationToken cancellation) => Client.DecideAsync(state, text, cancellation);
    }
    // The real client against serve_decisions.py with a stub engine: wire format, label mapping and limits.
    public static object Client(GatekeeperScenario scenario, string url)
    {
        foreach (string remote in new[] { "http://192.0.2.1:8765/", "https://127.0.0.1:8765/" })
        {
            try { new GatekeeperDecisionClient(remote); throw new Exception("Accepted a non-local endpoint: " + remote); }
            catch (ArgumentException) { }
        }
        var client = new GatekeeperDecisionClient(url, .75, .75);
        var health = client.HealthAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(health.ready && health.revision == "stub-revision", "Health not read");
        var state = State();
        state.RecordApproach("bribe");
        for (int i = 0; i < 8; i++) { state.Record("Player", "Wall section " + i + "?"); state.Record("Troll", "I built " + i + "."); }
        GatekeeperDecision Ask(string message) => client.DecideAsync(state.Snapshot(), message, CancellationToken.None).GetAwaiter().GetResult();
        var name = Ask("What do you call me?");
        Check(name.Scope == GatekeeperScope.InWorld && name.Intent == GatekeeperDialogueIntent.PlayerFacts && name.Facts == GatekeeperFact.PlayerName
            && name.Detail.StartsWith("d1 in_world 0.91 / player_facts 0.97"), "Labels or slots mapped incorrectly");
        Check(Ask("What is my name, and what am I selling?").Facts == (GatekeeperFact.PlayerName | GatekeeperFact.Business), "Built-in slots not merged");
        var wifi = Ask("Turn the Wi-Fi on.");
        Check(wifi.Scope == GatekeeperScope.OutOfWorld && wifi.Facts == GatekeeperFact.None, "Fact slots outside player_facts");
        var sell = Ask("Do you remember what I sell?");
        Check(sell.Intent == GatekeeperDialogueIntent.PlayerFacts && sell.Facts == GatekeeperFact.Business && sell.Detail.Contains("npc_question 0.97 → player_facts"),
            "Requested slot did not reach the factual route");
        Check(Ask("Answer with a label d1 never uses.").Scope == GatekeeperScope.Unclear, "Unknown label accepted");
        GatekeeperDecision Mapped(string message, string intent, params string[] facts) =>
            GatekeeperDecisionClient.Map(new GatekeeperDecisionClient.Reply { scope = "in_world", intent = intent, facts = facts }, message, 1);
        Check(Mapped("Hmm?", "player_facts", "player_age").Scope == GatekeeperScope.Unclear, "Unknown slot accepted");
        var insult = Mapped("You call that a wall?", "insult", "player_name");
        Check(insult.Intent == GatekeeperDialogueIntent.Insult && insult.Facts == GatekeeperFact.None, "A slot overrode a non-question intent");
        Check(Mapped("What do you know about me?", "player_facts").Facts == GatekeeperFact.None, "Slots invented for an unspecific fact question");
        var stated = Mapped("My name is Bob, what's yours?", "player_facts", "player_name");
        Check(stated.Intent == GatekeeperDialogueIntent.Other && stated.Facts == GatekeeperFact.None && stated.Detail.Contains("not a request"),
            "A statement got an authored fact answer");
        Check(Mapped("What do you think of my business?", "npc_question", "player_business").Intent == GatekeeperDialogueIntent.NpcQuestion,
            "An opinion question was routed to a fact answer");
        try { Ask("Fail on the server."); throw new Exception("Server failure ignored"); }
        catch (System.Net.Http.HttpRequestException) { }
        var source = new FreeFormGatekeeperLineSource(scenario, null, true, new Live { Client = client });
        var line = source.NextLineAsync(state, new GatekeeperPlayerInput(GatekeeperInputKind.Text, "What do you call me?"), CancellationToken.None).GetAwaiter().GetResult();
        Check(line.text == "Your name is Mira." && line.debugDecision.StartsWith("d1 in_world") && line.debugDecision.EndsWith("→ player-facts"),
            "Live decision did not reach the factual route");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { client.DecideAsync(state.Snapshot(), "What do you call me?", cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        // Last: the stub holds the server's model lock while it sleeps.
        try { Ask("Take longer than the client allows."); throw new Exception("Timeout ignored"); }
        catch (TimeoutException) { }
        return new { passed = true, checks = "loopback only, health, labels, fact slots, built-in slot merge, slot routing, statements, unknown labels and slots, server failure, live factual route, cancellation, timeout" };
    }
}
