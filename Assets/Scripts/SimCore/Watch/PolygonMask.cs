namespace BlockField.SimCore.Watch
{
    /// <summary>
    /// **床の境界ポリゴンの内側**を床面セルのマスクにする。
    ///
    /// 【近似をやめた経緯】「部屋の内側」を2回、近似で作ろうとして2回とも外した。
    /// 1回目は「メッシュ表面から 6 セル（48cm）以内」— **狭すぎて部屋の中央が
    /// 走査外**になった。2回目は「その床の列にメッシュ由来の固体があるか」—
    /// **広すぎて壁の外側まで含んだ**（天井のメッシュが外まで伸びるため。
    /// 実測で外接箱 2.55×3.20m のほぼ全域 8.19/9.14 m² が走査済みになった）。
    ///
    /// **3回目の近似は置かない。** Quest の Scene は床の境界ポリゴンを持っているので、
    /// それをそのまま使う（`ARPlane.classifications` の `Floor` と `ARPlane.boundary`）。
    ///
    /// 【判定は偶奇規則】セル中心がポリゴンの内側かを、水平レイの交差数で決める。
    /// 凹んだ部屋（L 字）でも正しく、穴の空いた部屋は想定しない。
    /// </summary>
    public static class PolygonMask
    {
        /// <summary>
        /// ポリゴンの内側にあるセルを走査済みにする。
        /// </summary>
        /// <param name="polygonXZ">床の境界（部屋座標の XZ、m）。長さは偶数で 3 点以上。</param>
        /// <param name="floorHeight">床面の高さ (m、部屋座標)。走査済みセルに一律で入る。</param>
        /// <returns>走査済みの床セル数。</returns>
        public static int Build(float[] polygonXZ, int width, int depth, float cellSize,
            float originX, float originZ, float floorHeight,
            out bool[] scanned, out float[] floorY)
        {
            scanned = new bool[width * depth];
            floorY = new float[width * depth];
            if (polygonXZ == null || polygonXZ.Length < 6) return 0;

            int n = 0;
            for (int z = 0; z < depth; z++)
            {
                float pz = originZ + (z + 0.5f) * cellSize;
                for (int x = 0; x < width; x++)
                {
                    float px = originX + (x + 0.5f) * cellSize;
                    if (!Contains(polygonXZ, px, pz)) continue;

                    int flat = z * width + x;
                    scanned[flat] = true;
                    floorY[flat] = floorHeight;
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 点がポリゴンの内側か（偶奇規則）。頂点は (x0,z0, x1,z1, ...) の並び。
        /// </summary>
        public static bool Contains(float[] polygonXZ, float x, float z)
        {
            if (polygonXZ == null || polygonXZ.Length < 6) return false;

            int count = polygonXZ.Length / 2;
            bool inside = false;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                float xi = polygonXZ[i * 2], zi = polygonXZ[i * 2 + 1];
                float xj = polygonXZ[j * 2], zj = polygonXZ[j * 2 + 1];

                // 辺が水平レイと交差するか。等号の扱いを片側に寄せて
                // 頂点を2度数えないようにする
                bool crosses = (zi > z) != (zj > z);
                if (!crosses) continue;

                float t = (z - zi) / (zj - zi);
                if (x < xi + t * (xj - xi)) inside = !inside;
            }
            return inside;
        }
    }
}
