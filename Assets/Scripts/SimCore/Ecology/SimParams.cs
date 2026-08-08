namespace BlockField.SimCore.Ecology
{
    /// <summary>シミュレーションパラメータ (Demo 2 D3/D4)。</summary>
    public struct SimParams
    {
        /// <summary>植物: 毎ティックの抽選候補セル数。</summary>
        public int plantSpawnCandidates;

        /// <summary>植物の上限数。</summary>
        public int plantCap;

        /// <summary>植生場: 植物存在セルへの毎ティック書き込み量（上限1.0）。</summary>
        public float vegetationDeposit;

        /// <summary>植生場: 4近傍平均へ寄せる拡散率。</summary>
        public float vegetationDiffuse;

        /// <summary>植生場: 毎ティックの減衰率（τ小の速い層）。</summary>
        public float vegetationDecay;

        /// <summary>
        /// 植生場の床値。植物スポーン確率 = suitability × max(植生場, 床値)。
        /// 無からの発生は稀に、既存植物の近傍は高確率になる。
        /// </summary>
        public float vegetationFloor;

        /// <summary>動物: 毎ティックの抽選候補セル数（低頻度）。</summary>
        public int animalSpawnCandidates;

        /// <summary>動物: 候補セルが suitability 1.0 のとき、この確率でスポーン。</summary>
        public float animalSpawnChance;

        /// <summary>動物の上限数（狼を含む総数、出生を含むハードキャップ）。</summary>
        public int animalCap;

        /// <summary>
        /// 野生スポーンの停止水準（animalCap の内数）。
        /// animalCap との差分が出生（繁殖）用の余裕になる — 野生スポーンがキャップに
        /// 張り付くと繁殖の枠が無くなるため分離した（M2観測可能性の調整）。
        /// </summary>
        public int animalSpawnCap;

        /// <summary>狼の上限数（animalCap の内数）。</summary>
        public int wolfCap;

        /// <summary>徘徊: 毎ティックの移動試行確率。</summary>
        public float moveChance;

        /// <summary>徘徊: 毎ティックのランダム向き変更確率（移動とは独立）。</summary>
        public float turnChance;

        /// <summary>空腹の毎ティック進行量（1.0 で餓死）。</summary>
        public float hungerPerTick;

        /// <summary>繁殖可能な空腹上限（双方これ未満で繁殖候補になる）。</summary>
        public float breedHungerMax;

        /// <summary>隣接ペア成立時の毎ティック繁殖確率。</summary>
        public float breedChance;

        public static SimParams Default => new SimParams
        {
            plantSpawnCandidates = 10,
            plantCap = 200,
            vegetationDeposit = 0.3f,
            vegetationDiffuse = 0.15f,
            vegetationDecay = 0.02f,
            vegetationFloor = 0.02f,
            animalSpawnCandidates = 2,
            animalSpawnChance = 0.5f,
            animalCap = 30,
            animalSpawnCap = 20,
            wolfCap = 4,
            moveChance = 0.5f,
            turnChance = 0.2f,
            hungerPerTick = 0.01f,
            breedHungerMax = 0.4f,
            breedChance = 0.2f,
        };
    }
}
