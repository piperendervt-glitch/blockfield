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
            /// **生メッシュの線画**。焼き込みの元データの三角形の辺をそのまま描く。
            /// 点で描くと部屋がただの箱に見えて凹凸が分からなかった（2026-08-16）。
            /// </summary>
            RawMesh = 3,
            /// <summary>生メッシュ（水色の線）と固体セル（橙の点）を重ねる。</summary>
            RawMeshAndSolid = 4,
            /// <summary>
            /// **外接箱の比較**。水槽の外接箱（橙）と元データの外接箱（水色）を並べる。
            /// 同じデータから作られているので、水槽が元データを1セルぶん囲むはず。
            /// </summary>
            BoundsCompare = 5,
        }

        public static readonly string[] ModeNames =
        {
            "なし", "固体セル(遮蔽あり)", "固体セル(遮蔽なし)",
            "生メッシュの線画", "生メッシュ＋固体セル", "外接箱の比較",
        };

        /// <summary>各段で何を確かめるか。装着中に読めるようパネルへ出す。</summary>
        public static readonly string[] ModeHints =
        {
            "グリップで切り替え",
            "点が現実の壁・床・机の面に載っていれば OK",
            "段2で消えた点がここで出るなら、そのセルは壁の向こう",
            "水色の線が現実の部屋の形と重なっていれば OK",
            "水色の線と橙の点が重なっていれば焼き込みは正しい",
            "橙(水槽)が水色(元データ)を1セルぶん囲んでいれば OK",
        };

        /// <summary>点の一辺 (m)。セルより十分小さくしないと位置関係が読めない。</summary>
        const float k_DotSize = 0.025f;

        /// <summary>外接箱の稜線の太さ (m)。</summary>
        const float k_EdgeThickness = 0.012f;

        /// <summary>生メッシュの線の太さ (m)。細くしないと面が塗り潰れる。</summary>
        const float k_LineThickness = 0.004f;

        /// <summary>描く辺の上限。72FPS を保てる範囲で形が読める本数。</summary>
        const int k_MaxEdges = 12000;

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
        Matrix4x4[] m_Edges;         // 生メッシュの辺（部屋座標）
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

            if (m_Mode == Mode.BoundsCompare)
            {
                var g = field.Grid;
                // 水槽の外接箱（橙）
                DrawBox(space,
                    new Vector3(g.OriginX, g.OriginY, g.OriginZ),
                    new Vector3(g.OriginX + g.Width * g.CellSize,
                                g.OriginY + g.Height * g.CellSize,
                                g.OriginZ + g.Depth * g.CellSize),
                    m_OccludedMaterial != null ? m_ThroughMaterial : null, k_EdgeThickness);
                // 元データの外接箱（水色）。細くして内側にあることが分かるようにする
                var b = m_Flow.ScanRoomBounds;
                DrawBox(space, b.min, b.max, m_RawMeshMaterial, k_EdgeThickness * 0.6f);
                return;
            }

            if (m_BuiltForBake != m_Flow.BakeSerial) BuildPoints(field.Grid);

            // 【生メッシュも部屋座標で描く】以前はワールド直描きにしていたが、
            // リセンタでワールド座標系そのものが動くので基準にならない。
            // 固体セルとまったく同じ経路にしておけば、リセンタしても互いの関係は崩れず、
            // 重ねたときにボクセル化だけを比べられる
            if (m_Mode == Mode.RawMesh || m_Mode == Mode.RawMeshAndSolid)
            {
                DrawRawMeshEdges(space);
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
        /// 元データの三角形の**辺を線で描く**。
        ///
        /// 【点をやめた理由】頂点を間引いて点で描いていたが、部屋がただの立方体に見えて
        /// 壁・床・家具の凹凸が分からなかった（2026-08-16）。面の形は辺が見えないと読めない。
        /// 長い辺ほど形を決めるので、**辺を長さ順に上から採る**。間引くにしても
        /// 短い辺（面の内側の細分）から捨てるほうが形が残る。
        /// </summary>
        void DrawRawMeshEdges(Matrix4x4 space)
        {
            var v = m_Flow.ScanRoomVertices;
            var tris = m_Flow.ScanTriangles;
            if (v == null || tris == null || m_RawMeshMaterial == null) return;

            if (m_Edges == null || m_RawBuiltFor != m_Flow.BakeSerial)
            {
                m_RawBuiltFor = m_Flow.BakeSerial;
                BuildEdges(v, tris);
            }
            for (int i = 0; i < m_Edges.Length; i += 1023)
            {
                int n = Mathf.Min(1023, m_Edges.Length - i);
                System.Array.Copy(m_Edges, i, m_Batch, 0, n);
                for (int k = 0; k < n; k++) m_Batch[k] = space * m_Batch[k];
                Graphics.DrawMeshInstanced(m_Cube, 0, m_RawMeshMaterial, m_Batch, n);
                DrawnCells += n;
            }
        }

        void BuildEdges(float[] v, int[] tris)
        {
            var seen = new System.Collections.Generic.HashSet<long>();
            var edges = new System.Collections.Generic.List<(float len, Matrix4x4 m)>();

            void AddEdge(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seen.Add(key)) return;
                var pa = new Vector3(v[a * 3], v[a * 3 + 1], v[a * 3 + 2]);
                var pb = new Vector3(v[b * 3], v[b * 3 + 1], v[b * 3 + 2]);
                var d = pb - pa;
                float len = d.magnitude;
                if (len < 1e-4f) return;
                var m = Matrix4x4.TRS((pa + pb) * 0.5f,
                    Quaternion.FromToRotation(Vector3.forward, d / len),
                    new Vector3(k_LineThickness, k_LineThickness, len));
                edges.Add((len, m));
            }

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                AddEdge(tris[t], tris[t + 1]);
                AddEdge(tris[t + 1], tris[t + 2]);
                AddEdge(tris[t + 2], tris[t]);
            }

            // 長い辺ほど形を決める。多すぎるときは短いものから捨てる
            edges.Sort((p, q) => q.len.CompareTo(p.len));
            int keep = Mathf.Min(edges.Count, k_MaxEdges);
            m_Edges = new Matrix4x4[keep];
            for (int i = 0; i < keep; i++) m_Edges[i] = edges[i].m;

            Debug.Log($"[Aquarium] 生メッシュの辺を作成: {keep} 本" +
                $"（全 {edges.Count} 本のうち長い順。部屋座標で描く）");
        }

        /// <summary>直方体の12稜線を細長い立方体で描く。</summary>
        void DrawBox(Matrix4x4 space, Vector3 lo, Vector3 hi, Material mat, float t)
        {
            if (mat == null) return;
            int n = 0;
            void Edge(float ax, float ay, float az, float bx, float by, float bz)
            {
                var c = new Vector3((ax + bx) * 0.5f, (ay + by) * 0.5f, (az + bz) * 0.5f);
                var s = new Vector3(Mathf.Max(t, Mathf.Abs(bx - ax)),
                                    Mathf.Max(t, Mathf.Abs(by - ay)),
                                    Mathf.Max(t, Mathf.Abs(bz - az)));
                m_Batch[n++] = space * Matrix4x4.TRS(c, Quaternion.identity, s);
            }
            foreach (float y in new[] { lo.y, hi.y })
            {
                Edge(lo.x, y, lo.z, hi.x, y, lo.z); Edge(lo.x, y, hi.z, hi.x, y, hi.z);
                Edge(lo.x, y, lo.z, lo.x, y, hi.z); Edge(hi.x, y, lo.z, hi.x, y, hi.z);
            }
            Edge(lo.x, lo.y, lo.z, lo.x, hi.y, lo.z); Edge(hi.x, lo.y, lo.z, hi.x, hi.y, lo.z);
            Edge(lo.x, lo.y, hi.z, lo.x, hi.y, hi.z); Edge(hi.x, lo.y, hi.z, hi.x, hi.y, hi.z);

            Graphics.DrawMeshInstanced(m_Cube, 0, mat, m_Batch, n);
            DrawnCells += n;
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
