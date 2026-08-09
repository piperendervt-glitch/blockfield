using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BlockField.SimCore.Terrain;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems; // PlaneClassifications
using Debug = UnityEngine.Debug;

namespace BlockField
{
    /// <summary>
    /// 部屋のシーンモデル取得 (Demo 4.5 G1)。MeshRecon（Demo 3 E6 の偵察コード）の後継で、
    /// 恒久機能として ARMeshManager / ARPlaneManager からシーンモデルを取得する。
    ///
    /// 動作:
    /// 1. メッシュ頂点数の変化が k_StableSeconds 続けて無ければ「安定」とみなす
    /// 2. 安定後に1回だけ実行して停止（以降は ARMeshManager も止めて性能影響を残さない）
    ///    a. メッシュを CPU 読み出し（頂点・三角形をワールド座標へ）
    ///    b. 平面一覧（中心・範囲・classification）を取得
    ///    c. 生メッシュをアーカイブ（下記の注意を参照）
    /// 結果は <see cref="Result"/> に置く。ハイトマップ化 (G2) は RoomTerrainBuilder の責務。
    ///
    /// 【アーカイブと M4 の関係 — 重要】
    /// c. で保存する room_mesh_archive_*.bin は**反復用資材であり M4 の保証対象外**である。
    /// M4 が保証するのは「同一の RoomObservation から地形合成以降が同一 ContentHash を
    /// 生む」ことであり、アーカイブからリプレイしてもレイキャスト（浮動小数点の幾何演算）が
    /// 再実行されるため bit-exact は保証されない。アーカイブは
    /// 「G2 のパラメータを変えて再導出したいときに HMD 再装着を省くための資材」である。
    /// リプレイ入力は EventLog に載る RoomObservation（整数）のみ。
    /// </summary>
    public sealed class RoomScanner : MonoBehaviour
    {
        /// <summary>メッシュ頂点数が変化しないままこの秒数が経過したら安定とみなす。</summary>
        const float k_StableSeconds = 5f;

        /// <summary>安定判定のポーリング間隔（秒）。</summary>
        const float k_PollInterval = 1f;

        /// <summary>観測グリッドのセルサイズ（m）。地形のブロックサイズと合わせる。</summary>
        const float k_CellSize = 0.04f;

        /// <summary>観測グリッドの最大辺セル数（部屋 4.5m / 4cm ≒ 113 に対する安全上限）。</summary>
        const int k_MaxGridSide = 256;

        [SerializeField] ARMeshManager m_MeshManager;
        [SerializeField] ARPlaneManager m_PlaneManager;
        [SerializeField] DioramaOrigin m_Origin;

        public ARMeshManager meshManager { get => m_MeshManager; set => m_MeshManager = value; }
        public ARPlaneManager planeManager { get => m_PlaneManager; set => m_PlaneManager = value; }

        /// <summary>スパシャルアンカー原点 (Demo 0 T2)。観測時のポーズを記録するために参照する。</summary>
        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }

        /// <summary>スキャン結果（ワールド座標のメッシュと平面ラベル）。未完了なら null。</summary>
        public ScanResult Result { get; private set; }

        /// <summary>スキャンが完了したか。</summary>
        public bool IsComplete => Result != null;

        /// <summary>
        /// スキャン結果。ハイトマップ化 (G2) は RoomTerrainBuilder の責務なので、
        /// ここではワールド座標のメッシュとラベル解決関数までを提供する。
        /// </summary>
        public sealed class ScanResult
        {
            public float[] Vertices;      // ワールド座標 (x,y,z の3要素ずつ)
            public int[] Triangles;
            public Bounds Bounds;         // ワールド空間バウンズ
            public int PlaneCount;
            public System.Func<float, float, float, SurfaceLabel> LabelResolver;

            /// <summary>
            /// **観測時点**のアンカー原点のワールドポーズ。観測結果をアンカー相対に固定するために使う。
            ///
            /// 【なぜ必要か】ワールド座標は HMD の着脱による再ローカライズでずれるが、
            /// スパシャルアンカーは現実の部屋に貼り付いたままである（Demo 0 T2 で確立）。
            /// 観測はワールド座標で行うため、そのままワールドに置くと再装着後にずれる
            /// （2026-08-09 の実機セッションで発生）。
            ///
            /// 【なぜ観測時点か】合成時に読むとスキャン〜合成の間の再ローカライズを
            /// 取りこぼす。観測と同じ瞬間のポーズで固定する。
            /// </summary>
            public Pose OriginPoseAtScan;

