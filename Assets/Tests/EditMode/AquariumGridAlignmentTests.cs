using System;
using System.Collections.Generic;
using BlockField.Aquarium;
using NUnit.Framework;
using UnityEngine;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// 系列2 Phase B: 格子を部屋の主軸に合わせる。
    ///
    /// 【なぜ要るか】格子はアンカーのローカル座標で持っているが、アンカーは
    /// 設置時のヨー角を持つ（実測 rotY=127°）。アンカーの軸に合わせた箱は
    /// **部屋が斜めに入って膨らむ**。実測 3.19 × 2.07 × 2.58m の部屋に対し
    /// 3.96 × 2.08 × 3.99m の格子ができていた（水平面積で約1.8倍、
    /// 増えた分はほぼ部屋の外の空間）。
    ///
    /// 幾何計算なのでテストを付ける。Phase B は判定のない表現作業だが、
    /// 「実機で見て気づく」しかない状態にはしない
    /// （このフェーズの問題5件はすべて実機で初めて分かった。
    ///   ヘッドレスのテストは全部通っていた）。
    /// </summary>
    public class AquariumGridAlignmentTests
    {
        /// <summary>部屋らしい直方体の頂点群（軸平行、幅 w・高さ h・奥行 d）。</summary>
        static float[] BoxVertices(float w, float h, float d, int stepsPerAxis = 12)
        {
            var v = new List<float>();
            for (int i = 0; i <= stepsPerAxis; i++)
            {
                float fx = w * i / stepsPerAxis;
                for (int j = 0; j <= stepsPerAxis; j++)
                {
                    float fz = d * j / stepsPerAxis;
                    // 床と天井（水平面）だけで外接箱は決まる
                    v.Add(fx); v.Add(0f); v.Add(fz);
                    v.Add(fx); v.Add(h); v.Add(fz);
                }
            }
            return v.ToArray();
        }

        static (float w, float d) HorizontalExtent(float[] verts)
        {
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                if (verts[i] < minX) minX = verts[i];
                if (verts[i] > maxX) maxX = verts[i];
                if (verts[i + 2] < minZ) minZ = verts[i + 2];
                if (verts[i + 2] > maxZ) maxZ = verts[i + 2];
            }
            return (maxX - minX, maxZ - minZ);
        }

        /// <summary>
        /// **斜めに入った部屋のヨー角を見つけられること。**
        ///
        /// 実測の部屋（3.19 × 2.58m）をアンカーのヨー 127° 相当だけ回した状態から、
        /// 主軸へ戻す角度を復元する。直方体は 90° 周期で対称なので、
        /// 127° は 37° と同一視される。
        /// </summary>
        [Test]
        public void FindBestYaw_RecoversTheRoomOrientation()
        {
            foreach (float applied in new[] { 0f, 17f, 37f, 62f, 127f, 213f })
            {
                var verts = BoxVertices(3.19f, 2.07f, 2.58f);
                AquariumFlow.RotateAroundY(verts, applied);

                float found = AquariumFlow.FindBestYaw(verts);
                AquariumFlow.RotateAroundY(verts, -found);
                var (w, d) = HorizontalExtent(verts);

                // 90° 周期なので、幅と奥行が入れ替わる可能性がある
                bool matches =
                    (Math.Abs(w - 3.19f) < 0.05f && Math.Abs(d - 2.58f) < 0.05f)
                    || (Math.Abs(w - 2.58f) < 0.05f && Math.Abs(d - 3.19f) < 0.05f);
                Assert.IsTrue(matches,
                    $"{applied}° 回した部屋を戻せていない（復元後 {w:F2} x {d:F2}m、" +
                    $"見つけたヨー {found:F1}°）");
            }
        }

        /// <summary>
        /// **膨らみが実際に減ること。** アンカー軸のままだと約1.8倍だった水平面積が、
        /// 主軸へ合わせるとほぼ真の面積になる。
        /// </summary>
        [Test]
        public void Alignment_ShrinksTheHorizontalFootprint()
        {
            const float trueW = 3.19f, trueD = 2.58f;
            float trueArea = trueW * trueD;

            var verts = BoxVertices(trueW, 2.07f, trueD);
            AquariumFlow.RotateAroundY(verts, 127f);

            var (rawW, rawD) = HorizontalExtent(verts);
            float rawArea = rawW * rawD;
            Assert.Greater(rawArea / trueArea, 1.5f,
                "127° 回した状態で膨らんでいるはず（この前提が崩れたら本修正は不要）");

            AquariumFlow.RotateAroundY(verts, -AquariumFlow.FindBestYaw(verts));
            var (fitW, fitD) = HorizontalExtent(verts);
            float fitArea = fitW * fitD;

            Assert.Less(fitArea / trueArea, 1.05f,
                $"主軸へ合わせた後の面積比が {fitArea / trueArea:F3}。真の面積に寄るはず");
            Assert.Less(fitArea, rawArea * 0.7f,
                $"膨らみが減っていない（{rawArea:F2} → {fitArea:F2} m²）");
        }

        /// <summary>
        /// 回転が可逆であること。焼き込みは「回してから」行い、描画は「戻して」行うので、
        /// ここがずれると流れが部屋に対して斜めに描かれる。
        /// </summary>
        [Test]
        public void RotateAroundY_IsReversible()
        {
            var original = BoxVertices(3.19f, 2.07f, 2.58f, stepsPerAxis: 4);
            var work = (float[])original.Clone();

            AquariumFlow.RotateAroundY(work, 37.5f);
            AquariumFlow.RotateAroundY(work, -37.5f);

            for (int i = 0; i < original.Length; i++)
            {
                Assert.AreEqual(original[i], work[i], 1e-4f, $"要素 {i} が戻っていない");
            }
        }

        /// <summary>
        /// 正方形に近い部屋では、どの向きでも面積が変わらないので角度が定まらない。
        /// **それでも落ちない**こと（面積が最小の点を1つ返せばよい）。
        /// </summary>
        [Test]
        public void FindBestYaw_HandlesASquareRoomWithoutFailing()
        {
            var verts = BoxVertices(3.0f, 2.4f, 3.0f);
            float yaw = AquariumFlow.FindBestYaw(verts);
            Assert.GreaterOrEqual(yaw, 0f);
            Assert.Less(yaw, 90f);

            AquariumFlow.RotateAroundY(verts, -yaw);
            var (w, d) = HorizontalExtent(verts);
            Assert.LessOrEqual(w * d, 3.0f * 3.0f * 1.02f, "正方形なら面積は増えないはず");
        }
    }
}
