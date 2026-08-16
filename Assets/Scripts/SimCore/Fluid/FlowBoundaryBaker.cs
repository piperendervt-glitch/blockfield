using System;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// シーンメッシュを流体格子の境界へ焼き込む (系列2 Phase B)。
    ///
    /// 【2段階になっている理由】
    /// 1. 三角形 → 固体マスク: **浮動小数点の幾何演算を含む**。観測の時点で1回だけ走る
    /// 2. 固体マスク → 距離場: **整数チャンファー距離**。浮動小数点を使わない
    ///
    /// 焼き込み**結果**（マスクと量子化距離）を観測イベントとして記録するので、
    /// リプレイ経路には 1 が入らない。生メッシュを記録して毎回焼き直す案は
    /// 採らなかった（リプレイのたびに三角形の細分が走り、環境差でビットが揺れる）。
    ///
    /// 【表示用のボクセル化とは別物】`RoomMeshVoxelizer` は表示専用で M4 の
    /// 保証対象外だが、こちらは**力学の入力**である。流れの回り方が境界で決まるため。
    /// </summary>
    public static class FlowBoundaryBaker
    {
        /// <summary>三角形をなぞる刻み（セルサイズ比）。0.5 未満なら面に穴が開かない。</summary>
        const float k_StepRatio = 0.5f;

        /// <summary>安全弁: 1三角形あたりの1辺の最大分割数。</summary>
        const int k_MaxSubdivision = 128;

        /// <summary>
        /// 三角形群を固体マスクへ焼き込む。頂点はアンカーローカル座標 (m)。
        /// </summary>
        /// <returns>新たに埋めたセル数</returns>
        public static int BakeSolid(FlowGrid grid, float[] vertices, int[] triangles)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (vertices == null || triangles == null) return 0;

            float cell = grid.CellSize;
            float step = cell * k_StepRatio;
            int filled = 0;

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t] * 3, i1 = triangles[t + 1] * 3, i2 = triangles[t + 2] * 3;
                if (i0 + 2 >= vertices.Length || i1 + 2 >= vertices.Length || i2 + 2 >= vertices.Length)
                {
                    continue;
                }

                float ax = vertices[i0], ay = vertices[i0 + 1], az = vertices[i0 + 2];
                float bx = vertices[i1], by = vertices[i1 + 1], bz = vertices[i1 + 2];
                float cx = vertices[i2], cy = vertices[i2 + 1], cz = vertices[i2 + 2];

                // 2辺の長さから細分数を決める。刻みがセルの半分以下になるように
                float ab = Length(bx - ax, by - ay, bz - az);
                float ac = Length(cx - ax, cy - ay, cz - az);
                int nu = Clamp((int)Math.Ceiling(ab / step) + 1, 1, k_MaxSubdivision);
                int nv = Clamp((int)Math.Ceiling(ac / step) + 1, 1, k_MaxSubdivision);

                for (int u = 0; u <= nu; u++)
                {
                    float fu = (float)u / nu;
                    for (int v = 0; v <= nv - u * nv / nu; v++)
                    {
                        float fv = (float)v / nv;
                        if (fu + fv > 1f) break;

                        float px = ax + (bx - ax) * fu + (cx - ax) * fv;
                        float py = ay + (by - ay) * fu + (cy - ay) * fv;
                        float pz = az + (bz - az) * fu + (cz - az) * fv;

                        int gx = (int)Math.Floor((px - grid.OriginX) / cell);
                        int gy = (int)Math.Floor((py - grid.OriginY) / cell);
                        int gz = (int)Math.Floor((pz - grid.OriginZ) / cell);
                        if (!grid.InRange(gx, gy, gz)) continue;
                        if (grid.IsSolid(gx, gy, gz)) continue;

                        grid.SetSolid(gx, gy, gz, true);
                        filled++;
                    }
                }
            }
            return filled;
        }

        /// <summary>
        /// 格子の外周（壁の代わり）を固体にする。部屋メッシュが閉じていない場合の保険。
        /// 水槽の縁であり、これが無いと流れが格子の外へ抜ける。
        /// </summary>
        public static void SealBorders(FlowGrid grid)
        {
            for (int z = 0; z < grid.Depth; z++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    for (int x = 0; x < grid.Width; x++)
                    {
                        bool border = x == 0 || y == 0 || z == 0
                            || x == grid.Width - 1 || y == grid.Height - 1 || z == grid.Depth - 1;
                        if (border) grid.SetSolid(x, y, z, true);
                    }
                }
            }
        }

        // 3D チャンファー距離の重み（量子化単位 1/32 セル）。
        // 軸 = 1.0 セル、辺 = √2 ≈ 1.414、角 = √3 ≈ 1.732 を 32 倍して丸めた整数。
        // **整数のまま掃引する**ので、環境差で結果が変わらない
        const int k_Axis = 32;    // 1.000 × 32
        const int k_Edge = 45;    // 1.414 × 32 = 45.25
        const int k_Corner = 55;  // 1.732 × 32 = 55.43

        /// <summary>
        /// 固体マスクから境界までの距離場を作る（整数チャンファー、2パス掃引）。
        /// 固体セルは 0、流体セルは最も近い固体までの距離（1/32 セル単位、255 で飽和）。
        /// </summary>
        public static void BakeDistance(FlowGrid grid)
        {
            int w = grid.Width, h = grid.Height, d = grid.Depth;
            const int inf = int.MaxValue / 4;
            var dist = new int[grid.CellCount];

            for (int i = 0; i < dist.Length; i++)
            {
                dist[i] = grid.IsSolidAt(i) ? 0 : inf;
            }

            // 前方掃引（z, y, x の昇順）→ 後方掃引（降順）。
            // 各掃引で「既に確定した側の近傍」だけを見る
            for (int pass = 0; pass < 2; pass++)
            {
                bool forward = pass == 0;
                for (int zi = 0; zi < d; zi++)
                {
                    int z = forward ? zi : d - 1 - zi;
                    for (int yi = 0; yi < h; yi++)
                    {
                        int y = forward ? yi : h - 1 - yi;
                        for (int xi = 0; xi < w; xi++)
                        {
                            int x = forward ? xi : w - 1 - xi;
                            int idx = grid.Index(x, y, z);
                            int best = dist[idx];
                            if (best == 0) continue;

                            for (int dz = -1; dz <= 1; dz++)
                            {
                                for (int dy = -1; dy <= 1; dy++)
                                {
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        if (dx == 0 && dy == 0 && dz == 0) continue;
                                        // 掃引方向の「後ろ側」だけを見る
                                        int order = dz * 9 + dy * 3 + dx;
                                        if (forward ? order > 0 : order < 0) continue;

                                        int nx = x + dx, ny = y + dy, nz = z + dz;
                                        if (!grid.InRange(nx, ny, nz)) continue;

                                        int steps = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                                        int cost = steps == 1 ? k_Axis : steps == 2 ? k_Edge : k_Corner;
                                        int candidate = dist[grid.Index(nx, ny, nz)] + cost;
                                        if (candidate < best) best = candidate;
                                    }
                                }
                            }
                            dist[idx] = best;
                        }
                    }
                }
            }

            for (int i = 0; i < dist.Length; i++)
            {
                int v = dist[i];
                grid.SetDistance(i, v >= FlowGrid.MaxQuantizedDistance
                    ? FlowGrid.MaxQuantizedDistance
                    : (byte)v);
            }
        }

        static float Length(float x, float y, float z) => (float)Math.Sqrt(x * x + y * y + z * z);

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