            /// <summary>アンカー原点が取れていたか（取れていなければワールド直置きにフォールバック）。</summary>
            public bool HasOriginPose;
        }

        /// <summary>この秒数を過ぎてもメッシュが0なら警告を1回出す。</summary>
        const float k_NoMeshWarnSeconds = 60f;

        float m_Elapsed;
        float m_NextPollTime = k_PollInterval;
        float m_StableSince = -1f;
        int m_LastVertexCount = -1;
        bool m_LoggedSessionState;
        bool m_WarnedNoMesh;

        void Update()
        {
            if (IsComplete)
            {
                return;
            }

            m_Elapsed += Time.deltaTime;
            if (m_Elapsed < m_NextPollTime)
            {
                return;
            }
            m_NextPollTime += k_PollInterval;

            // XROrigin / ARSession の状態は1回だけ出す（切り分け用）
            if (!m_LoggedSessionState)
            {
                m_LoggedSessionState = true;
                LogSessionState();
            }

            int meshCount = CountMeshes();
            int vertexCount = CountVertices();
            var (planeCount, labelBreakdown) = SummarizePlanes();

            // 無条件の生存ログ。メッシュ0でも必ず出す
            // （出ないこと自体が「コンポーネントが動いていない」ことの証拠になる）
            Debug.Log($"[RoomScanner] 待機中: meshes={meshCount} verts={vertexCount} planes={planeCount} " +
                $"elapsed={m_Elapsed:F0}s meshMgr(enabled={IsMeshManagerEnabled()}, running={IsMeshSubsystemRunning()}) " +
                $"labels=[{labelBreakdown}]");

            if (vertexCount == 0)
            {
                // 平面が取れていてメッシュだけ0なら ARMeshManager 固有の問題と切り分けられる
                if (!m_WarnedNoMesh && m_Elapsed >= k_NoMeshWarnSeconds)
                {
                    m_WarnedNoMesh = true;
                    Debug.LogWarning($"[RoomScanner] {k_NoMeshWarnSeconds:F0}秒経過してもメッシュが0件。" +
                        $"planes={planeCount} なので、平面が取れていればメッシュ供給側 (ARMeshManager / " +
                        $"Meta Quest: Meshing / Space Setup の room mesh) の問題として切り分けられる。");
                }
                return;
            }

            if (vertexCount != m_LastVertexCount)
            {
                m_LastVertexCount = vertexCount;
                m_StableSince = m_Elapsed;
                Debug.Log($"[RoomScanner] メッシュ更新中: 頂点数={vertexCount}");
                return;
            }

            if (m_Elapsed - m_StableSince < k_StableSeconds)
            {
                return; // まだ安定待ち
            }

            Debug.Log($"[RoomScanner] メッシュが安定 (頂点数={vertexCount}, {k_StableSeconds}秒変化なし)。スキャンを実行する。");
            RunScan();
        }

        void LogSessionState()
        {
            var origin = GetComponentInParent<Unity.XR.CoreUtils.XROrigin>();
            string originInfo = origin != null
                ? $"mode={origin.CurrentTrackingOriginMode} pos={origin.transform.position:F2}"
                : "なし";
            var session = FindAnyObjectByType<ARSession>();
            string sessionInfo = session != null
                ? $"enabled={session.enabled} state={ARSession.state}"
                : "なし";
            Debug.Log($"[RoomScanner] 起動状態: XROrigin({originInfo}) ARSession({sessionInfo})");
        }

        bool IsMeshManagerEnabled() => m_MeshManager != null && m_MeshManager.enabled;

        /// <summary>メッシュ供給サブシステムが running か（ARMeshManager 固有問題の切り分け用）。</summary>
        bool IsMeshSubsystemRunning()
        {
            var subsystem = m_MeshManager != null ? m_MeshManager.subsystem : null;
            return subsystem != null && subsystem.running;
        }

        int CountMeshes()
        {
            var meshes = m_MeshManager != null ? m_MeshManager.meshes : null;
            return meshes?.Count ?? 0;
        }

        /// <summary>平面数と classification 内訳（メッシュ以外の経路も同時に見るため）。</summary>
        (int count, string breakdown) SummarizePlanes()
        {
            if (m_PlaneManager == null)
            {
                return (0, "planeMgr=null");
            }

            var counts = new Dictionary<SurfaceLabel, int>();
            int total = 0;
            foreach (var plane in m_PlaneManager.trackables)
            {
                total++;
                var label = MapLabel(plane.classifications);
                counts.TryGetValue(label, out int n);
                counts[label] = n + 1;
            }

            if (total == 0)
            {
                return (0, "なし");
            }

            var parts = new List<string>();
            foreach (var kv in counts)
            {
                parts.Add($"{kv.Key}:{kv.Value}");
            }
            parts.Sort(System.StringComparer.Ordinal); // 決定論的な並び
            return (total, string.Join(" ", parts));
        }

