using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static partial class XybridVillageGateScene
{
    private const string ResidentPrefab = "Assets/Synty/SidekickCharacters/Characters/GoblinFighters/GoblinFighter_03/GoblinFighter_03.prefab";
    private const string ResidentIdle = "Assets/Synty/AnimationBaseLocomotion/Animations/Sidekick/Masculine/Idles/A_MOD_BL_Idle_Standing_Masc.fbx";

    [MenuItem("Xybrid/Avatar Demo/Build Camp Activity")]
    public static void BuildCampActivity()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before building camp activity.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != Demo + "/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open GoblinSpeakingDemo first.");
        var set = GameObject.Find("Village Gate Set");
        var keeper = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ResidentPrefab);
        var idle = AssetDatabase.LoadAllAssetsAtPath(ResidentIdle).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__"));
        var torch = AssetDatabase.LoadAssetAtPath<GameObject>(Pack + "Props/SM_Prop_Torch_01.prefab");
        var planks = AssetDatabase.LoadAssetAtPath<GameObject>(Pack + "Environment/SM_Env_Road_Planks_Straight_01.prefab");
        var fire = GameObject.Find("Village campfire flame");
        if (set == null || keeper == null || fire == null) throw new InvalidOperationException("Build the fortified village gate first.");
        if (prefab == null || idle == null || torch == null || planks == null) throw new InvalidOperationException("Restore the manifest-listed character, standing idle, camp torch and planks first.");
        EnsureFolder(Demo + "/Materials"); EnsureFolder(Materials); converted.Clear();
        Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName("Build visible camp activity");
        var old = set.transform.Find("Camp activity");
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
        var activity = Group("Camp activity", set.transform);

        // Only the tower openings and the first patch of interior visible during entry are dressed.
        // The road, the gates, Trola's portrait and the director's routes are independent of this group.
        Resident("West lookout", activity, prefab, idle, keeper, new Vector3(-4.02f, 2.35f, -6.0f), .52f, 24, 1.7f, CampResident.Duty.Lookout);
        Resident("East lookout", activity, prefab, idle, keeper, new Vector3(4.25f, 2.35f, -6.0f), .50f, -28, 9.4f, CampResident.Duty.Lookout);
        Resident("Fireside resident", activity, prefab, idle, keeper, new Vector3(-.4f, 0, -9.8f), .83f, 98, 3.2f, CampResident.Duty.ByTheFire);

        for (int side = -1; side <= 1; side += 2)
        {
            // Short cantilevered decks continue the existing 2.3 m tower floors past the hanging roof cloth.
            var deck = Place("Environment/SM_Env_Road_Planks_Straight_01", side < 0 ? "West watch deck" : "East watch deck", activity,
                new Vector3(side < 0 ? -4.02f : 4.25f, 2.304f, -6.3f), .50f);
            foreach (var collider in deck.GetComponentsInChildren<Collider>(true)) Undo.DestroyObjectImmediate(collider);
            var at = new Vector3(side * 3.4f, 2.304f, -6.15f);
            var watchTorch = Place("Props/SM_Prop_Torch_01", side < 0 ? "West watch light" : "East watch light", activity, at, .14f);
            foreach (var collider in watchTorch.GetComponentsInChildren<Collider>(true)) Undo.DestroyObjectImmediate(collider);
            var flame = Fire(side < 0 ? "West watch flame" : "East watch flame", activity, at + Vector3.up * .833f, .55f);
            var main = flame.GetComponent<ParticleSystem>().main; main.startColor = Color.white;
            flame.GetComponent<ParticleSystemRenderer>().sharedMaterial = fire.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            var lamp = flame.GetComponentInChildren<Light>();
            lamp.color = new Color(1, .58f, .28f); lamp.intensity = 2.6f; lamp.range = 2.1f;
        }
        CampSmoke(activity, fire.transform.position + Vector3.up * .35f);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Debug.Log("[Camp activity] Two lookouts, one fireside resident, watch lights and hearth smoke saved.");
    }

    private static void Resident(string name, Transform parent, GameObject prefab, AnimationClip idle,
        GatekeeperController keeper, Vector3 at, float scale, float yaw, float phase, CampResident.Duty duty)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(go, "Place " + name); go.name = name; go.transform.SetParent(parent, false);
        go.transform.position = at; go.transform.rotation = Quaternion.Euler(0, yaw, 0); go.transform.localScale = Vector3.one * scale;
        // A background rig must never inherit the presenter's speech/input/performance components.
        foreach (var component in go.GetComponentsInChildren<MonoBehaviour>(true))
            if (component != null) Undo.DestroyObjectImmediate(component);
        foreach (var source in go.GetComponentsInChildren<AudioSource>(true)) Undo.DestroyObjectImmediate(source);
        foreach (var collider in go.GetComponentsInChildren<Collider>(true)) Undo.DestroyObjectImmediate(collider);
        var animator = go.GetComponent<Animator>();
        if (animator != null) { Undo.RecordObject(animator, "Sample background idle"); animator.enabled = false; animator.applyRootMotion = false; PrefabUtility.RecordPrefabInstancePropertyModifications(animator); }
        var resident = Undo.AddComponent<CampResident>(go);
        resident.idle = idle; resident.duty = duty; resident.phase = phase;
        resident.idleSpeed = duty == CampResident.Duty.Lookout ? .78f : .9f;
        resident.viewer = Camera.main; resident.gatekeeper = keeper;
        resident.Pose(0);
        // Keep imported mesh/material references shared; only pose and placement overrides belong to this scene.
        var skins = go.GetComponentsInChildren<SkinnedMeshRenderer>();
        // Tiny lookout meshes otherwise pick up stippled self-shadowing at the long approach distance.
        foreach (var skin in skins) { Undo.RecordObject(skin, "Background contact shadows"); skin.shadowCastingMode = ShadowCastingMode.On; skin.receiveShadows = duty != CampResident.Duty.Lookout; PrefabUtility.RecordPrefabInstancePropertyModifications(skin); }
        foreach (var transform in go.GetComponentsInChildren<Transform>(true)) PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
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
