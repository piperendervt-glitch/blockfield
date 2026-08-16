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
    public static void BuildQuest() => Build(SceneBootstrap.ScenePath, EnsureMainScene);

    /// <summary>
    /// 水槽シーン (系列2 Phase B) をビルドする。
    /// バッチモード: ... -executeMethod BuildScript.BuildAquarium
    ///
    /// **Main.unity とは別の APK にはしない。** 入れ替えて同じパッケージ名で
    /// 上書きインストールする形なので、実機には最後にビルドしたほうが入る。
    /// どちらを焼いたかはログの先頭行で分かるようにしてある。
    /// </summary>
    [MenuItem("Tools/Project Setup/Build Aquarium APK")]
    public static void BuildAquarium() =>
        Build(AquariumSceneBootstrap.ScenePath, EnsureAquariumScene);

    static void EnsureMainScene()
    {
        if (!File.Exists(SceneBootstrap.ScenePath))
        {
            Debug.Log("[BuildScript] Main.unity が無いため生成する。");
            SceneBootstrap.CreateMainScene();
        }
    }

    static void EnsureAquariumScene()
    {
        if (!File.Exists(AquariumSceneBootstrap.ScenePath))
        {
            Debug.Log("[BuildScript] Aquarium.unity が無いため生成する。");
            AquariumSceneBootstrap.CreateAquariumScene();
        }
    }

    static void Build(string scenePath, Action ensureScene)
    {
        try
        {
            // シーンが無ければコードで生成（GUI手作業に依存しない）
            ensureScene();

            Debug.Log($"[BuildScript] 焼くシーン: {scenePath}");
            Directory.CreateDirectory("Builds");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
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
