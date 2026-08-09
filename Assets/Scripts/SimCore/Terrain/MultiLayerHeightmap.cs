using System;
using System.Collections.Generic;

namespace BlockField.SimCore.Terrain
{
    /// <summary>
    /// 多層ハイトマップ化 (Demo 4.5 G2)。UnityEngine 非依存。
    ///
    /// XZ グリッドの各セル中心から真下へレイを飛ばし、メッシュ三角形との交差を集めて
    /// 「積もり面」を列挙する。床のみのセルは1面、机があるセルは2面（机上→床）になる。
    ///
    /// 【M4 の保証範囲との関係】
    /// 本クラスの**出力はセル単位の整数高さ**（<see cref="SurfaceHit.cellY"/>）である。
    /// 浮動小数点の幾何演算はここで完結し、以降の地形合成（G3）はこの整数列のみを読む。
    /// これにより、リプレイ経路から float 幾何演算が構造的に排除される
    /// （prereg demo45 論点2 決定: (iii) 二本立て）。
    ///
    /// 本クラス自体は M4 の保証対象外（実機 ARM64 で1回だけ走る）。M4 が保証するのは
    /// 「同一の RoomObservation から地形合成以降が同一 ContentHash を生む」ことである。
    /// </summary>
    public static class MultiLayerHeightmap
    {
        /// <summary>
        /// この値より法線 Y が小さい面は「積もり面」にしない。
        /// **符号あり**で判定する（絶対値ではない）— 下向き面（天井の裏・机の裏）に
        /// 雪は積もらないため。初版は絶対値で判定しており、実機で天井が積もり面として
        /// 検出される過検出を起こした（2026-08-09 実測: 平均3.67面/セル）。
        /// </summary>
        public const float UpwardNormalThreshold = 0.5f;

        /// <summary>この距離（m）以内の縦方向に近接するヒットは同一面としてマージする。</summary>
        public const float MergeDistanceMeters = 0.08f;

        /// <summary>
        /// 1セルあたりの積もり面の上限。表面場（Demo 4.5）では最上面しか使わないが、
        /// デバッグと将来のフロア構造 (roadmap Demo 6 拡張点 (b)) のために数枚残す。
        /// </summary>
        public const int MaxSurfacesPerCell = 3;

        /// <summary>三角形のビンニング解像度（セル数）。性能のため XZ の粗いグリッドに分ける。</summary>
        const int k_BinSize = 8;

        /// <summary>
        /// 構築時の統計（ログ設計の補強）。過検出の原因を実機ログだけで切り分けられるようにする。
        /// </summary>
        public sealed class BuildStats
        {
            /// <summary>法線条件（上向きでない）で除外した面数。</summary>
            public int RejectedByNormal;

            /// <summary>ラベル（WallFace / Ceiling）で除外した面数。</summary>
            public int RejectedByLabel;

            /// <summary>セルあたり上限で切り捨てた面数。</summary>
            public int TruncatedByCap;

            /// <summary>近接マージで統合した面数。</summary>
            public int MergedHits;

            /// <summary>面数分布: 1面のセル数。</summary>
            public int CellsWith1;

            /// <summary>面数分布: 2面のセル数。</summary>
            public int CellsWith2;

            /// <summary>面数分布: 3面以上のセル数。</summary>
            public int CellsWith3Plus;

            /// <summary>計測用: 符号ありで上向き判定に通ったヒット数（巻き順の確定に使う）。</summary>
            public int UpwardSignedHits;

            /// <summary>計測用: 絶対値で判定に通ったヒット数（旧実装相当）。</summary>
            public int UpwardAbsHits;

            public override string ToString()
            {
                return $"分布[1面={CellsWith1} 2面={CellsWith2} 3面以上={CellsWith3Plus}] " +
                    $"除外[法線={RejectedByNormal} ラベル={RejectedByLabel} 上限={TruncatedByCap} マージ={MergedHits}] " +
                    $"計測[符号あり={UpwardSignedHits} 絶対値={UpwardAbsHits}]";
            }
        }

        /// <summary>
        /// メッシュ（頂点配列＋三角形インデックス、ワールド座標）から観測データを構築する。
        /// </summary>
        /// <param name="vertices">ワールド座標の頂点配列（x,y,z の3要素ずつ）。長さは 3*頂点数</param>
        /// <param name="triangles">三角形インデックス（3つずつ）</param>
        /// <param name="labelResolver">
        /// 面のラベル解決関数（ワールド x, y, z → SurfaceLabel）。null なら Unknown。
        /// 平面ラベル（ARPlaneManager）との突き合わせは Runtime 側の責務。
        /// </param>
        public static RoomObservation Build(
            float[] vertices,
            int[] triangles,
            float cellSize,
            float minWorldX, float minWorldZ,
            int width, int depth,
            Func<float, float, float, SurfaceLabel> labelResolver = null)
        {
            return Build(vertices, triangles, cellSize, minWorldX, minWorldZ, width, depth,
                labelResolver, out _);
        }

