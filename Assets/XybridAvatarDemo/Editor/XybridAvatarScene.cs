using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class XybridAvatarScene
{
    private const string Root = "Assets/XybridAvatarDemo";

    [MenuItem("Xybrid/Avatar Demo/Build Speaking Goblin Scene")]
    public static void Build()
    {
        if (SceneManager.GetActiveScene().isDirty) throw new System.InvalidOperationException("Save your current scene before building the demo.");
        if (File.Exists(Root + "/GoblinSpeakingDemo.unity")) throw new System.InvalidOperationException("The demo already exists. Open it instead of replacing it.");
        Directory.CreateDirectory(Root + "/Materials");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(.19f, .23f, .29f);
        RenderSettings.fog = false;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Synty/SidekickCharacters/Characters/GoblinFighters/GoblinFighter_03/GoblinFighter_03.prefab");
        if (prefab == null) throw new System.InvalidOperationException("Import Goblin Fighters before building this scene.");
        var goblin = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(goblin, "Create speaking goblin");
        goblin.name = "Goblin Presenter";
        goblin.transform.position = new Vector3(-.72f, 0, 0);
        var animator = goblin.GetComponent<Animator>();
        if (animator != null) animator.enabled = false;
        foreach (var behaviour in goblin.GetComponentsInChildren<MonoBehaviour>(true))
            if (behaviour != null) behaviour.enabled = false;
        var player = goblin.AddComponent<XybridAvatarPlayer>();
        player.speech = goblin.AddComponent<AudioSource>();
        player.speech.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/Audio/speech.wav");
        player.speech.playOnAwake = false;
        player.speech.spatialBlend = 0;
        player.mouthCues = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Audio/mouth-cues.json");
        player.idle = AssetDatabase.LoadAllAssetsAtPath("Assets/Synty/AnimationBaseLocomotion/Animations/Sidekick/Masculine/Idles/A_MOD_BL_Idle_Standing_Masc.fbx")
            .OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__"));
        var script = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Audio/script.txt");
        if (script != null) player.Script = script.text.Trim();

        var cameraObject = New("Main Camera");
        cameraObject.tag = "MainCamera";
        var camera = cameraObject.AddComponent<Camera>();
        cameraObject.AddComponent<AudioListener>();
        camera.transform.position = new Vector3(0, 1.56f, 2.75f);
        camera.transform.LookAt(new Vector3(0, 1.53f, 0));
        camera.fieldOfView = 34;
        camera.nearClipPlane = .1f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.017f, .026f, .033f);
        camera.allowHDR = false;
        camera.allowMSAA = true;
        Light("Key", new Color(1, .87f, .69f), 2.3f, new Vector3(28, 155, 0));
        Light("Fill", new Color(.51f, .69f, 1), .9f, new Vector3(6, -48, 0));
        Light("Rim", new Color(.48f, 1, .73f), 1.7f, new Vector3(14, 10, 0));
        var backdrop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(backdrop, "Create backdrop");
        backdrop.name = "Studio Backdrop";
        backdrop.transform.position = new Vector3(0, 2, -1.5f);
        backdrop.transform.localScale = new Vector3(15, 8, .1f);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var material = new Material(shader) { name = "Avatar Studio" };
        material.SetColor("_BaseColor", new Color(.017f, .026f, .035f));
        AssetDatabase.CreateAsset(material, Root + "/Materials/Studio.mat");
        backdrop.GetComponent<Renderer>().sharedMaterial = material;

        var scenario = XybridConversationScene.Import(
            AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Scenarios/PlaceholderConversation.json"),
            Root + "/Scenarios/PlaceholderConversation.asset");
        XybridConversationScene.Configure(player, camera, scenario);
        player.EvaluateAt(-1, 0);
        EditorSceneManager.SaveScene(scene, Root + "/GoblinSpeakingDemo.unity");
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = goblin;
        Debug.Log("[Xybrid Avatar] Speaking goblin scene ready. Press Play to listen.");
    }

    private static GameObject New(string name) { var g = new GameObject(name); Undo.RegisterCreatedObjectUndo(g, name); return g; }
    private static void Light(string name, Color color, float intensity, Vector3 rotation)
    {
        var g = New(name);g.transform.rotation = Quaternion.Euler(rotation);
        var l = g.AddComponent<UnityEngine.Light>();l.type = LightType.Directional;l.color = color;l.intensity = intensity;
    }
}
