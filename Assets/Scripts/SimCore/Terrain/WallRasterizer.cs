using System;
using System.Collections.Generic;

namespace BlockField.SimCore.Terrain
{
    /// <summary>
    /// 壁面の水平フットプリント (Demo 4.5 G4)。UnityEngine 非依存にするため
    /// 平面 (ARPlane) から XZ 平面上の線分として抜き出した形で渡す。
    ///
    /// 壁は鉛直なので、XZ に落とすと「中心・水平方向・半長」の線分1本になる。
    /// </summary>
    public struct WallSegment
    {
        /// <summary>線分の中心（ワールドXZ, m）。</summary>
        public float centerX;
        public float centerZ;

        /// <summary>線分の方向（水平・正規化済み）。</summary>
        public float dirX;
        public float dirZ;

        /// <summary>方向に沿った半長 (m)。</summary>
        public float halfLength;

        public WallSegment(float centerX, float centerZ, float dirX, float dirZ, float halfLength)
        {
            this.centerX = centerX;
            this.centerZ = centerZ;
            this.dirX = dirX;
            this.dirZ = dirZ;
            this.halfLength = halfLength;
        }
    }

    /// <summary>
    /// 壁の Boundary 化 (Demo 4.5 G4)。WallFace ラベルの平面を観測グリッドの
    /// 「通行不可セル」としてラスタライズする。
    ///
    /// 【なぜ観測時にやるか】結果はセル単位の bool として RoomObservation に載り、
    /// ContentHash にも入る。以降の地形合成は整数のみを読むため、M4 の保証範囲
    /// （同一の観測から同一の地形）を壊さない。float 幾何演算はここで完結する。
    /// </summary>
    public static class WallRasterizer
    {
        /// <summary>壁の厚み (m)。実測の壁平面は薄いので、セル1個ぶん確実に埋まる幅を持たせる。</summary>
        public const float ThicknessMeters = 0.06f;

        /// <summary>線分に沿ったサンプリング間隔（セルサイズに対する比）。</summary>
        const float k_StepRatio = 0.4f;

        /// <summary>
        /// 観測グリッドの最外周を無条件に通行不可にする (Demo 4.5 G4)。
        ///
        /// 【なぜ必要か】WallFace 平面だけでは壁が閉じない。実測の部屋では平面が4枚しかなく、
        /// 窓・ドア・家具の陰・平面化されない壁で切れ目が生じた（2026-08-09 第3回セッション）。
        /// M2 の目的は「動物が部屋の外に漏れないこと」なので、観測バウンズの外周そのものを
        /// 柵にするのが確実である。平面由来の壁セルは部屋内部の仕切りに効くので併用する。
        ///
        /// 天井側は不要（動物は飛ばない）。立てたセル数を返す。
        /// </summary>
        public static int SealPerimeter(RoomObservation observation)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            int marked = 0;
            int w = observation.Width;
            int d = observation.Depth;

            for (int x = 0; x < w; x++)
            {
                marked += MarkOnce(observation, x, 0);
                marked += MarkOnce(observation, x, d - 1);
            }
            for (int z = 0; z < d; z++)
            {
                marked += MarkOnce(observation, 0, z);
                marked += MarkOnce(observation, w - 1, z);
            }
            return marked;
        }

        static int MarkOnce(RoomObservation observation, int x, int z)
        {
            if (observation.IsBlocked(x, z))
            {
                return 0;
            }
            observation.SetBlocked(x, z);
            return 1;
        }

        /// <summary>
        /// 壁線分群を観測グリッドへラスタライズし、通行不可セルを立てる。
        /// 立てたセル数を返す。
        /// </summary>
        public static int Rasterize(RoomObservation observation, IReadOnlyList<WallSegment> walls)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }
            if (walls == null || walls.Count == 0)
            {
                return 0;
            }

            float cell = observation.CellSize;
            float step = cell * k_StepRatio;
            float halfThickness = ThicknessMeters * 0.5f;
            int marked = 0;

            foreach (var w in walls)
            {
                // 方向の正規化（呼び出し側の丸め誤差に備える）
                float len = (float)Math.Sqrt(w.dirX * w.dirX + w.dirZ * w.dirZ);
                if (len < 1e-6f || w.halfLength <= 0f)
                {
                    continue;
                }
                float dx = w.dirX / len;
                float dz = w.dirZ / len;

                // 法線方向（厚みを付ける向き）
                float nx = -dz;
                float nz = dx;

                int steps = (int)(w.halfLength * 2f / step) + 1;
                for (int i = 0; i <= steps; i++)
                {
                    float t = -w.halfLength + i * step;
                    if (t > w.halfLength)
                    {
                        t = w.halfLength;
                    }

                    for (int s = -1; s <= 1; s++)
                    {
                        float offset = s * halfThickness;
                        float px = w.centerX + dx * t + nx * offset;
                        float pz = w.centerZ + dz * t + nz * offset;

                        int cx = FloorToInt((px - observation.OriginWorldX) / cell);
                        int cz = FloorToInt((pz - observation.OriginWorldZ) / cell);
                        if (cx < 0 || cx >= observation.Width || cz < 0 || cz >= observation.Depth)
                        {
                            continue;
                        }
                        if (observation.IsBlocked(cx, cz))
                        {
                            continue;
                        }
                        observation.SetBlocked(cx, cz);
                        marked++;
                    }
                }
            }

            return marked;
        }

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return (v < i) ? i - 1 : i;
        }
    }
}
