using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 見守り（L0）シーン (Assets/Scenes/Watch.unity) をコードで生成する。
///
/// 【水槽シーンから作る】AR セッション・アンカー・シーンメッシュ取得・部屋の焼き込みは
/// 系列2 が持っている**1本の経路**をそのまま使う。同じ組み立てを2か所に書けば
/// 2回間違えられる（横断原則「書ける場所を1つに絞る」）。
/// アクアリウム固有のもの（クラゲ・粒子・水槽パネル・水槽入力・デバッグ表示）だけ外し、
/// L0 の層を足す。
///
/// **`AquariumFlow` は残す。** 部屋の焼き込み（シーンメッシュ → 格子 → 距離場）が
/// そこにあり、L0 の「走査済み領域」はその距離場から決まるためである。
/// 系列2 は停止したが、部屋を測る部分は系列3 の土台として生きている。
///
/// 【層は1シーンに並べる】分割の基準は「**20Hz の固定ティックを維持できなくなったら**」。
/// 体感ではなく <c>WatchPanel</c> の遅延と落しティック数で判断する。
/// </summary>
public static class WatchSceneBootstrap
{
    public const string ScenePath = "Assets/Scenes/Watch.unity";

    [MenuItem("Tools/Project Setup/Create Watch Scene")]
    public static void CreateWatchScene()
    {
        if (File.Exists(ScenePath))
        {
            Debug.Log($"[WatchSceneBootstrap] {ScenePath} は既に存在するためスキップ。" +
                "作り直すときは先に削除すること。");
            return;
        }

        // 土台は水槽シーン。無ければ作らせる
        AquariumSceneBootstrap.CreateAquariumScene();
        var scene = EditorSceneManager.OpenScene(AquariumSceneBootstrap.ScenePath,
            OpenSceneMode.Single);

        // --- アクアリウム固有のものを外す（部屋を測る部分は残す）---
        foreach (string name in new[]
        {
            "Aquarium Jellyfish", "Jellyfish View", "Flow Particles (View)",
            "Aquarium Debug View", "Aquarium Input", "Aquarium Panel",
            "Anchor Space Renderer",
        })
        {
            var go = GameObject.Find(name);
            if (go != null) Object.DestroyImmediate(go);
        }

        var diorama = Object.FindFirstObjectByType<BlockField.DioramaOrigin>();
        var flow = Object.FindFirstObjectByType<BlockField.Aquarium.AquariumFlow>();
        if (diorama == null || flow == null)
        {
            Debug.LogError("[WatchSceneBootstrap] アンカーか流れ場が見つからない。中止する。");
            return;
        }

        // --- 描画とヘッドポーズの唯一の入口 ---
        // 【diorama.OriginTransform を使う側であること】アンカーを載せた
        // GameObject（原点に作られ一度も動かない箱）を渡してはいけない。
        // 系列2 で実際にこの取り違えをして、水槽が床に埋まった
        var spaceGo = new GameObject("Watch Space Renderer");
        var space = spaceGo.AddComponent<BlockField.Watch.WatchSpaceRenderer>();
        space.origin = diorama;

        // --- L0 プロデューサ（現段階では頭位置1つだけ）---
        var headGo = new GameObject("Head Pose Producer");
        var headProducer = headGo.AddComponent<BlockField.Watch.HeadPoseProducer>();
        headProducer.space = space;

        // --- L0 の場（20Hz 固定ティック）---
        var fieldGo = new GameObject("Watch Field (L0)");
        var watchField = fieldGo.AddComponent<BlockField.Watch.WatchField>();
        watchField.room = flow;
        watchField.head = headProducer;
        // 床の境界ポリゴンの出どころ。近似をやめて Scene の面を直接使う
        watchField.planes = Object.FindFirstObjectByType<UnityEngine.XR.ARFoundation.ARPlaneManager>();

        // --- 表示 ---
        var viewGo = new GameObject("Watch View");
        var view = viewGo.AddComponent<BlockField.Watch.WatchView>();
        view.field = watchField;
        view.space = space;
        // 【半透明を使わない】MR ではアルファ<1 がパススルーと合成される。
        // 区別は明度で付ける（居場所=明るい / カバレッジ=中 / 欠測=暗い）
        view.headMaterial = Mat("WatchHead", new Color(1f, 0.95f, 0.4f));
        view.coveredMaterial = Mat("WatchCovered", new Color(0.25f, 0.65f, 0.9f));
        view.missingMaterial = Mat("WatchMissing", new Color(0.18f, 0.18f, 0.2f));
        watchField.view = view;

        // --- 入力（左手グリップで表示段を切り替える）---
        var inputGo = new GameObject("Watch Input");
        var input = inputGo.AddComponent<BlockField.Watch.WatchInput>();
        input.view = view;
        input.field = watchField;

        // --- パネル ---
        var canvasGo = new GameObject("Watch Panel");
        canvasGo.transform.position = new Vector3(0f, 1.2f, 1.4f);
        canvasGo.transform.localScale = Vector3.one * 0.0007f;
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(620f, 240f);

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
        uiText.text = "Watch (L0)";
        uiText.rectTransform.anchorMin = Vector2.zero;
        uiText.rectTransform.anchorMax = Vector2.one;
        uiText.rectTransform.offsetMin = new Vector2(16f, 12f);
        uiText.rectTransform.offsetMax = new Vector2(-16f, -12f);

        var panel = canvasGo.AddComponent<BlockField.Watch.WatchPanel>();
        panel.field = watchField;
        panel.head = headProducer;
        panel.space = space;
        panel.view = view;
        panel.text = uiText;

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[WatchSceneBootstrap] {ScenePath} を生成した（L0: 頭位置のみ）");
    }

    static Material Mat(string name, Color color)
    {
        string path = $"Assets/Materials/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(shader) { name = name };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        mat.enableInstancing = true;

        Directory.CreateDirectory("Assets/Materials");
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        return mat;
    }
}
