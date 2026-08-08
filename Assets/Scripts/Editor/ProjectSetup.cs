using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Quest 3 MR 向けプロジェクト初期設定。
/// バッチモード: Unity.exe -batchmode -nographics -quit -projectPath . -buildTarget Android -executeMethod ProjectSetup.Apply
/// </summary>
public static class ProjectSetup
{
    private const string SettingsDir = "Assets/Settings";
    private const string RendererDataPath = SettingsDir + "/URP-Renderer.asset";
    private const string PipelineAssetPath = SettingsDir + "/URP-Asset.asset";

    [MenuItem("Tools/Project Setup/Apply")]
    public static void Apply()
    {
        try
        {
            ConfigurePlayerSettings();
            ConfigureUrp();

            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectSetup] Apply completed successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ProjectSetup] Apply FAILED: {e}");
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

    private static void ConfigurePlayerSettings()
    {
        PlayerSettings.productName = "blockfield";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.piperender.blockfield");

        // IL2CPP + ARM64 のみ
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // Min SDK 32 (Android 12L) — Meta Quest Camera (Passthrough) の要件
        PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;

        // Graphics API: Vulkan のみ
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });

        Debug.Log("[ProjectSetup] PlayerSettings configured (IL2CPP / ARM64 / MinSdk32 / Vulkan).");
    }

    private static void ConfigureUrp()
    {
        if (!Directory.Exists(SettingsDir))
        {
            Directory.CreateDirectory(SettingsDir);
            AssetDatabase.Refresh();
        }

        // Renderer Data
        var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererDataPath);
        if (rendererData == null)
        {
            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, RendererDataPath);
        }

        // シェーダー等の未割り当てリソースをパッケージから補完
        try
        {
            ResourceReloader.ReloadAllNullIn(rendererData, UniversalRenderPipelineAsset.packagePath);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ProjectSetup] ResourceReloader skipped: {e.Message}");
        }
        EditorUtility.SetDirty(rendererData);

        // Pipeline Asset
        var pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
        if (pipelineAsset == null)
        {
            pipelineAsset = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(pipelineAsset, PipelineAssetPath);
        }
        EditorUtility.SetDirty(pipelineAsset);

        // Graphics / Quality へ割り当て
        GraphicsSettings.defaultRenderPipeline = pipelineAsset;
        int currentLevel = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = pipelineAsset;
        }
        QualitySettings.SetQualityLevel(currentLevel, false);

        Debug.Log($"[ProjectSetup] URP assets created at {SettingsDir} and assigned to Graphics/Quality settings.");
    }
}
