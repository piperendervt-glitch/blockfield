using BlockField.SimCore.Terrain;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockField
{
    /// <summary>
    /// 部屋地形の表示モード切替と診断可視化 (Demo 4.5 G3)。
    ///
    /// 地形そのものの合成と描画は TerrainField の責務（G7 で World と統合した）。
    /// 本コンポーネントは「何を積もり面と判定したか」を実機で目視するための
    /// マーカーと、Bボタンによるモード切替だけを持つ。
    ///
    ///   モード0 通常  — 積もった地形とエンティティ
    ///   モード1 診断  — 積もり面の色分けマーカー（地形とエンティティは隠す）
    /// </summary>
    public sealed class RoomTerrainView : MonoBehaviour
    {
        /// <summary>モード切替の連打防止（秒）。</summary>
        const float k_ToggleCooldown = 0.5f;

        public enum ViewMode
        {
            /// <summary>通常表示（積もった地形のみ）。</summary>
            Normal = 0,

            /// <summary>診断表示（積もり面の色分けマーカー）。</summary>
            Diagnostic = 1,
        }

        [SerializeField] RoomTerrainBuilder m_Builder;
        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] Material m_Material;

        public RoomTerrainBuilder builder { get => m_Builder; set => m_Builder = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }

        /// <summary>頂点色対応 BlockField/OcclusionUnlit (_VERTEX_COLOR 有効) の共有マテリアル。</summary>
        public Material material { get => m_Material; set => m_Material = value; }

        /// <summary>現在の表示モード。</summary>
        public ViewMode Mode { get; private set; } = ViewMode.Normal;

        /// <summary>マーカーを作り終えたか（パネル表示用）。</summary>
        public bool IsComposed => m_MarkerObject != null;

        /// <summary>積もり面マーカーの表示を切り替える（FieldOverlayView が巡回に合わせて呼ぶ）。</summary>
        public void SetMarkersVisible(bool visible)
        {
            if (m_MarkerObject != null && m_MarkerObject.activeSelf != visible)
            {
                m_MarkerObject.SetActive(visible);
            }
        }

        InputAction m_ModeAction;
        GameObject m_MarkerObject;
        Mesh m_MarkerMesh;
        Transform m_TrackedParent;
        bool m_ModeRequested;
        float m_LastModeTime = float.NegativeInfinity;

        void Awake()
        {
            m_ModeAction = new InputAction("RoomTerrainMode", InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");
            m_ModeAction.performed += OnModePerformed;
        }

        void OnDestroy()
        {
            m_ModeAction.performed -= OnModePerformed;
            m_ModeAction.Dispose();
        }

        void OnEnable() => m_ModeAction.Enable();
        void OnDisable() => m_ModeAction.Disable();

        void OnModePerformed(InputAction.CallbackContext _) => m_ModeRequested = true;

        void Update()
        {
            // 地形ルートができてから作る（マーカーは地形と同じ親＝アンカー相対に置く）。
            // シード巡回で地形が作り直されるとルートごと破棄されるため、親の入れ替わりを
            // 検知して作り直す（初版は1回だけ作って終わりで、シード巡回後にマーカーが消えていた）
            var observation = m_Builder != null ? m_Builder.Observation : null;
            var root = m_TerrainField != null ? m_TerrainField.TerrainRoot : null;
            if (observation == null || root == null)
            {
                return;
            }
            if (m_MarkerObject == null || m_TrackedParent != root)
            {
                BuildMarkers(observation, root);
                return;
            }

            bool requested = m_ModeRequested;
            m_ModeRequested = false;
            if (requested && Time.unscaledTime - m_LastModeTime >= k_ToggleCooldown)
            {
                m_LastModeTime = Time.unscaledTime;
                SetMode(Mode == ViewMode.Normal ? ViewMode.Diagnostic : ViewMode.Normal);
            }
        }

        void BuildMarkers(RoomObservation observation, Transform root)
        {
            // 古いマーカーは必ず捨てる。以前はメッシュだけ捨てて GameObject を残していたため、
            // 作り直しのたびに古い緑の板が積み残る可能性があった
            // （実機で「非表示にしても緑の枠が残る」と報告された原因の一つ）
            if (m_MarkerObject != null)
            {
                Destroy(m_MarkerObject);
                m_MarkerObject = null;
            }
            if (m_MarkerMesh != null)
            {
                Destroy(m_MarkerMesh);
                m_MarkerMesh = null;
            }
            m_TrackedParent = root;

            m_MarkerMesh = SurfaceMarkerMesher.Build(observation, observation.CellSize);
            if (m_MarkerMesh == null)
            {
                Debug.LogWarning("[RoomTerrain] 積もり面が無くマーカーを作れなかった。");
                return;
            }

            m_MarkerObject = new GameObject("Surface Markers");
            m_MarkerObject.transform.SetParent(root, false);
            m_MarkerObject.transform.localPosition = Vector3.zero;
            m_MarkerObject.AddComponent<MeshFilter>().sharedMesh = m_MarkerMesh;
            m_MarkerObject.AddComponent<MeshRenderer>().sharedMaterial = m_Material;

            SetMode(ViewMode.Normal);

            Debug.Log($"[RoomTerrain] 診断マーカー: 頂点={m_MarkerMesh.vertexCount} " +
                $"親={root.name} local={root.localPosition:F3} world={root.position:F3}。" +
                "Bボタンで 通常/診断 を切り替える。");
        }

        void SetMode(ViewMode mode)
        {
            Mode = mode;

            // 地形は診断では隠す。エンティティは**両モードで見せる** —
            // 診断モードの飢餓色分け (Demo 5a) は動物が見えていないと意味がないため
            if (m_TerrainField != null)
            {
                m_TerrainField.SetFieldVisible(mode == ViewMode.Normal);
                m_TerrainField.SetEntitiesVisible(true);
            }
            // マーカーの表示は FieldOverlayView が巡回状態に応じて制御する
            // （場と同時に出ると緑どうしが混ざって読めないため）。ここでは通常モードで消すだけ
            if (m_MarkerObject != null && mode == ViewMode.Normal)
            {
                m_MarkerObject.SetActive(false);
            }

            Debug.Log($"[RoomTerrain] 表示モード: {(mode == ViewMode.Normal ? "0 通常" : "1 診断")}");
            DebugPanel.Notify($"room mode {(int)mode} {(mode == ViewMode.Normal ? "NORMAL" : "DIAG")}");
        }
    }
}
