using BlockField.SimCore.Fluid;
using UnityEngine;

namespace BlockField.Aquarium
{
    /// <summary>
    /// **焼き込んだ固体セルが現実の壁と重なっているかを実機で確かめる**ための表示
    /// (系列2 Phase C、View 専用)。
    ///
    /// 【なぜ要るか】これまでの検証はすべて内部座標系どうしの整合だった。
    /// RoomScanner のバウンズ・アンカーのポーズ・焼き込みバウンズが互いに矛盾しないことは
    /// 示せるが、**その全部が同じだけ現実からずれていれば内部整合は成立する**。
    /// 固体セルが実際の壁の手前や向こうに層を作っていれば、境界処理が完璧に動いても
    /// 見た目には壁を貫通する。参照点が全部システムの内側にあるので、
    /// **パススルー越しに目で突き合わせる**以外に確かめる方法がない
    /// （2026-08-16 の指摘。それまでの回答は問いに答えていなかった）。
    ///
    /// 【読み方】
    /// - 点が実際の壁の**面に貼り付いて**見える → 一致している
    /// - 点の層が壁の**手前に浮いて**見える → 焼き込みが内側にずれている
    /// - 遮蔽ありで**点が消える**のに遮蔽なしで見える → 焼き込みが壁の向こう側にある
    ///
    /// 【MR 合成の制約】アルファ&lt;1 はパススルーと合成されるので使えない。
    /// 点は不透明な小さい立方体で描き、セルより十分小さくして
    /// 「格子の点」に見えるようにする（面で埋めると位置関係が読めない）。
    /// </summary>
    public sealed class AquariumDebugView : MonoBehaviour
    {
        public enum Mode
        {
            None = 0,
            /// <summary>固体セル（遮蔽あり）。壁の向こう側にあるセルは消える。</summary>
            SolidOccluded = 1,
            /// <summary>固体セル（遮蔽なし）。層の全体像が見える。</summary>
            SolidThrough = 2,
            /// <summary>
            /// **生メッシュ（ワールド直描き）**。ARMeshManager が返した頂点を
            /// 焼き込みの座標変換を一切通さずに描く。元データが部屋に合っているかを見る。
            /// </summary>
            RawMesh = 3,
            /// <summary>生メッシュ（水色）と固体セル（橙）を重ねる。ずれの量が目で分かる。</summary>
            RawMeshAndSolid = 4,
            /// <summary>水槽の外接箱だけ。部屋を覆えているかを見る。</summary>
            TankBounds = 5,
        }

        public static readonly string[] ModeNames =
        {
            "なし", "固体セル(遮蔽あり)", "固体セル(遮蔽なし)",
            "生メッシュ(ワールド直)", "生メッシュ＋固体セル", "水槽の外接箱",
        };

        /// <summary>点の一辺 (m)。セルより十分小さくしないと位置関係が読めない。</summary>
        const float k_DotSize = 0.025f;

