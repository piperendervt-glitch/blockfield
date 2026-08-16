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
