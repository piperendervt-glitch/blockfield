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
        // Clear Flags と背景色は PassthroughController が実行時に設定する（下で接続）。
        // ここでの値はエディタで開いたときの見た目のための初期値にすぎない
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

        var cameraManager = camGo.AddComponent<ARCameraManager>();
        var cameraBackground = camGo.AddComponent<ARCameraBackground>();

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
        var shaderOcclusion = camGo.AddComponent<ARShaderOcclusion>();

        // USE_SCENE 権限フロー (Demo 0 T1)
        var gateGo = new GameObject("Scene Permission Gate");
        var gate = gateGo.AddComponent<BlockField.ScenePermissionGate>();
        gate.occlusionManager = occlusion;

        // パススルーの有効/無効を1箇所に集約 (Demo 4.5b VRモードの下ごしらえ)。
        // 現時点では起動時にパススルー有効を適用するだけで挙動は変わらない
        var passthrough = camGo.AddComponent<BlockField.PassthroughController>();
        passthrough.targetCamera = cam;
        passthrough.cameraManager = cameraManager;
        passthrough.cameraBackground = cameraBackground;
        passthrough.occlusionManager = occlusion;
        // VRモードで深度が凍結したまま遮蔽が効き続けるのを防ぐため、こちらも切る
        passthrough.shaderOcclusion = shaderOcclusion;

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
        // Demo 4.5: 原点マーカー（赤い立方体）は Demo 0 M1 判定用。役目は終わったので隠す
        diorama.showMarker = false;
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
        // roomBuilder は下で作った後に接続する（部屋モード = 箱庭グリッドを生成しない）

        // エンティティ表示 (Demo 2 D5 / Demo 3 E0,E7)。
        // 面明度差キューブ (ShadedCube) の頂点色を効かせるため _VERTEX_COLOR を有効化する
        var entityRenderer = dioramaGo.AddComponent<BlockField.EntityRenderer>();
        entityRenderer.terrainField = terrainField;
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
        roomScanner.origin = diorama; // 観測時のアンカーポーズを記録するため

        // 多層ハイトマップ化と壁の Boundary 化 (Demo 4.5 G2/G4)
        var roomTerrain = scannerGo.AddComponent<BlockField.RoomTerrainBuilder>();
        roomTerrain.scanner = roomScanner;
        roomTerrain.terrainField = terrainField;

        // G7: 生態系を部屋地形の上で動かす。箱庭グリッド (50x50) は生成されなくなる
        terrainField.roomBuilder = roomTerrain;

        // 診断表示 (Demo 4.5 G3)。右手Bで 通常/診断 を切り替える
        var roomViewGo = new GameObject("Room Terrain View (G3)");
        var roomView = roomViewGo.AddComponent<BlockField.RoomTerrainView>();
        roomView.builder = roomTerrain;
        roomView.terrainField = terrainField;
        roomView.material = terrainMat;

        // 部屋の外殻（壁・天井・床下）と MR/VR 切替 (Demo 4.5b V2/V3)。
        // 外殻は地形と同じ TerrainRoot 配下（アンカー相対）に置き、VRモードのみ表示する
        var shellGo = new GameObject("Room Shell View (V2)");
        var shell = shellGo.AddComponent<BlockField.RoomShellView>();
        shell.builder = roomTerrain;
        shell.terrainField = terrainField;
        shell.material = terrainMat;

        var vrModeGo = new GameObject("VR Mode Controller (V3)");
        var vrMode = vrModeGo.AddComponent<BlockField.VrModeController>();
        vrMode.passthrough = passthrough;
        vrMode.shell = shell;

        // HMD内デバッグパネル (World Space Canvas、カメラ前下方 0.6m に固定)
        var canvasGo = new GameObject("Debug Panel");
        canvasGo.transform.SetParent(camGo.transform, false);
        // グラフをパネルの上端に載せたぶん、全体を下げて視野に収める。
        // 0.0007 スケールで パネル620px=0.434m、グラフ143px+隙間10px=0.107m、
        // 合計 0.541m。0.6m 前方に置くと下端 -0.522m(-41.0°)、
        // 上端 +0.019m(+1.8°) で、Quest 3 の垂直FOV(±48°)に収まる
        canvasGo.transform.localPosition = new Vector3(0f, -0.305f, 0.6f);
        canvasGo.transform.localScale = Vector3.one * 0.0007f;
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRect = canvasGo.GetComponent<RectTransform>();
        // Demo 8 で場の2行を追加したため 15 行 → 高さを広げる
        canvasRect.sizeDelta = new Vector2(600f, 620f);

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
        panel.roomBuilder = roomTerrain;
        panel.roomView = roomView;
        panel.vrMode = vrMode;
        panel.shell = shell;
        panel.text = uiText;

        // 個体数の時系列グラフ (Demo 5a)。パネルの真上に幅30cmで置く。
        // Canvas のスケールが 0.0007 なので 430px ≒ 0.30m
        //
        // 【パネルの子にして上端に張り付ける理由】
        // 以前はカメラ直下の独立 Canvas で、パネルとは別の localPosition を
        // 持っていた。パネルは行が増えるたびに sizeDelta.y を広げるが、
        // 中心アンカーなので**高さを増やすと上端が伸びてグラフの下に潜り込む**。
        // 実際 Demo 8 で2行増やして 620px にした結果、上から3行ぶん
        // （USE_SCENE / Origin / **FPS**）がグラフに隠れ、実機セッション中に
        // FPS を読めなくなった。
        // 上端アンカー + pivot 下端にすると、パネルが何行増えても
        // グラフはその上へ押し出されるだけで、重なりが**構造的に起こらない**。
        var graphGo = new GameObject("Population Graph");
        graphGo.transform.SetParent(canvasGo.transform, false);
        var graphRect = graphGo.AddComponent<RectTransform>();
        graphRect.anchorMin = new Vector2(0.5f, 1f);   // パネル上端の中央に
        graphRect.anchorMax = new Vector2(0.5f, 1f);
        graphRect.pivot = new Vector2(0.5f, 0f);       // 自分の下端を基準に上へ伸ばす
        graphRect.sizeDelta = new Vector2(430f, 143f); // 300x100 のテクスチャと同じ比率
        graphRect.anchoredPosition = new Vector2(0f, 10f); // パネル上端から10pxの隙間

        var graphImageGo = new GameObject("Graph Image");
        graphImageGo.transform.SetParent(graphGo.transform, false);
        var rawImage = graphImageGo.AddComponent<RawImage>();
        rawImage.rectTransform.anchorMin = Vector2.zero;
        rawImage.rectTransform.anchorMax = Vector2.one;
        rawImage.rectTransform.offsetMin = Vector2.zero;
        rawImage.rectTransform.offsetMax = Vector2.zero;

        var graph = graphGo.AddComponent<BlockField.PopulationGraph>();
        graph.terrainField = terrainField;
        graph.image = rawImage;

        // 飢餓の色分け (Demo 5a) は診断モードのときだけ効かせる
        entityRenderer.roomView = roomView;

        // 草の表示 (Demo 8.5 K3)。植生場の値から直接メッシュを組む。
        // 植物 Entity が無くなったので、EntityRenderer は動物だけを扱う
        var grassGo = new GameObject("Grass (K3)");
        var grass = grassGo.AddComponent<BlockField.GrassView>();
        grass.terrainField = terrainField;
        grass.builder = roomTerrain;
        grass.material = terrainMat;

        // 場のオーバーレイ (Demo 8 H4)。診断モード中に左手Yで 植生→恐怖→獲物→非表示 を巡回
        var overlayGo = new GameObject("Field Overlay (H4)");
        var overlay = overlayGo.AddComponent<BlockField.FieldOverlayView>();
        overlay.builder = roomTerrain;
        overlay.terrainField = terrainField;
        overlay.roomView = roomView;
        overlay.material = terrainMat;
        panel.fieldOverlay = overlay;
        // 「草が0セルなのか、描画したが見えないのか」を切り分けられるようにする
        panel.grassView = grass;
        // 診断モードで場のオーバーレイを見ているときだけ草を隠す
        // （草が場を覆って濃淡が読めなくなるため）。通常モードでは必ず描く
        grass.fieldOverlay = overlay;
        grass.roomView = roomView;

        // 狼の軌跡 (Demo 8 H4)。診断モードで「今まさに恐怖場を書いている場所」を示す
        var trailGo = new GameObject("Wolf Trail (H4)");
        var trail = trailGo.AddComponent<BlockField.WolfTrailView>();
        trail.terrainField = terrainField;
        trail.roomView = roomView;
        trail.material = terrainMat;

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
