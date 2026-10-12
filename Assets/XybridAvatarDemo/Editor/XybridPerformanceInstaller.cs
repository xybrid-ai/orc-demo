using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Installs the troll performance layer into the gatekeeper scene: clears saved blend-shape
// overrides on the troll (the inner-brow 27.5 among them), creates the single settings asset,
// and wires GatekeeperPerformer to the mouth owner, controller, atmosphere and camera.
public static class XybridPerformanceInstaller
{
    public const string SettingsPath = "Assets/XybridAvatarDemo/Performance/TrollPerformance.asset";

    [Serializable] public sealed class Report
    {
        public bool installed;
        public string[] overridesFound = new string[0];
        public string[] nonZeroOverrides = new string[0];
        public string[] weightsAfter = new string[0];
        public bool settingsCreated;
        public string error = "";
    }

    [MenuItem("Xybrid/Avatar Demo/Install Performance Layer")]
    public static void InstallFromMenu() { var r = Install(); Debug.Log("[Performance] " + JsonUtility.ToJson(r, true)); }

    // Overwrites the settings asset with the code defaults (keeps its GUID, so scene references survive).
    [MenuItem("Xybrid/Avatar Demo/Reset Performance Settings To Defaults")]
    public static void ResetSettings()
    {
        var settings = AssetDatabase.LoadAssetAtPath<GatekeeperPerformanceSettings>(SettingsPath);
        if (settings == null) throw new FileNotFoundException("Install the performance layer first.", SettingsPath);
        var fresh = ScriptableObject.CreateInstance<GatekeeperPerformanceSettings>();
        fresh.name = settings.name;
        Undo.RecordObject(settings, "Reset troll performance settings");
        EditorUtility.CopySerialized(fresh, settings);
        UnityEngine.Object.DestroyImmediate(fresh);
        EditorUtility.SetDirty(settings); AssetDatabase.SaveAssets();
    }

    public static Report Install()
    {
        var report = new Report();
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before installing.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open the gatekeeper scene.");
        var player = UnityEngine.Object.FindFirstObjectByType<XybridAvatarPlayer>();
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (player == null || controller == null) throw new InvalidOperationException("The gatekeeper scene needs its character and controller.");

        // 1. Blend-shape overrides: record them, then revert every one so the prefab's zeros are the rest pose.
        var found = new List<string>(); var nonZero = new List<string>();
        var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(player.gameObject);
        foreach (var renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.sharedMesh == null || instanceRoot == null || !PrefabUtility.IsPartOfPrefabInstance(renderer)) continue;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(renderer);
            var mods = PrefabUtility.GetPropertyModifications(instanceRoot) ?? new PropertyModification[0];
            bool Shape(PropertyModification m) => m.target == source && m.propertyPath.StartsWith("m_BlendShapeWeights", StringComparison.Ordinal);
            foreach (var m in mods.Where(Shape))
            {
                int open = m.propertyPath.IndexOf('['), close = m.propertyPath.IndexOf(']');
                int index = open >= 0 && close > open && int.TryParse(m.propertyPath.Substring(open + 1, close - open - 1), out var parsed) ? parsed : -1;
                string name = index >= 0 && index < renderer.sharedMesh.blendShapeCount ? renderer.sharedMesh.GetBlendShapeName(index) : m.propertyPath;
                float prefab = source != null && index >= 0 && index < source.sharedMesh.blendShapeCount ? source.GetBlendShapeWeight(index) : 0;
                string entry = renderer.name + "/" + name + " scene=" + m.value + " prefab=" + prefab;
                found.Add(entry);
                if (float.TryParse(m.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && Mathf.Abs(value - prefab) > 1e-4f) nonZero.Add(entry);
            }
            PrefabUtility.SetPropertyModifications(instanceRoot, mods.Where(m => !Shape(m)).ToArray());
            var after = new List<string>();
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                if (Mathf.Abs(renderer.GetBlendShapeWeight(i)) > 1e-4f) after.Add(renderer.sharedMesh.GetBlendShapeName(i) + "=" + renderer.GetBlendShapeWeight(i));
            report.weightsAfter = report.weightsAfter.Concat(after).ToArray();
        }
        report.overridesFound = found.ToArray(); report.nonZeroOverrides = nonZero.ToArray();

        // 2. The single settings asset.
        var settings = AssetDatabase.LoadAssetAtPath<GatekeeperPerformanceSettings>(SettingsPath);
        if (settings == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            settings = ScriptableObject.CreateInstance<GatekeeperPerformanceSettings>();
            AssetDatabase.CreateAsset(settings, SettingsPath);
            report.settingsCreated = true;
        }

        // 3. The performer, wired to its collaborators.
        var performer = player.GetComponent<GatekeeperPerformer>();
        if (performer == null) performer = Undo.AddComponent<GatekeeperPerformer>(player.gameObject);
        Undo.RecordObject(performer, "Wire troll performance layer");
        performer.settings = settings; performer.character = player; performer.gatekeeper = controller;
        performer.atmosphere = UnityEngine.Object.FindFirstObjectByType<TrolaAtmosphere>();
        performer.viewer = Camera.main;
        EditorUtility.SetDirty(performer);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        report.installed = true;
        return report;
    }
}
