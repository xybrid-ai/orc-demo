using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Tripwire for writes to the gatekeeper scene from outside Play Mode. Unity restores the scene when Play
// Mode ends, so a scene that was clean before Play and turns dirty within a few seconds after it was
// written by some script in Edit Mode; the warning names the changed objects and, for serialized edits,
// the calling script. Before every save it returns the troll to his prefab state (blend shapes and bones),
// since the idle and the performance layer pose him at runtime and a saved pose is only a leftover preview.
[InitializeOnLoad]
static class XybridSceneGuard
{
    private const float WatchSeconds = 5;
    private const string CleanKey = "XybridSceneGuard.CleanBeforePlay", UntilKey = "XybridSceneGuard.WatchUntil";
    private static readonly List<string> changes = new List<string>();
    private static bool reported;

    static XybridSceneGuard()
    {
        EditorApplication.playModeStateChanged += OnPlayMode;
        ObjectChangeEvents.changesPublished += OnChanges;
        Undo.postprocessModifications += OnModifications;
        EditorApplication.update += Check;
        EditorSceneManager.sceneSaving += OnSaving;
    }

    private static bool Watching => !EditorApplication.isPlayingOrWillChangePlaymode && EditorApplication.timeSinceStartup < SessionState.GetFloat(UntilKey, 0);

    private static void OnPlayMode(PlayModeStateChange change)
    {
        var scene = SceneManager.GetActiveScene();
        // Session state, because entering Play Mode reloads the domain.
        if (change == PlayModeStateChange.ExitingEditMode)
            SessionState.SetBool(CleanKey, scene.path == "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity" && !scene.isDirty);
        else if (change == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(CleanKey, false))
        {
            changes.Clear(); reported = false;
            SessionState.SetFloat(UntilKey, (float)EditorApplication.timeSinceStartup + WatchSeconds);
        }
    }

    private static void OnChanges(ref ObjectChangeEventStream stream)
    {
        if (!Watching) return;
        for (int i = 0; i < stream.length; i++)
        {
            var kind = stream.GetEventType(i); EntityId id = EntityId.None;
            switch (kind)
            {
                case ObjectChangeKind.ChangeGameObjectOrComponentProperties: { stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var e); id = e.entityId; break; }
                case ObjectChangeKind.ChangeGameObjectStructure: { stream.GetChangeGameObjectStructureEvent(i, out var e); id = e.entityId; break; }
                case ObjectChangeKind.ChangeGameObjectStructureHierarchy: { stream.GetChangeGameObjectStructureHierarchyEvent(i, out var e); id = e.entityId; break; }
                case ObjectChangeKind.ChangeGameObjectParent: { stream.GetChangeGameObjectParentEvent(i, out var e); id = e.entityId; break; }
                case ObjectChangeKind.CreateGameObjectHierarchy: { stream.GetCreateGameObjectHierarchyEvent(i, out var e); id = e.entityId; break; }
                case ObjectChangeKind.DestroyGameObjectHierarchy: { stream.GetDestroyGameObjectHierarchyEvent(i, out var e); id = e.entityId; break; }
                case ObjectChangeKind.ChangeScene: break;
                default: continue;
            }
            changes.Add(kind + " " + (id.IsValid() ? Describe(EditorUtility.EntityIdToObject(id)) : "scene"));
        }
    }

    private static UndoPropertyModification[] OnModifications(UndoPropertyModification[] modifications)
    {
        if (Watching)
            foreach (var m in modifications)
                changes.Add(Describe(m.currentValue.target) + " " + m.currentValue.propertyPath + " by " + Caller());
        return modifications;
    }

    private static void Check()
    {
        if (reported || !Watching) return;
        var scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity" || !scene.isDirty) return;
        reported = true;
        Debug.LogWarning("[Scene guard] " + scene.name + " was clean before Play Mode and was modified just after it, so a script wrote to it from Edit Mode. "
            + (changes.Count > 0 ? "Changes: " + string.Join("; ", changes.Distinct().Take(12)) : "No change event named the object.")
            + " Undo (Ctrl/Cmd+Z) or reload the scene to discard them.");
    }

    // Every save of the gatekeeper scene first returns the troll to his prefab state (XybridTrollPrefabState).
    private static void OnSaving(Scene scene, string path)
    {
        if (scene.path != "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity" || EditorApplication.isPlaying) return;
        XybridAvatarPlayer player = null;
        foreach (var root in scene.GetRootGameObjects()) { player = root.GetComponentInChildren<XybridAvatarPlayer>(true); if (player != null) break; }
        var restored = XybridTrollPrefabState.Restore(player);
        if (restored.Count > 0) Debug.Log("[Scene guard] Restored the troll to his prefab state before saving " + scene.name + ": " + string.Join(", ", restored) + ".");
    }

    private static string Describe(UnityEngine.Object target)
    {
        if (target is Component component) return Path(component.transform) + " (" + component.GetType().Name + ")";
        if (target is GameObject gameObject) return Path(gameObject.transform);
        return target != null ? target.name + " (" + target.GetType().Name + ")" : "a destroyed object";
    }
    private static string Path(Transform t) { string path = t.name; while (t.parent != null) { t = t.parent; path = t.name + "/" + path; } return path; }
    private static string Caller()
    {
        foreach (string frame in Environment.StackTrace.Split('\n').Select(f => f.Trim()))
        {
            if (!frame.StartsWith("at ", StringComparison.Ordinal)) continue;
            string call = frame.Substring(3);
            if (call.StartsWith("System.") || call.StartsWith("UnityEditor.") || call.StartsWith("UnityEngine.") || call.StartsWith("XybridSceneGuard")) continue;
            int args = call.IndexOf('(');
            return args > 0 ? call.Substring(0, args) : call;
        }
        return "an unknown caller";
    }
}
