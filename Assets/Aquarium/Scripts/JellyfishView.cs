using UnityEngine;

namespace BlockField.Aquarium
{
    /// <summary>
    /// クラゲの傘を描く (系列2 Phase C、**View 専用**)。
    ///
    /// 【拍動が見えることが最優先】この段の目的は「生きて見えるか」の早期確認である。
    /// リムの収縮（<see cref="SimCore.Fluid.Jellyfish.Contraction"/>）で
    /// 傘の各方向の半径を変え、**縮んで戻る動きをそのまま形にする**。
    /// 神経の状態が姿になって見えることが、この段で確かめたいことそのもの。
    ///
    /// 【MR 合成の制約】アルファ&lt;1 はパススルーと合成されるので使えない。
    /// 傘は**不透明**で描く。クラゲの透明感は出せないが、
    /// これは Demo 0 以来の制約であり、明度とスケールで代替する。
    ///
    /// 【足・表情は作らない】prereg jelly_1 の選定理由「内面の真正性と表現工数の比が
    /// 全生物中最良（漂い+拍動、足・表情不要）」に従う。
    /// </summary>
    public sealed class JellyfishView : MonoBehaviour
    {
        [SerializeField] AquariumJellyfish m_Jelly;
        [SerializeField] Material m_Material;
        [SerializeField] Transform m_AnchorSpace;

        public AquariumJellyfish jelly { get => m_Jelly; set => m_Jelly = value; }
        public Material material { get => m_Material; set => m_Material = value; }
        public Transform anchorSpace { get => m_AnchorSpace; set => m_AnchorSpace = value; }

        /// <summary>傘の高さ（直径に対する比）。ミズクラゲはやや扁平。</summary>
        const float k_HeightRatio = 0.55f;

        /// <summary>収縮でリムがどれだけ縮むか（半径に対する比）。</summary>
        const float k_ContractionDepth = 0.32f;

        Mesh m_Mesh;
        Vector3[] m_Vertices;
        int m_RingCells;

        void OnDestroy()
        {
            if (m_Mesh != null) Destroy(m_Mesh);
        }

        void LateUpdate()
        {
            var body = m_Jelly != null ? m_Jelly.Body : null;
            if (body == null || m_Material == null) return;

            int n = body.Ring.CellCount;
            if (m_Mesh == null || m_RingCells != n)
            {
                Build(n);
            }

            float radius = body.BellDiameter * 0.5f;
            float height = body.BellDiameter * k_HeightRatio;

            // 頂点0 = 頂点（傘のてっぺん）、1..n = リム
            m_Vertices[0] = new Vector3(0f, height * 0.5f, 0f);
            for (int i = 0; i < n; i++)
            {
                double a = 2.0 * Mathf.PI * i / n;
                // 収縮したセルほどリムが内側かつ上へ寄る（傘をすぼめる動き）
                float c = body.Contraction(i);
                float r = radius * (1f - k_ContractionDepth * c);
                float y = -height * 0.5f + height * 0.35f * c;
                m_Vertices[i + 1] = new Vector3(r * (float)Mathf.Cos((float)a), y,
                                                r * (float)Mathf.Sin((float)a));
            }
            m_Mesh.SetVertices(m_Vertices);
            m_Mesh.RecalculateNormals();
            m_Mesh.RecalculateBounds();

            // 部屋座標 → アンカー → ワールド（粒子と同じ経路）
            var anchor = m_AnchorSpace != null ? m_AnchorSpace.localToWorldMatrix : Matrix4x4.identity;
            var roomToAnchor = Matrix4x4.Rotate(
                Quaternion.Euler(0f, m_Jelly.flow != null ? m_Jelly.flow.RoomYawDegrees : 0f, 0f));
            var trs = Matrix4x4.TRS(new Vector3(body.X, body.Y, body.Z),
                                    Quaternion.identity, Vector3.one);

            Graphics.DrawMesh(m_Mesh, anchor * roomToAnchor * trs, m_Material, 0);
        }

        void Build(int ringCells)
        {
            if (m_Mesh != null) Destroy(m_Mesh);
            m_RingCells = ringCells;
            m_Vertices = new Vector3[ringCells + 1];

            // 頂点からリムへの扇。リムは閉じないので、下から見ると中が見える
            // ——これは意図どおりで、クラゲの傘は椀状である
            var tris = new int[ringCells * 3];
            for (int i = 0; i < ringCells; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = 1 + i;
                tris[i * 3 + 2] = 1 + (i + 1) % ringCells;
            }

            m_Mesh = new Mesh { name = "JellyfishBell" };
            m_Mesh.SetVertices(new Vector3[ringCells + 1]);
            m_Mesh.SetTriangles(tris, 0);
        }
    }
}