        int CountVertices()
        {
            var meshes = m_MeshManager != null ? m_MeshManager.meshes : null;
            if (meshes == null)
            {
                return 0;
            }
            int total = 0;
            foreach (var mf in meshes)
            {
                if (mf != null && mf.sharedMesh != null)
                {
                    total += mf.sharedMesh.vertexCount;
                }
            }
            return total;
        }

        void RunScan()
        {
            var stopwatch = Stopwatch.StartNew();

            // a. メッシュを CPU 読み出し（ワールド座標へ変換して結合）
            if (!TryReadMeshes(out var verts, out var tris, out var bounds))
            {
                Debug.LogWarning("[RoomScanner] メッシュの読み出しに失敗。次のポーリングで再試行する。");
                return;
            }

            // b. 平面一覧（ラベル解決に使う）
            var planes = ReadPlanes();

            // c. 生メッシュのアーカイブ（M4 の保証対象外 — クラスコメント参照）
            ArchiveRawMesh(verts, tris);

            // d. 観測時点のアンカーポーズを記録（再装着後の位置ずれ対策）
            var originTransform = m_Origin != null ? m_Origin.OriginTransform : null;
            bool hasOrigin = originTransform != null;
            var originPose = hasOrigin
                ? new Pose(originTransform.position, originTransform.rotation)
                : Pose.identity;

            stopwatch.Stop();

            Result = new ScanResult
            {
                Vertices = verts,
                Triangles = tris,
                Bounds = bounds,
                PlaneCount = planes.Count,
                LabelResolver = (wx, wy, wz) => ResolveLabel(planes, wx, wy, wz),
                OriginPoseAtScan = originPose,
                HasOriginPose = hasOrigin,
            };

            Debug.Log($"[RoomScanner] スキャン完了: {stopwatch.ElapsedMilliseconds}ms " +
                $"頂点数={verts.Length / 3} 三角形数={tris.Length / 3} 平面数={planes.Count} " +
                $"bounds(center={bounds.center:F2}, size={bounds.size:F2})");

            if (hasOrigin)
            {
                Debug.Log($"[RoomScanner] 観測時のアンカーポーズを記録: pos={originPose.position:F3} " +
                    $"rot={originPose.rotation.eulerAngles:F1}。部屋地形はこのポーズ基準で固定する。");
            }
            else
            {
                Debug.LogWarning("[RoomScanner] アンカー原点が未確定のままスキャンした。" +
                    "部屋地形はワールド直置きになり、HMD 着脱で位置がずれる。");
            }

            // 恒久機能だが、スキャンは1回で足りるのでメッシュ供給は止める（性能影響を残さない）
            if (m_MeshManager != null)
            {
                m_MeshManager.enabled = false;
            }
        }

        /// <summary>観測グリッドのセルサイズ (m)。RoomTerrainBuilder が参照する。</summary>
        public static float CellSize => k_CellSize;

        /// <summary>観測グリッドの最大辺セル数。RoomTerrainBuilder が参照する。</summary>
        public static int MaxGridSide => k_MaxGridSide;

