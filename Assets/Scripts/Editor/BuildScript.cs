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
    // 【APK 名をターゲット別に分ける】以前は両方 Builds/blockfield.apk だったため、
    // **ファイル名からどちらを焼いたか区別できなかった**。2026-08-19 に
    // -Aquarium を付け忘れて本編シーンを実機へ入れ、水槽の実機セッションのつもりで
    // 生態系を起動している（ビルドし直しで15分の損失）。名前で分ける
    private const string MainOutputPath = "Builds/blockfield_main.apk";
    private const string AquariumOutputPath = "Builds/blockfield_aquarium.apk";

    [MenuItem("Tools/Project Setup/Build Quest APK")]
    public static void BuildQuest() => Build(SceneBootstrap.ScenePath, EnsureMainScene, MainOutputPath);

    /// <summary>
    /// 水槽シーン (系列2 Phase B) をビルドする。
    /// バッチモード: ... -executeMethod BuildScript.BuildAquarium
    ///
    /// **APK のファイル名は分ける**（blockfield_aquarium.apk）。パッケージ名は同じなので
    /// 実機には最後にインストールしたほうが入るが、少なくとも
    /// **どちらを焼いたかがファイル名で分かる**ようにする。
    /// </summary>
    [MenuItem("Tools/Project Setup/Build Aquarium APK")]
    public static void BuildAquarium() =>
        Build(AquariumSceneBootstrap.ScenePath, EnsureAquariumScene, AquariumOutputPath);

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

    static void Build(string scenePath, Action ensureScene, string OutputPath)
    {
        WriteBuildStamp(scenePath);

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

    /// <summary>
    /// 「どのシーン・どのコミットか」を Resources へ刻む。実機のパネルに出す。
    /// 2026-08-19 にシーンを取り違えたまま実機セッションを始めた件の再発防止。
    /// </summary>
    static void WriteBuildStamp(string scenePath)
    {
        string scene = Path.GetFileNameWithoutExtension(scenePath);
        string branch = Git("rev-parse --abbrev-ref HEAD");
        string head = Git("rev-parse --short HEAD");
        string dirty = string.IsNullOrEmpty(Git("status --porcelain")) ? "" : "+dirty";
        string stamp = $"{scene} | {branch}@{head}{dirty} | {DateTime.Now:MM-dd HH:mm}";

        Directory.CreateDirectory("Assets/Resources");
        File.WriteAllText("Assets/Resources/BuildStamp.txt", stamp);
        AssetDatabase.Refresh();
        Debug.Log($"[BuildScript] 刻印: {stamp}");
    }

    static string Git(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return outp.Trim();
        }
        catch { return "?"; }
    }
}
