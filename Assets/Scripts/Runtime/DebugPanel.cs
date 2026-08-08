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
        [SerializeField] Text m_Text;

        public DioramaOrigin diorama { get => m_Diorama; set => m_Diorama = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public ARPlaneManager planeManager { get => m_PlaneManager; set => m_PlaneManager = value; }
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

            return
                $"USE_SCENE: {perm}   Planes: {planes}   RayHit: {rayHit}\n" +
                $"Origin: {origin}   AnchorSaved: {(anchorSaved ? "Y" : "N")}\n" +
                $"Blocks: {blocks}   Field: {field}   FPS: {fps:F1}\n" +
                $"Seed: {seed}   Gen: {genMs}ms\n" +
                $"Last: {s_LastEvent}";
        }
    }
}
