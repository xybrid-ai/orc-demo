using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Builds only the demo set. Licensed prefabs/textures stay in the local project.
public static class XybridVillageGateScene
{
    private const string Demo = "Assets/XybridAvatarDemo";
    private const string Pack = "Assets/Synty/PolygonGoblinWarCamp/Prefabs/";
    private const string Materials = Demo + "/Materials/VillageGate";
    private static readonly Dictionary<Material, Material> converted = new Dictionary<Material, Material>();

    [MenuItem("Xybrid/Avatar Demo/Build Fortified Village Gate")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before building the set.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != Demo + "/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open GoblinSpeakingDemo first.");
        var keeper = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        var camera = Camera.main;
        if (keeper == null || camera == null) throw new InvalidOperationException("Configure the gatekeeper encounter first.");
        foreach (var path in new[] { "Buildings/SM_Bld_Gate_01", "Buildings/SM_Bld_Tower_01", "Buildings/SM_Bld_Log_Wall_01", "Environment/SM_Env_Ground_Dirt_Flat_01" })
            if (AssetDatabase.LoadAssetAtPath<GameObject>(Pack + path + ".prefab") == null)
                throw new InvalidOperationException("Finish importing Goblin War Camp: " + path);
        if (Shader.Find("Universal Render Pipeline/Lit") == null) throw new InvalidOperationException("This set requires URP.");

