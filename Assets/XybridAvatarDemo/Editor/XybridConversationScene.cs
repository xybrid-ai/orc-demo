using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public static class XybridConversationScene
{
    private const string Root = "Assets/XybridAvatarDemo";
    [Serializable] private sealed class Draft { public string title; public string startNodeId; public NodeDraft[] nodes; }
    [Serializable] private sealed class NodeDraft { public string id; public SentenceDraft[] sentences; public ConversationScenario.Choice[] choices; }
    [Serializable] private sealed class SentenceDraft { public string text; public string audio; public string mouthCues; }

    [MenuItem("Xybrid/Avatar Demo/Import Selected Conversation JSON")]
    public static void ImportSelected()
    {
        var json = Selection.activeObject as TextAsset;
        if (json == null) throw new InvalidOperationException("Select a conversation JSON asset first.");
        string path = AssetDatabase.GetAssetPath(json);
        Import(json, Path.ChangeExtension(path, ".asset"));
    }

    public static ConversationScenario Import(TextAsset json, string assetPath)
    {
        var draft = JsonUtility.FromJson<Draft>(json.text);
        if (draft == null || draft.nodes == null) throw new ArgumentException("The conversation JSON needs a nodes array.");
        var candidate = ScriptableObject.CreateInstance<ConversationScenario>();
        try
        {
            candidate.name = draft.title;
            candidate.startNodeId = draft.startNodeId;
            foreach (var node in draft.nodes)
            {
                if (node == null) throw new ArgumentException("A conversation node is null.");
                var mapped = new ConversationScenario.Node { id = node.id };
                foreach (var sentence in node.sentences ?? Array.Empty<SentenceDraft>())
                {
                    if (sentence == null) throw new ArgumentException("A conversation sentence is null.");
                    mapped.sentences.Add(new ConversationScenario.Sentence { text = sentence.text, audio = Load<AudioClip>(sentence.audio), mouthCues = Load<TextAsset>(sentence.mouthCues) });
                }
                mapped.choices.AddRange(node.choices ?? Array.Empty<ConversationScenario.Choice>());
                candidate.nodes.Add(mapped);
            }
            if (!candidate.Validate(out var error)) throw new ArgumentException(error);
            var existing = AssetDatabase.LoadAssetAtPath<ConversationScenario>(assetPath);
            if (existing != null)
            {
                Undo.RecordObject(existing, "Update conversation scenario");
                EditorUtility.CopySerialized(candidate, existing);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                return existing;
            }
            AssetDatabase.CreateAsset(candidate, assetPath);
            AssetDatabase.SaveAssets();
            var saved = candidate;
            candidate = null;
            return saved;
        }
        finally { if (candidate != null) UnityEngine.Object.DestroyImmediate(candidate); }
    }

    private static T Load<T>(string path) where T : UnityEngine.Object
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string resolved = path.StartsWith("Assets/", StringComparison.Ordinal) ? path : Root + "/" + path;
        var asset = AssetDatabase.LoadAssetAtPath<T>(resolved);
        if (asset == null) throw new ArgumentException("Cannot load " + typeof(T).Name + ": " + resolved);
        return asset;
    }

    [MenuItem("Xybrid/Avatar Demo/Configure Conversation Scene")]
    public static void ConfigureCurrent()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before configuring the scene.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != Root + "/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open the GoblinSpeakingDemo scene first.");
        var player = UnityEngine.Object.FindFirstObjectByType<XybridAvatarPlayer>();
        var camera = Camera.main;
        if (player == null || camera == null) throw new InvalidOperationException("The scene needs a character and camera.");
        var scenario = AssetDatabase.LoadAssetAtPath<ConversationScenario>(Root + "/Scenarios/PlaceholderConversation.asset");
        if (scenario == null) scenario = Import(AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Scenarios/PlaceholderConversation.json"), Root + "/Scenarios/PlaceholderConversation.asset");
        Configure(player, camera, scenario);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[Conversation] Minimal UI and branching conversation are ready.");
    }

    public static void Configure(XybridAvatarPlayer player, Camera camera, ConversationScenario scenario)
    {
        if (!scenario.Validate(out var error)) throw new ArgumentException(error);
        Undo.RecordObject(player, "Configure conversation speech");
        player.StopSpeech();
        player.autoPlay = false;
        player.caption = player.status = player.buttonLabel = null;
        player.progress = null;
        Undo.RecordObject(player.transform, "Center character");
        player.transform.position = Vector3.zero;
        Undo.RecordObject(camera.transform, "Frame conversational character");
        camera.transform.position = new Vector3(0, 1.38f, 2.75f);
        camera.transform.LookAt(new Vector3(0, 1.35f, 0));
        // Remove the presentation and playback panels from the previous demo.
        foreach (string name in new[] { "Presentation", "Conversation UI" })
        {
            var old = GameObject.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old);
        }
        var controller = player.GetComponent<ConversationController>();
        if (controller == null) controller = Undo.AddComponent<ConversationController>(player.gameObject);
        Undo.RecordObject(controller, "Assign conversation scenario");
        controller.scenario = scenario;
        controller.character = player;
        controller.autoStart = true;

        var root = UI("Conversation UI", null);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = .5f;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        root.AddComponent<GraphicRaycaster>();
        var panel = UI("Dialogue Panel", root.transform);
        var rect = (RectTransform)panel.transform;
        rect.anchorMin = new Vector2(.19f, .035f);
        rect.anchorMax = new Vector2(.81f, .035f);
        rect.pivot = new Vector2(.5f, 0);
        rect.sizeDelta = Vector2.zero;
        panel.AddComponent<Image>().color = new Color(.015f, .022f, .028f, .88f);
        var layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 20, 20);
        layout.spacing = 12;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var subtitle = UI("Sentence", panel.transform).AddComponent<Text>();
        subtitle.font = font;
        subtitle.fontSize = 28;
        subtitle.color = new Color(.94f, .95f, .96f);
        subtitle.alignment = TextAnchor.MiddleCenter;
        subtitle.horizontalOverflow = HorizontalWrapMode.Wrap;
        subtitle.verticalOverflow = VerticalWrapMode.Overflow;
        subtitle.raycastTarget = false;
        subtitle.gameObject.AddComponent<LayoutElement>().minHeight = 42;

        var choices = UI("Choices", panel.transform);
        var choiceLayout = choices.AddComponent<VerticalLayoutGroup>();
        choiceLayout.spacing = 10;
        choiceLayout.childControlWidth = choiceLayout.childControlHeight = true;
        choiceLayout.childForceExpandWidth = true;
        choiceLayout.childForceExpandHeight = false;
        var template = ChoiceButton("Choice Template", choices.transform, font);
        template.gameObject.SetActive(false);
        var action = ChoiceButton("Advance", panel.transform, font);
        action.GetComponent<LayoutElement>().preferredHeight = 42;
        action.gameObject.SetActive(false);
        var view = root.AddComponent<ConversationView>();
        view.conversation = controller;
        view.sentence = subtitle;
        view.choices = (RectTransform)choices.transform;
        view.choiceTemplate = template;
        view.action = action;
        view.actionText = action.GetComponentInChildren<Text>(true);
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var events = new GameObject("Event System");
            Undo.RegisterCreatedObjectUndo(events, "Create dialogue event system");
            events.AddComponent<EventSystem>();
            events.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }
        player.EvaluateAt(-1, 0);
        view.Refresh();
        EditorUtility.SetDirty(player);
        EditorUtility.SetDirty(controller);
        EditorSceneManager.MarkSceneDirty(player.gameObject.scene);
    }

    private static GameObject UI(string name, Transform parent)
    {
        var g = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(g, "Create dialogue UI");
        if (parent != null) g.transform.SetParent(parent, false);
        return g;
    }

    private static Button ChoiceButton(string name, Transform parent, Font font)
    {
        var g = UI(name, parent);
        g.AddComponent<Image>().color = new Color(.12f, .18f, .19f, .97f);
        var element = g.AddComponent<LayoutElement>();
        element.minHeight = 56;
        element.preferredHeight = 56;
        var button = g.AddComponent<Button>();
        var colors = button.colors;
        colors.highlightedColor = new Color(.62f, .84f, .72f);
        colors.selectedColor = colors.highlightedColor;
        button.colors = colors;
        var text = UI("Text", g.transform).AddComponent<Text>();
        var rect = (RectTransform)text.transform;
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(16, 8); rect.offsetMax = new Vector2(-16, -8);
        text.font = font;
        text.fontSize = 25;
        text.color = new Color(.87f, .96f, .90f);
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        return button;
    }
}
