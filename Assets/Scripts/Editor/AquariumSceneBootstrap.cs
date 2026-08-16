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
/// 水槽シーン (Assets/Scenes/Aquarium.unity) をコードで生成する (系列2 Phase B)。
///
/// 【Main.unity との違い】
/// - **外したもの**: voxel terrain の生成、植生、生態系（TerrainField / EntityRenderer /
///   RoomTerrainBuilder / GrassView など一式）
/// - **残したもの**: 空間アンカー（DioramaOrigin）、シーンメッシュ取得（RoomScanner +
///   ARMeshManager）、パススルー、権限フロー
///
/// アンカーとシーンメッシュを残すのは、それが**部屋を水槽にするための空間情報**
/// そのものだからである。外部センサーを買わずに境界・擾乱・光の3つが揃う
/// （roadmap 系列2「着手順序の原則: センサーは後回し」）。
///
/// 【クラゲは入れない】Phase B は流れだけを見える状態にする段。
/// </summary>
public static class AquariumSceneBootstrap
{
    public const string ScenePath = "Assets/Scenes/Aquarium.unity";

    const string k_OcclusionShader = "BlockField/OcclusionUnlit";

    [MenuItem("Tools/Project Setup/Create Aquarium Scene")]
    public static void CreateAquariumScene()
    {
        if (File.Exists(ScenePath))
        {
            Debug.Log($"[AquariumSceneBootstrap] {ScenePath} は既に存在するためスキップ。" +
                "再生成する場合は削除してから実行すること。");
            return;
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- AR Session ---
        var sessionGo = new GameObject("AR Session");
        sessionGo.AddComponent<ARSession>();
        sessionGo.AddComponent<ARInputManager>();

        // --- XR Origin (AR) ---
        var originGo = new GameObject("XR Origin (AR)");
        var origin = originGo.AddComponent<XROrigin>();

        var offsetGo = new GameObject("Camera Offset");
        offsetGo.transform.SetParent(originGo.transform, false);

        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        camGo.transform.SetParent(offsetGo.transform, false);

        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.nearClipPlane = 0.05f;
        // HDR は無効必須（B10G11R11 はアルファを持たずパススルー合成が破綻する）
        cam.allowHDR = false;

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

        var occlusion = camGo.AddComponent<AROcclusionManager>();
        occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
        occlusion.enabled = false;
        var shaderOcclusion = camGo.AddComponent<ARShaderOcclusion>();

        var gateGo = new GameObject("Scene Permission Gate");
        var gate = gateGo.AddComponent<BlockField.ScenePermissionGate>();
        gate.occlusionManager = occlusion;

        var passthrough = camGo.AddComponent<BlockField.PassthroughController>();
        passthrough.targetCamera = cam;
        passthrough.cameraManager = cameraManager;
        passthrough.cameraBackground = cameraBackground;
        passthrough.occlusionManager = occlusion;
        passthrough.shaderOcclusion = shaderOcclusion;

        origin.Camera = cam;
        origin.CameraFloorOffsetObject = offsetGo;
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;

        var planeManager = originGo.AddComponent<ARPlaneManager>();
        var anchorManager = originGo.AddComponent<ARAnchorManager>();

        // --- 空間アンカー（susuwatari-mirror のアンカー基準原点方式）---
        // 格子はこのアンカーのローカル座標で持つ。再センタリングやアンカー復元で
        // 格子が部屋からずれないようにするため
        var anchorGo = new GameObject("Anchor Origin");
        var diorama = anchorGo.AddComponent<BlockField.DioramaOrigin>();
        diorama.showMarker = false;
        diorama.planeManager = planeManager;
        diorama.anchorManager = anchorManager;
        diorama.trackingSpace = offsetGo.transform;
        diorama.originMaterial = GetOrCreateMaterial("OriginRed", new Color(0.9f, 0.1f, 0.1f), k_OcclusionShader);
        diorama.reticleMaterial = GetOrCreateMaterial("ReticleWhite", new Color(0.3f, 1f, 0.4f),
            "Universal Render Pipeline/Unlit");

        // --- シーンメッシュ取得（voxel terrain は作らない）---
        var scannerGo = new GameObject("Room Scanner");
        scannerGo.transform.SetParent(originGo.transform, false);
        var meshManager = scannerGo.AddComponent<ARMeshManager>();
        meshManager.meshPrefab = GetOrCreateReconMeshPrefab();
        var roomScanner = scannerGo.AddComponent<BlockField.RoomScanner>();
        roomScanner.meshManager = meshManager;
        roomScanner.planeManager = planeManager;
        roomScanner.origin = diorama;
        // RoomTerrainBuilder は付けない（地形を作らないのが Phase B）

        // --- 流れ場 ---
        var flowGo = new GameObject("Aquarium Flow");
        var flow = flowGo.AddComponent<BlockField.Aquarium.AquariumFlow>();
        flow.scanner = roomScanner;
        flow.origin = diorama;

        // 粒子は不透明で描く（アルファ<1 はパススルーと合成されるため使えない）。
        // 明度とスケールで速さを見せる。
        //
        // 【オクルージョン対応シェーダーを使う】2026-08-16 のセッションで
        // **家具の向こう側の粒子が手前に重なって見えた**。原因はここで
        // URP/Unlit を指定していたことで、環境深度による遮蔽が一切効いていなかった。
        // 「環境深度の取得だけでは不足。ARShaderOcclusion ＋ XR_HARD_OCCLUSION 対応
        // シェーダーが必須」は Demo 0 で確立済みの知見だったのに、
        // 新しいマテリアルで踏み外した。
        // BlockFieldOcclusionUnlit は multi_compile_instancing を持つので
        // DrawMeshInstanced とも両立する。
        var particleMat = GetOrCreateMaterial("FlowParticle", new Color(0.6f, 0.85f, 1f),
            k_OcclusionShader);
        particleMat.enableInstancing = true;
        EditorUtility.SetDirty(particleMat);

        var particleGo = new GameObject("Flow Particles (View)");
        var particles = particleGo.AddComponent<BlockField.Aquarium.FlowParticleView>();
        particles.flow = flow;
        particles.material = particleMat;
        // 格子はアンカーローカルなので、描画もアンカーの下に置く
        particles.anchorSpace = anchorGo.transform;

        // ログに粒子数を出すための参照（View には干渉しない）
        flow.particles = particles;

        // --- クラゲ1体（Phase C-7/8）---
        // 神経は jelly_1 (jelly-1.1) の ExcitableField をそのまま使う。
        // 推力は 2D リム収縮のまま（dV/dt は抗力係数の逆算後）
        var jellyGo = new GameObject("Aquarium Jellyfish");
        var jelly = jellyGo.AddComponent<BlockField.Aquarium.AquariumJellyfish>();
        jelly.flow = flow;
        flow.jelly = jelly;

        // 傘も不透明（アルファ<1 はパススルーと合成される）。
        // オクルージョン対応シェーダーを使う——粒子で踏み外した轍を踏まない
        var bellMat = GetOrCreateMaterial("JellyfishBell", new Color(0.85f, 0.9f, 1f),
            k_OcclusionShader);
        // 【両面描画】傘は底の開いた椀なので、下から見ると内側の面を見ることになる。
        // 既定の裏面カリングだと、真下や中に入った位置から傘が消える。
        // 実機で「法線が逆では」と指摘された件は巻き方向の誤りが主因だが
        // （JellyfishView.Build のコメント）、椀を描く以上ここは両面が正しい
        // GetOrCreateMaterial は既存 .mat を使い回すので、既に作られていた材質にも
        // 効かせるには明示的に書き戻す（粒子の材質で同じ罠を踏んでいる）
        bellMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        EditorUtility.SetDirty(bellMat);
        AssetDatabase.SaveAssets();
        var jellyViewGo = new GameObject("Jellyfish View");
        var jellyView = jellyViewGo.AddComponent<BlockField.Aquarium.JellyfishView>();
        jellyView.jelly = jelly;
        jellyView.material = bellMat;
        jellyView.anchorSpace = anchorGo.transform;

        // --- デバッグ表示（焼き込んだ壁が現実の壁と重なっているかを見る）---
        // 遮蔽ありは粒子と同じオクルージョン対応シェーダー。遮蔽なしは素の Unlit で、
        // 壁の向こう側にあるセルも描く。2つを切り替えると前後関係が読める
        var solidOccludedMat = GetOrCreateMaterial("DebugSolidOccluded",
            new Color(1f, 0.35f, 0.1f), k_OcclusionShader);
        var solidThroughMat = GetOrCreateMaterial("DebugSolidThrough",
            new Color(0.15f, 1f, 0.35f), "Universal Render Pipeline/Unlit");
        // DrawMeshInstanced はマテリアル側で有効になっていないと1個ずつ描かれる
        solidOccludedMat.enableInstancing = true;
        solidThroughMat.enableInstancing = true;
        EditorUtility.SetDirty(solidOccludedMat);
        EditorUtility.SetDirty(solidThroughMat);
        AssetDatabase.SaveAssets();
        var debugGo = new GameObject("Aquarium Debug View");
        var debugView = debugGo.AddComponent<BlockField.Aquarium.AquariumDebugView>();
        debugView.flow = flow;
        debugView.occludedMaterial = solidOccludedMat;
        debugView.throughMaterial = solidThroughMat;
        debugView.anchorSpace = anchorGo.transform;

        var inputGo = new GameObject("Aquarium Input");
        var input = inputGo.AddComponent<BlockField.Aquarium.AquariumInput>();
        input.flow = flow;
        input.particles = particles;
        input.jelly = jelly;
        input.debugView = debugView;

        // --- パネル（Main.unity と同じ様式。FPS を先頭行に置く）---
        var canvasGo = new GameObject("Aquarium Panel");
        canvasGo.transform.SetParent(camGo.transform, false);
        canvasGo.transform.localPosition = new Vector3(0f, -0.28f, 0.6f);
        canvasGo.transform.localScale = Vector3.one * 0.0007f;
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        // 6行ぶん（デバッグ表示の行を足した）
        canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(620f, 235f);

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
        uiText.text = "Aquarium";
        uiText.rectTransform.anchorMin = Vector2.zero;
        uiText.rectTransform.anchorMax = Vector2.one;
        uiText.rectTransform.offsetMin = new Vector2(16f, 12f);
        uiText.rectTransform.offsetMax = new Vector2(-16f, -12f);

        var panel = canvasGo.AddComponent<BlockField.Aquarium.AquariumPanel>();
        panel.flow = flow;
        panel.particles = particles;
        panel.jelly = jelly;
        panel.debugView = debugView;
        panel.text = uiText;

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[AquariumSceneBootstrap] {ScenePath} を生成した（流れ + クラゲ1体）");
    }

    static Material GetOrCreateMaterial(string name, Color color, string shaderName)
    {
        string path = $"Assets/Materials/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            return existing;
        }

        var shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[AquariumSceneBootstrap] シェーダーが見つからない: {shaderName}");
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }
        var mat = new Material(shader);
        mat.name = name;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);

        Directory.CreateDirectory("Assets/Materials");
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    /// <summary>ARMeshManager が要求するメッシュプレハブ（表示はしない）。</summary>
    static MeshFilter GetOrCreateReconMeshPrefab()
    {
        const string path = "Assets/Prefabs/ReconMesh.prefab";
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null)
        {
            return existing.GetComponent<MeshFilter>();
        }

        Directory.CreateDirectory("Assets/Prefabs");
        var go = new GameObject("ReconMesh");
        go.AddComponent<MeshFilter>();
        var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return saved.GetComponent<MeshFilter>();
    }
}
