using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Returns the troll to his prefab state: every blend shape at the prefab's weight and every bone at the
// prefab's local position, rotation and scale, with no blend-shape or bone overrides left on the instance.
// His placement (the instance root's transform), name and other component settings are kept. At runtime the
// idle clip and the performance layer pose him every frame, so a saved pose is only a leftover preview.
// XybridSceneGuard runs this before every save of the gatekeeper scene.
public static class XybridTrollPrefabState
{
    [MenuItem("Xybrid/Avatar Demo/Restore Troll To Prefab State")]
    public static void RestoreFromMenu()
    {
        var player = UnityEngine.Object.FindFirstObjectByType<XybridAvatarPlayer>();
        if (player == null) throw new InvalidOperationException("Open the gatekeeper scene first.");
        var restored = Restore(player);
        Debug.Log("[Gatekeeper] " + (restored.Count > 0 ? "Restored the troll to his prefab state: " + string.Join(", ", restored) : "The troll already matches his prefab."));
        if (restored.Count > 0) EditorSceneManager.MarkSceneDirty(player.gameObject.scene);
    }

    // Returns what it restored, e.g. "12 bone overrides"; empty when the troll already matched.
    public static List<string> Restore(XybridAvatarPlayer player)
    {
        var restored = new List<string>();
        var root = player != null ? PrefabUtility.GetOutermostPrefabInstanceRoot(player.gameObject) : null;
        if (root == null || EditorApplication.isPlaying) return restored;
        var rootSource = PrefabUtility.GetCorrespondingObjectFromSource(root.transform);

        // Values written by scripts (EvaluateAt, previews) are not recorded as overrides yet; reset them directly.
        int shapes = 0, bones = 0;
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(renderer);
            if (renderer.sharedMesh == null || source == null) continue;
            int sourceCount = source.sharedMesh != null ? source.sharedMesh.blendShapeCount : 0;
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                float rest = i < sourceCount ? source.GetBlendShapeWeight(i) : 0;
                if (renderer.GetBlendShapeWeight(i) != rest) { renderer.SetBlendShapeWeight(i, rest); shapes++; }
            }
        }
        foreach (var bone in root.GetComponentsInChildren<Transform>(true))
        {
            if (bone == root.transform) continue;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(bone);
            if (source == null) continue;
            if (bone.localPosition != source.localPosition || bone.localRotation != source.localRotation || bone.localScale != source.localScale)
            {
                bone.localPosition = source.localPosition; bone.localRotation = source.localRotation; bone.localScale = source.localScale;
                bones++;
            }
        }

        // Recorded overrides: blend-shape weights, and bone transforms other than the root's placement.
        var modifications = PrefabUtility.GetPropertyModifications(root) ?? new PropertyModification[0];
        bool Pose(PropertyModification m) =>
            m.target is SkinnedMeshRenderer && m.propertyPath.StartsWith("m_BlendShapeWeights", StringComparison.Ordinal)
            || m.target is Transform t && t != rootSource && (m.propertyPath.StartsWith("m_LocalPosition", StringComparison.Ordinal)
                || m.propertyPath.StartsWith("m_LocalRotation", StringComparison.Ordinal) || m.propertyPath.StartsWith("m_LocalScale", StringComparison.Ordinal)
                || m.propertyPath.StartsWith("m_LocalEulerAnglesHint", StringComparison.Ordinal));
        int overrides = modifications.Count(Pose);
        if (overrides > 0) PrefabUtility.SetPropertyModifications(root, modifications.Where(m => !Pose(m)).ToArray());

        if (overrides > 0) restored.Add(overrides + " blend-shape and bone overrides");
        if (shapes > 0) restored.Add(shapes + " blend-shape weights");
        if (bones > 0) restored.Add(bones + " bone poses");
        return restored;
    }
}
