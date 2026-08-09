namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 植生場 (Demo 3 E1)。動的な表面場（τ小＝速い層）。
    /// 毎ティック: 植物存在セルへの書き込み（Simulation 側）→ 拡散 → 減衰。
    /// 「場が繁殖の主体」— 植物スポーン確率はこの場から読み出される。
    /// 意味論（表面場）は <see cref="IField"/> のコメントを参照。
    /// </summary>
    public sealed class VegetationField : DiffusingField
    {
        /// <summary>World.Fields のキー。ContentHash の畳み込み順は名前昇順。</summary>
        public const string FieldName = "vegetation";

        public VegetationField(int width, int depth)
            : base(FieldName, width, depth)
        {
        }

        public override void Update(SimParams p)
        {
            Update(p.vegetationDiffuse, p.vegetationDecay);
        }

        /// <summary>拡散率・減衰率を直接指定する版（テスト・調整用）。</summary>
        public void Update(float diffuseRate, float decayRate)
        {
            UpdateDiffusion(diffuseRate, decayRate);
        }
    }
}
