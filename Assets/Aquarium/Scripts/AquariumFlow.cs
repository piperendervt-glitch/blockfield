using System.Diagnostics;
using BlockField.SimCore.Fluid;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 部屋を水で満たす流れ場の本体 (系列2 Phase B)。
    ///
    /// 【役割の分界】計算は全部 SimCore（<see cref="FlowField"/>）にある。
    /// このクラスがやるのは「Unity から取れたものを SimCore の形に直して渡す」
    /// ことと「固定ティックで進める」ことだけ。UnityEngine 型は SimCore へ渡さない。
    ///
    /// 【固定ティックで進める】ψ のノイズ項は縞に分けて更新するので、
    /// フレーム駆動にするとフレームレート依存になり決定論が壊れる。
    /// <see cref="k_TickHz"/> の固定間隔で進め、描画側は補間して読む
    /// （既存の「表示と真実の分離」と同形）。
    /// </summary>
    public sealed class AquariumFlow : MonoBehaviour
    {
        /// <summary>流れ場を進める固定周波数 (Hz)。フレームレートとは独立。</summary>
        const float k_TickHz = 20f;

        /// <summary>計測ログを出す間隔（秒）。パネルに出す値はログにも出す（CLAUDE.md）。</summary>
        const float k_LogInterval = 1f;

        /// <summary>
        /// 実機で切り替えるセルサイズ (m)。
        /// 8cm で十分見えるなら 5.5cm を選ぶ理由がない、という判断のための選択肢。
        /// </summary>
        public static readonly float[] CellSizeChoices = { 0.08f, 0.065f, 0.055f };

        [SerializeField] RoomScanner m_Scanner;
        [SerializeField] DioramaOrigin m_Origin;
        [SerializeField] int m_CellSizeIndex = 1;

        public RoomScanner scanner { get => m_Scanner; set => m_Scanner = value; }
        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }

        /// <summary>現在のセルサイズの選択肢番号（0 = 8cm, 1 = 6.5cm, 2 = 5.5cm）。</summary>
        public int CellSizeIndex => m_CellSizeIndex;

        public float CellSize => CellSizeChoices[m_CellSizeIndex];

        /// <summary>流れ場。焼き込みが済むまで null。</summary>
        public FlowField Field { get; private set; }

        // --- 計測（すべてログにも出す） ---
        public long BakeMs { get; private set; }
        public int SolidCells { get; private set; }
        public double TickMs { get; private set; }
        public double MaxSpeed { get; private set; }
        public string Status { get; private set; } = "スキャン待ち";

        float m_TickAccumulator;
        float m_NextLog;
        readonly Stopwatch m_Watch = new Stopwatch();

        void Update()
        {
            if (Field == null)
            {
                TryBake();
                return;
            }

            // 固定ティック。1フレームで複数ティック進むこともある（描画が遅れたとき）
            m_TickAccumulator += Time.deltaTime;
            float step = 1f / k_TickHz;
            int ticked = 0;
            m_Watch.Restart();
            while (m_TickAccumulator >= step && ticked < 4)
            {
                Field.Tick();
                m_TickAccumulator -= step;
                ticked++;
            }
            m_Watch.Stop();
            if (ticked > 0)
            {
                TickMs = m_Watch.Elapsed.TotalMilliseconds / ticked;
            }

            if (Time.unscaledTime >= m_NextLog)
            {
                m_NextLog = Time.unscaledTime + k_LogInterval;
                LogMetrics();
            }
        }

        /// <summary>
        /// セルサイズを切り替えて焼き直す。実機でコストと見え方を比べるための操作。
        /// </summary>
        public void CycleCellSize()
        {
            m_CellSizeIndex = (m_CellSizeIndex + 1) % CellSizeChoices.Length;
            Field = null;
            Status = $"セルサイズ {CellSize * 100f:F1}cm で焼き直し";
            Debug.Log($"[Aquarium] セルサイズを {CellSize * 100f:F1}cm に切り替え。焼き直す");
        }

        void TryBake()
        {
            if (m_Scanner == null || !m_Scanner.IsComplete)
            {
                Status = "スキャン待ち";
                return;
            }
            var scan = m_Scanner.Result;
            if (scan?.Vertices == null || scan.Vertices.Length < 9)
            {
                Status = "メッシュが空";
                return;
            }

            m_Watch.Restart();

            // 頂点はワールド座標なので、アンカーローカルへ移す。
            // アンカー基準で持てば、再センタリングやアンカー復元で格子がずれない
            var toLocal = AnchorWorldToLocal(scan);
            int vertexCount = scan.Vertices.Length / 3;
            var local = new float[scan.Vertices.Length];
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            for (int v = 0; v < vertexCount; v++)
            {
                int i = v * 3;
                var p = toLocal.MultiplyPoint3x4(
                    new Vector3(scan.Vertices[i], scan.Vertices[i + 1], scan.Vertices[i + 2]));
                local[i] = p.x; local[i + 1] = p.y; local[i + 2] = p.z;
                if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
                if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
            }

            // 部屋の外周にも水を置く余地を1セル分足す（壁面を格子の内側に収める）
            float cell = CellSize;
            var grid = FlowGrid.FromBounds(
                minX - cell, minY - cell, minZ - cell,
                maxX + cell, maxY + cell, maxZ + cell, cell);

            SolidCells = FlowBoundaryBaker.BakeSolid(grid, local, scan.Triangles);
            FlowBoundaryBaker.SealBorders(grid);
            FlowBoundaryBaker.BakeDistance(grid);

            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();
            Field = field;

            m_Watch.Stop();
            BakeMs = m_Watch.ElapsedMilliseconds;
            Status = "流れ場を構築した";

            Debug.Log($"[Aquarium] 焼き込み完了: セル {cell * 100f:F1}cm / " +
                $"格子 {grid.Width}x{grid.Height}x{grid.Depth}={grid.CellCount} / " +
                $"固体 {SolidCells} / 所要 {BakeMs}ms / " +
                $"バウンズ({maxX - minX:F2}x{maxY - minY:F2}x{maxZ - minZ:F2}m)");
        }

        /// <summary>
        /// ワールド → アンカーローカルの行列。
        /// スキャン時のアンカーポーズを使う（現在のポーズではない）。
        /// 観測時の座標系で焼き込むことで、後から原点が動いても格子が部屋に貼り付く。
        /// </summary>
        Matrix4x4 AnchorWorldToLocal(RoomScanner.ScanResult scan)
        {
            if (scan.HasOriginPose)
            {
                return Matrix4x4.TRS(scan.OriginPoseAtScan.position,
                                     scan.OriginPoseAtScan.rotation, Vector3.one).inverse;
            }
            var t = m_Origin != null ? m_Origin.OriginTransform : null;
            return t != null ? t.worldToLocalMatrix : Matrix4x4.identity;
        }

        void LogMetrics()
        {
            if (Field == null)
            {
                Debug.Log($"[Aquarium] {Status}");
                return;
            }
            var g = Field.Grid;

            // 最大流速は表示のスケール決めにも使うので毎秒測る
            double max = 0;
            for (int z = 1; z < g.Depth - 1; z += 2)
            {
                for (int y = 1; y < g.Height - 1; y += 2)
                {
                    for (int x = 1; x < g.Width - 1; x += 2)
                    {
                        Field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        double s = Mathf.Sqrt(vx * vx + vy * vy + vz * vz);
                        if (s > max) max = s;
                    }
                }
            }
            MaxSpeed = max;

            Debug.Log($"[Aquarium] 格子: セル {CellSize * 100f:F1}cm " +
                $"{g.Width}x{g.Height}x{g.Depth}={g.CellCount} 固体={SolidCells} " +
                $"焼き込み={BakeMs}ms / ティック={TickMs:F2}ms ({k_TickHz:F0}Hz) " +
                $"最大流速={MaxSpeed:F4} tick={Field.TickCount} FPS={1f / Mathf.Max(1e-4f, Time.smoothDeltaTime):F1}");
        }
    }
}
