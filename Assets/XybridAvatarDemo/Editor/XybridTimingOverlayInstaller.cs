using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Adds the voice timing overlay (F3) to the gatekeeper scene and saves it. Safe to rerun.
public static class XybridTimingOverlayInstaller
{
    [MenuItem("Xybrid/Avatar Demo/Add Voice Timing Overlay")]
    public static string Install()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open " + "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity" + " first.");
        if (scene.isDirty) throw new InvalidOperationException("Save or discard the scene's changes first, so only the overlay is saved.");
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null) throw new InvalidOperationException("The gatekeeper scene has no GatekeeperController.");
        var overlay = controller.GetComponent<GatekeeperTimingOverlay>();
        if (overlay == null) overlay = Undo.AddComponent<GatekeeperTimingOverlay>(controller.gameObject);
        Undo.RecordObject(overlay, "Wire voice timing overlay");
        overlay.gatekeeper = controller;
        EditorUtility.SetDirty(overlay);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        string message = "Voice timing overlay on '" + controller.name + "' (" + overlay.toggleKey + " toggles it).";
        Debug.Log("[Gatekeeper] " + message);
        return message;
    }
}
