using System;

namespace BlockField.SimCore.Voxel
{
    /// <summary>
    /// ボクセルレイマーチ (Demo 4 F3)。Amanatides &amp; Woo の DDA。
    /// 座標は「セル空間」（1セル=1単位、セル (x,y,z) は [x,x+1)×[y,y+1)×[z,z+1) を占める）。
    /// ワールド座標からの変換は呼び出し側 (Runtime) の責務。UnityEngine 非依存の純関数。
    /// </summary>
    public static class VoxelRaycast
    {
        /// <summary>
        /// 最初の非Airセルを探す。ヒット時は hitCell と、レイが入った面の法線
        /// （軸方向の単位ベクトル）を返す。開始セルが非Airの場合は法線 (0,0,0)。
        /// </summary>
        public static bool Raycast(VoxelGrid grid,
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float maxDistance,
            out Int3 hitCell, out Int3 hitNormal)
        {
            hitCell = default;
            hitNormal = default;

            float length = MathF.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            if (length < 1e-6f || maxDistance <= 0f)
            {
                return false;
            }
            dirX /= length;
            dirY /= length;
            dirZ /= length;

            int cx = FloorToInt(originX);
            int cy = FloorToInt(originY);
            int cz = FloorToInt(originZ);

            var startCell = new Int3(cx, cy, cz);
            if (grid.Get(startCell) != BlockId.Air)
            {
                hitCell = startCell;
                hitNormal = new Int3(0, 0, 0);
                return true;
            }

            int stepX = Math.Sign(dirX);
            int stepY = Math.Sign(dirY);
            int stepZ = Math.Sign(dirZ);

            float tMaxX = ComputeTMax(originX, dirX, cx);
            float tMaxY = ComputeTMax(originY, dirY, cy);
            float tMaxZ = ComputeTMax(originZ, dirZ, cz);
            float tDeltaX = stepX != 0 ? MathF.Abs(1f / dirX) : float.PositiveInfinity;
            float tDeltaY = stepY != 0 ? MathF.Abs(1f / dirY) : float.PositiveInfinity;
            float tDeltaZ = stepZ != 0 ? MathF.Abs(1f / dirZ) : float.PositiveInfinity;

            while (true)
            {
                float t;
                int axis;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    cx += stepX;
                    t = tMaxX;
                    tMaxX += tDeltaX;
                    axis = 0;
                }
                else if (tMaxY <= tMaxZ)
                {
                    cy += stepY;
                    t = tMaxY;
                    tMaxY += tDeltaY;
                    axis = 1;
                }
                else
                {
                    cz += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                    axis = 2;
                }

                if (t > maxDistance)
                {
                    return false;
                }

                var cell = new Int3(cx, cy, cz);
                if (grid.Get(cell) != BlockId.Air)
                {
                    hitCell = cell;
                    hitNormal = axis == 0 ? new Int3(-stepX, 0, 0)
                        : axis == 1 ? new Int3(0, -stepY, 0)
                        : new Int3(0, 0, -stepZ);
                    return true;
                }
            }
        }

        static float ComputeTMax(float origin, float dir, int cell)
        {
            if (dir > 0f)
            {
                return (cell + 1 - origin) / dir;
            }
            if (dir < 0f)
            {
                return (origin - cell) / -dir;
            }
            return float.PositiveInfinity;
        }

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return (v < i) ? i - 1 : i;
        }
    }
}
