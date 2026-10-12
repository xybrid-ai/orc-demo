using System;
using UnityEngine;
using UnityEngine.InputSystem;

// Runtime-only UI. Never adds an editor component or changes the saved scene.
public sealed class GatekeeperSpeechControls : MonoBehaviour
{
    public GatekeeperController controller;
    private bool visible = true;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null) return;
        controller.gameObject.AddComponent<GatekeeperSpeechControls>().controller = controller;
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-xybridSpeechTest") >= 0)
            controller.gameObject.AddComponent<GatekeeperSpeechValidation>().controller = controller;
    }
    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f4Key.wasPressedThisFrame) visible = !visible;
    }
    private void Select(GatekeeperLiveSpeech.Provider provider, bool sentences, bool streaming)
    {
        controller.Suspend(); controller.liveSpeech.SelectProvider(provider, sentences, streaming); controller.Resume();
    }
    private void OnGUI()
    {
        if (!visible || controller == null || controller.liveSpeech == null) return;
        var speech = controller.liveSpeech;
        GUILayout.BeginArea(new Rect(12, Screen.height - 210, 460, 200), GUI.skin.box);
        GUILayout.Label("Voice selection (F4 hides; switching restarts the encounter)");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Kokoro")) Select(GatekeeperLiveSpeech.Provider.Kokoro, false, false);
        if (GUILayout.Button("Kitten full line")) Select(GatekeeperLiveSpeech.Provider.Kitten, false, true);
        if (GUILayout.Button("Kitten sentences")) Select(GatekeeperLiveSpeech.Provider.Kitten, true, true);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Kitten + Rhubarb")) Select(GatekeeperLiveSpeech.Provider.Kitten, false, false);
        if (GUILayout.Button("Stop / restart")) controller.Restart();
#if XYBRID_KITTEN_RC4_SDK
        if (GUILayout.Button("Kitten SDK")) Select(GatekeeperLiveSpeech.Provider.KittenSdk, false, true);
#endif
        GUILayout.EndHorizontal();
        string voiceName = speech.kittenVoice == "warchief" ? "WarChief" : speech.kittenVoice;
        GUILayout.Label("Dialogue voice: " + speech.provider + (speech.provider == GatekeeperLiveSpeech.Provider.Kitten ? " / " + voiceName : "") + (speech.provider == GatekeeperLiveSpeech.Provider.KittenSdk
            ? " / metadata settings" : speech.sentenceChunks ? " / sentence delivery" : ""));
        if (!string.IsNullOrEmpty(controller.CurrentText) && (controller.IsPreparedReaction || !controller.CurrentWasLive))
            GUILayout.Label("Current audio: prerecorded Kokoro.");
        var data = controller.LastGenerated;
        if (data?.outcome != null)
            GUILayout.Label(data.outcome.completion + ": first packet " + (data.outcome.firstPacketSeconds * 1000).ToString("0")
                + " ms; synthesis " + (data.outcome.synthesisSeconds * 1000).ToString("0") + " ms");
        if (data?.stream != null)
            GUILayout.Label("Audio " + data.stream.DurationSeconds.ToString("0.0") + " s; buffer underrun " + (data.stream.GapSeconds * 1000).ToString("0") + " ms");
        if (!string.IsNullOrEmpty(speech.LastProviderError)) GUILayout.Label(speech.LastProviderError + " — select Kokoro to fall back.");
        GUILayout.EndArea();
    }
}
