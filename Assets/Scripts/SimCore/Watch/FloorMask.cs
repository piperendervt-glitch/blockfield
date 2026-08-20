using BlockField.SimCore.Fluid;

namespace BlockField.SimCore.Watch
{
    /// <summary>
    /// 3D の固体格子を**床面の 2D マスク**へ畳む。
    ///
    /// 【走査済みの定義】**その列にシーンメッシュ由来の固体があるか。**
    /// 部屋の内側として確定している領域**全体**であり、**部屋の中央を含む**。
    /// 走査外になるのは、メッシュが得られていない領域（隣室・扉の向こう）だけである。
    ///
    /// 【以前の定義は誤りだった】「メッシュから 6 セル（48cm）以内」としていた。
    /// この 6 は距離場の飽和値ではなく**こちらが置いた定数**である（飽和は 7.97 セル）。
    /// 結果として**部屋の中央が走査外**になり、実機で足元の印が出ず、
    /// 境界も部屋の形ではなく**表面から 48cm の殻**になっていた（2026-08-19）。
    ///
    /// 【縁を混ぜない】流れ場の格子は外周が封じてあるので、その固体を使うと
    /// **全列が走査済み**になる。渡す格子は**メッシュだけを焼いたもの**であること。
    ///
    /// 【SimCore に置く理由】「部屋の中央が走査済みである」ことをテストで固定するため。
    /// MonoBehaviour の中にあると判定できない。
    /// </summary>
    public static class FloorMask
    {
        /// <summary>
        /// 列ごとに最初（最も低い）の固体を探し、走査済みかどうかと床の高さを返す。
        /// </summary>
        /// <param name="meshOnly">**メッシュだけを焼いた**格子。縁を封じていないこと。</param>
        /// <param name="scanned">長さ w*d。その床セルが走査済みか。</param>
        /// <param name="floorY">長さ w*d。床面の高さ (m、部屋座標)。走査外は 0。</param>
        /// <returns>走査済みの床セル数。</returns>
        public static int Fold(FlowGrid meshOnly, out bool[] scanned, out float[] floorY)
        {
            int w = meshOnly.Width, h = meshOnly.Height, d = meshOnly.Depth;
            scanned = new bool[w * d];
            floorY = new float[w * d];

            int n = 0;
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                {
                    int flat = z * w + x;
                    for (int y = 0; y < h; y++)
                    {
                        if (!meshOnly.IsSolid(x, y, z)) continue;
                        scanned[flat] = true;
                        floorY[flat] = meshOnly.OriginY + (y + 1) * meshOnly.CellSize;
                        n++;
                        break;
                    }
                }
            return n;
        }
    }
}
