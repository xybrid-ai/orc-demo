using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class XybridKittenChecks
{
    private const string ReportKey = "XybridKitten.Report";
    static XybridKittenChecks()
    {
        EditorApplication.playModeStateChanged += change =>
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            string report = SessionState.GetString(ReportKey, "");
            if (string.IsNullOrEmpty(report)) return;
            SessionState.EraseString(ReportKey);
            GatekeeperSpeechValidation.Begin(UnityEngine.Object.FindFirstObjectByType<GatekeeperController>(), report);
        };
        EditorApplication.update += () =>
        {
            if (!EditorApplication.isPlaying) return;
            var test = UnityEngine.Object.FindFirstObjectByType<GatekeeperSpeechValidation>();
            if (test != null && test.Done) EditorApplication.ExitPlaymode();
        };
    }
    public static void Queue(string report)
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        if (SceneManager.GetActiveScene().path != "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity")
            throw new InvalidOperationException("Open the troll scene first.");
        SessionState.SetString(ReportKey, Path.GetFullPath(report));
        EditorApplication.EnterPlaymode();
    }
    [MenuItem("Xybrid/Avatar Demo/Validate Kokoro And Kitten")]
    private static void Validate() => Queue(Path.Combine(Application.dataPath, "../Library/XybridAvatarDemo/kitten-editor-report.json"));
    // Call directly. No delayed build helper; no scene mutation or save.
    public static void Build(string appPath, string reportPath)
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        var target = NamedBuildTarget.Standalone;
        int previousArchitecture = PlayerSettings.GetArchitecture(target);
        string previousMinimum = PlayerSettings.macOS.targetOSVersion;
        try
        {
            PlayerSettings.SetArchitecture(target, 1); // macOS Apple Silicon, required by rc.4.
            PlayerSettings.macOS.targetOSVersion = "14.0"; // Complete local SDK dylib deployment minimum.
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity" },
                target = BuildTarget.StandaloneOSX, locationPathName = appPath,
                options = BuildOptions.Development });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            File.WriteAllText(reportPath, "result=" + result.summary.result + "\nseconds=" + result.summary.totalTime.TotalSeconds
                + "\nerrors=" + result.summary.totalErrors + "\narchitecture=Apple Silicon\n");
            if (result.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Kitten player build failed.");
        }
        finally
        {
            PlayerSettings.SetArchitecture(target, previousArchitecture);
            PlayerSettings.macOS.targetOSVersion = previousMinimum;
        }
    }
}