        /// <summary>統計付きの構築（ログ・テスト用）。</summary>
        public static RoomObservation Build(
            float[] vertices,
            int[] triangles,
            float cellSize,
            float minWorldX, float minWorldZ,
            int width, int depth,
            Func<float, float, float, SurfaceLabel> labelResolver,
            out BuildStats stats)
        {
            stats = new BuildStats();
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));

            var observation = new RoomObservation(width, depth, cellSize, minWorldX, minWorldZ);

            // 三角形を XZ の粗いビンへ振り分ける（レイごとに全三角形を見ないため）
            int binsX = (width + k_BinSize - 1) / k_BinSize;
            int binsZ = (depth + k_BinSize - 1) / k_BinSize;
            var bins = BinTriangles(vertices, triangles, cellSize, minWorldX, minWorldZ, width, depth, binsX, binsZ);

            var hits = new List<(float worldY, float normalY)>();

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    // セル中心から真下へのレイ
                    float rayX = minWorldX + (x + 0.5f) * cellSize;
                    float rayZ = minWorldZ + (z + 0.5f) * cellSize;

                    hits.Clear();
                    var bin = bins[(x / k_BinSize) + binsX * (z / k_BinSize)];
                    if (bin != null)
                    {
                        CollectHits(vertices, triangles, bin, rayX, rayZ, hits);
                    }
                    if (hits.Count == 0)
                    {
                        continue;
                    }

