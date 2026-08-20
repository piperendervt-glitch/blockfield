using UnityEngine;

namespace BlockField.Watch
{
    /// <summary>
    /// L0 の描画とヘッドポーズ取得の**唯一の入口**（系列3 見守り）。
    ///
    /// 【アンカー基準】系列2 で確立した規約をそのまま持ち込む。
    /// `Graphics.DrawMesh*` を呼んでよいのはこのファイルだけで、`Camera` を参照して
    /// よいのもここだけ。空間行列を各所で組み立てると、アンカーの適用漏れと回転の
    /// 符号ミスが同じ数だけ起きる（実際に3か所すべてで両方起きた）。
    ///
    /// 【アンカーが未確定なら描かない】identity へ落とさない。静かに壊れると
    /// 実機で見るまで分からない。
    ///
    /// 【頭位置もここで取る】頭位置は L0 のプロデューサの生値だが、部屋座標へ
    /// 移すにはアンカーが要る。**アンカーを触る場所を1つに保つ**ため、
    /// ここが部屋座標に直したものを渡す。プロデューサ側の変換は恒等になる。
    /// </summary>
    public sealed class WatchSpaceRenderer : MonoBehaviour
    {
        [SerializeField] DioramaOrigin m_Origin;

        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }

        public bool IsReady => m_Origin != null && m_Origin.OriginTransform != null;

        /// <summary>アンカーの識別子。登録座標はこれに紐づく。</summary>
        public string AnchorGuid => m_Origin != null ? m_Origin.AnchorGuid : null;

        Transform m_Head;

        Transform Head
        {
            get
            {
                if (m_Head != null) return m_Head;
                var cam = Camera.main;
                m_Head = cam != null ? cam.transform : null;
                return m_Head;
            }
        }

        /// <summary>部屋座標 → ワールドの行列。アンカー未確定なら false。</summary>
        public bool TryGetSpace(out Matrix4x4 space)
        {
            if (!IsReady) { space = default; return false; }
            space = m_Origin.OriginTransform.localToWorldMatrix;
            return true;
        }

        /// <summary>
        /// **頭位置を部屋座標で**返す。アンカー未確定・ヘッド未取得なら false
        /// （＝トラッキングが無い扱いにする。0 を返さない）。
        /// </summary>
        public bool TryGetHeadInRoom(out Vector3 roomPosition)
        {
            roomPosition = default;
            if (!IsReady) return false;
            var head = Head;
            if (head == null) return false;
            roomPosition = m_Origin.OriginTransform.InverseTransformPoint(head.position);
            return true;
        }

        /// <summary>部屋座標の1点に立方体を描く。アンカー未確定なら描かない。</summary>
        public bool DrawCube(Mesh mesh, Material material, Vector3 roomPosition, float size)
        {
            if (!TryGetSpace(out var space)) return false;
            var local = Matrix4x4.TRS(roomPosition, Quaternion.identity, Vector3.one * size);
            Graphics.DrawMesh(mesh, space * local, material, 0);
            return true;
        }

        /// <summary>
        /// 部屋座標の点群をまとめて描く（インスタンシング）。**描いた数を返す。**
        ///
        /// 【上限で黙って切り捨てない】`Graphics.DrawMeshInstanced` の 1023 は
        /// **1回の呼び出しの上限**であって、描ける総数の上限ではない。
        /// 分割して全部描き、返した数を呼び出し側が「描くつもりだった数」と
        /// 突き合わせられるようにする。
        /// </summary>
        /// <param name="scale">1セルの大きさ。床は薄い板にするので軸ごとに違う。</param>
        public int DrawBatched(Mesh mesh, Material material,
            Vector3[] roomPositions, int count, Vector3 scale, Matrix4x4[] scratch)
        {
            if (!TryGetSpace(out var space)) return 0;
            int drawn = 0;
            for (int i = 0; i < count; i += scratch.Length)
            {
                int n = Mathf.Min(scratch.Length, count - i);
                for (int k = 0; k < n; k++)
                {
                    scratch[k] = space * Matrix4x4.TRS(roomPositions[i + k], Quaternion.identity, scale);
                }
                Graphics.DrawMeshInstanced(mesh, 0, material, scratch, n);
                drawn += n;
            }
            return drawn;
        }
    }
}
