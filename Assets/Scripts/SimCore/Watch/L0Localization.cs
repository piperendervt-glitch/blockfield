using System;
using System.Globalization;

namespace BlockField.SimCore.Watch
{
    /// <summary>
    /// **L0b が出す変換**（デバイス座標 → 部屋座標）。3×4 の行列。
    ///
    /// 【なぜ独自に持つか】SimCore は UnityEngine 非依存なので `Matrix4x4` を使えない。
    /// 【なぜ時間の関数でよいか】機体のカメラは毎ティック変換が変わる。
    /// **固定カメラや頭位置は「定数」という特殊例**にすぎない（roadmap v14.1）。
    /// </summary>
    public readonly struct L0Transform
    {
        public readonly float M00, M01, M02, M03;
        public readonly float M10, M11, M12, M13;
        public readonly float M20, M21, M22, M23;

        public L0Transform(
            float m00, float m01, float m02, float m03,
            float m10, float m11, float m12, float m13,
            float m20, float m21, float m22, float m23)
        {
            M00 = m00; M01 = m01; M02 = m02; M03 = m03;
            M10 = m10; M11 = m11; M12 = m12; M13 = m13;
            M20 = m20; M21 = m21; M22 = m22; M23 = m23;
        }

        /// <summary>恒等変換。頭位置のプロデューサはこれを返す（段1 の状態）。</summary>
        public static L0Transform Identity => new L0Transform(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f);

        public void Apply(float x, float y, float z, out float rx, out float ry, out float rz)
        {
            rx = M00 * x + M01 * y + M02 * z + M03;
            ry = M10 * x + M11 * y + M12 * z + M13;
            rz = M20 * x + M21 * y + M22 * z + M23;
        }

        /// <summary>
        /// ログに残す短い識別子。**どの校正を使ったかを記録する**ため
        /// （生の観測＋当時の変換があって初めて、当時の解釈が再現できる）。
        /// </summary>
        public string Hash()
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                foreach (float v in new[] { M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23 })
                {
                    uint bits = (uint)BitConverter.SingleToInt32Bits(v);
                    for (int b = 0; b < 4; b++)
                    {
                        h ^= (byte)(bits >> (b * 8));
                        h *= 1099511628211UL;
                    }
                }
                return h.ToString("X16", CultureInfo.InvariantCulture).Substring(0, 8);
            }
        }
    }

    /// <summary>
    /// **L0b の出力**: 変換と、**その確からしさ**。
    ///
    /// 【プロデューサごとに独立】Quest は Meta の SLAM、固定カメラはマーカー＋幾何照合、
    /// 機体は点群位置合わせ。**同じ出力を出す限り、上は区別しない**（roadmap v14.1）。
    ///
    /// 【確からしさが閾値を割ったら】L0c が**そのプロデューサのカバレッジを空集合にする**。
    /// 固定カメラでは「校正がずれた」、機体では「自分がどこにいるか分からない」。
    /// **機体ではこれが「止まる」という挙動になる。**
    /// **古い変換に静かに落とさない** — 根拠を失った変換で座標を出し続けるのは、
    /// 近似を置くのと同型である。
    /// </summary>
    public readonly struct L0Localization
    {
        /// <summary>これを割ったらカバレッジを空集合にする。</summary>
        public const float MinConfidence = 0.5f;

        public readonly int ProducerId;
        public readonly L0Transform Transform;

        /// <summary>0〜1。段1 は追跡状態から導出する（追跡中 1、喪失 0）。</summary>
        public readonly float Confidence;

        public L0Localization(int producerId, in L0Transform transform, float confidence)
        {
            ProducerId = producerId;
            Transform = transform;
            Confidence = confidence;
        }

        public bool IsTrustworthy => Confidence >= MinConfidence;

        /// <summary>段1 の頭位置プロデューサ: 恒等変換＋追跡状態からの確からしさ。</summary>
        public static L0Localization Identity(int producerId, float confidence) =>
            new L0Localization(producerId, L0Transform.Identity, confidence);
    }

    /// <summary>
    /// **カバレッジの領域**（床の境界ポリゴンと床の高さ）。
    ///
    /// 【L0 は領域で出す】セル集合ではない。**格子へのラスタライズは L1 の仕事**である
    /// （L0 で格子化すると、セルサイズを変えたときに生の記録が使えなくなる）。
    /// </summary>
    public sealed class L0Region
    {
        /// <summary>床の境界（部屋座標の XZ、m）。(x0,z0, x1,z1, ...)。</summary>
        public float[] PolygonXZ { get; }

        /// <summary>床面の高さ (m、部屋座標)。</summary>
        public float FloorHeight { get; }

        public L0Region(float[] polygonXZ, float floorHeight)
        {
            PolygonXZ = polygonXZ;
            FloorHeight = floorHeight;
        }

        public int PointCount => PolygonXZ == null ? 0 : PolygonXZ.Length / 2;

        public bool Contains(float x, float z) => PolygonMask.Contains(PolygonXZ, x, z);
    }
}
