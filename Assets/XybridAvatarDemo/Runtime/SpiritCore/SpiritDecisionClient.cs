using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// A 404 from POST /spirit: the d1 server predates Vesper and needs a restart; she uses keywords meanwhile.
public sealed class SpiritEndpointMissingException : Exception { public SpiritEndpointMissingException(string message) : base(message) { } }

// Asks tools/npc-quality/serve_decisions.py (POST /spirit) what a thought to Vesper means. The server owns
// the question set (decision-spirit-*.json); this clips the request, keeps it on this machine and maps the
// labels onto the closed spirit contract. SpiritPolicy, never d1, picks what she says. Separate from
// GatekeeperDecisionClient on purpose: a slow thought must never take Trola off d1.
public sealed class SpiritDecisionClient : ISpiritDecider
{
    public const string DefaultEndpoint = "http://127.0.0.1:8765/";
    // The server enforces the same limits; patience never leaves the game, only the mood band does.
    public const int MessageChars = 80, NameChars = 80, BusinessChars = 160, LabelChars = 16, OptionCount = 4, OptionChars = 40,
        SaidChars = 240, TurnCount = 6, TurnChars = 240;
    // Health lists these once the server has the spirit set primed.
    public static readonly string[] Questions = { "scope", "intent", "plan", "reckless", "to_trola" };
    [Serializable] public sealed class FlagProbabilities { public float reckless, to_trola; }
    [Serializable] public sealed class SpiritReply
    {
        public string scope, intent, plan; public float scope_confidence, intent_confidence, plan_confidence;
        public string[] flags; public FlagProbabilities flag_probabilities; public float seconds; public int input_tokens;
    }
    [Serializable] public sealed class SpiritHealth { public bool ready; public string model, revision, device, error; public string[] spirit_questions; }
    private static readonly Dictionary<string, SpiritScope> Scopes = new Dictionary<string, SpiritScope>(StringComparer.Ordinal)
    {
        ["in_world"] = SpiritScope.InWorld, ["out_of_world"] = SpiritScope.OutOfWorld, ["role_override"] = SpiritScope.RoleOverride, ["unclear"] = SpiritScope.Unclear
    };
    private static readonly Dictionary<string, SpiritIntent> Intents = new Dictionary<string, SpiritIntent>(StringComparer.Ordinal)
    {
        ["ask_advice"] = SpiritIntent.AskAdvice, ["doubt"] = SpiritIntent.Doubt, ["ask_about_trola"] = SpiritIntent.AskAboutTrola,
        ["agree"] = SpiritIntent.Agree, ["dismiss"] = SpiritIntent.Dismiss, ["state_plan"] = SpiritIntent.StatePlan,
        ["ask_about_spirit"] = SpiritIntent.AskAboutSpirit, ["ask_spirit_to_act"] = SpiritIntent.AskSpiritToAct, ["other"] = SpiritIntent.Other
    };
    private static readonly Dictionary<string, SpiritPlan> Plans = new Dictionary<string, SpiritPlan>(StringComparer.Ordinal)
    {
        ["flatter"] = SpiritPlan.Flatter, ["bribe"] = SpiritPlan.Bribe, ["threaten"] = SpiritPlan.Threaten, ["ask_camp"] = SpiritPlan.AskCamp,
        ["pay_toll"] = SpiritPlan.PayToll, ["share_joke"] = SpiritPlan.ShareJoke, ["demand_gate"] = SpiritPlan.DemandGate, ["none"] = SpiritPlan.None
    };
    // Its own pooled client: no proxy lookup for loopback, no 100-continue round trip on Mono.
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.ExpectContinue = false;
        return http;
    }

    public Uri Endpoint { get; }
    private readonly TimeSpan decisionTimeout, healthTimeout;
    public SpiritDecisionClient(string endpoint, double decisionSeconds = 1.2, double healthSeconds = 1)
    {
        if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("A d1 endpoint is required.", nameof(endpoint));
        Endpoint = new Uri(endpoint.EndsWith("/") ? endpoint : endpoint + "/", UriKind.Absolute);
        // Thoughts stay on this machine, like Trola's decisions.
        if (!Endpoint.IsLoopback || Endpoint.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Spirit decisions must use http on this machine (127.0.0.1).", nameof(endpoint));
        decisionTimeout = TimeSpan.FromSeconds(decisionSeconds); healthTimeout = TimeSpan.FromSeconds(healthSeconds);
    }

    public Task<SpiritHealth> HealthAsync(CancellationToken cancellation) => SendAsync<SpiritHealth>(HttpMethod.Get, "health", null, healthTimeout, cancellation);

    // null when /spirit is usable; otherwise why the spirit is on keywords.
    public static string Problem(SpiritHealth health)
    {
        if (health == null) return "no d1 health";
        if (!health.ready) return "d1 not ready" + (string.IsNullOrEmpty(health.error) ? "" : ": " + health.error);
        foreach (string question in Questions)
            if (Array.IndexOf(health.spirit_questions ?? Array.Empty<string>(), question) < 0) return "no /spirit (restart serve_decisions.py)";
        return null;
    }

    public async Task<SpiritDecision> DecideAsync(SpiritRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string body = UnityEngine.JsonUtility.ToJson(Clip(request));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var reply = await SendAsync<SpiritReply>(HttpMethod.Post, "spirit", body, decisionTimeout, cancellation).ConfigureAwait(false);
        return Map(reply, timer.Elapsed.TotalMilliseconds);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string body, TimeSpan limit, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(limit);
        using var request = new HttpRequestMessage(method, new Uri(Endpoint, path));
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        int status; string text;
        try
        {
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            status = (int)response.StatusCode;
        }
        // The caller gave up: only cancellation escapes, whatever the transport threw (Mono may wrap aborts).
        catch (Exception error) when (cancellation.IsCancellationRequested && !(error is OperationCanceledException))
        {
            throw new OperationCanceledException("Spirit decision cancelled.", error, cancellation);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("d1 /" + path + " did not answer within " + limit.TotalSeconds.ToString("0.0#", CultureInfo.InvariantCulture) + " s.", error);
        }
        cancellation.ThrowIfCancellationRequested();   // A reply that raced cancellation is stale.
        if (status == 404 && method == HttpMethod.Post) throw new SpiritEndpointMissingException("d1 server has no /" + path + "; restart serve_decisions.py.");
        if (status < 200 || status > 299) throw new HttpRequestException("d1 server returned " + status + ": " + Cut(text, 160));
        try { return UnityEngine.JsonUtility.FromJson<T>(text); }
        catch (Exception error) { throw new FormatException("d1 /" + path + " returned unreadable JSON.", error); }
    }

    // Never mutates the caller's request. Stage and mood pass through unchanged so the server rejects bad
    // values; history keeps the last 6 Player/Spirit turns (Trola's lines never enter her transcript).
    public static SpiritRequest Clip(SpiritRequest request)
    {
        request ??= new SpiritRequest();
        var options = new List<string>();
        foreach (string option in request.options ?? Array.Empty<string>())
            if (option != null && options.Count < OptionCount) options.Add(Cut(option, OptionChars));
        var turns = new List<SpiritTurn>();
        foreach (var turn in request.history ?? Array.Empty<SpiritTurn>())
            if (turn != null && (turn.speaker == "Player" || turn.speaker == "Spirit")) turns.Add(new SpiritTurn { speaker = turn.speaker, text = Cut(turn.text, TurnChars) });
        if (turns.Count > TurnCount) turns.RemoveRange(0, turns.Count - TurnCount);
        return new SpiritRequest
        {
            message = Cut(request.message?.Trim(), MessageChars), name = Cut(request.name, NameChars), business = Cut(request.business, BusinessChars),
            stage = Cut(request.stage, LabelChars), mood = Cut(request.mood, LabelChars), spirit_said = Cut(request.spirit_said, SaidChars),
            options = options.ToArray(), history = turns.ToArray()
        };
    }

    // Unknown labels fall back field by field (Unclear, Other, None) at zero confidence; the detail names them.
    public static SpiritDecision Map(SpiritReply reply, double milliseconds)
    {
        var decision = new SpiritDecision { Source = SpiritDecisionSource.D1, Ms = milliseconds };
        string timing = milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
        if (reply == null) { decision.Detail = "d1 returned no decision · " + timing; return decision; }
        var unknown = new List<string>();
        if (Scopes.TryGetValue(reply.scope ?? "", out var scope)) { decision.Scope = scope; decision.ScopeP = P(reply.scope_confidence); }
        else unknown.Add("scope " + reply.scope);
        if (Intents.TryGetValue(reply.intent ?? "", out var intent)) { decision.Intent = intent; decision.IntentP = P(reply.intent_confidence); }
        else unknown.Add("intent " + reply.intent);
        if (Plans.TryGetValue(reply.plan ?? "", out var plan)) { decision.Plan = plan; decision.PlanP = P(reply.plan_confidence); }
        else unknown.Add("plan " + reply.plan);
        foreach (string flag in reply.flags ?? Array.Empty<string>())
        {
            if (flag == "reckless") decision.Reckless = true;
            else if (flag == "to_trola") decision.ToTrola = true;
            else unknown.Add("flag " + flag);
        }
        decision.RecklessP = P(reply.flag_probabilities?.reckless ?? 0); decision.ToTrolaP = P(reply.flag_probabilities?.to_trola ?? 0);
        decision.Detail = "d1 " + Label(reply.scope, decision.ScopeP) + " · " + Label(reply.intent, decision.IntentP)
            + " · plan " + Label(reply.plan, decision.PlanP) + " · reckless " + F(decision.RecklessP) + " · to_trola " + F(decision.ToTrolaP)
            + (unknown.Count > 0 ? " · unknown " + string.Join(", ", unknown) : "") + " · " + timing;
        return decision;
    }
    private static float P(float p) => p > 1 ? 1 : p > 0 ? p : 0;   // NaN and negatives read as 0
    private static string F(float p) => p.ToString(".00", CultureInfo.InvariantCulture);
    private static string Label(string label, float p) => (string.IsNullOrEmpty(label) ? "?" : Cut(label, 32)) + " " + F(p);
    // Never splits a surrogate pair, so the server sees valid text.
    private static string Cut(string text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= limit) return text;
        int length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text.Substring(0, length);
    }
}