                    // 高い順に並べ、近接ヒットをマージしてから面として記録する
                    hits.Sort((a, b) => b.worldY.CompareTo(a.worldY));
                    EmitSurfaces(observation, x, z, hits, cellSize, labelResolver, rayX, rayZ, stats);
                }
            }

            return observation;
        }

        /// <summary>
        /// 上向き面のヒットを積もり面として記録する。近接ヒットは同一面としてマージ。
        /// フロアIDは「上から数えた面の順番」を用いる（同一セル内で安定・決定論的）。
        /// </summary>
        static void EmitSurfaces(
            RoomObservation observation, int x, int z,
            List<(float worldY, float normalY)> sortedHits,
            float cellSize,
            Func<float, float, float, SurfaceLabel> labelResolver,
            float rayX, float rayZ,
            BuildStats stats)
        {
            float lastEmittedY = float.PositiveInfinity;
            int emitted = 0;

            foreach (var (worldY, normalY) in sortedHits)
            {
                // 計測: 巻き順の確定用に両規約のヒット数を数える
                // （符号ありが 0 に近ければメッシュの巻き順が逆ということ）
                if (normalY > UpwardNormalThreshold) stats.UpwardSignedHits++;
                if (Math.Abs(normalY) > UpwardNormalThreshold) stats.UpwardAbsHits++;

                // (1) 上向き面のみ（符号あり）。下向き面＝天井の裏・机の裏には積もらない
                if (normalY <= UpwardNormalThreshold)
                {
                    stats.RejectedByNormal++;
                    continue;
                }

                // 近接ヒットのマージ（薄板の表裏など）
                if (lastEmittedY - worldY < MergeDistanceMeters)
                {
                    stats.MergedHits++;
                    continue;
                }

                var label = labelResolver != null ? labelResolver(rayX, worldY, rayZ) : SurfaceLabel.Unknown;

                // (2) ラベルによる除外。壁面と天井には積もらせない
                // （Floor / Table / Other / Unknown は採用。棚は Other に落ちるため残す）
                if (label == SurfaceLabel.WallFace || label == SurfaceLabel.Ceiling)
                {
                    stats.RejectedByLabel++;
                    continue;
                }

                // (3) セルあたりの上限。高い順に走査しているので上位 N 面が残る
                if (emitted >= MaxSurfacesPerCell)
                {
                    stats.TruncatedByCap++;
                    continue;
                }

                lastEmittedY = worldY;
                int cellY = FloorToInt(worldY / cellSize);
                // floorId は上から数える（0 = 最上面）
                observation.AddHit(x, z, new SurfaceHit(cellY, worldY, emitted, label));
                emitted++;
            }

            // (4) 面数分布
            if (emitted == 1) stats.CellsWith1++;
            else if (emitted == 2) stats.CellsWith2++;
            else if (emitted >= 3) stats.CellsWith3Plus++;
        }

        /// <summary>真下向きレイと三角形の交差を集める（Möller–Trumbore）。</summary>
        static void CollectHits(
            float[] vertices, int[] triangles, List<int> triangleStarts,
            float rayX, float rayZ,
            List<(float worldY, float normalY)> results)
        {
            foreach (int t in triangleStarts)
            {
                int i0 = triangles[t] * 3;
                int i1 = triangles[t + 1] * 3;
                int i2 = triangles[t + 2] * 3;

                if (TryIntersectDownRay(
                        vertices[i0], vertices[i0 + 1], vertices[i0 + 2],
                        vertices[i1], vertices[i1 + 1], vertices[i1 + 2],
                        vertices[i2], vertices[i2 + 1], vertices[i2 + 2],
                        rayX, rayZ,
                        out float hitY, out float normalY))
                {
                    results.Add((hitY, normalY));
                }
            }
        }

        /// <summary>
        /// 真下向き（0,-1,0）のレイと三角形の交差判定 (Möller–Trumbore)。
        /// 原点は (rayX, +∞, rayZ) 相当なので、交差した場合の y をそのまま返す。
        /// 戻り値の normalY は三角形法線の Y 成分（正規化済み、**符号あり**）。
        /// 呼び出し側が上向き/下向きを区別できるよう絶対値は取らない。
        /// </summary>
        public static bool TryIntersectDownRay(
            float ax, float ay, float az,
            float bx, float by, float bz,
            float cx, float cy, float cz,
            float rayX, float rayZ,
            out float hitY, out float normalY)
        {
            hitY = 0f;
            normalY = 0f;

            // エッジ
            float e1x = bx - ax, e1y = by - ay, e1z = bz - az;
            float e2x = cx - ax, e2y = cy - ay, e2z = cz - az;

            // dir = (0, -1, 0)。 h = dir × e2 = (-1*e2z - 0, 0 - 0, 0 - (-1)*e2x) = (-e2z, 0, e2x)
            float hx = -e2z, hy = 0f, hz = e2x;
            float det = e1x * hx + e1y * hy + e1z * hz;
            if (det > -1e-9f && det < 1e-9f)
            {
                return false; // レイと平行
            }

            float invDet = 1f / det;
            float sx = rayX - ax, sy = 0f - ay, sz = rayZ - az; // レイ原点 y は後で相殺されるため 0 起点でよい
            float u = invDet * (sx * hx + sy * hy + sz * hz);
            if (u < 0f || u > 1f)
            {
                return false;
            }

            // q = s × e1
            float qx = sy * e1z - sz * e1y;
            float qy = sz * e1x - sx * e1z;
            float qz = sx * e1y - sy * e1x;

            // v = invDet * dot(dir, q) 、dir = (0,-1,0) なので -qy
            float v = invDet * (-qy);
            if (v < 0f || u + v > 1f)
            {
                return false;
            }

            // t = invDet * dot(e2, q)。交点 y = 0 + (-1)*t = -t
            float t = invDet * (e2x * qx + e2y * qy + e2z * qz);
            hitY = -t;

            // 法線 = e1 × e2 の Y 成分を正規化
            float nx = e1y * e2z - e1z * e2y;
            float ny = e1z * e2x - e1x * e2z;
            float nz = e1x * e2y - e1y * e2x;
            float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12f)
            {
                return false; // 退化三角形
            }
            normalY = ny / len; // 符号を保持する（上向き/下向きの区別に使う）
            return true;
        }

        /// <summary>三角形を XZ の粗いビンへ振り分ける（AABB が重なるビン全てに登録）。</summary>
        static List<int>[] BinTriangles(
            float[] vertices, int[] triangles,
            float cellSize, float minWorldX, float minWorldZ,
            int width, int depth, int binsX, int binsZ)
        {
            var bins = new List<int>[binsX * binsZ];

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t] * 3;
                int i1 = triangles[t + 1] * 3;
                int i2 = triangles[t + 2] * 3;

                float minX = Math.Min(vertices[i0], Math.Min(vertices[i1], vertices[i2]));
                float maxX = Math.Max(vertices[i0], Math.Max(vertices[i1], vertices[i2]));
                float minZ = Math.Min(vertices[i0 + 2], Math.Min(vertices[i1 + 2], vertices[i2 + 2]));
                float maxZ = Math.Max(vertices[i0 + 2], Math.Max(vertices[i1 + 2], vertices[i2 + 2]));

                int cellMinX = FloorToInt((minX - minWorldX) / cellSize);
                int cellMaxX = FloorToInt((maxX - minWorldX) / cellSize);
                int cellMinZ = FloorToInt((minZ - minWorldZ) / cellSize);
                int cellMaxZ = FloorToInt((maxZ - minWorldZ) / cellSize);

                int binMinX = Clamp(cellMinX / k_BinSize, 0, binsX - 1);
                int binMaxX = Clamp(cellMaxX / k_BinSize, 0, binsX - 1);
                int binMinZ = Clamp(cellMinZ / k_BinSize, 0, binsZ - 1);
                int binMaxZ = Clamp(cellMaxZ / k_BinSize, 0, binsZ - 1);

                // グリッド外の三角形は捨てる
                if (cellMaxX < 0 || cellMinX >= width || cellMaxZ < 0 || cellMinZ >= depth)
                {
                    continue;
                }

                for (int bz = binMinZ; bz <= binMaxZ; bz++)
                {
                    for (int bx = binMinX; bx <= binMaxX; bx++)
                    {
                        int index = bx + binsX * bz;
                        var list = bins[index];
                        if (list == null)
                        {
                            list = new List<int>();
                            bins[index] = list;
                        }
                        list.Add(t);
                    }
                }
            }

            return bins;
        }

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return (v < i) ? i - 1 : i;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