        /// <summary>外接箱の稜線の太さ (m)。</summary>
        const float k_EdgeThickness = 0.012f;

        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] Material m_OccludedMaterial;
        [SerializeField] Material m_ThroughMaterial;
        [SerializeField] Material m_RawMeshMaterial;
        [SerializeField] Transform m_AnchorSpace;
        [SerializeField] Mode m_Mode = Mode.None;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }
        public Material occludedMaterial { get => m_OccludedMaterial; set => m_OccludedMaterial = value; }
        public Material throughMaterial { get => m_ThroughMaterial; set => m_ThroughMaterial = value; }
        public Material rawMeshMaterial { get => m_RawMeshMaterial; set => m_RawMeshMaterial = value; }
        public Transform anchorSpace { get => m_AnchorSpace; set => m_AnchorSpace = value; }

        public Mode Current => m_Mode;
        public string CurrentName => ModeNames[(int)m_Mode];
        public int DrawnCells { get; private set; }

        Mesh m_Cube;
        Matrix4x4[] m_Batch;
        Vector3[] m_Points;          // 部屋座標での点（焼き直しのたびに作り直す）
        long m_BuiltForBake = -1;
        Vector3[] m_RawPoints;       // 生メッシュの点（ワールド座標のまま）
        long m_RawBuiltFor = -1;

        public void CycleMode()
        {
            m_Mode = (Mode)(((int)m_Mode + 1) % ModeNames.Length);
            Debug.Log($"[Aquarium] デバッグ表示: {CurrentName}");
        }

        void OnDestroy()
        {
            if (m_Cube != null) Destroy(m_Cube);
        }

        void LateUpdate()
        {
            DrawnCells = 0;
            var field = m_Flow != null ? m_Flow.Field : null;
            if (m_Mode == Mode.None || field == null) return;

            if (m_Cube == null) m_Cube = BuildCube();
            if (m_Batch == null) m_Batch = new Matrix4x4[1023];

            // 部屋座標 → アンカー → ワールド（粒子・クラゲと同じ経路）
            var space = m_AnchorSpace != null ? m_AnchorSpace.localToWorldMatrix : Matrix4x4.identity;
            space *= AquariumFlow.RoomToAnchorRotation(m_Flow.RoomYawDegrees);

            if (m_Mode == Mode.TankBounds)
            {
                DrawTankBounds(field.Grid, space);
                return;
            }

            if (m_BuiltForBake != m_Flow.BakeSerial) BuildPoints(field.Grid);

            // 【生メッシュはワールド直描き】焼き込みが通る座標変換を一切通さない。
            // これが部屋に合っていれば、メッシュの取得と描画側の座標は正しい
            if (m_Mode == Mode.RawMesh || m_Mode == Mode.RawMeshAndSolid)
            {
                DrawRawMesh(Matrix4x4.identity);
            }
            if (m_Mode == Mode.RawMesh) return;

            var mat = m_Mode == Mode.SolidOccluded ? m_OccludedMaterial : m_ThroughMaterial;
            DrawPoints(m_Points, space, mat, k_DotSize);
        }

        void DrawPoints(Vector3[] points, Matrix4x4 space, Material mat, float size)
        {
            if (mat == null || points == null) return;
            var scale = Vector3.one * size;
            int batched = 0;
            for (int i = 0; i < points.Length; i++)
            {
                m_Batch[batched++] = space * Matrix4x4.TRS(points[i], Quaternion.identity, scale);
                if (batched == m_Batch.Length)
                {
                    Graphics.DrawMeshInstanced(m_Cube, 0, mat, m_Batch, batched);
                    DrawnCells += batched;
                    batched = 0;
                }
            }
            if (batched > 0)
            {
                Graphics.DrawMeshInstanced(m_Cube, 0, mat, m_Batch, batched);
                DrawnCells += batched;
            }
        }

        /// <summary>
        /// ARMeshManager の頂点をそのまま描く。**変換を一切掛けない**のが要点で、
        /// 焼き込み側の座標変換が疑わしいときの基準線になる。
        /// 点が多いので間引く（形が分かれば足りる）。
        /// </summary>
        void DrawRawMesh(Matrix4x4 space)
        {
            var v = m_Flow.ScanWorldVertices;
            if (v == null || m_RawMeshMaterial == null) return;

            if (m_RawPoints == null || m_RawBuiltFor != m_Flow.BakeSerial)
            {
                m_RawBuiltFor = m_Flow.BakeSerial;
                int total = v.Length / 3;
                int stride = Mathf.Max(1, total / 6000);
                var list = new System.Collections.Generic.List<Vector3>(total / stride + 1);
                for (int i = 0; i < total; i += stride)
                {
                    list.Add(new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]));
                }
                m_RawPoints = list.ToArray();
                Debug.Log($"[Aquarium] 生メッシュの点を作成: {m_RawPoints.Length} 個" +
                    $"（全 {total} 頂点を {stride} 個おきに間引き。ワールド座標のまま描く）");
            }
            DrawPoints(m_RawPoints, space, m_RawMeshMaterial, k_DotSize);
        }

        /// <summary>
        /// 描く点を作る。**メッシュ由来の固体セルのうち、水に接している面のセルだけ**。
        ///
        /// 外周シール（水槽の縁）は現実の壁ではなく人工の蓋なので描かない。
        /// これを混ぜると、部屋の外側 1 セルに層が出て「ずれている」ように見えてしまう。
        /// 内部のセルも描かない（面が塗り潰れて手前か奥かが読めなくなる）。
        /// </summary>
        void BuildPoints(FlowGrid g)
        {
            m_BuiltForBake = m_Flow.BakeSerial;
            var mask = m_Flow.MeshSolidMask;
            if (mask == null) { m_Points = new Vector3[0]; return; }

            var list = new System.Collections.Generic.List<Vector3>(16384);
            for (int z = 0; z < g.Depth; z++)
                for (int y = 0; y < g.Height; y++)
                    for (int x = 0; x < g.Width; x++)
                    {
                        if (!mask[g.Index(x, y, z)]) continue;
                        if (!HasFluidNeighbour(g, mask, x, y, z)) continue;
                        list.Add(new Vector3(
                            g.OriginX + (x + 0.5f) * g.CellSize,
                            g.OriginY + (y + 0.5f) * g.CellSize,
                            g.OriginZ + (z + 0.5f) * g.CellSize));
                    }
            m_Points = list.ToArray();
            Debug.Log($"[Aquarium] デバッグ表示の点を作成: {m_Points.Length} 個" +
                $"（メッシュ由来の固体 {m_Flow.MeshSolidCells} のうち水に接する面）");
        }

        static bool HasFluidNeighbour(FlowGrid g, bool[] mask, int x, int y, int z)
        {
            for (int k = 0; k < 6; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                int ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                int nz = z + (k == 4 ? 1 : k == 5 ? -1 : 0);
                if (!g.InRange(nx, ny, nz)) return true;
                if (!mask[g.Index(nx, ny, nz)]) return true;
            }
            return false;
        }

        /// <summary>外接箱の12稜線を細長い直方体で描く。</summary>
        void DrawTankBounds(FlowGrid g, Matrix4x4 space)
        {
            var mat = m_ThroughMaterial != null ? m_ThroughMaterial : m_OccludedMaterial;
            if (mat == null) return;

            float x0 = g.OriginX, y0 = g.OriginY, z0 = g.OriginZ;
            float x1 = x0 + g.Width * g.CellSize;
            float y1 = y0 + g.Height * g.CellSize;
            float z1 = z0 + g.Depth * g.CellSize;
            float t = k_EdgeThickness;

            int n = 0;
            void Edge(float ax, float ay, float az, float bx, float by, float bz)
            {
                var c = new Vector3((ax + bx) * 0.5f, (ay + by) * 0.5f, (az + bz) * 0.5f);
                var s = new Vector3(Mathf.Max(t, Mathf.Abs(bx - ax)),
                                    Mathf.Max(t, Mathf.Abs(by - ay)),
                                    Mathf.Max(t, Mathf.Abs(bz - az)));
                m_Batch[n++] = space * Matrix4x4.TRS(c, Quaternion.identity, s);
            }

            foreach (float y in new[] { y0, y1 })
            {
                Edge(x0, y, z0, x1, y, z0); Edge(x0, y, z1, x1, y, z1);
                Edge(x0, y, z0, x0, y, z1); Edge(x1, y, z0, x1, y, z1);
            }
            Edge(x0, y0, z0, x0, y1, z0); Edge(x1, y0, z0, x1, y1, z0);
            Edge(x0, y0, z1, x0, y1, z1); Edge(x1, y0, z1, x1, y1, z1);

            Graphics.DrawMeshInstanced(m_Cube, 0, mat, m_Batch, n);
            DrawnCells = n;
        }

        /// <summary>一辺1の立方体。DrawMeshInstanced のスケールで大きさを決める。</summary>
        static Mesh BuildCube()
        {
            var mesh = new Mesh { name = "AquariumDebugCube" };
            var v = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
            };
            var t = new[]
            {
                0,2,1, 0,3,2,   // -Z
                4,5,6, 4,6,7,   // +Z
                0,1,5, 0,5,4,   // -Y
                3,7,6, 3,6,2,   // +Y
                0,4,7, 0,7,3,   // -X
                1,2,6, 1,6,5,   // +X
            };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
