using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

/// <summary>
/// 最小のMR用シーン (Assets/Scenes/Main.unity) をコードで生成する。
/// GUI手作業ではなくスクリプト経由でシーンを構築する規約 (CLAUDE.md) に従う。
/// </summary>
public static class SceneBootstrap
{
    public const string ScenePath = "Assets/Scenes/Main.unity";

    [MenuItem("Tools/Project Setup/Create Main Scene")]
    public static void CreateMainScene()
    {
        if (File.Exists(ScenePath))
        {
            Debug.Log($"[SceneBootstrap] {ScenePath} は既に存在するためスキップ。再生成する場合は削除してから実行すること。");
            return;
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- AR Session ---
        var sessionGo = new GameObject("AR Session");
        sessionGo.AddComponent<ARSession>();
        sessionGo.AddComponent<ARInputManager>();

        // --- XR Origin (AR) 相当 ---
        var originGo = new GameObject("XR Origin (AR)");
        var origin = originGo.AddComponent<XROrigin>();

        var offsetGo = new GameObject("Camera Offset");
        offsetGo.transform.SetParent(originGo.transform, false);

        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        camGo.transform.SetParent(offsetGo.transform, false);

        var cam = camGo.AddComponent<Camera>();
        // パススルー合成のため Solid Color 黒・アルファ0
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.nearClipPlane = 0.05f;
        // HDRバッファ(B10G11R11)はアルファを持たずパススルー合成が壊れるため無効化
        cam.allowHDR = false;

        // HMDトラッキング (Input System)
        var tpd = camGo.AddComponent<TrackedPoseDriver>();
        var positionAction = new InputAction("Position", InputActionType.Value, expectedControlType: "Vector3");
        positionAction.AddBinding("<XRHMD>/centerEyePosition");
        var rotationAction = new InputAction("Rotation", InputActionType.Value, expectedControlType: "Quaternion");
        rotationAction.AddBinding("<XRHMD>/centerEyeRotation");
        var trackingStateAction = new InputAction("Tracking State", InputActionType.Value, expectedControlType: "Integer");
        trackingStateAction.AddBinding("<XRHMD>/trackingState");
        tpd.positionInput = new InputActionProperty(positionAction);
        tpd.rotationInput = new InputActionProperty(rotationAction);
        tpd.trackingStateInput = new InputActionProperty(trackingStateAction);

        camGo.AddComponent<ARCameraManager>();
        camGo.AddComponent<ARCameraBackground>();

        // Occlusion: Environment Depth = Fastest。
        // com.oculus.permission.USE_SCENE (XR_FB_scene) のランタイム権限取得前に
        // 有効化するとMeta OpenXRが毎フレームエラーを出すため、生成時は無効。
        // 実行時に ScenePermissionGate が権限を要求し、許可後に enabled = true にする。
        var occlusion = camGo.AddComponent<AROcclusionManager>();
        occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
        occlusion.enabled = false;

        // USE_SCENE 権限フロー (Demo 0 T1)
        var gateGo = new GameObject("Scene Permission Gate");
        var gate = gateGo.AddComponent<BlockField.ScenePermissionGate>();
        gate.occlusionManager = occlusion;

        origin.Camera = cam;
        origin.CameraFloorOffsetObject = offsetGo;

        // Trackable マネージャー群。
        // 注: ARPlaneManager / ARAnchorManager は [RequireComponent(typeof(XROrigin))] のため
        // 空の GameObject には置けない（置くと XROrigin が二重生成される）。XR Origin 本体に載せる。
        var planeManager = originGo.AddComponent<ARPlaneManager>();
        var anchorManager = originGo.AddComponent<ARAnchorManager>();

        // ジオラマ原点＋ダミーボクセル (Demo 0 T2+T3)
        var dioramaGo = new GameObject("Diorama");
        var diorama = dioramaGo.AddComponent<BlockField.DioramaOrigin>();
        diorama.planeManager = planeManager;
        diorama.anchorManager = anchorManager;
        diorama.trackingSpace = offsetGo.transform;
        diorama.originMaterial = GetOrCreateMaterial("OriginRed", new Color(0.9f, 0.1f, 0.1f));
        diorama.reticleMaterial = GetOrCreateMaterial("ReticleWhite", new Color(1f, 1f, 1f, 0.8f));

        var voxelField = dioramaGo.AddComponent<BlockField.DummyVoxelField>();
        voxelField.origin = diorama;
        voxelField.voxelMaterials = new[]
        {
            GetOrCreateMaterial("Voxel0", new Color(0.35f, 0.65f, 0.30f)),
            GetOrCreateMaterial("Voxel1", new Color(0.55f, 0.42f, 0.28f)),
            GetOrCreateMaterial("Voxel2", new Color(0.75f, 0.70f, 0.50f)),
            GetOrCreateMaterial("Voxel3", new Color(0.45f, 0.55f, 0.65f)),
        };
        voxelField.bridgeMaterial = GetOrCreateMaterial("BridgeBlue", new Color(0.2f, 0.4f, 0.9f));

        Directory.CreateDirectory("Assets/Scenes");
        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            Debug.LogError($"[SceneBootstrap] シーン保存に失敗: {ScenePath}");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(1);
            }
            return;
        }

        // Build Settings のシーン一覧にも登録
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();

        Debug.Log($"[SceneBootstrap] {ScenePath} を生成した。");
    }

    /// <summary>Assets/Materials/ に URP Lit のマテリアルアセットを（無ければ）生成して返す。</summary>
    static Material GetOrCreateMaterial(string name, Color color)
    {
        const string dir = "Assets/Materials";
        string path = dir + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
        {
            return mat;
        }

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
        }

        mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
