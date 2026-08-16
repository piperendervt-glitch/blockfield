using UnityEngine;

namespace BlockField.Aquarium
{
    /// <summary>
    /// **水槽モードの描画の唯一の入口** (系列2 Phase C)。
    ///
    /// 【なぜ1か所に集めるか】部屋座標をワールドへ移す行列を、粒子・クラゲ・
    /// デバッグ表示がそれぞれ独立に組み立てていた。**同じ組み立てを3回書けば
    /// 3回間違えられる**。実際に2種類の誤りが同時に起きた:
    ///
    /// 1. 主軸ヨーを戻す回転の符号が3か所とも逆で、2×ヨー = 75.6° ずれていた
    /// 2. **アンカーが一度も適用されていなかった。** シーン生成が
    ///    <see cref="DioramaOrigin"/> を載せた GameObject（原点に作られ、
    ///    一度も動かない箱）を渡していた。本物は実行時に ARAnchor の子として
    ///    作られる <see cref="DioramaOrigin.OriginTransform"/> のほう。
    ///    結果、全部が実質ワールド座標で描かれていた
    ///
    /// 2 の症状はすべて実機で観測されている:
    /// - 水槽が部屋と一致しない（アンカーのヨー 126.9° ぶん回っていた）
    /// - **粒子が 1.5m 付近までしか無い**（アンカー高 0.787m ぶん下にずれ、
    ///   格子の上端 1.34m が天井 2.04m ではなくそこで終わっていた）。2度報告された
    /// - クラゲが壁を抜ける（力学は部屋座標で正しいのに描画だけ別の場所）
    /// - **HMD を被り直すとずれる**。ワールド座標系は再ローカライズで動くが、
    ///   アンカーは実部屋に貼り付いたまま。アンカー基準なら着脱に耐える
    ///
    /// 【規約】`Assets/Aquarium/Scripts/` 以下で `Graphics.DrawMesh*` を呼んで良いのは
    /// このファイルだけ。grep テストで固定してある
    /// （<c>AquariumRenderingConventionTests</c>）。生態系側の
    /// `VoxelGrid.TrySetBlockEcology` と同じく、経路を1本に絞って例外を機械的に禁じる。
    ///
    /// 【黙って壊れさせない】アンカーが未確定のときは**描かない**。
    /// 以前は <c>Matrix4x4.identity</c> へ落ちていて、まさにそれが上の 2 を
    /// 見えなくしていた。
    /// </summary>
    public sealed class AnchorSpaceRenderer : MonoBehaviour
    {
        [SerializeField] DioramaOrigin m_Origin;
        [SerializeField] AquariumFlow m_Flow;

        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }
        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }

        /// <summary>アンカーが立っていて描画できる状態か。パネルとログに出す。</summary>
        public bool IsReady => m_Origin != null && m_Origin.OriginTransform != null;

        readonly Matrix4x4[] m_Batch = new Matrix4x4[1023];
        bool m_WarnedNotReady;

        /// <summary>
        /// 部屋座標 → ワールドの行列。**アンカーの現在のポーズ**を毎回読む。
        /// 焼き込み時のポーズを固定値で持つと、再ローカライズに追従できない。
        /// </summary>
        public bool TryGetSpace(out Matrix4x4 space)
        {
            space = Matrix4x4.identity;
            if (!IsReady)
            {
                if (!m_WarnedNotReady)
                {
                    m_WarnedNotReady = true;
                    Debug.LogError("[Aquarium] アンカーが未確定のまま描画が要求された。" +
                        "描画は行わない（identity へ落とすと座標のずれが見えなくなるため）。" +
                        $"origin={(m_Origin == null ? "未配線" : "OriginTransform が null")}");
                }
                return false;
            }
            m_WarnedNotReady = false;

            float yaw = m_Flow != null ? m_Flow.RoomYawDegrees : 0f;
            space = m_Origin.OriginTransform.localToWorldMatrix
                * AquariumFlow.RoomToAnchorRotation(yaw);
            return true;
        }

        /// <summary>
        /// **カメラを向く回転を、部屋座標で**返す。ビルボードはこれを使うこと。
        ///
        /// 【なぜ渡す側で作らせないか】カメラの回転はワールド座標の量である。
        /// これをそのまま部屋座標の行列に入れると、<see cref="TryGetSpace"/> が
        /// 左から掛かるぶん**アンカーの姿勢だけ余計に回る**。
        /// 粒子はまさにこれをやっており、アンカーが効いていなかった間は
        /// 主軸ヨーぶん（37.8°）だけずれていた。アンカーを正しく適用した瞬間に
        /// アンカーのヨー 126.9° が乗り、**ずれが約 89° になって平面が横を向いた**
        /// （2026-08-16 の実機報告）。新しい不具合ではなく、
        /// アンカー修正で顕在化した既存の誤りだった。
        ///
        /// 【型で守る】呼ぶ側がカメラに触れないようにするのが要点。
        /// `Assets/Aquarium/Scripts/` で `Camera` を参照して良いのはこのファイルだけ、
        /// という grep テストを置いてある（<c>AquariumRenderingConventionTests</c>）。
        /// 位置だけ集約しても、**回転を引数で受け取る形なら呼ぶ側が間違えられる**。
        /// </summary>
        public bool TryGetBillboardRotation(out Quaternion roomRotation)
        {
            roomRotation = Quaternion.identity;
            if (!TryGetSpace(out var space)) return false;

            var camera = Camera.main;
            if (camera == null) return false;

            // ワールドの向き → 部屋座標の向き。space が左から掛かるぶんを打ち消す
            roomRotation = Quaternion.Inverse(space.rotation) * camera.transform.rotation;
            return true;
        }

        /// <summary>部屋座標の点群へ同じメッシュを並べて描く。</summary>
        public int DrawInstanced(Mesh mesh, Material material,
            Vector3[] roomPositions, int count, Vector3 scale, Quaternion rotation)
        {
            if (mesh == null || material == null || roomPositions == null) return 0;
            if (!TryGetSpace(out var space)) return 0;

            int drawn = 0, batched = 0;
            int n = Mathf.Min(count, roomPositions.Length);
            for (int i = 0; i < n; i++)
            {
                m_Batch[batched++] = space * Matrix4x4.TRS(roomPositions[i], rotation, scale);
                if (batched == m_Batch.Length)
                {
                    Graphics.DrawMeshInstanced(mesh, 0, material, m_Batch, batched);
                    drawn += batched; batched = 0;
                }
            }
            if (batched > 0)
            {
                Graphics.DrawMeshInstanced(mesh, 0, material, m_Batch, batched);
                drawn += batched;
            }
            return drawn;
        }

        /// <summary>
        /// 部屋座標での TRS 行列をそのまま並べて描く
        /// （粒子のようにカメラ向きを持つもの、線分のように向きが個別のもの）。
        /// </summary>
        public int DrawInstancedRaw(Mesh mesh, Material material,
            Matrix4x4[] roomMatrices, int count, MaterialPropertyBlock block = null)
        {
            if (mesh == null || material == null || roomMatrices == null) return 0;
            if (!TryGetSpace(out var space)) return 0;

            int drawn = 0, batched = 0;
            int n = Mathf.Min(count, roomMatrices.Length);
            for (int i = 0; i < n; i++)
            {
                m_Batch[batched++] = space * roomMatrices[i];
                if (batched == m_Batch.Length)
                {
                    Graphics.DrawMeshInstanced(mesh, 0, material, m_Batch, batched, block,
                        UnityEngine.Rendering.ShadowCastingMode.Off, false);
                    drawn += batched; batched = 0;
                }
            }
            if (batched > 0)
            {
                Graphics.DrawMeshInstanced(mesh, 0, material, m_Batch, batched, block,
                    UnityEngine.Rendering.ShadowCastingMode.Off, false);
                drawn += batched;
            }
            return drawn;
        }

        /// <summary>部屋座標に1つだけ描く（クラゲの傘）。</summary>
        public bool DrawOne(Mesh mesh, Material material, Vector3 roomPosition)
        {
            if (mesh == null || material == null) return false;
            if (!TryGetSpace(out var space)) return false;

            Graphics.DrawMesh(mesh,
                space * Matrix4x4.TRS(roomPosition, Quaternion.identity, Vector3.one),
                material, 0);
            return true;
        }

        /// <summary>部屋座標の点をワールドへ移す（ログに世界座標を出すため）。</summary>
        public Vector3 RoomToWorld(Vector3 roomPosition) =>
            TryGetSpace(out var space) ? space.MultiplyPoint3x4(roomPosition) : roomPosition;
    }
}
