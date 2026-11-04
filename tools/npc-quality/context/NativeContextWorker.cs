// Resident managed worker for the Gemma context experiment. It calls the installed Xybrid 0.16.0 native
// library through the pinned 0.16.0 generated bindings; NPC_NATIVE_LIB names that library.
// Every request builds a fresh native context (SetSystem, then Push per historical turn), verifies what the
// context holds, streams the final user turn once with a native cancellation token, and disposes the context.
// Generated text is never pushed, so cancelled, rejected or earlier output cannot reach a later request.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using XybridBolt;

static class NativeContextWorker
{
    static readonly string[] Stops = { "<turn|>", "<|turn>" }; // GatekeeperNpcModel.Generate

    static XybridEnvelope Text(string value, string role) =>
        new XybridEnvelope(new XybridEnvelopeKind.Text(value), new[] { new XybridMetadataEntry("xybrid.role", role) });

    static (string role, string text) Read(XybridEnvelope envelope)
    {
        string role = envelope.Metadata.Where(m => m.Key == "xybrid.role").Select(m => m.Value).LastOrDefault();
        return (role, envelope.Kind is XybridEnvelopeKind.Text text ? text.Value : null);
    }

    // Rebuild the native context and confirm it holds exactly the requested system and turns.
    static XybridConversationContext Build(string persona, IReadOnlyList<(string role, string text)> turns, JsonObject receipt)
    {
        var context = new XybridConversationContext();
        try
        {
            context.SetSystem(Text(persona, "system"));
            foreach (var (role, text) in turns)
            {
                if (role != "user" && role != "assistant") throw new InvalidOperationException("History roles must be user or assistant, not " + role);
                context.Push(Text(text, role));
            }
            var held = context.History().Select(Read).ToArray();
            if (!context.HasSystem() || context.HistoryLen() != turns.Count || held.Length != turns.Count
                || !held.Select(h => h.role + "\0" + h.text).SequenceEqual(turns.Select(t => t.role + "\0" + t.text)))
                throw new InvalidOperationException("Native context does not hold exactly the requested turns");
            receipt["context_id"] = context.Id();
            receipt["native_history"] = new JsonArray(held.Select(h => (JsonNode)new JsonObject { ["role"] = h.role, ["text"] = h.text }).ToArray());
            return context;
        }
        catch { context.Dispose(); throw; }
    }

    static List<(string role, string text)> Turns(JsonNode request) =>
        (request["messages"]?.AsArray() ?? new JsonArray()).Select(m => (m["role"].GetValue<string>(), m["text"].GetValue<string>())).ToList();

    static void Main(string[] args)
    {
        var native = Environment.GetEnvironmentVariable("NPC_NATIVE_LIB") ?? throw new InvalidOperationException("NPC_NATIVE_LIB must name the installed native library");
        NativeLibrary.SetDllImportResolver(typeof(XybridModel).Assembly, (name, assembly, path) => name == "xybrid_bolt" ? NativeLibrary.Load(native) : IntPtr.Zero);
        if (args.Length == 1 && args[0] == "--context-checks") { Console.WriteLine(ContextChecks().ToJsonString()); return; }
        using var model = XybridModel.FromDirectory(args[0]);
        Console.WriteLine(new JsonObject { ["ready"] = true }.ToJsonString());
        string input;
        while ((input = Console.ReadLine()) != null)
        {
            var request = JsonNode.Parse(input);
            var receipt = new JsonObject { ["id"] = request["id"]?.GetValue<string>() };
            try
            {
                if (request["command"]?.GetValue<string>() == "inspect")
                {
                    // No inference: rebuild a context and report what it holds (isolation checks).
                    using var inspected = Build(request["persona"].GetValue<string>(), Turns(request), receipt);
                    Console.WriteLine(receipt.ToJsonString());
                    continue;
                }
                Generate(model, request, receipt);
            }
            catch (Exception error) { receipt["error"] = error.ToString(); }
            Console.WriteLine(receipt.ToJsonString());
        }
    }

