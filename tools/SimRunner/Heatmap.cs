using System;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Voxel;

namespace SimRunner
{
    /// <summary>
    /// 場と地形を PNG 画像にする。
    ///
    /// 【表示の規約は実機・エディタと揃える】場ごとの表示基準値と平方根変換は
    /// <see cref="EcologyStats.FieldDisplayScale"/> / <see cref="EcologyStats.FieldDisplayIntensity"/>
    /// をそのまま使う。ここで独自のスケールを使うと、PNG で見た印象と
    /// 実機で見た印象がずれて、リモートでの判断が実機の判断とつながらなくなる。
    /// </summary>
    public static class Heatmap
    {
        /// <summary>1セルを何ピクセルで描くか。50x50 の箱庭なら 8 で 400x400。</summary>
        public const int CellPixels = 8;

        /// <summary>場ごとの色。実機・エディタのオーバーレイと同じ色相にする。</summary>
        public static (byte r, byte g, byte b) ColorFor(string fieldName) => fieldName switch
        {
            VegetationField.FieldName => (30, 220, 60),
            FearField.FieldName => (240, 60, 50),
            PreyField.FieldName => (70, 130, 245),
            DeathField.FieldName => (230, 40, 255),
            TrampleField.FieldName => (190, 120, 45),
            _ => (200, 200, 200), // suitability
        };

        /// <summary>
        /// 場のヒートマップ。適性0のセル（植物が湧けない＝壁や穴）は暗い灰色で塗り、
        /// 「場が薄い」のか「そもそも対象外」なのかを区別できるようにする。
        /// </summary>
        public static byte[] RenderField(World world, ScalarField field, out int width, out int height)
        {
            width = world.Width * CellPixels;
            height = world.Depth * CellPixels;
            var rgb = new byte[width * height * 3];

            float scale = EcologyStats.FieldDisplayScale(field.Name);
            var (cr, cg, cb) = ColorFor(field.Name);

            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    byte r, g, b;
                    if (world.Suitability.GetAtColumn(x, z) <= 0f)
                    {
                        r = g = b = 40; // 対象外セル
                    }
                    else
                    {
                        float t = EcologyStats.FieldDisplayIntensity(field.GetAtColumn(x, z), scale);
                        // 0 のセルも背景と区別できるよう、下地を少し明るくしておく
                        float k = 0.12f + 0.88f * t;
                        r = (byte)(cr * k);
                        g = (byte)(cg * k);
                        b = (byte)(cb * k);
                    }
                    FillCell(rgb, width, x, z, r, g, b);
                }
            }
            return rgb;
        }

        /// <summary>
        /// 地形の俯瞰。表層ブロックの種類で塗り、高さで明暗を付ける。
        /// 「どういう地形の上で何が起きたか」を1枚で分かるようにするためのもの。
        /// 生き物は点で重ねる。
        /// </summary>
        public static byte[] RenderTerrain(World world, out int width, out int height)
        {
            width = world.Width * CellPixels;
            height = world.Depth * CellPixels;
            var rgb = new byte[width * height * 3];

            int minH = int.MaxValue, maxH = int.MinValue;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    int h = world.GetSurfaceHeight(x, z);
                    if (h == World.NoSurfaceHeight)
                    {
                        continue;
                    }
                    if (h < minH) minH = h;
                    if (h > maxH) maxH = h;
                }
            }
            if (minH > maxH)
            {
                minH = maxH = 0;
            }
            float span = Math.Max(1, maxH - minH);

            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    int h = world.GetSurfaceHeight(x, z);
                    byte r, g, b;
                    if (h == World.NoSurfaceHeight)
                    {
                        r = g = b = 0;
                    }
                    else
                    {
                        var id = world.Grid.Get(new Int3(x, h - 1, z));
                        (byte br, byte bg, byte bb) = id switch
                        {
                            BlockId.Grass => ((byte)80, (byte)140, (byte)70),
                            BlockId.Snow => ((byte)225, (byte)230, (byte)240),
                            BlockId.Stone => ((byte)120, (byte)120, (byte)125),
                            _ => ((byte)60, (byte)55, (byte)50),
                        };
                        // 高いほど明るく。起伏が読めるように 0.6〜1.0 に写す
                        float k = 0.6f + 0.4f * ((h - minH) / span);
                        r = (byte)(br * k);
                        g = (byte)(bg * k);
                        b = (byte)(bb * k);
                    }
                    FillCell(rgb, width, x, z, r, g, b);
                }
            }

            // 生き物を重ねる（中央の小さな四角）。実機の色分けに合わせる
            foreach (var e in world.Entities)
            {
                (byte r, byte g, byte b)? c = e.kind switch
                {
                    EntityKind.GrassTuft => ((byte)90, (byte)230, (byte)90),
                    EntityKind.Flower => ((byte)240, (byte)230, (byte)80),
                    EntityKind.Sheep => ((byte)255, (byte)255, (byte)255),
                    EntityKind.Pig => ((byte)245, (byte)150, (byte)175),
                    EntityKind.Wolf => ((byte)60, (byte)60, (byte)70),
                    _ => null,
                };
                if (c == null || !world.InBounds(e.cell.x, e.cell.z))
                {
                    continue;
                }
                int inset = e.IsAnimal ? 1 : 2;
                FillCell(rgb, width, e.cell.x, e.cell.z, c.Value.r, c.Value.g, c.Value.b, inset);
            }

            return rgb;
        }

        static void FillCell(byte[] rgb, int imageWidth, int cx, int cz, byte r, byte g, byte b, int inset = 0)
        {
            int x0 = cx * CellPixels + inset;
            int z0 = cz * CellPixels + inset;
            int size = CellPixels - inset * 2;
            for (int dy = 0; dy < size; dy++)
            {
                int row = (z0 + dy) * imageWidth;
                for (int dx = 0; dx < size; dx++)
                {
                    int i = (row + x0 + dx) * 3;
                    rgb[i] = r;
                    rgb[i + 1] = g;
                    rgb[i + 2] = b;
                }
            }
        }
    }
}
