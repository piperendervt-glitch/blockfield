namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 適性場 (Demo 2 D3)。静的な表面場 — 地形から導出され、毎ティックの更新は行わない
    /// （ブロック変更時に World が局所再計算する）。
    /// 意味論（表面場）は <see cref="IField"/> のコメントを参照。
    /// </summary>
    public sealed class SuitabilityField : ScalarField
    {
        /// <summary>World.Fields のキー。ContentHash の畳み込み順は名前昇順。</summary>
        public const string FieldName = "suitability";

        public SuitabilityField(int width, int depth)
            : base(FieldName, width, depth)
        {
        }
    }
}