        EnsureFolder(Demo + "/Materials"); EnsureFolder(Materials);
        converted.Clear();
        Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName("Build fortified village gate");
        foreach (string name in new[] { "Gatekeeper Set", "Village Gate Set" })
        {
            var old = GameObject.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old);
        }
        var root = new GameObject("Village Gate Set");
        Undo.RegisterCreatedObjectUndo(root, "Build village entrance");
        var architecture = Group("Fortifications", root.transform);
        var camp = Group("Village beyond the gate", root.transform);
        var terrain = Group("Approach and ground", root.transform);
        var details = Group("Banners and torchlight", root.transform);

        var gate = Place("Buildings/SM_Bld_Gate_01", "Village double gate", architecture, new Vector3(0, 0, -5.2f), .40f);
        var barrier = Undo.AddComponent<GatekeeperBarrier>(root);
        barrier.gatekeeper = keeper;
        barrier.hinge = gate.transform.Find("SM_Bld_Gate_01_Door_01");
        barrier.secondHinge = gate.transform.Find("SM_Bld_Gate_01_Door_02");
        if (barrier.hinge == null || barrier.secondHinge == null) throw new InvalidOperationException("Gate prefab needs both door pivots.");

        for (int side = -1; side <= 1; side += 2)
        {
            Place("Buildings/SM_Bld_Tower_01", side < 0 ? "West watchtower" : "East watchtower", architecture, new Vector3(side * 3.65f, 0, -7.1f), .42f);
            Place("Buildings/SM_Bld_Log_Wall_01", "Inner palisade " + side, architecture, new Vector3(side * 3.35f, 0, -5.45f), .75f);
            Place("Buildings/SM_Bld_Log_Wall_02", "Outer palisade " + side, architecture, new Vector3(side * 6.7f, 0, -5.7f), .75f, side * -6);
            Place("Props/SM_Prop_Defence_Spikes_01", "Defensive stakes " + side, architecture, new Vector3(side * 4.9f, 0, -3.7f), .45f, side * 10);
            Place("Buildings/Parts/SM_Bld_Part_Banner_04", "Watchtower standard " + side, details, new Vector3(side * 3.65f, 3.85f, -6.12f), .60f);
            Place("Props/SM_Prop_Torch_01", "Gate torch " + side, details, new Vector3(side * 2.22f, 0, -3.95f), .40f);
            Fire("Torch flame " + side, details, new Vector3(side * 2.22f, 2.38f, -3.95f), 1);
            Place("Buildings/SM_Bld_Tent_Medium_01", "Village tent " + side, camp, new Vector3(side * 3.7f, 0, -12), .65f, side * 12);
            Place("Environment/SM_Env_Tree_Marsh_02", "Village tree " + side, camp, new Vector3(side * 9, 0, -18), .65f, side * 25);
            Place("Environment/SM_Env_Rock_02", "Approach boulder " + side, terrain, new Vector3(side * 2.65f, 0, -.4f), .40f, side * 22);
            Place("Environment/SM_Env_Grass_Preset_01", "Roadside grass " + side, terrain, new Vector3(side * 3.8f, 0, -.7f), .20f, side * 40);
            Place("Environment/SM_Env_Grass_Preset_02", "Wall grass " + side, terrain, new Vector3(side * 5.1f, 0, -3.8f), .25f);
        }
        Place("Buildings/Parts/SM_Bld_Part_Banner_01", "Gate crest", details, new Vector3(0, 3.87f, -4.54f), .50f);
        Place("Props/SM_Prop_Barrel_01", "Toll provisions", details, new Vector3(-1.5f, 0, -2.5f), .45f, -15);
        Place("Props/SM_Prop_Crate_02", "Supply crate", details, new Vector3(-1.9f, 0, -2.65f), .40f, 12);
        Place("Props/SM_Prop_Camp_Fire_01", "Village campfire", camp, new Vector3(1, 0, -10), .55f);
        Fire("Village campfire flame", camp, new Vector3(1, .25f, -10), 1.4f);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        Undo.RegisterCreatedObjectUndo(ground, "Create surrounding ground");
        ground.name = "Surrounding earth"; ground.transform.SetParent(terrain, false);
        ground.transform.position = new Vector3(0, -.10f, -10); ground.transform.localScale = new Vector3(8, 1, 8);
        ground.GetComponent<Renderer>().sharedMaterial = SolidMaterial("Earth", new Color(.24f, .28f, .19f));
        var dirt = Place("Environment/SM_Env_Ground_Dirt_Flat_01", "Worn gate approach", terrain, new Vector3(7.5f, -.025f, 5), 3);
        dirt.transform.localScale = new Vector3(3, .10f, 3);
        PrefabUtility.RecordPrefabInstancePropertyModifications(dirt.transform);
        for (int i = 0; i < 5; i++)
            Place("Environment/SM_Env_Road_Planks_Straight_01", "Plank path " + i, terrain, new Vector3(0, .012f, 1 - i * 2.35f), .55f);

        var backdrop = GameObject.Find("Studio Backdrop");
        if (backdrop != null) { Undo.RecordObject(backdrop, "Replace studio backdrop"); backdrop.SetActive(false); }
        Undo.RecordObject(camera, "Frame village encounter"); Undo.RecordObject(camera.transform, "Frame village encounter");
        camera.transform.position = new Vector3(0, 1.46f, 3.25f);
        camera.transform.rotation = Quaternion.LookRotation(new Vector3(0, 1.48f, 0) - camera.transform.position);
        camera.fieldOfView = 38; camera.clearFlags = CameraClearFlags.Skybox;
        camera.backgroundColor = new Color(.45f, .54f, .58f);
        ConfigureLight("Key", new Color(1, .88f, .70f), 1.65f, new Vector3(38, 145, 0));
        ConfigureLight("Fill", new Color(.58f, .72f, .89f), .42f, new Vector3(16, 305, 0));
        ConfigureLight("Rim", new Color(1, .76f, .44f), .65f, new Vector3(28, 12, 0));
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(.43f, .50f, .57f);
        RenderSettings.ambientEquatorColor = new Color(.27f, .29f, .25f);
        RenderSettings.ambientGroundColor = new Color(.16f, .14f, .11f);
        RenderSettings.fog = true; RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(.46f, .53f, .55f); RenderSettings.fogDensity = .018f;
        RenderSettings.skybox = Sky();
        keeper.character.EvaluateAt(-1, 0);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Debug.Log("[Village Gate] Fortifications, twin doors, camp, approach, and lighting saved.");
        if (GameObject.Find("Dusk Atmosphere") != null) XybridDuskScene.Apply();
    }

    private static Transform Group(string name, Transform parent)
    {
        var group = new GameObject(name); Undo.RegisterCreatedObjectUndo(group, "Create village group");
        group.transform.SetParent(parent, false); return group.transform;
    }

    private static GameObject Place(string relative, string name, Transform parent, Vector3 position, float scale, float yaw = 0)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Pack + relative + ".prefab");
        if (prefab == null) throw new InvalidOperationException("Missing village piece: " + relative);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(go, "Place " + name); go.name = name;
        go.transform.SetParent(parent, false); go.transform.position = position;
        go.transform.localScale = Vector3.one * scale; go.transform.rotation = Quaternion.Euler(0, yaw, 0);
        PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
        foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
        {
            Undo.RecordObject(renderer, "Use village URP material");
            if (renderer.name.EndsWith("_Shadow", StringComparison.Ordinal))
            {
                renderer.enabled = false; PrefabUtility.RecordPrefabInstancePropertyModifications(renderer); continue;
            }
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) if (materials[i] != null) materials[i] = LitMaterial(materials[i]);
            renderer.sharedMaterials = materials;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }
        return go;
    }

    private static Material LitMaterial(Material source)
    {
        if (converted.TryGetValue(source, out var existing)) return existing;
        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
        string path = Materials + "/" + source.name + "-" + guid.Substring(0, 8) + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, path); }
        Undo.RecordObject(mat, "Set scene palette material");
        string map = source.HasProperty("_Albedo_Map") ? "_Albedo_Map" : source.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
        Texture texture = source.HasProperty(map) ? source.GetTexture(map) : null;
        Color tint = Color.white;
        foreach (string key in new[] { "_Albedo_Tint", "_Albedo_Color", "_BaseColor", "_Color" })
            if (source.HasProperty(key)) { tint = source.GetColor(key); break; }
        mat.SetTexture("_BaseMap", texture); mat.SetColor("_BaseColor", tint);
        mat.SetFloat("_Smoothness", .05f); mat.SetFloat("_Metallic", 0);
        if (source.HasProperty(map))
        {
            mat.SetTextureScale("_BaseMap", source.GetTextureScale(map)); mat.SetTextureOffset("_BaseMap", source.GetTextureOffset(map));
        }
        if (source.HasProperty("_Normal_Map") && source.GetTexture("_Normal_Map") != null)
        {
            mat.SetTexture("_BumpMap", source.GetTexture("_Normal_Map")); mat.EnableKeyword("_NORMALMAP");
        }
        if (source.IsKeywordEnabled("_ALPHATEST_ON") || source.name.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            mat.SetFloat("_AlphaClip", 1); mat.SetFloat("_Cutoff", .4f); mat.EnableKeyword("_ALPHATEST_ON");
        }
        if (source.name.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0) mat.SetFloat("_Cull", 0);
        if (source.name.StartsWith("Banner", StringComparison.Ordinal)) mat.SetFloat("_Cull", 0);
        converted[source] = mat; EditorUtility.SetDirty(mat); return mat;
    }

    private static Material SolidMaterial(string name, Color color)
    {
        string path = Materials + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, path); }
        mat.SetColor("_BaseColor", color); mat.SetFloat("_Smoothness", 0); EditorUtility.SetDirty(mat); return mat;
    }

    private static Material Sky()
    {
        string path = Materials + "/VillageSky.mat";
        var sky = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (sky == null) { sky = new Material(Shader.Find("Skybox/Procedural")); AssetDatabase.CreateAsset(sky, path); }
        sky.SetColor("_SkyTint", new Color(.52f, .60f, .69f));
        sky.SetColor("_GroundColor", new Color(.31f, .33f, .27f));
        sky.SetFloat("_AtmosphereThickness", 1.15f); sky.SetFloat("_Exposure", .85f); sky.SetFloat("_SunSize", .025f);
        EditorUtility.SetDirty(sky); return sky;
    }

    private static void ConfigureLight(string name, Color color, float intensity, Vector3 angles)
    {
        var go = GameObject.Find(name); if (go == null) return;
        var light = go.GetComponent<Light>(); if (light == null) return;
        Undo.RecordObject(light, "Set outdoor lighting"); Undo.RecordObject(go.transform, "Set outdoor lighting");
        light.color = color; light.intensity = intensity; go.transform.rotation = Quaternion.Euler(angles);
        light.shadows = name == "Key" ? LightShadows.Soft : LightShadows.None;
        if (name == "Key") RenderSettings.sun = light;
    }

    private static void Fire(string name, Transform parent, Vector3 position, float scale)
    {
        var go = new GameObject(name); Undo.RegisterCreatedObjectUndo(go, "Create torch flame");
        go.transform.SetParent(parent, false); go.transform.position = position; go.transform.localScale = Vector3.one * scale;
        var particles = go.AddComponent<ParticleSystem>(); particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particles.main; main.duration = 1; main.loop = true; main.playOnAwake = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(.30f, .65f); main.startSpeed = new ParticleSystem.MinMaxCurve(.25f, .55f);
        main.startSize = new ParticleSystem.MinMaxCurve(.11f, .22f); main.maxParticles = 64; main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startColor = new Color(1, .63f, .16f, .85f);
        var emission = particles.emission; emission.rateOverTime = 28;
        var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 8; shape.radius = .08f;
        go.transform.rotation = Quaternion.Euler(-90, 0, 0);
        var color = particles.colorOverLifetime; color.enabled = true;
        var gradient = new Gradient(); gradient.SetKeys(new[] { new GradientColorKey(new Color(1, .90f, .40f), 0), new GradientColorKey(new Color(1, .30f, .04f), .6f), new GradientColorKey(new Color(.4f, .06f, .01f), 1) }, new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(.9f, .15f), new GradientAlphaKey(0, 1) }); color.color = gradient;
        var size = particles.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1, new AnimationCurve(new Keyframe(0, .4f), new Keyframe(.25f, 1), new Keyframe(1, 0)));
        var renderer = go.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = FlameMaterial(); renderer.renderMode = ParticleSystemRenderMode.Billboard;
        var lamp = new GameObject("Warm torch light"); Undo.RegisterCreatedObjectUndo(lamp, "Create torch light"); lamp.transform.SetParent(go.transform, false);
        var light = lamp.AddComponent<Light>(); light.type = LightType.Point; light.color = new Color(1, .40f, .10f); light.intensity = 2; light.range = 3.5f;
        particles.Play();
    }

    private static Material FlameMaterial()
    {
        string path = Materials + "/Flame.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path); if (mat != null) return mat;
        var texture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            float radius = new Vector2((x - 15.5f) / 15.5f, (y - 15.5f) / 15.5f).magnitude;
            texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1 - radius) * 2)));
        }
        texture.Apply(); AssetDatabase.CreateAsset(texture, Materials + "/FlameMask.asset");
        mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")); mat.SetTexture("_BaseMap", texture);
        mat.SetFloat("_Surface", 1); mat.SetFloat("_Blend", 0); mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0); mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); mat.renderQueue = 3000;
        AssetDatabase.CreateAsset(mat, path); return mat;
    }

    private static void EnsureFolder(string path)
    {
        if (!AssetDatabase.IsValidFolder(path))
        {
            int slash = path.LastIndexOf('/'); AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
