using System.Collections.Generic;
using System.Diagnostics;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace BlockField
{
    /// <summary>
    /// 雪積もり地形の実機表示と診断可視化 (Demo 4.5 G3)。
    ///
    /// RoomTerrainBuilder が観測を作り終えたら SnowfallComposer で地形を合成し、
    /// ChunkMesher でメッシュ化して**ワールド座標に直接**置く
    /// （XROrigin は Floor モードで原点＝床なので座標変換は不要。RoomScanner の実測で確認済み）。
    ///
    /// Bボタンで表示モードを切り替える:
    ///   モード0 通常  — 積もった地形のみ
    ///   モード1 診断  — 検出した積もり面を色分けマーカーで表示（地形は隠す）
    ///
    /// 診断モードは実機で「どこを積もり面と判定したか」をユーザーが直接目視するためのもので、
    /// ログでの計測追い込みの代わりになる（緑=採用した最上面 / 青=2面目以降 / 枠色=ラベル）。
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
        [SerializeField] DioramaOrigin m_Origin;
        [SerializeField] Material m_Material;

        public RoomTerrainBuilder builder { get => m_Builder; set => m_Builder = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }

        /// <summary>スパシャルアンカー原点。部屋地形はこの配下に置いて再装着後のずれを防ぐ。</summary>
        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }

        /// <summary>頂点色対応 BlockField/OcclusionUnlit (_VERTEX_COLOR 有効) の共有マテリアル。</summary>
        public Material material { get => m_Material; set => m_Material = value; }

        /// <summary>現在の表示モード。</summary>
        public ViewMode Mode { get; private set; } = ViewMode.Normal;

        /// <summary>合成済みか（パネル表示用）。</summary>
        public bool IsComposed => m_Result != null;

        /// <summary>積もらせた面数（= 面を持つセル数）。</summary>
        public int SurfaceCount => m_Result?.SurfaceCount ?? 0;

        /// <summary>積もったブロック数。</summary>
        public int SnowBlockCount => m_Result?.BlockCount ?? 0;

        /// <summary>合成＋メッシュ化にかかった時間 (ms)。</summary>
        public long ComposeMs { get; private set; }

        InputAction m_ModeAction;
        SnowfallResult m_Result;
        GameObject m_Root;
        GameObject m_MarkerObject;
        Mesh m_MarkerMesh;
        bool m_ModeRequested;
        float m_LastModeTime = float.NegativeInfinity;
        readonly List<GameObject> m_Chunks = new();
        readonly List<Mesh> m_Meshes = new();

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
            if (m_Result == null)
            {
                var observation = m_Builder != null ? m_Builder.Observation : null;

                // アンカー原点が確定するまで待つ。ワールドに直置きすると HMD の着脱による
                // 再ローカライズで部屋地形だけがずれる（2026-08-09 の実機セッションで発生）
                if (observation != null && m_Origin != null && m_Origin.OriginTransform != null)
                {
                    Compose(observation);
                }
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

        void Compose(RoomObservation observation)
        {
            var stopwatch = Stopwatch.StartNew();

            var p = SnowfallParams.Default;
            m_Result = SnowfallComposer.Compose(observation, p);

            float cellSize = observation.CellSize;

            // 観測セル (x,z) のレイはセル中心 min + (x+0.5)*cell を通る。
            // ChunkMesher はセル (x,y,z) の中心を (x*cell, (y+0.5)*cell, z*cell) に置くため、
            // ルートを半セル分ずらすと観測時のワールド座標に一致する。
            // Y は cellY*cell が観測時のワールド高さ。
            var scanWorldPose = new Pose(
                new Vector3(
                    observation.OriginWorldX + cellSize * 0.5f,
                    0f,
                    observation.OriginWorldZ + cellSize * 0.5f),
                Quaternion.identity);

            m_Root = new GameObject("Room Terrain");
            AttachToAnchor(m_Root.transform, scanWorldPose);

            int chunkCount = 0;
            foreach (var pair in m_Result.Grid.Chunks)
            {
                var mesh = ChunkMesher.BuildChunkMesh(m_Result.Grid, pair.Key, pair.Value, cellSize);
                if (mesh == null)
                {
                    continue;
                }

                m_Meshes.Add(mesh);
                var go = new GameObject($"RoomChunk {pair.Key}");
                go.transform.SetParent(m_Root.transform, false);
                go.transform.localPosition = new Vector3(
                    pair.Key.x * Chunk.Size * cellSize,
                    pair.Key.y * Chunk.Size * cellSize,
                    pair.Key.z * Chunk.Size * cellSize);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = m_Material;
                m_Chunks.Add(go);
                chunkCount++;
            }

            // 診断マーカー（全面ぶん1メッシュ）
            m_MarkerMesh = SurfaceMarkerMesher.Build(observation, cellSize);
            if (m_MarkerMesh != null)
            {
                m_MarkerObject = new GameObject("Surface Markers");
                m_MarkerObject.transform.SetParent(m_Root.transform, false);
                m_MarkerObject.transform.localPosition = Vector3.zero;
                m_MarkerObject.AddComponent<MeshFilter>().sharedMesh = m_MarkerMesh;
                m_MarkerObject.AddComponent<MeshRenderer>().sharedMaterial = m_Material;
            }

            stopwatch.Stop();
            ComposeMs = stopwatch.ElapsedMilliseconds;

            SetMode(ViewMode.Normal);

            Debug.Log($"[RoomTerrain] 雪積もり合成: {ComposeMs}ms seed={p.seed} " +
                $"積もり面={m_Result.SurfaceCount} ブロック={m_Result.BlockCount} " +
                $"チャンク={chunkCount} 層数[{m_Result.HistogramText()}] " +
                $"cellY範囲={m_Result.MinCellY}..{m_Result.MaxCellY} " +
                $"ハッシュ={m_Result.Grid.ComputeContentHash():X16}");
            Debug.Log($"[RoomTerrain] 表示ルート: 親={m_Root.transform.parent?.name ?? "(ワールド)"} " +
                $"local={m_Root.transform.localPosition:F3} world={m_Root.transform.position:F3} " +
                $"cell={cellSize}m マーカー頂点={(m_MarkerMesh != null ? m_MarkerMesh.vertexCount : 0)}。" +
                "Bボタンで 通常/診断 を切り替える。");
        }

        /// <summary>
        /// 観測時のワールドポーズを保ったまま、アンカー原点の配下へ取り付ける。
        ///
        /// 【なぜアンカー配下か】ワールド座標は HMD の着脱による再ローカライズでずれるが、
        /// スパシャルアンカーは現実の部屋に貼り付いたままである（Demo 0 T2）。
        /// アンカーの子にしておけば、再ローカライズでアンカーが動いても部屋地形は一緒に動き、
        /// 現実の床・机との位置関係が保たれる。
        ///
        /// 【なぜ観測時のポーズを使うか】アンカーの現在ポーズで換算すると、スキャンから
        /// 合成までの間に再ローカライズが起きた場合にその分だけずれる。
        /// RoomScanner が観測と同じ瞬間に記録したポーズで換算する。
        ///
        /// 【M4 との関係】ここで扱うのは**表示配置だけ**である。観測データ (cellY) は
        /// 整数のまま変換していないので、リプレイ入力とコンテンツハッシュには影響しない。
        /// 部屋の向きに沿ったグリッド軸（観測時のワールド軸）もそのまま保たれる。
        /// </summary>
        void AttachToAnchor(Transform target, Pose scanWorldPose)
        {
            var originTransform = m_Origin != null ? m_Origin.OriginTransform : null;
            if (originTransform == null)
            {
                target.SetPositionAndRotation(scanWorldPose.position, scanWorldPose.rotation);
                Debug.LogWarning("[RoomTerrain] アンカー原点が無いためワールド直置きにした。HMD 着脱で位置がずれる。");
                return;
            }

            var scan = m_Builder != null && m_Builder.scanner != null ? m_Builder.scanner.Result : null;
            Pose anchorPose;
            if (scan != null && scan.HasOriginPose)
            {
                anchorPose = scan.OriginPoseAtScan;
            }
            else
            {
                anchorPose = new Pose(originTransform.position, originTransform.rotation);
                Debug.LogWarning("[RoomTerrain] 観測時のアンカーポーズが無い（原点確定前にスキャンした）。" +
                    "現在のポーズで換算するため、その間に再ローカライズがあるとずれる。");
            }

            var inverseRotation = Quaternion.Inverse(anchorPose.rotation);
            target.SetParent(originTransform, false);
            target.localPosition = inverseRotation * (scanWorldPose.position - anchorPose.position);
            target.localRotation = inverseRotation * scanWorldPose.rotation;
        }

        void SetMode(ViewMode mode)
        {
            Mode = mode;

            bool showTerrain = mode == ViewMode.Normal;
            foreach (var chunk in m_Chunks)
            {
                chunk.SetActive(showTerrain);
            }
            if (m_MarkerObject != null)
            {
                m_MarkerObject.SetActive(mode == ViewMode.Diagnostic);
            }

            Debug.Log($"[RoomTerrain] 表示モード: {(mode == ViewMode.Normal ? "0 通常" : "1 診断")}");
            DebugPanel.Notify($"room mode {(int)mode} {(mode == ViewMode.Normal ? "NORMAL" : "DIAG")}");
        }
    }
}
