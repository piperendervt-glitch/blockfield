using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
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

    /// <summary>環境深度オクルージョン対応シェーダー（オクルードさせたい全マテリアルに適用）。</summary>
    const string k_OcclusionShader = "BlockField/OcclusionUnlit";

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

        // 環境深度をグローバルシェーダープロパティへ流す (これが無いと AROcclusionManager を
        // 有効化しても描画には一切適用されない)。オクルージョンさせたいマテリアルは
        // BlockField/OcclusionUnlit (XR_HARD_OCCLUSION 対応) を使うこと。
        // 本体は常時有効で問題ない: XR_HARD_OCCLUSION キーワードは AROcclusionManager が
        // 権限取得後に有効化されて深度フレームが届いて初めて点灯する。
        camGo.AddComponent<ARShaderOcclusion>();

        // USE_SCENE 権限フロー (Demo 0 T1)
        var gateGo = new GameObject("Scene Permission Gate");
        var gate = gateGo.AddComponent<BlockField.ScenePermissionGate>();
        gate.occlusionManager = occlusion;

        origin.Camera = cam;
        origin.CameraFloorOffsetObject = offsetGo;
        // Floor基準を明示（未指定だとDeviceモードにフォールバックし CameraYOffset(1.1176) が
        // 加算され、原点確定位置のy値が床基準でなくなり紛らわしいため）
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;

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
        diorama.originMaterial = GetOrCreateMaterial("OriginRed", new Color(0.9f, 0.1f, 0.1f), k_OcclusionShader);
        // レティクルは視認性優先で Unlit の明るい緑
        diorama.reticleMaterial = GetOrCreateMaterial("ReticleWhite", new Color(0.3f, 1f, 0.4f), "Universal Render Pipeline/Unlit");

        // 地形表示 (Demo 1 B2): 頂点色対応のオクルージョンシェーダー1マテリアルに統一
        var terrainMat = GetOrCreateMaterial("TerrainVertexColor", Color.white, k_OcclusionShader);
        terrainMat.SetFloat("_UseVertexColor", 1f);
        terrainMat.EnableKeyword("_VERTEX_COLOR");
        EditorUtility.SetDirty(terrainMat);

        var terrainField = dioramaGo.AddComponent<BlockField.TerrainField>();
        terrainField.origin = diorama;
        terrainField.terrainMaterial = terrainMat;

        // エンティティ表示 (Demo 2 D5 / Demo 3 E0,E7)。
        // 面明度差キューブ (ShadedCube) の頂点色を効かせるため _VERTEX_COLOR を有効化する
        var entityRenderer = dioramaGo.AddComponent<BlockField.EntityRenderer>();
        entityRenderer.terrainField = terrainField;
        entityRenderer.grassTuftMaterial = CreateEntityMaterial("EntityGrassTuft", new Color(0.25f, 0.8f, 0.25f));
        entityRenderer.flowerMaterial = CreateEntityMaterial("EntityFlower", new Color(0.95f, 0.85f, 0.25f));
        entityRenderer.sheepMaterial = CreateEntityMaterial("EntitySheep", new Color(0.95f, 0.95f, 0.95f));
        entityRenderer.pigMaterial = CreateEntityMaterial("EntityPig", new Color(0.95f, 0.65f, 0.7f));
        entityRenderer.wolfMaterial = CreateEntityMaterial("EntityWolf", new Color(0.55f, 0.55f, 0.6f));

        // 設置・破壊操作 (Demo 4 F3)。全マテリアル不透明（MR合成制約）
        var interactor = dioramaGo.AddComponent<BlockField.BlockInteractor>();
        interactor.terrainField = terrainField;
        interactor.trackingSpace = offsetGo.transform;
        interactor.breakHighlightMaterial = GetOrCreateMaterial("AimBreakFrame", new Color(0.95f, 0.2f, 0.15f), k_OcclusionShader);
        interactor.placeHighlightMaterial = GetOrCreateMaterial("AimPlaceFrame", Color.white, k_OcclusionShader);
        interactor.pendingPlaceMaterial = GetOrCreateMaterial("PendingPlace", new Color(0.6f, 0.6f, 0.63f), k_OcclusionShader);
        interactor.pendingBreakMaterial = GetOrCreateMaterial("PendingBreak", new Color(0.12f, 0.12f, 0.13f), k_OcclusionShader);
        interactor.rayHitMaterial = GetOrCreateMaterial("RayHit", Color.white, k_OcclusionShader);
        interactor.rayMissMaterial = GetOrCreateMaterial("RayMiss", new Color(0.35f, 0.35f, 0.38f), k_OcclusionShader);

        // 部屋スキャン (Demo 4.5 G1)。ARMeshManager は XR Origin の子である必要がある
        // （Demo 3 E6 の MeshRecon を恒久機能化したもの）
        var scannerGo = new GameObject("Room Scanner (G1)");
        scannerGo.transform.SetParent(originGo.transform, false);
        var meshManager = scannerGo.AddComponent<ARMeshManager>();
        meshManager.meshPrefab = GetOrCreateReconMeshPrefab();
        var roomScanner = scannerGo.AddComponent<BlockField.RoomScanner>();
        roomScanner.meshManager = meshManager;
        roomScanner.planeManager = planeManager;

        // 多層ハイトマップ化 (Demo 4.5 G2)。表示はまだ行わず、統計をログ出力するのみ
        var roomTerrain = scannerGo.AddComponent<BlockField.RoomTerrainBuilder>();
        roomTerrain.scanner = roomScanner;
        roomTerrain.terrainField = terrainField;

        // HMD内デバッグパネル (World Space Canvas、カメラ前下方 0.6m に固定)
        var canvasGo = new GameObject("Debug Panel");
        canvasGo.transform.SetParent(camGo.transform, false);
        canvasGo.transform.localPosition = new Vector3(0f, -0.25f, 0.6f);
        canvasGo.transform.localScale = Vector3.one * 0.0007f;
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(600f, 300f);

        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(canvasGo.transform, false);
        var bg = bgGo.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.55f);
        bg.rectTransform.anchorMin = Vector2.zero;
        bg.rectTransform.anchorMax = Vector2.one;
        bg.rectTransform.offsetMin = Vector2.zero;
        bg.rectTransform.offsetMax = Vector2.zero;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(canvasGo.transform, false);
        var uiText = textGo.AddComponent<Text>();
        uiText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        uiText.fontSize = 30;
        uiText.color = Color.white;
        uiText.alignment = TextAnchor.UpperLeft;
        uiText.horizontalOverflow = HorizontalWrapMode.Overflow;
        uiText.verticalOverflow = VerticalWrapMode.Overflow;
        uiText.text = "DebugPanel";
        uiText.rectTransform.anchorMin = Vector2.zero;
        uiText.rectTransform.anchorMax = Vector2.one;
        uiText.rectTransform.offsetMin = new Vector2(16f, 12f);
        uiText.rectTransform.offsetMax = new Vector2(-16f, -12f);

        var panel = canvasGo.AddComponent<BlockField.DebugPanel>();
        panel.diorama = diorama;
        panel.terrainField = terrainField;
        panel.planeManager = planeManager;
        panel.text = uiText;

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

    /// <summary>エンティティ用マテリアル: オクルージョンシェーダー＋頂点色（面明度差）有効。</summary>
    static Material CreateEntityMaterial(string name, Color color)
    {
        var mat = GetOrCreateMaterial(name, color, k_OcclusionShader);
        mat.SetFloat("_UseVertexColor", 1f);
        mat.EnableKeyword("_VERTEX_COLOR");
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>
    /// 不透明設定の強制。MR合成の制約（α&lt;1の描画はパススルーと合成される）により
    /// 実機マテリアルは原則すべて不透明にする。
    /// </summary>
    static void ApplyOpaqueDefaults(Material mat)
    {
        mat.SetFloat("_Surface", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
        mat.SetFloat("_ZWrite", 1f);
        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = -1;
    }

    /// <summary>偵察メッシュ用プレハブ（MeshFilter＋無効化レンダラー＝見た目なし）。</summary>
    static MeshFilter GetOrCreateReconMeshPrefab()
    {
        const string dir = "Assets/Prefabs";
        const string path = dir + "/ReconMesh.prefab";
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing == null)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            var temp = new GameObject("ReconMesh");
            temp.AddComponent<MeshFilter>();
            var renderer = temp.AddComponent<MeshRenderer>();
            renderer.enabled = false; // ログ取得のみ、描画しない
            existing = PrefabUtility.SaveAsPrefabAsset(temp, path);
            Object.DestroyImmediate(temp);
        }
        return existing.GetComponent<MeshFilter>();
    }

    /// <summary>
    /// Assets/Materials/ にマテリアルアセットを（無ければ）生成して返す。
    /// 既存アセットにもシェーダー・色を適用し、コードを唯一の情報源にする。
    /// </summary>
    static Material GetOrCreateMaterial(string name, Color color, string shaderName = "Universal Render Pipeline/Lit")
    {
        const string dir = "Assets/Materials";
        string path = dir + "/" + name + ".mat";
        var shader = Shader.Find(shaderName);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
        {
            mat.shader = shader;
            mat.color = color;
            ApplyOpaqueDefaults(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
        }

        mat = new Material(shader) { color = color };
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
