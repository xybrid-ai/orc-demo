using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class XybridNameOpeningScene
{
    [MenuItem("Xybrid/Avatar Demo/Configure Name Opening")]
    public static void Configure()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open the gatekeeper scene.");
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null || controller.scenario == null) throw new InvalidOperationException("Configure the existing gatekeeper first.");
        var scenario = controller.scenario;
        Undo.RecordObject(scenario, "Add the gatekeeper name opening");
        scenario.captureName = true;
        void Line(string id, string text, GatekeeperMood mood)
        {
            var line = scenario.FindLine(id);
            if (line == null) { line = new GatekeeperScenario.Line { id = id }; scenario.lines.Add(line); }
            line.text = text; line.recordedMood = mood;
        }
        Line("name-demand", "Halt, traveller. Before you ask about my gate, tell me your name.", GatekeeperMood.Bored);
        Line("intro", "{name}, is it? Why should I open my gate?", GatekeeperMood.Bored);
        Line("name-assigned", "No proper name? I will call you {name}. Why should I open my gate?", GatekeeperMood.Bored);
        Line("bribe", "Good, {name}. Hand over the gold, and we have a deal.", GatekeeperMood.Amused);
        Line("win", "Go through, {name}, before I change my mind.", GatekeeperMood.Amused);
        Line("insult-irritated", "Careful, {name_mangled}. Watch your tongue.", GatekeeperMood.Irritated);
        Line("insult-furious", "Watch your mouth, {name_mangled}! One more word and you are out!", GatekeeperMood.Furious);
        if (scenario.choices.Count == 0)
        {
            scenario.choices.Add(new GatekeeperScenario.Choice { label = "Ask politely", reply = "Please let me through, sir." });
            scenario.choices.Add(new GatekeeperScenario.Choice { label = "Flatter him", reply = "You are a mighty guardian." });
            scenario.choices.Add(new GatekeeperScenario.Choice { label = "Offer gold", reply = "I offer you gold coins." });
            scenario.choices.Add(new GatekeeperScenario.Choice { label = "Insult him", reply = "You are an idiot." });
        }
        if (!scenario.Validate(out var error)) throw new ArgumentException(error);
        var live = controller.GetComponent<GatekeeperLiveSpeech>();
        if (live == null) live = Undo.AddComponent<GatekeeperLiveSpeech>(controller.gameObject);
        Undo.RecordObject(controller, "Use live speech for name slots");
        controller.liveSpeech = live;
        PrefabUtility.RecordPrefabInstancePropertyModifications(controller);
        var view = UnityEngine.Object.FindFirstObjectByType<GatekeeperView>();
        if (view != null) { Undo.RecordObject(view.input, "Limit the name input"); view.input.characterLimit = GatekeeperNames.AnswerMaxLength; view.Refresh(); }
        EditorUtility.SetDirty(scenario);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[Gatekeeper] Name opening, authored choices, and live named replies saved.");
    }
}
