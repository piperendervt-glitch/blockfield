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
