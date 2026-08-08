namespace BlockField.SimCore.Ecology
{
    /// <summary>シミュレーションパラメータ (Demo 2 D3/D4)。</summary>
    public struct SimParams
    {
        /// <summary>植物: 毎ティックの抽選候補セル数。</summary>
        public int plantSpawnCandidates;

        /// <summary>植物: suitability × 乱数 がこの値を超えたらスポーン。</summary>
        public float plantSpawnThreshold;

        /// <summary>植物の上限数。</summary>
        public int plantCap;

        /// <summary>動物: 毎ティックの抽選候補セル数（低頻度）。</summary>
        public int animalSpawnCandidates;

        /// <summary>動物: 候補セルが suitability 1.0 のとき、この確率でスポーン。</summary>
        public float animalSpawnChance;

        /// <summary>動物の上限数。</summary>
        public int animalCap;

        /// <summary>徘徊: 毎ティックの移動試行確率。</summary>
        public float moveChance;

        /// <summary>徘徊: 毎ティックのランダム向き変更確率（移動とは独立）。</summary>
        public float turnChance;

        public static SimParams Default => new SimParams
        {
            plantSpawnCandidates = 4,
            plantSpawnThreshold = 0.5f,
            plantCap = 200,
            animalSpawnCandidates = 1,
            animalSpawnChance = 0.3f,
            animalCap = 20,
            moveChance = 0.5f,
            turnChance = 0.2f,
        };
    }
}
