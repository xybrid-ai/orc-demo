using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Talks to tools/npc-quality/serve_decisions.py on this machine. The server owns the pinned d1
// checkpoint and its questions; this sends the snapshot and new message, then maps the labels onto
// the closed decision contract. Names, amounts and effects never come from d1.
public sealed class GatekeeperDecisionClient
{
    public const string DefaultEndpoint = "http://127.0.0.1:8765/";
    [Serializable] private sealed class Turn { public string speaker, text; }
    [Serializable] private sealed class Request
    {
        public string message, name, business, outcome;
        public int patience;
        public string[] approaches;
        public Turn[] history;
    }
    [Serializable] public sealed class Reply
    {
        public string scope, intent;
        public string[] facts;
        public float scope_confidence, intent_confidence;
    }
    [Serializable] public sealed class Health
    {
        public bool ready;
        public string model, revision, device, error;
    }
    private static readonly Dictionary<string, GatekeeperScope> Scopes = new Dictionary<string, GatekeeperScope>(StringComparer.Ordinal)
    {
        ["in_world"] = GatekeeperScope.InWorld, ["out_of_world"] = GatekeeperScope.OutOfWorld,
        ["role_override"] = GatekeeperScope.RoleOverride, ["unclear"] = GatekeeperScope.Unclear
    };
    private static readonly Dictionary<string, GatekeeperDialogueIntent> Intents = new Dictionary<string, GatekeeperDialogueIntent>(StringComparer.Ordinal)
    {
        ["other"] = GatekeeperDialogueIntent.Other, ["player_facts"] = GatekeeperDialogueIntent.PlayerFacts,
        ["npc_question"] = GatekeeperDialogueIntent.NpcQuestion, ["offer_recall"] = GatekeeperDialogueIntent.OfferRecall,
        ["admission_status"] = GatekeeperDialogueIntent.AdmissionStatus, ["insult"] = GatekeeperDialogueIntent.Insult,
        ["praise"] = GatekeeperDialogueIntent.Praise, ["apology"] = GatekeeperDialogueIntent.Apology,
        ["player_fear"] = GatekeeperDialogueIntent.PlayerFear, ["farewell"] = GatekeeperDialogueIntent.Farewell
    };
    private static readonly Dictionary<string, GatekeeperFact> Facts = new Dictionary<string, GatekeeperFact>(StringComparer.Ordinal)
    { ["player_name"] = GatekeeperFact.PlayerName, ["player_business"] = GatekeeperFact.Business, ["other_player_fact"] = GatekeeperFact.Unknown };
    // One pooled client: no proxy lookup for loopback, and no 100-continue round trip on Mono.
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.ExpectContinue = false;
        return http;
    }

    public readonly Uri Endpoint;
    private readonly TimeSpan decisionTimeout, healthTimeout;
    public GatekeeperDecisionClient(string endpoint, double decisionSeconds = 4, double healthSeconds = 1)
    {
        Endpoint = new Uri(endpoint.EndsWith("/") ? endpoint : endpoint + "/", UriKind.Absolute);
        // Player text stays on this machine, like the NPC model.
        if (!Endpoint.IsLoopback || Endpoint.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("d1 decisions must use http on this machine (127.0.0.1).", nameof(endpoint));
        decisionTimeout = TimeSpan.FromSeconds(decisionSeconds); healthTimeout = TimeSpan.FromSeconds(healthSeconds);
    }

    public Task<Health> HealthAsync(CancellationToken cancellation) => SendAsync<Health>(HttpMethod.Get, "health", null, healthTimeout, cancellation);

    public async Task<GatekeeperDecision> DecideAsync(GatekeeperConversationSnapshot state, string message, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string body = JsonUtility.ToJson(Build(state, message));
        var timer = Stopwatch.StartNew();
        var reply = await SendAsync<Reply>(HttpMethod.Post, "decide", body, decisionTimeout, cancellation).ConfigureAwait(false);
        return Map(reply, message, timer.Elapsed.TotalMilliseconds);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string body, TimeSpan limit, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(limit);
        using var request = new HttpRequestMessage(method, new Uri(Endpoint, path));
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("d1 server returned " + (int)response.StatusCode + ": " + Clip(text, 160));
            return JsonUtility.FromJson<T>(text);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("d1 did not answer within " + limit.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s.");
        }
    }

    // The server checks the same limits; the last 12 turns match the spike's state.
    private static Request Build(GatekeeperConversationSnapshot state, string message)
    {
        var turns = new List<Turn>();
        for (int i = Math.Max(0, state.History.Count - 12); i < state.History.Count; i++)
        {
            string entry = state.History[i];
            int colon = entry.IndexOf(": ", StringComparison.Ordinal);
            string speaker = colon < 0 ? null : entry.Substring(0, colon);
            // The controller records the NPC as "Troll"; the decision state names her.
            if (speaker == "Troll") speaker = "Trola";
            if (speaker == "Player" || speaker == "Trola") turns.Add(new Turn { speaker = speaker, text = Clip(entry.Substring(colon + 2), 480) });
        }
        var approaches = new List<string>();
        foreach (string approach in state.Approaches) if (approaches.Count < 8) approaches.Add(Clip(approach, 40));
        return new Request
        {
            message = Clip(message, 480), name = Clip(state.Name, 80), business = Clip(state.Business, 160),
            outcome = state.Outcome.ToString(), patience = Math.Max(0, Math.Min(100, state.Patience)),
            approaches = approaches.ToArray(), history = turns.ToArray()
        };
    }

    // Unknown labels become Unclear rather than a guessed intent.
    public static GatekeeperDecision Map(Reply reply, string message, double milliseconds)
    {
        string timing = "; " + milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
        if (reply == null || !Scopes.TryGetValue(reply.scope ?? "", out var scope) || !Intents.TryGetValue(reply.intent ?? "", out var intent))
            return Unclear("d1 returned unknown labels " + reply?.scope + "/" + reply?.intent + timing);
        var facts = GatekeeperFact.None;
        foreach (string name in reply.facts ?? Array.Empty<string>())
        {
            if (!Facts.TryGetValue(name ?? "", out var fact)) return Unclear("d1 returned unknown fact slot " + name + timing);
            facts |= fact;
        }
        // d1's intent question can file "Do you remember what I sell?" as a question about Trola.
        // A requested player-fact slot then routes it to the factual answer, which costs no patience;
        // insults, offers, admission and farewells keep their own routes. d1 also reads statements
        // such as "My name is Bob, what's yours?" as fact requests, so authored fact answers need a
        // request; anything else gets a written reply.
        bool asks = GatekeeperFactQuestions.AsksForFacts(message);
        bool promoted = asks && scope == GatekeeperScope.InWorld && facts != GatekeeperFact.None
            && (intent == GatekeeperDialogueIntent.NpcQuestion || intent == GatekeeperDialogueIntent.Other);
        bool statement = !asks && intent == GatekeeperDialogueIntent.PlayerFacts;
        if (promoted) intent = GatekeeperDialogueIntent.PlayerFacts;
        if (statement) intent = GatekeeperDialogueIntent.Other;
        if (intent != GatekeeperDialogueIntent.PlayerFacts) facts = GatekeeperFact.None;
        else
        {
            // Explicit forms the built-in route recognizes still fill slots d1 left out.
            var known = GatekeeperFactQuestions.Interpret(message);
            if (known != null && known.Intent == GatekeeperDialogueIntent.PlayerFacts) facts |= known.Facts;
        }
        string detail = "d1 " + reply.scope + " " + reply.scope_confidence.ToString("0.00", CultureInfo.InvariantCulture)
            + " / " + reply.intent + " " + reply.intent_confidence.ToString("0.00", CultureInfo.InvariantCulture)
            + (promoted ? " → player_facts" : statement ? " → other (not a request)" : "")
            + (facts != GatekeeperFact.None ? " / " + facts : "") + timing;
        return new GatekeeperDecision(scope, intent, facts, detail);
    }
    private static GatekeeperDecision Unclear(string detail) => new GatekeeperDecision(GatekeeperScope.Unclear, GatekeeperDialogueIntent.Other, GatekeeperFact.None, detail);
    private static string Clip(string text, int limit) => string.IsNullOrEmpty(text) ? "" : text.Length <= limit ? text : text.Substring(0, limit);
}