    static void Generate(XybridModel model, JsonNode request, JsonObject receipt)
    {
        uint max = request["max_tokens"]?.GetValue<uint>() ?? 112u;
        string grammar = request["grammar"]?.GetValue<string>();
        double timeout = request["timeout_seconds"]?.GetValue<double>() ?? 90;
        int cancelAfter = request["cancel_after_tokens"]?.GetValue<int>() ?? 0; // transport diagnostic only
        var config = new XybridGenerationConfig(max, 1f, .95f, 0f, 64u, 1f, Stops, grammar, Array.Empty<XybridToolDefinition>());
        var options = new XybridRunOptions(config, Array.Empty<XybridAbortSignal>(), false, 0, null);
        var events = new JsonArray();
        var clock = Stopwatch.StartNew();
        double? first = null;
        string finish = null;
        using var context = Build(request["persona"].GetValue<string>(), Turns(request), receipt);
        using var cancel = new XybridCancellationToken();
        int running = 1, timedOut = 0;
        // Cooperative watchdog: it cancels the native token, which the run checks between tokens, so a run stuck in
        // load or prefill is not interrupted; run_context.py's per-request timeout is the fail-closed backstop.
        using var watchdog = new Timer(_ =>
        {
            lock (cancel) if (Volatile.Read(ref running) == 1) { timedOut = 1; cancel.Cancel(); }
        }, null, TimeSpan.FromSeconds(timeout), Timeout.InfiniteTimeSpan);
        ulong stream = 0;
        try
        {
            stream = model.RunStreamWithContext(Text(request["prompt"].GetValue<string>(), "user"), context, options, cancel);
            int tokens = 0;
            while (true)
            {
                var item = model.StreamNext(stream);
                if (item.Kind == XybridStreamEventKind.Complete) break;
                if (item.Kind != XybridStreamEventKind.Token || item.Token is not { } token) continue;
                double elapsed = clock.Elapsed.TotalSeconds;
                if (!string.IsNullOrEmpty(token.Token)) { first ??= elapsed; tokens++; }
                finish = token.FinishReason ?? finish;
                events.Add(new JsonObject { ["seconds"] = elapsed, ["text"] = token.Token, ["token_id"] = token.TokenId });
                if (cancelAfter > 0 && tokens >= cancelAfter && !cancel.IsCancelled()) { cancel.Cancel(); receipt["cancel_requested_seconds"] = elapsed; }
            }
            lock (cancel) Volatile.Write(ref running, 0); // a late watchdog tick cannot mark a finished run as timed out
            var result = model.StreamResult(stream);
            if (result.ExecutionTarget != XybridExecutionTarget.Local) throw new InvalidOperationException("Expected local execution");
            receipt["raw"] = result.Envelope.Kind is XybridEnvelopeKind.Text text ? text.Value : "";
            var metadata = new JsonObject();
            foreach (var group in result.Envelope.Metadata.GroupBy(m => m.Key)) metadata[group.Key] = group.Last().Value;
            metadata["execution_target"] = "local";
            receipt["metadata"] = metadata;
            var m = result.Metrics;
            receipt["metrics"] = new JsonObject
            {
                ["total_ms"] = m.TotalMs, ["ttft_ms"] = m.TtftMs, ["tokens_per_second"] = m.TokensPerSecond, ["prefill_tps"] = m.PrefillTps,
                ["decode_tps"] = m.DecodeTps, ["tokens_out"] = m.TokensOut
            };
        }
        catch (XybridErrorException error) when (error.Error is XybridError.Cancelled || cancel.IsCancelled())
        {
            receipt["raw"] = "";
            receipt["error"] = "cancelled: " + error.Error;
        }
        finally
        {
            lock (cancel) Volatile.Write(ref running, 0);
            if (stream != 0) model.StreamClose(stream);
            receipt["cancelled"] = cancel.IsCancelled();
            receipt["timed_out"] = timedOut == 1;
            receipt["seconds"] = clock.Elapsed.TotalSeconds;
            receipt["ttft_seconds"] = first;
            receipt["finish_reason"] = finish;
            receipt["events"] = events;
        }
    }

    // Native context behavior without a model: role round trips, exact history, isolation between contexts and clear.
    static JsonObject ContextChecks()
    {
        var passed = new JsonArray();
        void Check(bool ok, string what) { if (!ok) throw new InvalidOperationException("Native context check failed: " + what); passed.Add(what); }
        var turns = new List<(string, string)> { ("user", "My name is Mira."), ("assistant", "Noted.\nMind the gate."), ("user", "Thanks.") };
        var receipt = new JsonObject();
        using (var first = Build("System A", turns, receipt))
        {
            Check(first.HistoryLen() == 3, "SetSystem and Push hold three turns");
            Check(first.History().Select(Read).Select(t => t.role).SequenceEqual(new[] { "user", "assistant", "user" }), "Roles survive the native round trip");
            Check(first.History().Select(Read).ElementAt(1).text == "Noted.\nMind the gate.", "Multi-line turn text is preserved");
            using var second = Build("System B", new List<(string, string)>(), new JsonObject());
            Check(second.HistoryLen() == 0 && first.HistoryLen() == 3, "A fresh context starts empty and leaves others intact");
            first.Clear();
            Check(first.HistoryLen() == 0 && first.HasSystem(), "Clear drops history and keeps the system turn");
        }
        bool rejected = false;
        try { using var bad = Build("System", new List<(string, string)> { ("model", "x") }, new JsonObject()); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unknown roles are rejected before inference");
        using (var cancel = new XybridCancellationToken())
        {
            Check(!cancel.IsCancelled(), "A new native cancellation token is not cancelled");
            cancel.Cancel();
            Check(cancel.IsCancelled(), "Native cancellation token reports cancellation");
        }
        return new JsonObject { ["passed"] = true, ["checks"] = passed };
    }
}
