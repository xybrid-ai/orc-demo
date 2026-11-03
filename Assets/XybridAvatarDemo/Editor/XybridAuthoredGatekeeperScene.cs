using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class XybridAuthoredGatekeeperScene
{
    private static GameObject bakeOwner;
    private static GatekeeperLiveSpeech baker;
    private static GatekeeperScenario baking;
    private static int bakeIndex;
    private static Task<GatekeeperLiveSpeech.Generated> pending;
    private static string bakeReport;

    [MenuItem("Xybrid/Avatar Demo/Configure Authored Gatekeeper")]
    public static void Configure()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before authoring the scene.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open the existing gatekeeper scene.");
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null || controller.scenario == null) throw new InvalidOperationException("The existing conversation is required.");
        var scenario = controller.scenario;
        Undo.RecordObject(scenario, "Author the troll gatekeeper encounter");
        scenario.authoredSource = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/XybridAvatarDemo/Scenarios/TrolaAuthoredConversation.json");
        if (scenario.authoredSource == null) throw new FileNotFoundException("Install the authored source JSON.");
        scenario.authoredConversation = scenario.captureName = true;
        scenario.debugStartInFreeForm = false;
        scenario.initialPatience = 3;
        scenario.lines = scenario.ReadStory().lines;
        // A rerun reuses already baked static assets.
        foreach (var line in scenario.lines)
        {
            if (GatekeeperNames.HasSlots(line.text)) continue;
            string prefix = "Assets/XybridAvatarDemo/Audio/Authored/" + line.id;
            line.audio = AssetDatabase.LoadAssetAtPath<AudioClip>(prefix + ".wav");
            line.mouthCues = AssetDatabase.LoadAssetAtPath<TextAsset>(prefix + ".json");
        }
        if (!scenario.Validate(out var error)) throw new InvalidOperationException(error);
        var live = controller.GetComponent<GatekeeperLiveSpeech>();
        if (live == null) live = Undo.AddComponent<GatekeeperLiveSpeech>(controller.gameObject);
        Undo.RecordObject(controller, "Connect authored speech"); controller.liveSpeech = live;
        controller.subtitleHoldMaximum = Mathf.Max(controller.subtitleHoldMaximum, 10); // A voiceless whole turn (~110 characters) stays readable.
        var atmosphere = UnityEngine.Object.FindFirstObjectByType<TrolaAtmosphere>();
        if (atmosphere == null || atmosphere.fireLights == null || atmosphere.fireLights.Length < 2) throw new InvalidOperationException("The existing torch scene is required.");
        Undo.RecordObject(atmosphere, "Connect patience to torchlight"); atmosphere.gatekeeper = controller;
        var view = UnityEngine.Object.FindFirstObjectByType<GatekeeperView>();
        if (view != null)
        {
            Undo.RecordObject(view.sentence, "Use literal slot subtitles"); view.sentence.supportRichText = false;
            view.sentence.verticalOverflow = VerticalWrapMode.Overflow; // Same as GatekeeperView.OnEnable: a whole turn wraps to two lines.
            view.Refresh();
        }
        EditorUtility.SetDirty(scenario);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Debug.Log("[Gatekeeper] Authored two-slot, four-approach, three-ending encounter saved.");
    }

    [MenuItem("Xybrid/Avatar Demo/Toggle Direct Free Form Start")]
    public static void ToggleDirectFreeForm()
    {
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null) throw new InvalidOperationException("Open the gatekeeper scene.");
        var scenario = controller.scenario;
        Undo.RecordObject(scenario, "Toggle direct free-form start");
        scenario.debugStartInFreeForm = !scenario.debugStartInFreeForm;
        EditorUtility.SetDirty(scenario); AssetDatabase.SaveAssets();
        if (EditorApplication.isPlaying) controller.Restart();
        Debug.Log("[Gatekeeper] Direct free-form start: " + scenario.debugStartInFreeForm);
    }

    // Bake only immutable text. Names and business always use live, matching cues.
    public static void Bake(string reportPath)
    {
        if (EditorApplication.isPlaying || baking != null) throw new InvalidOperationException("Bake in Edit Mode once.");
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        baking = controller.scenario; bakeReport = reportPath; bakeIndex = 0; pending = null;
        bakeOwner = new GameObject("Authored gatekeeper voice bake") { hideFlags = HideFlags.HideAndDontSave };
        baker = bakeOwner.AddComponent<GatekeeperLiveSpeech>();
        baker.modelBundle = controller.liveSpeech.modelBundle; baker.voiceId = controller.liveSpeech.voiceId;
        baker.rhubarbExecutable = controller.liveSpeech.rhubarbExecutable;
        Directory.CreateDirectory("Assets/XybridAvatarDemo/Audio/Authored");
        File.WriteAllText(bakeReport, "{\"complete\":false}");
        EditorApplication.update += BakeTick;
    }
    private static void BakeTick()
    {
        try
        {
            while (bakeIndex < baking.lines.Count && (GatekeeperNames.HasSlots(baking.lines[bakeIndex].text) || baking.lines[bakeIndex].audio != null)) bakeIndex++;
            if (bakeIndex == baking.lines.Count)
            {
                EditorUtility.SetDirty(baking); AssetDatabase.SaveAssets();
                File.WriteAllText(bakeReport, "{\"complete\":true,\"passed\":true,\"static_lines\":" + baking.lines.FindAll(l => !GatekeeperNames.HasSlots(l.text)).Count + "}");
                StopBake(); return;
            }
            var line = baking.lines[bakeIndex];
            if (pending == null) { pending = baker.Prepare(line.text); return; }
            if (!pending.IsCompleted) return;
            var data = pending.GetAwaiter().GetResult();
            // Offline baking needs the full cue track; live playback needs only PCM readiness.
            if (!data.analysis.IsCompleted) return;
            var analysis = data.analysis.GetAwaiter().GetResult();
            data.cues = analysis.cues; data.lipSyncError = analysis.error;
            // Baked lines must carry real Rhubarb cues; the loudness-jaw fallback is for live lines only.
            if (data.cues == null) throw new InvalidOperationException("Lip sync failed for " + line.id + ": " + data.lipSyncError);
            string prefix = "Assets/XybridAvatarDemo/Audio/Authored/" + line.id;
            WriteWav(prefix + ".wav", data.pcm); File.WriteAllText(prefix + ".json", data.cues);
            AssetDatabase.ImportAsset(prefix + ".wav", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(prefix + ".json", ImportAssetOptions.ForceSynchronousImport);
            line.audio = AssetDatabase.LoadAssetAtPath<AudioClip>(prefix + ".wav");
            line.mouthCues = AssetDatabase.LoadAssetAtPath<TextAsset>(prefix + ".json");
            if (line.audio == null || line.mouthCues == null || !XybridAvatarPlayer.ValidateCues(line.mouthCues, line.audio, out var error)) throw new InvalidOperationException("Baked cues failed: " + line.id);
            bakeIndex++; pending = null;
            File.WriteAllText(bakeReport, "{\"complete\":false,\"processed\":" + bakeIndex + "}");
        }
        catch (Exception exception)
        {
            File.WriteAllText(bakeReport, JsonUtility.ToJson(new BakeFailure { complete = true, error = exception.Message }));
            Debug.LogException(exception); StopBake();
        }
    }
    [Serializable] private sealed class BakeFailure { public bool complete; public string error; }
    private static void StopBake()
    {
        EditorApplication.update -= BakeTick;
        if (bakeOwner != null) UnityEngine.Object.DestroyImmediate(bakeOwner);
        bakeOwner = null; baker = null; baking = null; pending = null;
    }
    private static void WriteWav(string path, byte[] pcm)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(24000); w.Write(48000); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        }
    }
}
