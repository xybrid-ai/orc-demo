using System;
using UnityEngine;
using UnityEngine.InputSystem;

// Runtime debug controls; summary edits are applied by Jump, without saving the scene.
public sealed class GatekeeperSpeechControls : MonoBehaviour
{
    public GatekeeperController controller;
    private bool visible = true;
    public string debugName = "Mira", debugBusiness = "selling turnips", debugApproaches = "bribe";
    public int debugPatience = 3;
    public GatekeeperOutcome debugEnding = GatekeeperOutcome.Passed;
    private Vector2 scroll;
    private float nextSnapshot;
    private void SaveSnapshot()
    {
        if (controller == null || Time.realtimeSinceStartup < nextSnapshot) return;
        nextSnapshot = Time.realtimeSinceStartup + 1;
        var a = controller.character; var audio = a != null ? a.speech : null;
        var npc = controller.GetComponent<GatekeeperNpcModel>();
        var state = new System.Text.StringBuilder().AppendLine("UTC: " + DateTime.UtcNow.ToString("O"))
            .AppendLine("Phase: " + controller.State + "; queued: " + controller.HasQueuedReply + "; reply: " + controller.QueuedReplyText)
            .AppendLine("Audio: playing=" + audio?.isPlaying + "; paused=" + a?.IsPlaybackPaused + "; listenerPaused=" + AudioListener.pause + "; loop=" + audio?.loop)
            .AppendLine("Samples: " + audio?.timeSamples + " / " + audio?.clip?.samples + "; pitch=" + audio?.pitch)
            .AppendLine("NPC: ready=" + npc?.IsReady + "; error=" + npc?.LastError)
            .AppendLine(controller.TryGetComponent(out GatekeeperDecisionService decisions) ? decisions.Describe() : "Decisions: not prepared")
            .AppendLine(SpiritSignals.Describe())
            .AppendLine("Player: " + controller.PlayerName + "; business=" + controller.Business)
            .AppendLine("Speech text: " + GatekeeperLiveSpeech.StyleName(GatekeeperLiveSpeech.Style))
            .AppendLine("History:");
        if (controller.Conversation != null) foreach (var entry in controller.Conversation.History) state.AppendLine(entry);
        state.AppendLine("Lines:");
        foreach (var entry in controller.DebugLines)
            state.AppendLine(entry.line.debugSource + ": " + entry.line.text + SpokenAs(entry, "\n") + "\nDecision: " + entry.line.debugDecision + "\nRaw: " + entry.line.debugRaw);
        try {
            string folder = System.IO.Path.Combine(Application.persistentDataPath, "TrolaSpeechDebug");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "session-state.txt"), state.ToString());
        } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
    }
    // "Spoken as: ..." when the voice was given other words than the subtitle (a flowing Phonon line); else "".
    private static string SpokenAs(GatekeeperController.DebugLine entry, string prefix)
    {
        var voice = entry.voice;
        return voice != null && voice.speechText != null && voice.speechText != voice.text ? prefix + "Spoken as: " + voice.speechText : "";
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null) return;
        controller.gameObject.AddComponent<GatekeeperSpeechControls>().controller = controller;
    }
    public void Jump() => controller.DebugFreeForm(debugName, debugBusiness, debugApproaches, debugEnding, debugPatience);
    private void Update()
    {
        SaveSnapshot();
        if (Keyboard.current == null) return;
        if (Keyboard.current.f4Key.wasPressedThisFrame) visible = !visible;
        if (Keyboard.current.f5Key.wasPressedThisFrame) Jump();
    }
    private void OnGUI()
    {
        if (!visible || controller == null || controller.liveSpeech == null) return;
        GUILayout.BeginArea(new Rect(12, 12, Mathf.Min(590, Screen.width - 24), Mathf.Max(200, Screen.height - 170)), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.Label("Trola debug — F4 hides, F5 jumps to free-form");
        GUILayout.Label("Name"); debugName = GUILayout.TextField(debugName, 40);
        GUILayout.Label("Business"); debugBusiness = GUILayout.TextField(debugBusiness, 100);
        GUILayout.Label("Approaches (comma separated)"); debugApproaches = GUILayout.TextField(debugApproaches, 100);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Patience " + debugPatience);
        debugPatience = Mathf.RoundToInt(GUILayout.HorizontalSlider(debugPatience, 0, 5));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        foreach (var ending in new[] { GatekeeperOutcome.Passed, GatekeeperOutcome.Refused, GatekeeperOutcome.Liked })
            if (GUILayout.Toggle(debugEnding == ending, ending == GatekeeperOutcome.Passed ? "Passage" : ending.ToString(), GUI.skin.button)) debugEnding = ending;
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Jump / apply summary")) Jump();
        if (GUILayout.Button(controller.scenario.debugUsePlaceholderReplies ? "Replies: placeholder — use Gemma" : "Replies: Gemma — use placeholder"))
        { controller.scenario.debugUsePlaceholderReplies = !controller.scenario.debugUsePlaceholderReplies; Jump(); }
        // Applies from the next message; the current encounter continues.
        if (GUILayout.Button(GatekeeperDecisionService.UseD1 ? "Decisions: d1 — use built-in routes" : "Decisions: built-in routes — use d1"))
            GatekeeperDecisionService.UseD1 = !GatekeeperDecisionService.UseD1;
        if (GUILayout.Button("Restart authored encounter")) controller.Restart();
        var speech = controller.liveSpeech;
        GUILayout.Label(speech.provider == GatekeeperLiveSpeech.Provider.Phonon
            ? "Voice: live Phonon / cloned voice / " + (speech.phononEngine == PhononEngine.Zzz ? "zzz rc.9" : "xn-ptts 0.6.0") + "; " + speech.phononThreads + " CPU threads"
            : "Voice: live " + speech.provider + " / " + speech.kittenVoice + "; " + speech.kittenThreads + " threads; Accelerate " + speech.accelerate);
        GUILayout.Label((speech.provider == GatekeeperLiveSpeech.Provider.Phonon && speech.phononWholeLines ? "Whole lines, edge silence trimmed" : "Complete clauses")
            + "; played WAVs: " + System.IO.Path.Combine(Application.persistentDataPath, "TrolaSpeechDebug"));
        // One style for Trola and Vesper, remembered like her voice. Lines already prepared keep theirs, so audio she
        // prefetched is dropped; zzz reseeds every call, so Restart replays a route with the same audio per text.
        bool flowing = GatekeeperLiveSpeech.Style == GatekeeperLiveSpeech.SpeechStyle.Flowing;
        if (GUILayout.Button(flowing ? "Speech text: flowing — use as written" : "Speech text: as written — use flowing"))
        {
            GatekeeperLiveSpeech.SetStyle(flowing ? GatekeeperLiveSpeech.SpeechStyle.AsWritten : GatekeeperLiveSpeech.SpeechStyle.Flowing);
            var vesper = SpiritDirector.Instance;
            if (vesper != null && vesper.Voice != null) vesper.Voice.Stop(.05f);
        }
        GUILayout.Label("Phonon only; applies from the next line. Restart authored encounter replays the same route.");
        // Always one label (IMGUI layout); "Spirit: not installed" when the spirit is absent or bailed out.
        GUILayout.Label(SpiritSignals.Describe());
        // Vesper's staged voices (tools/setup_spirit_voice.py --voice all): picking one plays her hub line in it; F9 cycles.
        var spirit = SpiritDirector.Instance;
        if (spirit != null && spirit.Voice != null && spirit.Voice.Voices.Count > 1)
        {
            var voices = spirit.Voice.Voices;
            for (int row = 0; row < voices.Count; row += 3)
            {
                GUILayout.BeginHorizontal();
                for (int i = row; i < Mathf.Min(row + 3, voices.Count); i++)
                    if (GUILayout.Toggle(voices[i].Key == spirit.Voice.VoiceKey, voices[i].Name, GUI.skin.button) && spirit.Voice.SelectVoice(voices[i].Key)) spirit.PreviewVoice();
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button(spirit.CanPreviewVoice ? "Hear Vesper (F9: next voice)" : "Hear Vesper — when Trola is waiting")) spirit.PreviewVoice();
        }
        var npc = controller.GetComponent<GatekeeperNpcModel>();
        if (npc != null && !string.IsNullOrEmpty(npc.LastError)) GUILayout.Label("NPC: " + npc.LastError);
        // Always one label: a button below can add the service mid-event.
        GUILayout.Label(controller.TryGetComponent(out GatekeeperDecisionService decisions) ? decisions.Describe() : "Decisions: not prepared");
        GUILayout.Label("Conversation: " + controller.State + " (" + controller.PhaseElapsed.ToString("0") + " s); Send " + (controller.HasQueuedReply ? "queued until speech finishes" : controller.CanSubmitText ? "available" : "waiting"));
        GUILayout.Label("Current patience: " + controller.Patience);
        for (int i = controller.DebugLines.Count - 1; i >= 0; i--)
        {
            var entry = controller.DebugLines[i]; var line = entry.line;
            GUILayout.Label(line.id + " [" + line.debugSource + "] " + line.text);
            string spokenAs = SpokenAs(entry, "");
            if (spokenAs.Length > 0) GUILayout.Label(spokenAs);
            if (!string.IsNullOrEmpty(line.debugDecision)) GUILayout.Label("Decision: " + line.debugDecision);
            GUILayout.Label("Parsed: " + line.emotion + " / " + line.intensity.ToString("0.00") + "; patience delta " + line.patience_delta + "; " + entry.patienceBefore + " → " + entry.patienceAfter);
            double voice = entry.voice?.totalSynthesisSeconds ?? 0;
            double audio = entry.voice?.totalAudioSeconds ?? 0;
            GUILayout.Label("Language model " + line.debugModelSeconds.ToString("0.00") + " s; voice " + voice.ToString("0.00") + " s; audio " + audio.ToString("0.00") + " s; RTF " + (audio > 0 ? (voice / audio).ToString("0.00") : "pending"));
            GUILayout.Label("Raw model output: " + (string.IsNullOrEmpty(line.debugRaw) ? "(no model call)" : line.debugRaw));
            if (!string.IsNullOrEmpty(entry.voiceError)) GUILayout.Label(entry.voiceError);
            if (!string.IsNullOrEmpty(entry.voice?.debugAudioPath)) GUILayout.Label(entry.voice.debugAudioPath);
            GUILayout.Space(8);
        }
        GUILayout.EndScrollView(); GUILayout.EndArea();
    }
}
