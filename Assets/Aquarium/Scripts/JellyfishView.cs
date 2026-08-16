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
        internal const float k_HeightRatio = 0.55f;

        /// <summary>収縮でリムがどれだけ縮むか（半径に対する比）。</summary>
        internal const float k_ContractionDepth = 0.32f;

        /// <summary>最大収縮時に傘がどれだけ背高になるか（高さに対する比）。</summary>
        internal const float k_ApexRise = 0.35f;

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

            BuildBellVertices(body, m_Vertices);
            m_Mesh.SetVertices(m_Vertices);
            m_Mesh.RecalculateNormals();
            m_Mesh.RecalculateBounds();

            // 部屋座標 → アンカー → ワールド（粒子と同じ経路。適用は1回ずつ）。
            //
            // 【傾きの原因ではない】主軸戻しは **Y軸まわりのヨーだけ**なので、
            // 二重に掛かっても水平面は水平のままで、傾きは生じない。
            // 実機セッションでここが疑われたが、傾きは上の
            // <see cref="BuildBellVertices"/> の写像が原因だった。
            // アンカー自体の傾きも、焼き込みバウンズの高さ 2.09m が
            // 実部屋 2.07m とほぼ一致することから 0.4° 未満と分かっている
            var anchor = m_AnchorSpace != null ? m_AnchorSpace.localToWorldMatrix : Matrix4x4.identity;
            var roomToAnchor = AquariumFlow.RoomToAnchorRotation(
                m_Jelly.flow != null ? m_Jelly.flow.RoomYawDegrees : 0f);
            var trs = Matrix4x4.TRS(new Vector3(body.X, body.Y, body.Z),
                                    Quaternion.identity, Vector3.one);

            Graphics.DrawMesh(m_Mesh, anchor * roomToAnchor * trs, m_Material, 0);
        }

        /// <summary>
        /// 傘の頂点を作る（頂点0 = てっぺん、1..n = リム）。
        ///
        /// 【リムは必ず水平な平面に載る】2026-08-16 の実機セッションで
        /// 「傘の底面が傾いている」と報告された。原因は**収縮の度合いを
        /// セルごとにリムの高さへ写していた**ことである。興奮波はリングを
        /// 1セル/ステップで巡るので、収縮はペースメーカー軸に沿って常に非対称になり、
        /// リムが平面から外れる。実測で**最大 13.6°、40ステップ周期のうち 21ステップ**
        /// （拍動の半分以上）にわたって傾いていた。
        ///
        /// 直したあとの写像:
        /// - リムの高さ … **一定**（= 平面かつ水平。傾きは構造的に起こらない）
        /// - リムの半径 … セルごとの収縮（**進行波はここに出る**。姿に神経が見えることは
        ///   この段で確かめたいことそのものなので、平均化して消しはしない）
        /// - てっぺんの高さ … リング**平均**の収縮（縮むと背が高くなる。
        ///   対称量なので傾きを生まない）
        /// </summary>
        internal static void BuildBellVertices(SimCore.Fluid.Jellyfish body, Vector3[] vertices)
        {
            int n = body.Ring.CellCount;
            float radius = body.BellDiameter * 0.5f;
            float height = body.BellDiameter * k_HeightRatio;

            float meanC = 0f;
            for (int i = 0; i < n; i++) meanC += body.Contraction(i);
            meanC /= n;

            vertices[0] = new Vector3(0f, height * (0.5f + k_ApexRise * meanC), 0f);
            for (int i = 0; i < n; i++)
            {
                float a = 2f * Mathf.PI * i / n;
                float r = radius * (1f - k_ContractionDepth * body.Contraction(i));
                vertices[i + 1] = new Vector3(r * Mathf.Cos(a), -height * 0.5f, r * Mathf.Sin(a));
            }
        }

        /// <summary>
        /// 頂点からリムへの扇。リムは閉じないので、下から見ると中が見える
        /// ——これは意図どおりで、クラゲの傘は椀状である。
        ///
        /// 【巻き方向】(0, i+1, i) の順。Unity は法線 = Cross(B-A, C-A) なので
        /// （組み込み Quad で規約を確認済み）、逆順の (0, i, i+1) では法線が
        /// **内向き・下向き**になり（実測 半径方向 -0.746 / 鉛直 -0.666）、
        /// 既定の裏面カリングで**外から傘が消える**。
        /// 2026-08-16 の実機で「法線が逆では」と指摘されたとおりだった。
        /// 材質は両面描画にもしてあるが、巻き方向自体を正しておく。
        /// </summary>
        internal static int[] BuildBellTriangles(int ringCells)
        {
            var tris = new int[ringCells * 3];
            for (int i = 0; i < ringCells; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = 1 + (i + 1) % ringCells;
                tris[i * 3 + 2] = 1 + i;
            }
            return tris;
        }

        void Build(int ringCells)
        {
            if (m_Mesh != null) Destroy(m_Mesh);
            m_RingCells = ringCells;
            m_Vertices = new Vector3[ringCells + 1];
            var tris = BuildBellTriangles(ringCells);

            m_Mesh = new Mesh { name = "JellyfishBell" };
            m_Mesh.SetVertices(new Vector3[ringCells + 1]);
            m_Mesh.SetTriangles(tris, 0);
        }
    }
}
