using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

// One explicit Editor smoke check. No build, benchmark, persistent queue or
// automatic Play/exit watcher. Leaves the game ready for the player afterward.
public static class XybridNpcEditorCheck
{
    private static GatekeeperController controller;
    private static GatekeeperNpcModel model;
    private static string reportPath;
    private static int stage;
    private static double deadline;
    private static GatekeeperScenario.Line generated;
    [Serializable] private sealed class Report
    {
        public bool complete, passed;
        public string model, text, emotion, error;
        public bool constrained_json, live_voice, authored_expulsion;
    }
    public static void Begin(string path)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null) throw new InvalidOperationException("Open the gatekeeper scene.");
        reportPath = path;
        deadline = EditorApplication.timeSinceStartup + 240;
        stage = 0; generated = null;
        model = GatekeeperNpcModel.Prepare(controller.scenario);
        var state = new GatekeeperConversationState(new GatekeeperEncounter(3, 5, true));
        state.Encounter.CaptureName("Rowan", controller.scenario.nameBlocklist, "Worm");
        state.Encounter.CaptureBusiness("trade", controller.scenario.nameBlocklist);
        state.Encounter.FinishStory(GatekeeperOutcome.Passed);
        state.RecordApproach("camp");
        state.Record("Player", "How is the camp?");
        state.Record("Troll", "Cook burns the stew. Captain calls it discipline.");
        controller.UseLineSource(new FreeFormGatekeeperLineSource(controller.scenario, model), state);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"complete\":false}");
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }
    private static void Tick()
    {
        try
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Play Mode ended.");
            if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("NPC Editor smoke check timed out.");
            if (controller.State == GatekeeperController.Phase.VoiceFailed)
                throw new InvalidOperationException("Game voice failed: " + controller.CurrentText);
            if (stage == 0 && !model.IsReady)
            {
                if (!string.IsNullOrEmpty(model.LastError)) throw new InvalidOperationException("NPC loading failed: " + model.LastError);
                return;
            }
            if (stage == 0 && controller.AcceptsText)
            {
                if (!controller.Submit("The cook burned my stew. What should I tell the captain?")) throw new InvalidOperationException("Player message was not accepted.");
                stage = 1;
            }
            bool playbackFinished = controller.State == GatekeeperController.Phase.AwaitingInput || controller.State == GatekeeperController.Phase.Ended;
            if (stage == 1 && controller.CurrentLineId == "free-model" &&
                (controller.character.IsSpeaking || playbackFinished))
            {
                if (model.LastReplyUsedFallback || string.IsNullOrWhiteSpace(model.LastRawReply)) throw new InvalidOperationException("Placeholder or fallback was used: " + model.LastError);
                if (!controller.CurrentWasLive) throw new InvalidOperationException("Generated line did not reach live speech.");
                if (controller.CurrentFallback != GatekeeperController.VoiceFallback.None || controller.IsSilentLine ||
                    controller.LastGenerated == null || controller.LastGenerated.clip == null)
                    throw new InvalidOperationException("Generated line did not complete live playback: " + controller.LastVoiceFailure);
                generated = controller.CurrentLine;
                stage = 2;
            }
            if (stage == 1 && controller.CurrentLineId == "free-fallback") throw new InvalidOperationException("Model failed: " + model.LastError);
            if (stage == 2 && !controller.character.IsSpeaking &&
                (controller.State == GatekeeperController.Phase.AwaitingInput || controller.State == GatekeeperController.Phase.Ended))
            {
                // Check the zero-patience authored sequence without another model
                // or speech run, as part of this same functional confirmation.
                var zero = new GatekeeperConversationState(new GatekeeperEncounter(0, 5, true));
                var ending = new FreeFormGatekeeperLineSource(controller.scenario, model);
                string[] ids = controller.scenario.ReadStory().nodes.Find(n => n.id == "expelled").variants[0].line_ids;
                foreach (string id in ids)
                {
                    var line = ending.NextLineAsync(zero, new GatekeeperPlayerInput(GatekeeperInputKind.Continue), CancellationToken.None).GetAwaiter().GetResult();
                    if (line == null || line.id != id) throw new InvalidOperationException("Authored expulsion sequence mismatch.");
                }
                ending.NextLineAsync(zero, new GatekeeperPlayerInput(GatekeeperInputKind.Continue), CancellationToken.None).GetAwaiter().GetResult();
                if (!ending.Presentation.Ended || zero.Encounter.Outcome != GatekeeperOutcome.Refused) throw new InvalidOperationException("Expulsion did not end the scene.");
                EditorApplication.update -= Tick;
                File.WriteAllText(reportPath, JsonUtility.ToJson(new Report { complete = true, passed = true, model = "Gemma4NPC-E2B-Q4_K_M", text = generated.text, emotion = generated.emotion, constrained_json = true, live_voice = true, authored_expulsion = true }, true));
                controller.Restart();
            }
        }
        catch (Exception error)
        {
            EditorApplication.update -= Tick;
            File.WriteAllText(reportPath, JsonUtility.ToJson(new Report { complete = true, passed = false, error = error.Message }, true));
            Debug.LogException(error);
        }
    }
}
