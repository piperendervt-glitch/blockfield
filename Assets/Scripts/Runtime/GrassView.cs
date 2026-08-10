using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlockField
{
    /// <summary>
    /// 草の表示 (Demo 8.5 K3)。**植生場の値から直接**メッシュを組む。
    ///
    /// 【なぜ Entity を描かないのか】草は Entity ではなく場の値になった。
    /// 表示側も「植物オブジェクトを1つずつ作って消す」のをやめ、
    /// 場を読んで1枚のメッシュにまとめる。GameObject が約155個減り、
    /// 生成・破棄のたびに走っていた処理も消える（Demo 8.5 M1 の表示側の効果）。
    ///
    /// 【美観は目的ではない】要件は「処理内容を人間が確認できること」。
    /// 値に応じた高さ3段階で、どこにどれだけ草があるかが読めれば足りる。
    /// **ちらつき対策（ヒステリシス）は行わない** — 閾値付近で高さが揺れるのは
    /// 「値が閾値付近にある」という情報そのものであり、隠すべきではない。
    ///
    /// 【更新頻度】場は 1Hz でしか動かないので、メッシュの組み直しも1秒に1回で足りる。
    /// 毎フレーム全セルを組み直すと重い（FieldOverlayView と同じ方針）。
    ///
    /// 【MR の制約】アルファ<1 はパススルーと合成されるため使えない。
    /// 濃さは**高さと明度**だけで表す（CLAUDE.md アーキテクチャ）。
    /// </summary>
    public sealed class GrassView : MonoBehaviour
    {
        /// <summary>更新間隔（秒）。場は1Hzで動くので毎秒で足りる。</summary>
        const float k_RefreshInterval = 1f;

        /// <summary>地表から浮かせる高さ (m)。Zファイティング回避。</summary>
        const float k_Lift = 0.001f;

        /// <summary>
        /// 草の高さ3段階の閾値と、そのときのブロックに対する高さの比。
        /// これ未満のセルは描かない（地面が見える）。
        ///
        /// 【事前登録の目安（0.2 / 0.5 / 0.8）は使えなかった】
        /// 植生場はロジスティック成長の釣り合い点（1 - 減衰率/成長率 = 0.29）で
        /// 頭打ちになり、摂食に食われて実際は**最大でも 0.345**にしかならない。
        /// 0.5 と 0.8 は**一度も到達しない**（実測 0セル）ため、
        /// 3段階のうち2つが永久に使われず、草が1段階しか出なかった。
        ///
        /// 実測分布（seed 12345、1,500ティック、適性2,225セル）から決め直した:
        ///   50% = 0.130 / 70% = 0.161 / 85% = 0.205 / 95% = 0.237 / 最大 0.345
        /// 分位点 70 / 85 / 95 を境界にして 0.16 / 0.20 / 0.24 とする。
        /// これで低・中・高がそれぞれ 約30% / 17% / 5% のセルに出る。
        ///
        /// **この値は現在の釣り合い点に紐づいている。**
        /// vegetationGrowth や vegetationDecay を変えたら分布ごと動くので、
        /// 同じ方法（分位点から決める）で取り直すこと。
        /// </summary>
        static readonly (float threshold, float height, float brightness)[] k_Steps =
        {
            (0.24f, 0.75f, 1.00f),
            (0.20f, 0.45f, 0.85f),
            (0.16f, 0.20f, 0.70f),
        };

        /// <summary>草の色（不透明）。地形の緑より彩度を上げて区別できるようにする。</summary>
        static readonly Color32 k_Color = new Color32(70, 200, 60, 255);

        /// <summary>横幅（セルに対する比）。1.0 にすると隣と接して面が見えなくなる。</summary>
        const float k_Width = 0.55f;

        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] RoomTerrainBuilder m_Builder;
        [SerializeField] Material m_Material;
        [SerializeField] FieldOverlayView m_FieldOverlay;

        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public RoomTerrainBuilder builder { get => m_Builder; set => m_Builder = value; }
        public Material material { get => m_Material; set => m_Material = value; }

        /// <summary>
        /// 場のオーバーレイ。表示中は草を描かない (Demo 8.5 段階4)。
        ///
        /// 草の房は幅0.55・高さ最大0.75ブロックあり、オーバーレイの平板（幅0.9）の
        /// **中央を覆ってしまう**。真上から見ると場が細い枠にしか見えず、
        /// 濃淡が読めない。場の値そのものを見たい場面では草は邪魔なので消す。
        /// </summary>
        public FieldOverlayView fieldOverlay { get => m_FieldOverlay; set => m_FieldOverlay = value; }

        /// <summary>直近の描画で草を描いたセル数（診断表示用）。</summary>
        public int DrawnCells { get; private set; }

        GameObject m_Object;
        Mesh m_Mesh;
        Transform m_TrackedParent;
        float m_NextRefresh;

        readonly List<Vector3> m_Vertices = new List<Vector3>();
        readonly List<Vector3> m_Normals = new List<Vector3>();
        readonly List<Color32> m_Colors = new List<Color32>();
        readonly List<int> m_Triangles = new List<int>();

        void OnDestroy()
        {
            if (m_Object != null)
            {
                Destroy(m_Object);
            }
            if (m_Mesh != null)
            {
                Destroy(m_Mesh);
            }
        }

        void Update()
        {
            // 場のオーバーレイ表示中は草を隠す（草が場を覆って濃淡が読めなくなるため）
            bool overlayShown = m_FieldOverlay != null
                && m_FieldOverlay.Current != FieldOverlayView.Layer.None
                && m_FieldOverlay.Current != FieldOverlayView.Layer.Markers;

            if (overlayShown)
            {
                if (m_Object != null && m_Object.activeSelf)
                {
                    m_Object.SetActive(false);
                }
                return;
            }

            if (Time.unscaledTime < m_NextRefresh)
            {
                return;
            }
            m_NextRefresh = Time.unscaledTime + k_RefreshInterval;

            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            var parent = m_TerrainField != null ? m_TerrainField.TerrainRoot : null;
            if (world == null || parent == null)
            {
                return;
            }

            // セルの実寸。部屋地形は観測グリッドのセルサイズを使う
            float cell = m_Builder != null && m_Builder.Observation != null
                ? m_Builder.Observation.CellSize
                : 0.04f;

            Build(world, cell);

            if (m_Object == null)
            {
                m_Object = new GameObject("Grass");
                m_Object.AddComponent<MeshFilter>();
                m_Object.AddComponent<MeshRenderer>().sharedMaterial = m_Material;
            }
            if (m_TrackedParent != parent)
            {
                m_Object.transform.SetParent(parent, false);
                m_Object.transform.localPosition = Vector3.zero;
                m_TrackedParent = parent;
            }
            m_Object.GetComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Object.SetActive(m_Mesh != null && m_Mesh.vertexCount > 0);
        }

        void Build(World world, float cell)
        {
            m_Vertices.Clear();
            m_Normals.Clear();
            m_Colors.Clear();
            m_Triangles.Clear();
            DrawnCells = 0;

            float half = cell * k_Width * 0.5f;

            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    float v = world.Vegetation.GetAtColumn(x, z);
                    int step = -1;
                    for (int i = 0; i < k_Steps.Length; i++)
                    {
                        if (v >= k_Steps[i].threshold)
                        {
                            step = i;
                            break;
                        }
                    }
                    if (step < 0)
                    {
                        continue;
                    }

                    int surfaceY = world.GetSurfaceHeight(x, z);
                    if (surfaceY == World.NoSurfaceHeight)
                    {
                        continue;
                    }

                    DrawnCells++;
                    AddBlade(
                        x * cell, surfaceY * cell + k_Lift, z * cell,
                        half, cell * k_Steps[step].height, k_Steps[step].brightness);
                }
            }

            if (m_Mesh == null)
            {
                m_Mesh = new Mesh { name = "Grass", indexFormat = IndexFormat.UInt32 };
            }
            m_Mesh.Clear();
            if (m_Vertices.Count == 0)
            {
                return;
            }
            m_Mesh.SetVertices(m_Vertices);
            m_Mesh.SetNormals(m_Normals);
            m_Mesh.SetColors(m_Colors);
            m_Mesh.SetTriangles(m_Triangles, 0);
        }

        /// <summary>
        /// 草の房を1つ置く。直方体の側面4枚＋上面1枚（底面は地面に接するので省く）。
        /// 面を減らすほど軽くなり、上から見ても横から見ても高さが読めればよい。
        /// </summary>
        void AddBlade(float cx, float baseY, float cz, float half, float height, float brightness)
        {
            var c = new Color32(
                (byte)(k_Color.r * brightness),
                (byte)(k_Color.g * brightness),
                (byte)(k_Color.b * brightness),
                255);

            float x0 = cx - half, x1 = cx + half;
            float z0 = cz - half, z1 = cz + half;
            float y1 = baseY + height;

            // 上面
            AddQuad(
                new Vector3(x0, y1, z0), new Vector3(x0, y1, z1),
                new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Vector3.up, c);

            // 側面。面明度差（Demo 1 D0 と同じ考え方）で立体感を出す
            var side = new Color32((byte)(c.r * 0.8f), (byte)(c.g * 0.8f), (byte)(c.b * 0.8f), 255);
            AddQuad(
                new Vector3(x0, baseY, z1), new Vector3(x0, y1, z1),
                new Vector3(x1, y1, z1), new Vector3(x1, baseY, z1), Vector3.forward, side);
            AddQuad(
                new Vector3(x1, baseY, z0), new Vector3(x1, y1, z0),
                new Vector3(x0, y1, z0), new Vector3(x0, baseY, z0), Vector3.back, side);
            AddQuad(
                new Vector3(x1, baseY, z1), new Vector3(x1, y1, z1),
                new Vector3(x1, y1, z0), new Vector3(x1, baseY, z0), Vector3.right, side);
            AddQuad(
                new Vector3(x0, baseY, z0), new Vector3(x0, y1, z0),
                new Vector3(x0, y1, z1), new Vector3(x0, baseY, z1), Vector3.left, side);
        }

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 color)
        {
            int i0 = m_Vertices.Count;
            m_Vertices.Add(a);
            m_Vertices.Add(b);
            m_Vertices.Add(c);
            m_Vertices.Add(d);
            for (int i = 0; i < 4; i++)
            {
                m_Normals.Add(normal);
                m_Colors.Add(color);
            }
            m_Triangles.Add(i0 + 0); m_Triangles.Add(i0 + 1); m_Triangles.Add(i0 + 2);
            m_Triangles.Add(i0 + 0); m_Triangles.Add(i0 + 2); m_Triangles.Add(i0 + 3);
        }

        /// <summary>
        /// 場の値がどの高さ段階になるか（テスト・エディタプレビューと共用）。
        /// 描かない場合は -1。
        /// </summary>
        public static int StepFor(float vegetation)
        {
            for (int i = 0; i < k_Steps.Length; i++)
            {
                if (vegetation >= k_Steps[i].threshold)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>段階数（3）。</summary>
        public static int StepCount => k_Steps.Length;

        /// <summary>段階 i の閾値と高さ比（エディタプレビューと共用）。</summary>
        public static (float threshold, float height, float brightness) Step(int index) => k_Steps[index];
    }
}
