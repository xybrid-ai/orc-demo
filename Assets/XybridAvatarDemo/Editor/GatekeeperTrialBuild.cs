using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// A separate player variant; the Editor and regular builds retain E2B.
public static class GatekeeperTrialBuild
{
    [Serializable] private sealed class Receipt
    {
        public string result, model = "gemma3npc-it-q4", voice = "Phonon zzz rc.9", path;
        public int errors, warnings;
        public double seconds;
        public ulong bytes;
    }

    public static void BuildMac()
    {
        string output = Environment.GetEnvironmentVariable("ORC_TRIAL_BUILD_PATH");
        if (string.IsNullOrWhiteSpace(output) || !output.EndsWith(".app", StringComparison.Ordinal))
            throw new InvalidOperationException("ORC_TRIAL_BUILD_PATH must name the output .app.");
        foreach (string relative in new[] { "XybridModels/gemma3npc-it-q4/model_metadata.json", "PhononZzz/model.gguf" })
            if (!File.Exists(Path.Combine(Application.streamingAssetsPath, relative)))
                throw new FileNotFoundException("Trial dependency missing: " + relative);

        int originalArchitecture = PlayerSettings.GetArchitecture(BuildTargetGroup.Standalone);
        try
        {
            PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1); // Apple Silicon ARM64.
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/XybridAvatarDemo/GoblinSpeakingDemo.unity" },
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                extraScriptingDefines = new[] { "XYBRID_GEMMA3_TRIAL" },
                options = BuildOptions.StrictMode
            });
            var summary = report.summary;
            File.WriteAllText(output + ".build.json", JsonUtility.ToJson(new Receipt
            {
                result = summary.result.ToString(), path = output,
                errors = (int)summary.totalErrors, warnings = (int)summary.totalWarnings,
                seconds = summary.totalTime.TotalSeconds, bytes = summary.totalSize
            }, true));
            if (summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Trial build failed: " + summary.result);
        }
        finally { PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, originalArchitecture); }
    }
}
