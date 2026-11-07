using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static partial class XybridVillageGateScene
{
    private const string ResidentFolder = "Assets/Synty/SidekickCharacters/Characters/GoblinFighters/";
    private const string IdleFolder = "Assets/Synty/AnimationIdles/Animations/Sidekick/Masculine/";
    private const string MotionFolder = "Assets/Synty/AnimationBaseLocomotion/Animations/Sidekick/Masculine/";

    private static AnimationClip CampClip(string path)
    {
        var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__"));
        if (clip == null) throw new InvalidOperationException("Restore the camp animation: " + path);
        return clip;
    }

    private static CampResident.Beat Beat(AnimationClip clip, float seconds = 0, bool loop = false, float speed = 1)
        => new CampResident.Beat { clip = clip, seconds = seconds, loop = loop, speed = speed };

    [MenuItem("Xybrid/Avatar Demo/Build Camp Activity")]
    public static void BuildCampActivity()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before building camp activity.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != Demo + "/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open GoblinSpeakingDemo first.");
        var set = GameObject.Find("Village Gate Set");
        var keeper = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        var fire = GameObject.Find("Village campfire flame");
        if (set == null || keeper == null || fire == null) throw new InvalidOperationException("Build the fortified village gate first.");
        // Resolve every dependency before replacing the old group. All licensed content stays local.
        var prefabs = new[] { 1, 2, 4, 5 }.ToDictionary(n => n, n => AssetDatabase.LoadAssetAtPath<GameObject>(
            ResidentFolder + "GoblinFighter_0" + n + "/GoblinFighter_0" + n + ".prefab"));
        foreach (var pair in prefabs)
            if (pair.Value == null) throw new InvalidOperationException("Restore GoblinFighter_0" + pair.Key + " first.");
        foreach (var prop in new[] { "Props/SM_Prop_Torch_01", "Environment/SM_Env_Road_Planks_Straight_01",
            "Props/SM_Prop_Seat_01", "Props/SM_Prop_Mug_01", "Props/SM_Prop_Spit_01", "Props/SM_Prop_Food_01",
            "Props/SM_Prop_Pot_01", "Props/SM_Prop_Bedroll_01", "Props/SM_Prop_Rack_Weapon_01", "Buildings/SM_Bld_Tent_Leader_01" })
            if (AssetDatabase.LoadAssetAtPath<GameObject>(Pack + prop + ".prefab") == null)
                throw new InvalidOperationException("Restore the camp prop: " + prop);
        var idle = CampClip(MotionFolder + "Idles/A_MOD_BL_Idle_Standing_Masc.fbx");
        var walk = CampClip(MotionFolder + "Locomotion/Walk/A_MOD_BL_Walk_F_Masc.fbx");
        AnimationClip Action(string folder, string name) => CampClip(IdleFolder + folder + "/A_MOD_IDL_" + name + "_Masc.fbx");
        var left = Action("Look/Actions", "Look_L"); var right = Action("Look/Actions", "Look_R");
        var behind = Action("Look/Actions", "Look_L_Behind");
        var foldedIn = Action("ArmsFolded/Stances", "ArmsFolded_Grumpy_Enter");
        var folded = Action("ArmsFolded/Stances", "ArmsFolded_Grumpy_Loop");
        var foldedOut = Action("ArmsFolded/Stances", "ArmsFolded_Grumpy_Exit");
        var drink = Action("Drink/Stances", "Drink_Hold_Loop");
        var sip = Action("Drink/Actions", "Drink_Sip"); var swig = Action("Drink/Actions", "Drink_Swig");
        var yawn = Action("Yawn/Actions", "Yawn"); var stretch = Action("Stretch/Actions", "Stretch_Arms");
        var talk = CampClip("Assets/Animations/Sitting Talking.fbx");
        var laugh = CampClip("Assets/Animations/Sitting/Sitting Laughing.fbx");
        EnsureFolder(Demo + "/Materials"); EnsureFolder(Materials); converted.Clear();
        Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName("Build visible camp life");
        var old = set.transform.Find("Camp activity");
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
        var activity = Group("Camp activity", set.transform);

        Resident("West lookout", activity, prefabs[1], keeper, new Vector3(-4.02f, 2.35f, -6), .52f, 24, 3, false,
            Beat(idle, 7, true), Beat(left), Beat(idle, 4, true), Beat(foldedIn), Beat(folded, 9, true), Beat(foldedOut), Beat(right));
        Resident("East lookout", activity, prefabs[4], keeper, new Vector3(4.25f, 2.35f, -6), .50f, -28, 13, false,
            Beat(idle, 11, true), Beat(right), Beat(idle, 6, true), Beat(behind), Beat(idle, 8, true));

        // Two different silhouettes around the existing hearth, to the right of the entry sightline.
        var drinker = Resident("Fireside drinker", activity, prefabs[2], keeper, new Vector3(-.28f, 0, -9.85f), .83f, 108, 4, true,
            Beat(drink, 6, true), Beat(sip), Beat(drink, 9, true), Beat(swig), Beat(drink, 5, true));
        HoldMug(drinker);
        var storyteller = Resident("Seated storyteller", activity, prefabs[5], keeper, new Vector3(.65f, 0, -11.5f), .83f, -22, 12, true,
            Beat(talk, 24, true), Beat(laugh), Beat(talk, 19, true));
        Seat(storyteller, activity);

        // Stay in the strip in front of the right tent; never cross the plank path, fire or gate swing.
        Vector3 near = new Vector3(3.3f, 0, -8.7f), far = new Vector3(4.9f, 0, -9.25f);
        float walkingSeconds = Vector3.Distance(near, far) / (.83f * 1.368f * .8f);
        var outbound = Beat(walk, walkingSeconds, true, .8f); outbound.walk = true; outbound.mark = far;
        var inbound = outbound; inbound.mark = near;
        var resident = Resident("Tent walker", activity, prefabs[4], keeper, near, .83f, -24, 2, true,
            Beat(idle, 4, true), outbound, Beat(idle, 3, true), Beat(stretch), Beat(idle, 2, true),
            inbound, Beat(idle, 5, true), Beat(yawn));
        for (int i = 0; i < resident.routine.Length; i++)
        {
            var beat = resident.routine[i];
            if (!beat.walk) { beat.mark = i >= 2 && i <= 4 ? far : near; beat.yaw = i >= 2 && i <= 4 ? -112 : -24; }
            resident.routine[i] = beat;
        }
        SaveResidentPose(resident);

        CampProp("Buildings/SM_Bld_Tent_Leader_01", "Camp hall beyond the hearth", activity, new Vector3(0, 0, -17), .6f);
        CampProp("Props/SM_Prop_Spit_01", "Supper over the hearth", activity, new Vector3(1, .12f, -10), .55f, 32);
        CampProp("Props/SM_Prop_Pot_01", "Hearth pot", activity, new Vector3(1.65f, 0, -9.45f), .65f, 8);
        CampProp("Props/SM_Prop_Food_01", "Supper beside the seat", activity, new Vector3(2.5f, .02f, -10.8f), .7f, 20);
        CampProp("Props/SM_Prop_Bedroll_01", "Tent bedroll", activity, new Vector3(4.6f, .02f, -10.1f), .8f, 12);
        CampProp("Props/SM_Prop_Rack_Weapon_01", "Watch weapons", activity, new Vector3(-2.7f, 0, -9.4f), .7f, 8);
        for (int side = -1; side <= 1; side += 2)
        {
            // Short decks extend the existing tower floors beyond their hanging cloth.
            CampProp("Environment/SM_Env_Road_Planks_Straight_01", side < 0 ? "West watch deck" : "East watch deck", activity,
                new Vector3(side < 0 ? -4.02f : 4.25f, 2.304f, -6.3f), .5f);
            var at = new Vector3(side * 3.4f, 2.304f, -6.15f);
            CampProp("Props/SM_Prop_Torch_01", side < 0 ? "West watch light" : "East watch light", activity, at, .14f);
            var flame = Fire(side < 0 ? "West watch flame" : "East watch flame", activity, at + Vector3.up * .833f, .55f);
            var main = flame.GetComponent<ParticleSystem>().main; main.startColor = Color.white;
            flame.GetComponent<ParticleSystemRenderer>().sharedMaterial = fire.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            var lamp = flame.GetComponentInChildren<Light>();
            lamp.color = new Color(1, .58f, .28f); lamp.intensity = 2.6f; lamp.range = 2.1f;
        }
        CampSmoke(activity, fire.transform.position + Vector3.up * .35f);
        // Gentle local fill reaches the seated resident without changing Trola's portrait lights.
        var glow = new GameObject("Hearth bounce", typeof(Light)); Undo.RegisterCreatedObjectUndo(glow, "Hearth bounce");
        glow.transform.SetParent(activity, false); glow.transform.position = new Vector3(1.4f, 1.15f, -10.3f);
        var fill = glow.GetComponent<Light>(); fill.type = LightType.Point; fill.color = new Color(1, .56f, .3f);
        fill.intensity = 1.4f; fill.range = 3; fill.shadows = LightShadows.None;
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Debug.Log("[Camp activity] Five residents with watch, drink, seated and walking routines; hearth props and lights saved.");
    }

    private static CampResident Resident(string name, Transform parent, GameObject prefab, GatekeeperController keeper,
        Vector3 at, float scale, float yaw, float phase, bool behindGate, params CampResident.Beat[] routine)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(go, "Place " + name); go.name = name; go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * scale;
        foreach (var component in go.GetComponentsInChildren<MonoBehaviour>(true))
            if (component != null) Undo.DestroyObjectImmediate(component);
        foreach (var source in go.GetComponentsInChildren<AudioSource>(true)) Undo.DestroyObjectImmediate(source);
        foreach (var collider in go.GetComponentsInChildren<Collider>(true)) Undo.DestroyObjectImmediate(collider);
        var animator = go.GetComponent<Animator>();
        if (animator == null || !animator.isHuman) throw new InvalidOperationException(name + " requires the restored humanoid avatar.");
        Undo.RecordObject(animator, "Sample background clips"); animator.enabled = false; animator.applyRootMotion = false;
        PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
        var resident = Undo.AddComponent<CampResident>(go);
        for (int i = 0; i < routine.Length; i++)
            if (!routine[i].walk) { routine[i].mark = at; routine[i].yaw = yaw; }
        resident.routine = routine; resident.phase = phase; resident.behindGate = behindGate;
        resident.viewer = Camera.main; resident.gatekeeper = keeper;
        foreach (var skin in go.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            Undo.RecordObject(skin, "Background contact shadows"); skin.shadowCastingMode = ShadowCastingMode.On;
            skin.receiveShadows = behindGate; skin.updateWhenOffscreen = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(skin);
        }
        SaveResidentPose(resident); return resident;
    }

    private static void SaveResidentPose(CampResident resident)
    {
        resident.Pose(0);
        foreach (var transform in resident.GetComponentsInChildren<Transform>(true)) PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
        EditorUtility.SetDirty(resident);
    }

    private static GameObject CampProp(string relative, string name, Transform parent, Vector3 at, float scale, float yaw = 0)
    {
        var go = Place(relative, name, parent, at, scale, yaw);
        foreach (var collider in go.GetComponentsInChildren<Collider>(true)) Undo.DestroyObjectImmediate(collider);
        return go;
    }

    private static void HoldMug(CampResident resident)
    {
        var hand = resident.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.RightHand);
        var mug = CampProp("Props/SM_Prop_Mug_01", "Held mug", resident.transform, Vector3.zero, 1);
        mug.transform.SetParent(hand, false);
        mug.transform.localPosition = new Vector3(.04f, .035f, .11f);
        mug.transform.localRotation = Quaternion.Euler(-90, 0, 0);
        mug.transform.localScale = Vector3.one;
        PrefabUtility.RecordPrefabInstancePropertyModifications(mug.transform);
    }

    private static void Seat(CampResident resident, Transform parent)
    {
        // Match the seat top to the retargeted hips, with feet resting on the ground.
        var animator = resident.GetComponent<Animator>();
        float foot = Mathf.Min(animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y,
            animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y) - .055f * resident.transform.localScale.x;
        for (int i = 0; i < resident.routine.Length; i++) resident.routine[i].mark.y -= foot;
        SaveResidentPose(resident);
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips).position;
        var seat = CampProp("Props/SM_Prop_Seat_01", "Storyteller seat", parent,
            new Vector3(hips.x, 0, hips.z), 1, resident.transform.eulerAngles.y);
        seat.transform.localScale = new Vector3(.8f, Mathf.Max(.2f, hips.y - .08f) / .64f, .8f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(seat.transform);
    }

    private static void CampSmoke(Transform parent, Vector3 at)
    {
        var go = new GameObject("Hearth smoke", typeof(ParticleSystem)); Undo.RegisterCreatedObjectUndo(go, "Create camp smoke");
        go.transform.SetParent(parent, false); go.transform.position = at;
        var ps = go.GetComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.duration = 12; main.loop = true; main.prewarm = true; main.playOnAwake = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(7, 10); main.startSpeed = .12f;
        main.startSize = new ParticleSystem.MinMaxCurve(.45f, .8f); main.startColor = new Color(.38f, .43f, .52f, .22f);
        main.maxParticles = 45; main.simulationSpace = ParticleSystemSimulationSpace.World;
        var emission = ps.emission; emission.rateOverTime = 3;
        var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = .18f;
        var velocity = ps.velocityOverLifetime; velocity.enabled = true; velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = .13f; velocity.y = .62f; velocity.z = -.035f;
        var size = ps.sizeOverLifetime; size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, .5f, 1, 3.2f));
        var color = ps.colorOverLifetime; color.enabled = true;
        var gradient = new Gradient(); gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(.65f, .2f), new GradientAlphaKey(.35f, .6f), new GradientAlphaKey(0, 1) }); color.color = gradient;
        var renderer = ps.GetComponent<ParticleSystemRenderer>(); renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sharedMaterial = SmokeMaterial(); renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        ps.Play();
    }

    private static Material SmokeMaterial()
    {
        var path = Materials + "/Camp smoke.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")); AssetDatabase.CreateAsset(material, path); }
        material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Materials + "/FlameMask.asset"));
        material.SetColor("_BaseColor", Color.white); material.SetFloat("_Surface", 1);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0); material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.renderQueue = 3000;
        EditorUtility.SetDirty(material); return material;
    }
}
