namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// クラゲと水槽の境界の扱い (系列2 Phase C)。
    ///
    /// 【なぜ <see cref="Jellyfish"/> と別ファイルなのか】M-J2b（jelly_1）は
    /// 「**動物が行き先を決めるのに方向を計算していない**」ことを主張し、
    /// grep テストでそれを固定している。壁の法線や壁までの距離は**環境の情報**で、
    /// 動物の内部状態ではないので、この主張とは別の層にある。
    /// とはいえ同じファイルに置くと grep の対象と混ざるため、
    /// 境界の処理はここに閉じ込め、遊泳のコードは触らない。
    ///
    /// 【この段では「入らせない」だけ】固体セルへ入る移動を受け付けない。
    /// 距離場による反発は実機を見てから判断する（2026-08-16 の申し合わせ）。
    /// </summary>
    public static class JellyBoundary
    {
        /// <summary>
        /// **壁から離れる向きの速度**を返す (m/s)。壁に近いほど強い。
        ///
        /// 【なぜ要るか】軸ごとの拒否は「壁を貫通しない」ことしか保証しない。
        /// 推力の向きはペースメーカーの位置で決まっていて**一定**なので、
        /// 壁に向かって進んだ個体は壁際で押し続け、そこに張り付いたままになる。
        /// 流れがあれば押し戻されて目立たないが、**止水ではむき出しになる**
        /// （2026-08-16 の実機。止水モードを入れて初めて見えた）。
        ///
        /// 【弾かれて見えないように】強さは壁からの距離で滑らかに落とす
        /// （帯の縁で 0、壁で最大の二次カーブ）。役割は「弾き返す」ではなく
        /// 「壁に沿って離れていく」こと。推力は書き換えず速度に足すだけなので、
        /// 接線方向の遊泳はそのまま残り、結果として壁沿いに滑る。
        ///
        /// 【向きは環境の情報】距離場の勾配は壁の形であって動物の内部状態ではない。
        /// M-J2b（動物が行き先を決めるのに方向を計算していない）とは別の層にある。
        /// だからこのファイルは grep の走査対象に入れていない。
        /// </summary>
        /// <param name="bandCells">この距離（セル数）より外では反発しない。</param>
        /// <param name="speed">壁面での強さ (m/s)。0 で無効。</param>
        public static void Repulsion(FlowGrid g, float x, float y, float z,
            float bandCells, float speed,
            out float rx, out float ry, out float rz)
        {
            rx = 0f; ry = 0f; rz = 0f;
            if (g == null || speed <= 0f || bandCells <= 0f) return;

            int gx = (int)System.Math.Floor((x - g.OriginX) / g.CellSize);
            int gy = (int)System.Math.Floor((y - g.OriginY) / g.CellSize);
            int gz = (int)System.Math.Floor((z - g.OriginZ) / g.CellSize);
            if (!g.InRange(gx, gy, gz)) return;

            // 壁面からの距離。FlowField の境界ランプと同じ取り方（セル中心ではなく面）
            float d = g.DistanceInCells(g.Index(gx, gy, gz)) - 0.5f;
            if (d >= bandCells) return;
            if (d < 0f) d = 0f;

            // 距離場の勾配 = 壁から離れる向き
            float nx = Sample(g, gx + 1, gy, gz) - Sample(g, gx - 1, gy, gz);
            float ny = Sample(g, gx, gy + 1, gz) - Sample(g, gx, gy - 1, gz);
            float nz = Sample(g, gx, gy, gz + 1) - Sample(g, gx, gy, gz - 1);
            float len = (float)System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) return;

            // 帯の縁で 0、壁で最大。二次で落とすと縁で滑らかにつながり、
            // 「境界を越えた瞬間に弾かれる」感じにならない
            float t = 1f - d / bandCells;
            float strength = speed * t * t;

            rx = nx / len * strength;
            ry = ny / len * strength;
            rz = nz / len * strength;
        }

        /// <summary>
        /// **体表のどの受容器が壁の帯に入っているか** (jelly_2 K3)。
        /// <paramref name="contact"/> に真偽を書き、入っているセル数を返す。
        ///
        /// 【なぜ「壁に最も近い1セル」ではないのか】当初の登録値は argmax だったが、
        /// **床に対して破綻する**。傘の縁の各点の床までの距離差は、傾き 13°
        /// （実測の最大）でも 0.034 m = **0.42 セル**しかなく、整数チャンファの
        /// 距離場では読めない。16セルが同値を返し、**どれが選ばれるかは
        /// タイブレークの実装が決める** — それは環境が決めた向きではなく
        /// こちらが書いた向きで、「環境が刺激の位置を決める」が成立しない。
        /// 沈降を入れた世界ではこれが定常状態である（prereg 追記13 A13.1）。
        ///
        /// 【体表で決める】受容器は体表にあり、**その部位が壁に近ければ発火する**。
        /// argmax もタイブレークも要らない。垂直な壁では壁側だけが入って非対称に、
        /// 床の真上では16セルが同時に入って対称になる。対称なら
        /// <c>Σ(amp_i · r̂_i) = 0</c> でトルクは構造的に 0（M-K2a）なので、
        /// **旋回せず推力だけが変わる** — 床に対する正しい振る舞いが自動的に出る。
        ///
        /// 【向きを計算していない】ここがやっているのは「体表の点が壁の帯に
        /// 入っているか」の判定だけで、逃避の向きは作らない。向きは伝播の
        /// 時間差と減衰勾配から創発する（M-J2b）。壁の位置は環境の情報なので
        /// このファイルは grep の走査対象に入れていない。
        /// </summary>
        /// <param name="radius">傘半径 (m)。受容器はリング＝縁にある。</param>
        /// <param name="bandCells">壁面からこの距離（セル数）以内で発火。</param>
        public static int SurfaceContact(FlowGrid g, float x, float y, float z,
            float radius, in JellyPosture posture, float[] cos, float[] sin,
            float bandCells, bool[] contact)
        {
            int n = contact.Length;
            for (int i = 0; i < n; i++) contact[i] = false;
            if (g == null || bandCells <= 0f) return 0;

            int hit = 0;
            for (int i = 0; i < n; i++)
            {
                posture.RadialAt(cos[i], sin[i], out float ux, out float uy, out float uz);
                float px = x + ux * radius, py = y + uy * radius, pz = z + uz * radius;

                int gx = (int)System.Math.Floor((px - g.OriginX) / g.CellSize);
                int gy = (int)System.Math.Floor((py - g.OriginY) / g.CellSize);
                int gz = (int)System.Math.Floor((pz - g.OriginZ) / g.CellSize);

                // 格子の外は壁とみなす（Sample と同じ約束）
                float d = g.InRange(gx, gy, gz)
                    ? g.DistanceInCells(g.Index(gx, gy, gz)) - 0.5f
                    : 0f;

                if (d < bandCells) { contact[i] = true; hit++; }
            }
            return hit;
        }

        /// <summary>
        /// **床からの高さ** (m)。真下を走査して最初の固体セルの上面までの距離を返す。
        /// 下に固体が無ければ -1。
        ///
        /// 【なぜ格子原点からの高さではないのか】M-K3d は「床からの高さの時間平均」で
        /// 判定する。実部屋の床は焼き込んだメッシュのセルであって格子の下端とは限らない。
        /// **判定と同じ量を実機ログにも出す**ため、下向きの走査で測る（prereg 追記17）。
        /// </summary>
        public static float HeightAboveFloor(FlowGrid g, float x, float y, float z)
        {
            if (g == null) return -1f;
            int gx = (int)System.Math.Floor((x - g.OriginX) / g.CellSize);
            int gy = (int)System.Math.Floor((y - g.OriginY) / g.CellSize);
            int gz = (int)System.Math.Floor((z - g.OriginZ) / g.CellSize);
            if (!g.InRange(gx, gy, gz)) return -1f;

            for (int k = gy; k >= 0; k--)
            {
                if (!g.IsSolid(gx, k, gz)) continue;
                float top = g.OriginY + (k + 1) * g.CellSize;
                return y - top;
            }
            return -1f;
        }

        /// <summary>
        /// **軸方向6本の面までの距離**を昇順に並べた最初の3つ (m)。到達しなければ大きな値。
        ///
        /// 【なぜ「最も近い壁」だけでは足りないか】隅の定義は「**2面から同時に**接している」
        /// ことなので、最近傍の距離だけでは**原理的に**単一の壁と分離できない
        /// （どちらも最近傍は同じ値になる）。2番目の値が小さければ2面、
        /// 大きければ1面である（prereg 追記18）。
        ///
        /// 3番目まで返すのは、`隅+床` と `壁+床` が 1番目・2番目だけでは
        /// 分離しないことが実測で分かっているため（追記18 A18.2）。
        /// **3番目は記録専用で判定には使わない**（追記18 A18.6）。
        ///
        /// 距離場（チャンファ）ではなく軸ごとの走査にするのは、距離場が
        /// **最近傍しか持たない**からである。
        /// </summary>
        public static void FaceDistances(FlowGrid g, float x, float y, float z,
            out float first, out float second, out float third)
        {
            first = second = third = k_FarDistance;
            if (g == null) return;

            int gx = (int)System.Math.Floor((x - g.OriginX) / g.CellSize);
            int gy = (int)System.Math.Floor((y - g.OriginY) / g.CellSize);
            int gz = (int)System.Math.Floor((z - g.OriginZ) / g.CellSize);
            if (!g.InRange(gx, gy, gz)) return;

            var d = new float[6];
            d[0] = RayDistance(g, gx, gy, gz, -1, 0, 0, x, y, z);
            d[1] = RayDistance(g, gx, gy, gz, 1, 0, 0, x, y, z);
            d[2] = RayDistance(g, gx, gy, gz, 0, -1, 0, x, y, z);
            d[3] = RayDistance(g, gx, gy, gz, 0, 1, 0, x, y, z);
            d[4] = RayDistance(g, gx, gy, gz, 0, 0, -1, x, y, z);
            d[5] = RayDistance(g, gx, gy, gz, 0, 0, 1, x, y, z);
            System.Array.Sort(d);
            first = d[0]; second = d[1]; third = d[2];
        }

        /// <summary>到達しなかった向きに入れる値 (m)。部屋より十分大きい。</summary>
        const float k_FarDistance = 99f;

        /// <summary>その向きへ走査して最初の固体セルの手前の面までの距離 (m)。</summary>
        static float RayDistance(FlowGrid g, int gx, int gy, int gz,
            int dx, int dy, int dz, float x, float y, float z)
        {
            for (int s = 1; s < 512; s++)
            {
                int cx = gx + dx * s, cy = gy + dy * s, cz = gz + dz * s;
                if (!g.InRange(cx, cy, cz)) return k_FarDistance;
                if (!g.IsSolid(cx, cy, cz)) continue;

                // 固体セルの手前の面
                if (dx != 0) return dx > 0 ? (g.OriginX + cx * g.CellSize) - x
                                           : x - (g.OriginX + (cx + 1) * g.CellSize);
                if (dy != 0) return dy > 0 ? (g.OriginY + cy * g.CellSize) - y
                                           : y - (g.OriginY + (cy + 1) * g.CellSize);
                return dz > 0 ? (g.OriginZ + cz * g.CellSize) - z
                              : z - (g.OriginZ + (cz + 1) * g.CellSize);
            }
            return k_FarDistance;
        }

        /// <summary>距離場の読み出し（格子の外は壁とみなして 0）。</summary>
        static float Sample(FlowGrid g, int x, int y, int z) =>
            g.InRange(x, y, z) ? g.DistanceInCells(g.Index(x, y, z)) : 0f;

        /// <summary>その座標のセルが水か（格子の外も水でないとみなす）。</summary>
        public static bool IsFluid(FlowGrid g, float x, float y, float z)
        {
            if (g == null) return true;
            int gx = (int)System.Math.Floor((x - g.OriginX) / g.CellSize);
            int gy = (int)System.Math.Floor((y - g.OriginY) / g.CellSize);
            int gz = (int)System.Math.Floor((z - g.OriginZ) / g.CellSize);
            return g.InRange(gx, gy, gz) && !g.IsSolid(gx, gy, gz);
        }

        /// <summary>
        /// 移動先が固体なら、その軸の移動だけを取り消す。
        ///
        /// 【軸ごとに見る理由】3成分まとめて拒否すると、壁に当たった瞬間に
        /// 完全に停止する。軸ごとなら壁に沿って滑るので、漂い続けられる。
        ///
        /// 【上へ逃がす処理を消した】以前は固体セルに入ったら 1 セルぶん
        /// 上へ押し上げていた。壁や家具の中に入った場合、上へ押しても壁の中のままなので
        /// **毎ステップ登り続けて天井に到達する**。2026-08-16 の実機ログで
        /// 72秒間その場に貼り付いた記録が残っている。入らせなければ逃がす必要もない。
        /// </summary>
        public static void ClampMove(FlowGrid g, float fromX, float fromY, float fromZ,
            ref float toX, ref float toY, ref float toZ)
        {
            if (g == null) return;
            if (!IsFluid(g, toX, fromY, fromZ)) toX = fromX;
            if (!IsFluid(g, toX, toY, fromZ)) toY = fromY;
            if (!IsFluid(g, toX, toY, toZ)) toZ = fromZ;
        }
    }
}