        /// <summary>全メッシュをワールド座標へ変換して1つの配列へ結合する。</summary>
        bool TryReadMeshes(out float[] verts, out int[] tris, out Bounds bounds)
        {
            verts = null;
            tris = null;
            bounds = default;

            var meshes = m_MeshManager != null ? m_MeshManager.meshes : null;
            if (meshes == null || meshes.Count == 0)
            {
                return false;
            }

            var vertList = new List<float>();
            var triList = new List<int>();
            bool hasBounds = false;

            foreach (var mf in meshes)
            {
                if (mf == null || mf.sharedMesh == null)
                {
                    continue;
                }
                var mesh = mf.sharedMesh;
                if (!mesh.isReadable)
                {
                    Debug.LogWarning($"[RoomScanner] メッシュ '{mesh.name}' が CPU 読み出し不可（isReadable=false）。スキップする。");
                    continue;
                }

                int baseIndex = vertList.Count / 3;
                var localVerts = mesh.vertices;
                var transform = mf.transform;
                foreach (var lv in localVerts)
                {
                    var wv = transform.TransformPoint(lv);
                    vertList.Add(wv.x);
                    vertList.Add(wv.y);
                    vertList.Add(wv.z);

                    if (!hasBounds)
                    {
                        bounds = new Bounds(wv, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(wv);
                    }
                }

                var localTris = mesh.triangles;
                foreach (var t in localTris)
                {
                    triList.Add(baseIndex + t);
                }
            }

            if (!hasBounds || triList.Count == 0)
            {
                return false;
            }

            verts = vertList.ToArray();
            tris = triList.ToArray();
            return true;
        }

        readonly struct PlaneInfo
        {
            public readonly Vector3 center;
            public readonly Vector2 extents;
            public readonly SurfaceLabel label;

            public PlaneInfo(Vector3 center, Vector2 extents, SurfaceLabel label)
            {
                this.center = center;
                this.extents = extents;
                this.label = label;
            }
        }

        List<PlaneInfo> ReadPlanes()
        {
            var result = new List<PlaneInfo>();
            if (m_PlaneManager == null)
            {
                return result;
            }

            foreach (var plane in m_PlaneManager.trackables)
            {
                result.Add(new PlaneInfo(plane.center, plane.extents, MapLabel(plane.classifications)));
            }
            return result;
        }

        /// <summary>
        /// AR Foundation の平面分類を SurfaceLabel へ写す。
        /// PlaneClassifications はビットフラグなので、地形として意味のあるものを優先順に見る。
        /// 棚に相当するラベルは存在せず Other に落ちる（prereg G5 注記: M3 の判定対象外）。
        /// </summary>
        static SurfaceLabel MapLabel(PlaneClassifications c)
        {
            if ((c & PlaneClassifications.Floor) != 0) return SurfaceLabel.Floor;
            if ((c & PlaneClassifications.Table) != 0) return SurfaceLabel.Table;
            if ((c & PlaneClassifications.Couch) != 0) return SurfaceLabel.Couch;
            if ((c & PlaneClassifications.Ceiling) != 0) return SurfaceLabel.Ceiling;
            if ((c & PlaneClassifications.WallFace) != 0) return SurfaceLabel.WallFace;
            if (c != PlaneClassifications.None) return SurfaceLabel.Other;
            return SurfaceLabel.Unknown;
        }

        /// <summary>
        /// 積もり面のラベル解決: その点を含む平面のうち、高さが最も近いものを採る。
        /// 表面場（Demo 4.5）では「そのセルの最上面が何にラベルされているか」だけが必要なため、
        /// 平面ラベルのみで足りる（submeshClassifications は使わない — 報告参照）。
        /// </summary>
        static SurfaceLabel ResolveLabel(List<PlaneInfo> planes, float wx, float wy, float wz)
        {
            SurfaceLabel best = SurfaceLabel.Unknown;
            float bestDy = float.MaxValue;

            foreach (var p in planes)
            {
                // 平面の XZ 範囲内か（extents は半径相当）
                if (Mathf.Abs(wx - p.center.x) > p.extents.x || Mathf.Abs(wz - p.center.z) > p.extents.y)
                {
                    continue;
                }
                float dy = Mathf.Abs(wy - p.center.y);
                if (dy < bestDy)
                {
                    bestDy = dy;
                    best = p.label;
                }
            }

            // 高さが大きく離れている平面はその面のラベルとみなさない
            return bestDy <= 0.15f ? best : SurfaceLabel.Unknown;
        }

        /// <summary>
        /// 生メッシュのアーカイブ。**M4 の保証対象外**（クラスコメント参照）。
        /// G2 のパラメータを変えて再導出したいときに HMD 再装着を省くための資材であり、
        /// これをリプレイ入力にしてはならない（float 幾何演算が再実行され bit-exact が壊れる）。
        /// </summary>
        void ArchiveRawMesh(float[] verts, int[] tris)
        {
            try
            {
                string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(Application.persistentDataPath, $"room_mesh_archive_{stamp}.bin");
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(verts.Length);
                    foreach (var v in verts) writer.Write(v);
                    writer.Write(tris.Length);
                    foreach (var t in tris) writer.Write(t);
                }
                Debug.Log($"[RoomScanner] 生メッシュをアーカイブ (M4 対象外): {path} " +
                    $"({verts.Length / 3}頂点, {tris.Length / 3}三角形)");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[RoomScanner] アーカイブ保存に失敗: {e.Message}");
            }
        }
    }
}
