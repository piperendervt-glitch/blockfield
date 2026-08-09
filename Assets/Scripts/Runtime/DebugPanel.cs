using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace BlockField
{
    /// <summary>
    /// HMD内デバッグパネル (Demo 0 テストUI)。
    /// カメラ前下方に固定した World Space Canvas に検証用の状態を毎秒表示する。
    /// 注: 内蔵フォント (LegacyRuntime.ttf) は日本語グリフを持たないため表示は英語。
    /// </summary>
    public sealed class DebugPanel : MonoBehaviour
    {
        const float k_RefreshInterval = 1f;

        [SerializeField] DioramaOrigin m_Diorama;
        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] ARPlaneManager m_PlaneManager;
        [SerializeField] RoomTerrainBuilder m_RoomBuilder;
        [SerializeField] RoomTerrainView m_RoomView;
        [SerializeField] Text m_Text;

        public DioramaOrigin diorama { get => m_Diorama; set => m_Diorama = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public ARPlaneManager planeManager { get => m_PlaneManager; set => m_PlaneManager = value; }
        public RoomTerrainBuilder roomBuilder { get => m_RoomBuilder; set => m_RoomBuilder = value; }
        public RoomTerrainView roomView { get => m_RoomView; set => m_RoomView = value; }
        public Text text { get => m_Text; set => m_Text = value; }

        static string s_LastEvent = "-";

        float m_SmoothedDeltaTime;
        float m_NextRefresh;

        /// <summary>各コンポーネントが直近イベントを1行で通知する。</summary>
        public static void Notify(string message)
        {
            s_LastEvent = message;
        }

        void Update()
        {
            // FPS: unscaledDeltaTime の指数移動平均
            m_SmoothedDeltaTime = Mathf.Lerp(m_SmoothedDeltaTime, Time.unscaledDeltaTime, 0.05f);

            if (Time.unscaledTime < m_NextRefresh || m_Text == null)
            {
                return;
            }
            m_NextRefresh = Time.unscaledTime + k_RefreshInterval;

            m_Text.text = BuildText();
        }

        string BuildText()
        {
            string perm;
#if UNITY_ANDROID && !UNITY_EDITOR
            perm = Permission.HasUserAuthorizedPermission(ScenePermissionGate.ScenePermission) ? "OK" : "NG";
#else
            perm = "n/a";
#endif

            int planes = 0;
            if (m_PlaneManager != null)
            {
                foreach (var _ in m_PlaneManager.trackables) planes++;
            }

            string rayHit = m_Diorama != null && m_Diorama.HasPlaneHit ? "Y" : "N";
            string origin = m_Diorama != null ? m_Diorama.State.ToString() : "-";
            bool anchorSaved = File.Exists(Path.Combine(Application.persistentDataPath, "diorama_anchor.json"));
            int blocks = m_TerrainField != null ? m_TerrainField.BlockCount : 0;
            uint seed = m_TerrainField != null ? m_TerrainField.CurrentSeed : 0;
            long genMs = m_TerrainField != null ? m_TerrainField.GenerationMs : 0;
            float fps = m_SmoothedDeltaTime > 0.0001f ? 1f / m_SmoothedDeltaTime : 0f;

            string field = m_TerrainField != null && m_TerrainField.FieldVisible ? "ON" : "OFF";

            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            long tick = world?.TickCount ?? 0;
            int plants = world?.PlantCount ?? 0;
            int animals = world?.AnimalCount ?? 0;
            int wolves = world?.WolfCount ?? 0;
            int starved = world?.StarvationCount ?? 0;
            int predated = world?.PredationCount ?? 0;
            int births = world?.BirthCount ?? 0;

            return
                $"USE_SCENE: {perm}   Planes: {planes}   RayHit: {rayHit}\n" +
                $"Origin: {origin}   AnchorSaved: {(anchorSaved ? "Y" : "N")}\n" +
                $"Blocks: {blocks}   Field: {field}   FPS: {fps:F1}\n" +
                $"Seed: {seed}   Gen: {genMs}ms\n" +
                BuildRoomText() +
                $"Tick: {tick}  Plants: {plants}  Animals: {animals}  Wolves: {wolves}\n" +
                $"Starve: {starved}  Pred: {predated}  Birth: {births}\n" +
                $"Last: {s_LastEvent}";
        }

        /// <summary>部屋地形 (Demo 4.5 G3) の常時表示項目。実機で状況を判断できるようにする。</summary>
        string BuildRoomText()
        {
            string mode = m_RoomView != null && m_RoomView.IsComposed
                ? (m_RoomView.Mode == RoomTerrainView.ViewMode.Normal ? "0 NORMAL" : "1 DIAG")
                : "-";

            int surfaces = m_RoomBuilder != null ? m_RoomBuilder.TotalHits : 0;
            int cells = m_RoomBuilder != null ? m_RoomBuilder.CellsWithHits : 0;
            float avg = cells > 0 ? (float)surfaces / cells : 0f;

            var stats = m_RoomBuilder != null ? m_RoomBuilder.Stats : null;
            string dist = stats != null
                ? $"{stats.CellsWith1}/{stats.CellsWith2}/{stats.CellsWith3Plus}"
                : "-/-/-";

            var composed = m_TerrainField != null ? m_TerrainField.RoomComposed : null;
            int snowBlocks = composed?.BlockCount ?? 0;
            long composeMs = m_TerrainField != null ? m_TerrainField.GenerationMs : 0;

            var biome = composed?.BiomeHistogram;
            string biomeText = biome != null && biome.Length >= 3
                ? $"{biome[0]}/{biome[1]}/{biome[2]}"
                : "-/-/-";
            int walls = composed != null ? composed.WallCellCount : 0;

            return
                $"Room[B]: {mode}   Surf: {surfaces} (avg {avg:F2})\n" +
                $"Dist1/2/3+: {dist}   Snow: {snowBlocks} ({composeMs}ms)\n" +
                $"Biome P/H/M: {biomeText}   Wall: {walls}\n";
        }
    }
}
