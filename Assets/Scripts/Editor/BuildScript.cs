using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Quest 3 向け Android APK ビルド。
/// バッチモード: Unity.exe -batchmode -nographics -quit -projectPath . -buildTarget Android -executeMethod BuildScript.BuildQuest
/// </summary>
public static class BuildScript
{
    private const string OutputPath = "Builds/blockfield.apk";

    [MenuItem("Tools/Project Setup/Build Quest APK")]
    public static void BuildQuest()
    {
        try
        {
            // シーンが無ければコードで生成（GUI手作業に依存しない）
            if (!File.Exists(SceneBootstrap.ScenePath))
            {
                Debug.Log("[BuildScript] Main.unity が無いため生成する。");
                SceneBootstrap.CreateMainScene();
            }

            Directory.CreateDirectory("Builds");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { SceneBootstrap.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[BuildScript] Build FAILED: result={summary.result}, errors={summary.totalErrors}");
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }
                return;
            }

            double sizeMb = summary.totalSize / (1024.0 * 1024.0);
            Debug.Log($"[BuildScript] Build succeeded: {OutputPath} ({sizeMb:F1} MB, {summary.totalTime.TotalSeconds:F0} sec)");
        }
        catch (Exception e)
        {
            Debug.LogError($"[BuildScript] Build FAILED with exception: {e}");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(1);
            }
            else
            {
                throw;
            }
        }
    }
}
