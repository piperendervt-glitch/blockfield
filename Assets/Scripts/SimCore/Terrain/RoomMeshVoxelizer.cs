using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Terrain
{
    /// <summary>
    /// 部屋メッシュ全体の表面ボクセル化 (Demo 4.5b V2)。
    ///
    /// 積もり面（真下レイキャストで拾える上向き面）だけでは、棚の側面・机の脚・椅子・
    /// 荷物といった**縦の面や下向きの面が一切ブロックにならない**。VRモードでは
    /// 現実映像が消えるため、それらが丸ごと欠けて部屋の形が分からなくなる。
    /// スキャンした三角形を直接なぞって、表面に触れるセルを埋める。
    ///
    /// 【方式】各三角形をバリセントリック座標で細分し、サンプル点の入るセルを埋める。
    /// 刻み幅はセルサイズの半分以下にとるので、三角形の内部に穴は開かない。
    /// 中身は埋めない（表面1層だけ）。
    ///
    /// 【M4 との関係】これは**表示専用**であり、リプレイ入力ではない。
    /// 生メッシュのアーカイブと同じ扱いで M4 の保証対象外
    /// （RoomScanner のクラスコメント参照）。生態系が乗る地形は
    /// あくまで RoomObservation の整数から合成する。
    /// </summary>
    public static class RoomMeshVoxelizer
    {
        /// <summary>サンプリング刻み（セルサイズに対する比）。0.5 未満なら穴は開かない。</summary>
        const float k_StepRatio = 0.5f;

        /// <summary>安全弁: 1三角形あたりの1辺の最大分割数。</summary>
        const int k_MaxSubdivision = 64;

        /// <summary>
        /// 三角形群をボクセル化して <paramref name="target"/> へ書き込む。
        /// </summary>
        /// <param name="skipGrid">既に埋まっているセルは書かない（地形との重複回避）。null 可</param>
        /// <returns>新たに埋めたセル数</returns>
        public static int Voxelize(
            float[] vertices, int[] triangles,
            float cellSize, float minWorldX, float minWorldZ,
            int width, int depth,
            VoxelGrid target, VoxelGrid skipGrid, BlockId blockId)
        {
            if (vertices == null || triangles == null || target == null)
            {
                return 0;
            }
            if (cellSize <= 0f)
            {
                return 0;
            }

            float step = cellSize * k_StepRatio;
            int filled = 0;

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t] * 3;
                int i1 = triangles[t + 1] * 3;
                int i2 = triangles[t + 2] * 3;

                float ax = vertices[i0], ay = vertices[i0 + 1], az = vertices[i0 + 2];
                float bx = vertices[i1], by = vertices[i1 + 1], bz = vertices[i1 + 2];
                float cx = vertices[i2], cy = vertices[i2 + 1], cz = vertices[i2 + 2];

                // 2辺の長さから分割数を決める（大きい三角形ほど細かく刻む）
                float e1 = Length(bx - ax, by - ay, bz - az);
                float e2 = Length(cx - ax, cy - ay, cz - az);
                int n1 = Steps(e1, step);
                int n2 = Steps(e2, step);
                int n = n1 > n2 ? n1 : n2;

                for (int i = 0; i <= n; i++)
                {
                    float u = (float)i / n;
                    for (int j = 0; i + j <= n; j++)
                    {
                        float v = (float)j / n;
                        float w = 1f - u - v;

                        float px = ax * w + bx * u + cx * v;
                        float py = ay * w + by * u + cy * v;
                        float pz = az * w + bz * u + cz * v;

                        int gx = FloorToInt((px - minWorldX) / cellSize);
                        int gz = FloorToInt((pz - minWorldZ) / cellSize);
                        if (gx < 0 || gx >= width || gz < 0 || gz >= depth)
                        {
                            continue;
                        }
                        int gy = FloorToInt(py / cellSize);

                        var cell = new Int3(gx, gy, gz);
                        if (target.Get(cell) != BlockId.Air)
                        {
                            continue;
                        }
                        if (skipGrid != null && skipGrid.Get(cell) != BlockId.Air)
                        {
                            continue;
                        }

                        target.SetBlock(cell, blockId, BlockOrigin.Reality);
                        filled++;
                    }
                }
            }

            return filled;
        }

        static int Steps(float edgeLength, float step)
        {
            int n = (int)(edgeLength / step) + 1;
            if (n < 1) n = 1;
            if (n > k_MaxSubdivision) n = k_MaxSubdivision;
            return n;
        }

        static float Length(float x, float y, float z) => (float)System.Math.Sqrt(x * x + y * y + z * z);

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return (v < i) ? i - 1 : i;
        }
    }
}
