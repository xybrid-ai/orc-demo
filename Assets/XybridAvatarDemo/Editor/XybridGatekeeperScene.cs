using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public static class XybridGatekeeperScene
{
    private const string Root = "Assets/XybridAvatarDemo";
    [Serializable] private sealed class Draft { public LineDraft[] lines; }
    [Serializable] private sealed class LineDraft { public string id, recordedMood, text, audio, mouthCues; }

    private static GatekeeperScenario CreateScenario()
    {
        string path = Root + "/Scenarios/TrolaGatekeeper.asset";
        var existing = AssetDatabase.LoadAssetAtPath<GatekeeperScenario>(path);
        if (existing != null) return existing;
        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Scenarios/GatekeeperLines.json");
        if (json == null) throw new InvalidOperationException("Import the gatekeeper lines first.");
        var draft = JsonUtility.FromJson<Draft>(json.text);
        var scenario = ScriptableObject.CreateInstance<GatekeeperScenario>();
        scenario.name = "Trola Gatekeeper";
        foreach (var line in draft.lines)
            scenario.lines.Add(new GatekeeperScenario.Line { id = line.id, text = line.text, recordedMood = (GatekeeperMood)Enum.Parse(typeof(GatekeeperMood), line.recordedMood), audio = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/" + line.audio), mouthCues = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/" + line.mouthCues) });
        scenario.voices.Add(new GatekeeperScenario.VoiceProfile { mood = GatekeeperMood.Bored, pitch = .90f, volume = .78f });
        scenario.voices.Add(new GatekeeperScenario.VoiceProfile { mood = GatekeeperMood.Irritated, pitch = .98f, volume = .92f, anger = .40f });
        scenario.voices.Add(new GatekeeperScenario.VoiceProfile { mood = GatekeeperMood.Furious, pitch = 1.08f, volume = 1, anger = .90f });
        scenario.voices.Add(new GatekeeperScenario.VoiceProfile { mood = GatekeeperMood.Amused, pitch = .95f, volume = .87f, amusement = .75f });
        if (!scenario.Validate(out var error)) { UnityEngine.Object.DestroyImmediate(scenario); throw new ArgumentException(error); }
        AssetDatabase.CreateAsset(scenario, path);
        AssetDatabase.SaveAssets();
        return scenario;
    }

    [MenuItem("Xybrid/Avatar Demo/Configure Trola Gatekeeper")]
    public static void Configure()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before configuring the encounter.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != Root + "/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open the GoblinSpeakingDemo scene first.");
        var player = UnityEngine.Object.FindFirstObjectByType<XybridAvatarPlayer>();
        var camera = Camera.main;
        if (player == null || camera == null) throw new InvalidOperationException("The scene needs a character and camera.");
        var scenario = CreateScenario();
        if (!scenario.Validate(out var error)) throw new ArgumentException(error);
        var prior = player.GetComponent<ConversationController>();
        if (prior != null) { Undo.RecordObject(prior, "Disable the placeholder conversation"); prior.enabled = false; }
        foreach (var name in new[] { "Conversation UI", "Gatekeeper UI", "Gatekeeper Set" })
        {
            var old = GameObject.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old);
        }
        Undo.RecordObject(player, "Assign gatekeeper playback");
        player.autoPlay = false;
        player.StopSpeech();
        var gatekeeper = player.GetComponent<GatekeeperController>();
        if (gatekeeper == null) gatekeeper = Undo.AddComponent<GatekeeperController>(player.gameObject);
        Undo.RecordObject(gatekeeper, "Assign gatekeeper encounter");
        gatekeeper.scenario = scenario;
        gatekeeper.character = player;
        gatekeeper.autoStart = true;
        gatekeeper.enabled = true;

        var root = UI("Gatekeeper UI", null);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = .5f;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        root.AddComponent<GraphicRaycaster>();
        var panel = UI("Dialogue", root.transform);
        var panelRect = (RectTransform)panel.transform;
        panelRect.anchorMin = new Vector2(.18f, .035f); panelRect.anchorMax = new Vector2(.82f, .035f); panelRect.pivot = new Vector2(.5f, 0); panelRect.sizeDelta = Vector2.zero;
        panel.AddComponent<Image>().color = new Color(.015f, .022f, .028f, .9f);
        var layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 20, 20); layout.spacing = 12;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var sentence = Label("Troll line", panel.transform, font, 28);
        sentence.gameObject.AddComponent<LayoutElement>().minHeight = 44;
        var row = UI("Your reply", panel.transform);
        var rowLayout = row.AddComponent<HorizontalLayoutGroup>(); rowLayout.spacing = 12;
        rowLayout.childControlWidth = rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false; rowLayout.childForceExpandHeight = true;
        row.AddComponent<LayoutElement>().preferredHeight = 62;
        var entry = UI("Typed response", row.transform);
        entry.AddComponent<LayoutElement>().flexibleWidth = 1;
        entry.AddComponent<Image>().color = new Color(.11f, .15f, .17f);
        var field = entry.AddComponent<InputField>();
        field.characterLimit = GatekeeperIntentRules.MaxInputLength;
        field.lineType = InputField.LineType.SingleLine;
        var entered = Label("Text", entry.transform, font, 25);
        Stretch(entered.rectTransform, new Vector2(16, 8)); entered.alignment = TextAnchor.MiddleLeft;
        field.textComponent = entered;
        var placeholder = Label("Placeholder", entry.transform, font, 25);
        placeholder.text = "What do you say to the gatekeeper?"; placeholder.color = new Color(.62f, .67f, .69f); placeholder.alignment = TextAnchor.MiddleLeft;
        Stretch(placeholder.rectTransform, new Vector2(16, 8)); field.placeholder = placeholder;
        var send = Button("Send", row.transform, font, "Send"); send.gameObject.AddComponent<LayoutElement>().preferredWidth = 130;
        var action = Button("Advance", panel.transform, font, "Skip"); action.gameObject.AddComponent<LayoutElement>().preferredHeight = 44; action.gameObject.SetActive(false);
        var view = root.AddComponent<GatekeeperView>();
        view.gatekeeper = gatekeeper; view.sentence = sentence; view.inputRow = row; view.input = field; view.send = send; view.action = action; view.actionText = action.GetComponentInChildren<Text>();
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var events = new GameObject("Event System"); Undo.RegisterCreatedObjectUndo(events, "Create input events"); events.AddComponent<EventSystem>(); events.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }
        AddGate(gatekeeper);
        view.Refresh();
        player.SetAffect(.90f, .78f, 0, 0);
        player.EvaluateAt(-1, 0);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[Gatekeeper] Typed replies, hidden patience, mood profiles, and endings are ready.");
        if (GameObject.Find("Dusk Atmosphere") != null) XybridDuskScene.Apply();
    }

    private static void AddGate(GatekeeperController gatekeeper)
    {
        if (GameObject.Find("Village Gate Set") != null) return;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Synty/PolygonFantasyVillage/Prefabs/Buildings/SM_Bld_Fence_Wood_Gate_01.prefab");
        if (prefab == null) return;
        var set = new GameObject("Gatekeeper Set"); Undo.RegisterCreatedObjectUndo(set, "Create gatekeeper backdrop");
        var gate = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(gate, "Place the gate"); gate.transform.SetParent(set.transform, false);
        gate.transform.position = new Vector3(1.512f, 0, -.95f); gate.transform.localScale = Vector3.one * 1.2f;
        var barrier = set.AddComponent<GatekeeperBarrier>(); barrier.gatekeeper = gatekeeper; barrier.hinge = gate.transform;
        var backdrop = GameObject.Find("Studio Backdrop");
        if (backdrop != null) { Undo.RecordObject(backdrop.transform, "Make space behind the gate"); backdrop.transform.position = new Vector3(0, 2, -5); }
    }

    private static GameObject UI(string name, Transform parent)
    {
        var g = new GameObject(name, typeof(RectTransform)); Undo.RegisterCreatedObjectUndo(g, "Create gatekeeper UI");
        if (parent != null) g.transform.SetParent(parent, false);
        return g;
    }
    private static Text Label(string name, Transform parent, Font font, int size)
    {
        var text = UI(name, parent).AddComponent<Text>(); text.font = font; text.fontSize = size; text.color = new Color(.94f, .96f, .95f); text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false; text.supportRichText = false; text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }
    private static Button Button(string name, Transform parent, Font font, string label)
    {
        var g = UI(name, parent); g.AddComponent<Image>().color = new Color(.13f, .22f, .20f);
        var button = g.AddComponent<Button>(); var text = Label("Label", g.transform, font, 25); text.text = label; Stretch(text.rectTransform, new Vector2(10, 8));
        return button;
    }
    private static void Stretch(RectTransform rect, Vector2 inset) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = inset; rect.offsetMax = -inset; }
}
