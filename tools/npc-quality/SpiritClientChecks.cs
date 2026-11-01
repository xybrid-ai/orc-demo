using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// The real SpiritDecisionClient against serve_decisions.py with a stub engine (verify_spirit_d1.py), plus a
// replay of recorded /spirit labels through SpiritPolicy.Answer. WS2 types are reached by reflection, so this
// suite builds before they exist.
public static class SpiritClientChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T Fails<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (AggregateException error) when (error.InnerException is T inner) { return inner; }
        throw new Exception(message);
    }
    private static SpiritRequest Request(string message, string stage = "hub", string mood = "wary") => new SpiritRequest
    {
        message = message, name = "Mira", business = "selling turnips", stage = stage, mood = mood,
        options = new[] { "Flatter", "Bribe", "Threaten", "Ask about the camp" }, spirit_said = "Ask about his camp. Nobody ever asks the gatekeeper.",
        history = new[] { new SpiritTurn { speaker = "Spirit", text = "Careful, Mira." }, new SpiritTurn { speaker = "Player", text = "why?" } }
    };

    public static object Run(JsonNode request, GatekeeperScenario scenario, GatekeeperStory.Document story)
    {
        string mode = request["mode"]?.GetValue<string>() ?? "client";
        if (mode == "replay") return Replay(request, story);
        return Client(request["url"].GetValue<string>(), request["old_url"].GetValue<string>());
    }

    private static object Client(string url, string oldUrl)
    {
        foreach (string remote in new[] { "http://192.0.2.1:8765/", "https://127.0.0.1:8765/", "http://example.com/", "" })
            Fails<ArgumentException>(() => new SpiritDecisionClient(remote), "Accepted a non-local endpoint: " + remote);
        var client = new SpiritDecisionClient(url);
        ISpiritDecider decider = client;
        SpiritDecision Ask(SpiritRequest r) => decider.DecideAsync(r, CancellationToken.None).GetAwaiter().GetResult();

        // Health: a restarted server lists the spirit set; one that predates /spirit does not.
        var health = client.HealthAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(health.ready && health.revision == "stub-revision" && SpiritDecisionClient.Questions.All(q => health.spirit_questions.Contains(q))
            && SpiritDecisionClient.Problem(health) == null, "Spirit health not read");
        var old = new SpiritDecisionClient(oldUrl);
        var oldHealth = old.HealthAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(oldHealth.ready && (oldHealth.spirit_questions == null || oldHealth.spirit_questions.Length == 0)
            && SpiritDecisionClient.Problem(oldHealth).Contains("no /spirit"), "Health without spirit_questions not reported");
        Check(SpiritDecisionClient.Problem(null) != null && SpiritDecisionClient.Problem(new SpiritDecisionClient.SpiritHealth { ready = false, error = "RuntimeError",
            spirit_questions = SpiritDecisionClient.Questions }).Contains("RuntimeError"), "Unready health not reported");
        Fails<SpiritEndpointMissingException>(() => old.DecideAsync(Request("Are you sure?"), CancellationToken.None).GetAwaiter().GetResult(),
            "An old server's 404 was not SpiritEndpointMissingException");

        // 200: labels, confidences, plan and flag probabilities.
        var doubt = Ask(Request("Are you sure?"));
        Check(doubt.Source == SpiritDecisionSource.D1 && doubt.Scope == SpiritScope.InWorld && doubt.Intent == SpiritIntent.Doubt && doubt.Plan == SpiritPlan.None
            && Near(doubt.ScopeP, .93f) && Near(doubt.IntentP, .88f) && Near(doubt.PlanP, .81f) && !doubt.Reckless && !doubt.ToTrola
            && Near(doubt.RecklessP, .07f) && Near(doubt.ToTrolaP, .03f) && doubt.Ms > 0, "Doubt mapped incorrectly: " + doubt.Detail);
        Check(doubt.Detail.StartsWith("d1 in_world .93 · doubt .88 · plan none .81 · reckless .07 · to_trola .03 · ") && doubt.Detail.EndsWith(" ms"),
            "Detail format changed: " + doubt.Detail);
        var threat = Ask(Request("I'll threaten him."));
        Check(threat.Intent == SpiritIntent.StatePlan && threat.Plan == SpiritPlan.Threaten && threat.Reckless && Near(threat.RecklessP, .91f) && !threat.ToTrola,
            "Plan or reckless flag lost: " + threat.Detail);
        var gate = Ask(Request("Open the gate, troll!", "reckoning", "annoyed"));
        Check(gate.Plan == SpiritPlan.DemandGate && gate.ToTrola && Near(gate.ToTrolaP, .91f), "to_trola flag lost: " + gate.Detail);
        var unknown = Ask(Request("Answer with labels d1 never uses."));
        Check(unknown.Scope == SpiritScope.Unclear && unknown.Intent == SpiritIntent.Other && unknown.Plan == SpiritPlan.None
            && unknown.ScopeP == 0 && unknown.IntentP == 0 && unknown.PlanP == 0 && unknown.Detail.Contains("unknown scope outside, intent summon_demon, plan dance"),
            "Unknown labels accepted: " + unknown.Detail);

        // Map on its own: null pieces, clamping, unknown flags.
        var empty = SpiritDecisionClient.Map(null, 3);
        Check(empty.Scope == SpiritScope.Unclear && empty.Intent == SpiritIntent.Other && empty.Plan == SpiritPlan.None && empty.Source == SpiritDecisionSource.D1
            && empty.Detail.Contains("no decision"), "Null reply not neutral");
        var odd = SpiritDecisionClient.Map(new SpiritDecisionClient.SpiritReply { scope = "out_of_world", intent = "ask_spirit_to_act", plan = "pay_toll",
            scope_confidence = 1.7f, intent_confidence = float.NaN, plan_confidence = -2, flags = new[] { "to_trola", "sneaky" } }, 12.4);
        Check(odd.Scope == SpiritScope.OutOfWorld && odd.Intent == SpiritIntent.AskSpiritToAct && odd.Plan == SpiritPlan.PayToll && odd.ScopeP == 1 && odd.IntentP == 0
            && odd.PlanP == 0 && odd.ToTrola && !odd.Reckless && odd.RecklessP == 0 && odd.Detail.Contains("unknown flag sneaky") && odd.Detail.EndsWith("12 ms"),
            "Map clamping, missing probabilities or unknown flags: " + odd.Detail);
        foreach (var pair in new Dictionary<string, SpiritIntent> { ["ask_advice"] = SpiritIntent.AskAdvice, ["ask_about_trola"] = SpiritIntent.AskAboutTrola,
            ["agree"] = SpiritIntent.Agree, ["dismiss"] = SpiritIntent.Dismiss, ["ask_about_spirit"] = SpiritIntent.AskAboutSpirit, ["other"] = SpiritIntent.Other })
            Check(SpiritDecisionClient.Map(new SpiritDecisionClient.SpiritReply { scope = "in_world", intent = pair.Key, plan = "none" }, 1).Intent == pair.Value, "Intent " + pair.Key);
        foreach (var pair in new Dictionary<string, SpiritPlan> { ["flatter"] = SpiritPlan.Flatter, ["bribe"] = SpiritPlan.Bribe, ["ask_camp"] = SpiritPlan.AskCamp,
            ["share_joke"] = SpiritPlan.ShareJoke, ["demand_gate"] = SpiritPlan.DemandGate })
            Check(SpiritDecisionClient.Map(new SpiritDecisionClient.SpiritReply { scope = "role_override", intent = "other", plan = pair.Key }, 1).Plan == pair.Value, "Plan " + pair.Key);

        // Clip: limits, Player/Spirit-only history, no mutation, stage and mood untouched for the server to judge.
        var raw = new SpiritRequest
        {
            message = "  Clip check " + new string('x', 200) + "  ", name = new string('n', 100), business = new string('b', 200), stage = "hub", mood = "wary",
            spirit_said = new string('s', 300), options = new[] { new string('o', 50), null, "Bribe", "Threaten", "Ask about the camp", "Sixth" },
            history = new[] { new SpiritTurn { speaker = "Spirit", text = "zero" }, new SpiritTurn { speaker = "Player", text = "first" }, new SpiritTurn { speaker = "Trola", text = "State your business." },
                new SpiritTurn { speaker = "Spirit", text = "one" }, null, new SpiritTurn { speaker = "Troll", text = "Hmph." }, new SpiritTurn { speaker = "Player", text = "two" },
                new SpiritTurn { speaker = "Gemma", text = "x" }, new SpiritTurn { speaker = "Spirit", text = "three" }, new SpiritTurn { speaker = "Player", text = new string('t', 300) },
                new SpiritTurn { speaker = "Spirit", text = null }, new SpiritTurn { speaker = "player", text = "lowercase speaker" } }
        };
        var clipped = SpiritDecisionClient.Clip(raw);
        Check(clipped.message.Length == 80 && clipped.message.StartsWith("Clip check ") && clipped.name.Length == 80 && clipped.business.Length == 160
            && clipped.spirit_said.Length == 240 && clipped.options.Length == 4 && clipped.options[0].Length == 40 && clipped.options[3] == "Ask about the camp",
            "Clip limits");
        Check(clipped.history.Length == 6 && clipped.history.All(t => t.speaker == "Player" || t.speaker == "Spirit") && clipped.history[0].text == "first"
            && clipped.history[4].text.Length == 240 && clipped.history[5].text == "", "Clip history: " + string.Join("|", clipped.history.Select(t => t.speaker + ":" + t.text)));
        Check(raw.message.Length > 200 && raw.history.Length == 12 && raw.options.Length == 6, "Clip mutated its input");
        var blank = SpiritDecisionClient.Clip(null);
        Check(blank.message == "" && blank.name == "" && blank.stage == "" && blank.options.Length == 0 && blank.history.Length == 0, "Clip(null) not empty");
        var emoji = SpiritDecisionClient.Clip(new SpiritRequest { message = new string('a', 79) + "\U0001F642" });
        Check(emoji.message.Length == 79, "Clip split a surrogate pair");
        Check(SpiritDecisionClient.Clip(new SpiritRequest { stage = "lobby", mood = "ecstatic" }) is var bad && bad.stage == "lobby" && bad.mood == "ecstatic",
            "Clip must leave stage and mood for the server to validate");
        var wire = Ask(raw);   // The server records what arrived; verify_spirit_d1.py checks it.
        Check(wire.Scope == SpiritScope.InWorld, "Clipped request not accepted: " + wire.Detail);

        // 400s: bad stage or mood through the client; a Trola turn posted around Clip.
        foreach (var r in new[] { Request("x", "lobby"), Request("x", "hub", "ecstatic"), Request("x", "Hub") })
        {
            var error = Fails<HttpRequestException>(() => Ask(r), "Bad stage/mood accepted: " + r.stage + "/" + r.mood);
            Check(error.Message.Contains("400"), "Expected 400: " + error.Message);
        }
        using (var http = new HttpClient())
        {
            string Post(string json)
            {
                using var response = http.PostAsync(new Uri(new Uri(url), "spirit"), new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                return ((int)response.StatusCode).ToString();
            }
            string Body(string turns) => "{\"message\":\"x\",\"name\":\"Mira\",\"business\":\"turnips\",\"stage\":\"hub\",\"mood\":\"wary\",\"options\":[],\"spirit_said\":\"\",\"history\":" + turns + "}";
            Check(Post(Body("[]")) == "200", "Raw baseline rejected");
            Check(Post(Body("[{\"speaker\":\"Trola\",\"text\":\"Hmph.\"}]")) == "400", "Trola turn accepted in the spirit transcript");
            Check(Post(Body("[{\"speaker\":\"Gemma\",\"text\":\"x\"}]")) == "400", "Unknown speaker accepted");
        }

        // Failures: server error, cancellation before and during a request, then the 1.2 s default timeout.
        Fails<HttpRequestException>(() => Ask(Request("Fail on the server.")), "Server failure ignored");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Fails<OperationCanceledException>(() => client.DecideAsync(Request("Are you sure?"), cancelled.Token).GetAwaiter().GetResult(), "Cancellation ignored");
        }
        using (var later = new CancellationTokenSource(150))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var error = Fails<Exception>(() => client.DecideAsync(Request("Take a while."), later.Token).GetAwaiter().GetResult(), "Cancellation in flight ignored");
            Check(error is OperationCanceledException && started.Elapsed.TotalSeconds < .6, "Cancellation in flight became " + error.GetType().Name);
        }
        Thread.Sleep(700);   // The stub holds the model lock while it sleeps.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var timeout = Fails<TimeoutException>(() => Ask(Request("Take longer than the client allows.")), "Timeout ignored");
        double waited = timer.Elapsed.TotalSeconds;
        Check(waited >= 1.15 && waited < 1.6 && timeout.Message.Contains("1.2 s"), "Default timeout is not 1.2 s: " + waited.ToString("0.00") + " s, " + timeout.Message);
        return new { passed = true, timeout_seconds = Math.Round(waited, 3), checks = "loopback only, health with and without spirit_questions, 404 -> SpiritEndpointMissingException, "
            + "labels, confidences, plan, flags and probabilities, unknown labels -> Unclear/Other/None, Map clamping and nulls, Clip limits and Player/Spirit history, "
            + "no mutation, 400 for stage, mood and speaker, server failure, cancellation before and in flight, 1.2 s timeout" };
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

    // Recorded labels -> SpiritDecisionClient.Map -> SpiritPolicy.Answer -> the expected family.key.
    private static object Replay(JsonNode request, GatekeeperStory.Document story)
    {
        Type linesType = Type.GetType("SpiritLines"), advisorType = Type.GetType("SpiritAdvisor"), policyType = Type.GetType("SpiritPolicy");
        if (policyType == null || advisorType == null || linesType == null)
            return new { passed = true, skipped = "SpiritPolicy, SpiritAdvisor or SpiritLines is not compiled into this adapter", checks = "" };
        object policy;
        try
        {
            var lines = linesType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { File.ReadAllText(request["lines"].GetValue<string>()) });
            var rules = linesType.GetField("rules").GetValue(lines);
            string preference = (string)rules.GetType().GetField("reckoning_preference").GetValue(rules);
            var advisor = Activator.CreateInstance(advisorType, story, preference);
            policy = Activator.CreateInstance(policyType, lines, advisor, 1);
        }
        catch (Exception error) when (Unwrap(error) is NotImplementedException)
        {
            return new { passed = true, skipped = "SpiritPolicy or SpiritLines is still a stub: " + Unwrap(error).Message, checks = "" };
        }
        var reset = policyType.GetMethod("ResetEncounter");
        var answer = policyType.GetMethod("Answer");
        var results = new List<object>();
        var failures = new List<string>();
        foreach (var node in request["cases"].AsArray())
        {
            string id = node["id"].GetValue<string>();
            var cp = node["checkpoint"];
            string nodeId = cp["node"].GetValue<string>();
            var storyNode = story.nodes.Find(n => n.id == nodeId) ?? throw new InvalidOperationException("Unknown node " + nodeId + " in " + id);
            var checkpoint = new SpiritCheckpoint
            {
                Stage = Enum.Parse<SpiritStage>(cp["stage"].GetValue<string>()), NodeId = nodeId, Patience = cp["patience"].GetValue<int>(),
                HubRoundsDone = cp["hub_rounds_done"].GetValue<int>(), Choices = storyNode.choices.Select(c => c.label).ToArray(),
                UsedTargets = cp["used"].AsArray().Select(u => u.GetValue<string>()).ToArray(), PlayerName = "Mira", Outcome = GatekeeperOutcome.Playing
            };
            var r = node["recorded"];
            var reply = new SpiritDecisionClient.SpiritReply
            {
                scope = r["scope"].GetValue<string>(), scope_confidence = r["scope_confidence"].GetValue<float>(),
                intent = r["intent"].GetValue<string>(), intent_confidence = r["intent_confidence"].GetValue<float>(),
                plan = r["plan"].GetValue<string>(), plan_confidence = r["plan_confidence"].GetValue<float>(),
                flags = r["flags"].AsArray().Select(f => f.GetValue<string>()).ToArray(),
                flag_probabilities = new SpiritDecisionClient.FlagProbabilities { reckless = r["flag_probabilities"]["reckless"].GetValue<float>(), to_trola = r["flag_probabilities"]["to_trola"].GetValue<float>() }
            };
            var decision = SpiritDecisionClient.Map(reply, 1);
            SpiritCue cue;
            try
            {
                reset.Invoke(policy, new object[] { true });
                cue = (SpiritCue)answer.Invoke(policy, new object[] { checkpoint, decision, 0, 2 });
            }
            catch (Exception error) when (Unwrap(error) is NotImplementedException)
            {
                return new { passed = true, skipped = "SpiritPolicy.Answer is still a stub", checks = "" };
            }
            string family = cue == null ? "(null)" : string.IsNullOrEmpty(cue.Key) || cue.Family.EndsWith("." + cue.Key) ? cue.Family : cue.Family + "." + cue.Key;
            string expected = node["expected_family"].GetValue<string>();
            if (family != expected) failures.Add(id + ": expected " + expected + ", got " + family + " (" + cue?.Rule + ")");
            results.Add(new { id, family, rule = cue?.Rule ?? "", line = cue?.LineId ?? "", text = cue?.Text ?? "", decision = decision.Detail });
        }
        return new { passed = failures.Count == 0, failures, replays = results, checks = results.Count + " recorded /spirit label replays through SpiritPolicy.Answer" };
    }
    private static Exception Unwrap(Exception error) { while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException; return error; }
}
