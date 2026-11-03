using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Optional local d1 decisions for free-form replies; see docs/decision-layer.md. The scene never
// depends on the server: while it is off or unreachable, replies use built-in
// fact routes and neutral authored replies. A request that fails mid-turn costs one clarification.
public sealed class GatekeeperDecisionService : MonoBehaviour, IGatekeeperDecisionSource
{
    // F4 toggle, shared across encounter restarts; "-xybridDecisions off" starts with it off and
    // "-xybridDecisions http://127.0.0.1:<port>/" selects another local server.
    public static bool UseD1 = true;
    public bool IsAvailable => UseD1 && connected;
    private GatekeeperDecisionClient client;
    private volatile bool connected;
    private volatile string status = "connecting", lastError = "";
    // After failed decisions the health check waits 5, 10 ... 30 s before reconnecting, so a
    // d1 that answers health but fails or stalls on decisions costs one clarification per wait.
    private int failures;
    private long retryAt;
    private bool reported;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

    public static GatekeeperDecisionService Prepare()
    {
        var owner = FindAnyObjectByType<GatekeeperController>();
        if (owner == null) throw new InvalidOperationException("A gatekeeper controller is required.");
        return owner.TryGetComponent(out GatekeeperDecisionService service) ? service : owner.gameObject.AddComponent<GatekeeperDecisionService>();
    }

    public string Describe()
    {
        if (client == null) return "Decisions: built-in fact routes (" + status + ")";
        if (!UseD1) return "Decisions: built-in facts and authored replies (d1 off)";
        return connected ? "Decisions: d1 " + status + " at " + client.Endpoint
            : "Decisions: d1 " + status + " at " + client.Endpoint + (string.IsNullOrEmpty(lastError) ? "" : " (" + lastError + ")")
              + "; built-in facts and authored replies";
    }

    private void Awake()
    {
        string endpoint = GatekeeperDecisionClient.DefaultEndpoint;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-xybridDecisions");
        if (at >= 0 && at + 1 < args.Length)
        {
            if (args[at + 1] == "off") UseD1 = false; else endpoint = args[at + 1];
        }
        try { client = new GatekeeperDecisionClient(endpoint); }
        catch (Exception error) when (error is ArgumentException || error is UriFormatException)
        {
            status = "unavailable: " + error.Message;
            Debug.LogWarning("[Trola] d1 decisions disabled: " + error.Message, this);
            return;
        }
        StartCoroutine(Probe());
    }

    // The server may start before or after Play Mode; reconnect whenever it answers.
    private IEnumerator Probe()
    {
        var pause = new WaitForSecondsRealtime(3);
        while (true)
        {
            if (!UseD1) { connected = false; status = "off"; }
            else
            {
                var health = client.HealthAsync(lifetime.Token);
                while (!health.IsCompleted) yield return null;
                var result = health.Status == TaskStatus.RanToCompletion ? health.Result : null;
                bool ready = result != null && result.ready && result.SupportsTopics;
                if (ready && !connected && DateTime.UtcNow.Ticks < Interlocked.Read(ref retryAt)) status = "waiting after a failed decision";
                else if (ready && !connected)
                {
                    string revision = result.revision ?? "";
                    status = "ready (" + result.device + ", " + (revision.Length > 8 ? revision.Substring(0, 8) : revision) + ")";
                    lastError = ""; connected = true; reported = true;
                    Debug.Log("[Trola] d1 decisions " + status + " at " + client.Endpoint, this);
                }
                else if (!ready)
                {
                    if (connected) Debug.LogWarning("[Trola] d1 decisions lost; free-form replies use built-in facts and authored replies until it answers again.", this);
                    else if (!reported)
                        Debug.Log("[Trola] d1 decisions offline at " + client.Endpoint + "; free-form replies use built-in facts and authored replies. Start tools/npc-quality/serve_decisions.py to connect.", this);
                    connected = false; reported = true; status = "offline";
                    if (health.IsFaulted) lastError = health.Exception.GetBaseException().Message;
                    else if (result != null && result.ready && !result.SupportsTopics) lastError = "restart serve_decisions.py for topic questions";
                    else if (result != null && !string.IsNullOrEmpty(result.error)) lastError = "d1 " + result.error;
                }
            }
            yield return pause;
        }
    }

    public async Task<GatekeeperDecision> DecideAsync(GatekeeperConversationSnapshot state, string message, CancellationToken cancellation)
    {
        try
        {
            var decision = await client.DecideAsync(state, message, cancellation).ConfigureAwait(false);
            Interlocked.Exchange(ref failures, 0);
            return decision;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Later replies keep the earlier path until the health probe reconnects.
            int failed = Interlocked.Increment(ref failures);
            Interlocked.Exchange(ref retryAt, DateTime.UtcNow.AddSeconds(Math.Min(30, 5 * failed)).Ticks);
            connected = false; status = "request failed"; lastError = error.Message;
            Debug.LogWarning("[Trola] d1 decision failed: " + error.Message); // Worker thread: no object context.
            throw;
        }
    }

    private void OnDestroy() => lifetime.Cancel();
}
