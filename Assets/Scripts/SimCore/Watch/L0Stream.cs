using System;
using System.Globalization;

namespace BlockField.SimCore.Watch
{
    /// <summary>
    /// L0 のカバレッジ。**「どこを測れていたか」であって「どこに居たか」ではない。**
    ///
    /// 【なぜ集合として持つか】自分の位置が分かるなら、**自分がどこに居ないかも分かる**。
    /// 台所に居ないことは推測ではなく**測定された事実**である。
    /// カバレッジが空集合のときだけ「分からない」になる。
    /// </summary>
    public enum L0Coverage : byte
    {
        /// <summary>空集合。全セルが欠測。トラッキング喪失・未装着・セッション外。</summary>
        None = 0,

        /// <summary>走査済みの部屋領域**全体**。頭のセルが 1、他は「測定された 0」。</summary>
        ScannedRoom = 1,
    }

    /// <summary>レコードのラベル（来歴）。値を推定で埋めないので「推定」は無い。</summary>
    public enum L0Label : byte
    {
        Measured = 0,
        TrackingLost = 1,
        NotWorn = 2,
    }

    /// <summary>
    /// L0 のレコード1件。**L0 はセンサではなくストリーム形式である。**
    ///
    /// プロデューサ識別子 / 位置 / 時刻 / 値 / カバレッジ / ラベル。
    /// 頭位置はこの形を書く**プロデューサの1つ**にすぎない。
    /// **L1 以上はプロデューサの種類を知らない。**
    ///
    /// 【現段階でプロデューサは1つ】抽象化を作り込まない。後で書き足せる形であればよい。
    /// </summary>
    public readonly struct L0Sample
    {
        public readonly int ProducerId;
        public readonly int Tick;

        /// <summary>**部屋座標**の位置 (m)。プロデューサ側で変換済み。</summary>
        public readonly float X, Y, Z;

        public readonly float Value;
        public readonly L0Coverage Coverage;
        public readonly L0Label Label;

        public L0Sample(int producerId, int tick, float x, float y, float z,
            float value, L0Coverage coverage, L0Label label)
        {
            ProducerId = producerId; Tick = tick;
            X = x; Y = y; Z = z;
            Value = value; Coverage = coverage; Label = label;
        }
    }

    /// <summary>
    /// L0 のプロデューサ。**各プロデューサは「部屋座標への変換」を1つ持つ。**
    /// 頭位置は恒等変換（すでに部屋座標で読める）。
    /// </summary>
    public interface IL0Producer
    {
        /// <summary>レコードに含める識別子。</summary>
        int ProducerId { get; }

        /// <summary>このプロデューサの生値を部屋座標へ写す。頭位置は恒等。</summary>
        void ToRoom(float x, float y, float z, out float rx, out float ry, out float rz);

        /// <summary>そのティックのレコードを1件返す。返せないときは false。</summary>
        bool TryRead(int tick, out L0Sample sample);
    }

    /// <summary>
    /// ログの1行との相互変換。**記録から同じ絵を再生できる**ことが要件なので、
    /// 書式は1か所にだけ書く（読みと書きが食い違うと再生が静かにずれる）。
    /// </summary>
    public static class L0LogFormat
    {
        public const string Tag = "[L0]";

        public static string Format(in L0Sample s) =>
            string.Format(CultureInfo.InvariantCulture,
                "{0} t={1} p={2} pos={3:F4},{4:F4},{5:F4} v={6:F4} cov={7} label={8}",
                Tag, s.Tick, s.ProducerId, s.X, s.Y, s.Z, s.Value, (int)s.Coverage, (int)s.Label);

        public static bool TryParse(string line, out L0Sample sample)
        {
            sample = default;
            if (line == null) return false;
            int at = line.IndexOf(Tag, StringComparison.Ordinal);
            if (at < 0) return false;

            int tick = 0, producer = 0, cov = 0, label = 0;
            float x = 0, y = 0, z = 0, v = 0;
            bool hasTick = false, hasPos = false;

            foreach (string token in line.Substring(at + Tag.Length).Split(' '))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) continue;
                string key = token.Substring(0, eq);
                string val = token.Substring(eq + 1);
                switch (key)
                {
                    case "t": hasTick = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out tick); break;
                    case "p": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out producer); break;
                    case "v": float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v); break;
                    case "cov": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out cov); break;
                    case "label": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out label); break;
                    case "pos":
                        string[] p = val.Split(',');
                        hasPos = p.Length == 3
                            && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                            && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                            && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                        break;
                }
            }
            if (!hasTick || !hasPos) return false;
            sample = new L0Sample(producer, tick, x, y, z, v, (L0Coverage)cov, (L0Label)label);
            return true;
        }
    }
}
