using System;

namespace BlockField.SimCore.Excitable
{
    /// <summary>
    /// <see cref="ExcitableField"/> に渡す近傍リストの工場 (jelly_1 J1)。
    ///
    /// 形ごとにクラスを増やさないのが汎用グラフ設計の狙いである。
    /// リング（クラゲの神経環）・鎖（魚の CPG 振動子列）・2次元シート
    /// （傘の網）は、いずれもここで組み立てた近傍リストの違いでしかない。
    /// </summary>
    public static class ExcitableGraphs
    {
        /// <summary>
        /// 環状（各セルの近傍は左右2つ）。クラゲの神経環。
        ///
        /// 並びはプロトタイプの <c>(i-1, (i+1) % n)</c> に合わせて
        /// 「左, 右」の順にしてあるが、入力は加算・振幅は最大値なので
        /// **順序は結果に影響しない**（テストで反転して固定してある）。
        /// </summary>
        /// <param name="cellCount">セル数。3 以上。</param>
        public static int[][] Ring(int cellCount)
        {
            // 2 以下だと左右の近傍が同一セルになり、同じ近傍を二重に数えてしまう。
            // 「近傍が2つある」という前提が壊れるので弾く
            if (cellCount < 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellCount), cellCount, "リングは3セル以上でなければならない");
            }

            var neighbors = new int[cellCount][];
            for (int i = 0; i < cellCount; i++)
            {
                neighbors[i] = new[]
                {
                    (i - 1 + cellCount) % cellCount,
                    (i + 1) % cellCount,
                };
            }
            return neighbors;
        }

        /// <summary>
        /// 直鎖（両端は近傍1つ）。魚の CPG 振動子列を想定した器。
        /// </summary>
        public static int[][] Chain(int cellCount)
        {
            if (cellCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellCount), cellCount, "鎖は2セル以上でなければならない");
            }

            var neighbors = new int[cellCount][];
            for (int i = 0; i < cellCount; i++)
            {
                if (i == 0)
                {
                    neighbors[i] = new[] { 1 };
                }
                else if (i == cellCount - 1)
                {
                    neighbors[i] = new[] { cellCount - 2 };
                }
                else
                {
                    neighbors[i] = new[] { i - 1, i + 1 };
                }
            }
            return neighbors;
        }

        /// <summary>
        /// 4近傍の2次元シート（傘の網を想定した器）。添字は <c>z * width + x</c>。
        /// </summary>
        /// <param name="wrap">端をつなぐか（true ならトーラス）。</param>
        public static int[][] Sheet(int width, int height, bool wrap = false)
        {
            if (width < 2 || height < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width), "シートは2x2以上でなければならない");
            }

            var neighbors = new int[width * height][];
            var buffer = new int[4];
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int count = 0;
                    // 並びは -x, +x, -z, +z の固定順。順序は結果に影響しないが、
                    // 添字の並びが実行ごとに変わらないことは決定論の前提である
                    AddIfValid(ref count, buffer, x - 1, z, width, height, wrap);
                    AddIfValid(ref count, buffer, x + 1, z, width, height, wrap);
                    AddIfValid(ref count, buffer, x, z - 1, width, height, wrap);
                    AddIfValid(ref count, buffer, x, z + 1, width, height, wrap);

                    var list = new int[count];
                    Array.Copy(buffer, list, count);
                    neighbors[z * width + x] = list;
                }
            }
            return neighbors;
        }

        static void AddIfValid(
            ref int count, int[] buffer, int x, int z, int width, int height, bool wrap)
        {
            if (wrap)
            {
                x = (x + width) % width;
                z = (z + height) % height;
            }
            else if (x < 0 || x >= width || z < 0 || z >= height)
            {
                return;
            }
            buffer[count++] = z * width + x;
        }
    }
}
