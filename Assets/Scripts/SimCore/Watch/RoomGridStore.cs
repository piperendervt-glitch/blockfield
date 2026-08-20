using System;
using System.Globalization;
using System.IO;

namespace BlockField.SimCore.Watch
{
    /// <summary>床面格子の仕様。**アンカー GUID に紐づけて保存する**。</summary>
    public readonly struct RoomGridSpec
    {
        public readonly float OriginX, OriginZ;
        public readonly int Width, Depth;
        public readonly float CellSize;

        public RoomGridSpec(float originX, float originZ, int width, int depth, float cellSize)
        {
            OriginX = originX; OriginZ = originZ;
            Width = width; Depth = depth; CellSize = cellSize;
        }

        public bool IsValid => Width > 0 && Depth > 0 && CellSize > 0f;

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "原点=({0:F3},{1:F3}) 寸法={2}x{3} セル={4:F3}m", OriginX, OriginZ, Width, Depth, CellSize);
    }

    /// <summary>
    /// 格子を**アンカー GUID に紐づけて保存し、次回以降は読む**。
    ///
    /// 【なぜ要るか】部屋の焼き込みを毎回シーンメッシュから作り直すと、
    /// **同じ部屋でも起動ごとに格子が変わる**（実測: 34×43 → 34×42）。
    /// 場はセルに溜まるので、**格子が変わった瞬間に場の対応が崩れる**。
    /// 「普段」を作るには複数セッションの蓄積が要る。
    ///
    /// 【GUID が違えば新規作成し、そのことを出す】**黙って切り替えない。**
    /// 別の部屋（別のアンカー）の場を引き継いだら、場の意味が壊れる。
    ///
    /// 【セルサイズの値はここでは決めない】保存と再利用の仕組みだけ。
    /// 値の判断は段1b（roadmap）。
    /// </summary>
    public static class RoomGridStore
    {
        public const string FilePrefix = "room_grid_";

        public static string PathFor(string directory, string anchorGuid) =>
            Path.Combine(directory, FilePrefix + Sanitize(anchorGuid) + ".txt");

        static string Sanitize(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "unknown";
            var chars = guid.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z') || c == '-';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }

        public static void Save(string directory, string anchorGuid, in RoomGridSpec spec)
        {
            Directory.CreateDirectory(directory);
            string body = string.Format(CultureInfo.InvariantCulture,
                "originX={0:R}\noriginZ={1:R}\nwidth={2}\ndepth={3}\ncell={4:R}\n",
                spec.OriginX, spec.OriginZ, spec.Width, spec.Depth, spec.CellSize);
            File.WriteAllText(PathFor(directory, anchorGuid), body);
        }

        /// <summary>保存された格子を読む。無ければ false（**近似で埋めない**）。</summary>
        public static bool TryLoad(string directory, string anchorGuid, out RoomGridSpec spec)
        {
            spec = default;
            string path = PathFor(directory, anchorGuid);
            if (!File.Exists(path)) return false;

            float ox = 0f, oz = 0f, cell = 0f;
            int w = 0, d = 0;
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "originX": float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out ox); break;
                        case "originZ": float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out oz); break;
                        case "width": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out w); break;
                        case "depth": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out d); break;
                        case "cell": float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out cell); break;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            var loaded = new RoomGridSpec(ox, oz, w, d, cell);
            if (!loaded.IsValid) return false;
            spec = loaded;
            return true;
        }
    }
}
