namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// シミュレーションパラメータ (Demo 2 D3/D4)。
    ///
    /// 【個体数まわりは密度で定義する (Demo 5a)】
    /// 上限と抽選候補数は**基準スケール（<see cref="ReferenceSuitableCells"/> の
    /// 適性セル）での値**として持ち、<see cref="Resolve"/> がワールドの適性セル数に
    /// 比例させて絶対数へ換算する。Simulation.Tick が毎ティックの先頭で換算するので、
    /// 呼び出し側は基準スケールの値を渡すだけでよい。
    ///
    /// 理由: 部屋スケール化 (Demo 4.5) でセル数が箱庭の2.3倍になったのに上限と
    /// 頻度が据え置きだったため、密度が半減し「植物が少なく餓死が多い」状態になった。
    /// これはバランスの問題ではなくスケール変更への追従漏れである。
    /// 実測（3,000ティックの定常値、適性セル基準）:
    ///   箱庭 2,225適性セル → 植物 89.6‰ / 動物 13.1‰、餓死 t3000=69
    ///   部屋 5,135適性セル → 植物 38.9‰ / 動物  3.9‰、餓死 t3000=173
    ///
    /// 確率・速度（moveChance, hungerPerTick, 場の拡散・減衰など）はセル数に
    /// 依存しない量なので換算しない。
    /// </summary>
    public struct SimParams
    {
        /// <summary>
        /// 密度の基準になる適性セル数。箱庭 50x50（seed=12345）の実測値。
        /// 既定値はこのスケールでの絶対数として書かれており、
        /// 箱庭で走らせれば従来と完全に同じ値に解決される（ContentHash も不変）。
        /// </summary>
        public const int ReferenceSuitableCells = 2225;

        /// <summary>植物: 毎ティックの抽選候補セル数（基準スケールでの値）。</summary>
        public int plantSpawnCandidates;

        /// <summary>植物の上限数（基準スケールでの値）。</summary>
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

        /// <summary>動物: 毎ティックの抽選候補セル数（低頻度、基準スケールでの値）。</summary>
        public int animalSpawnCandidates;

        /// <summary>動物: 候補セルが suitability 1.0 のとき、この確率でスポーン。</summary>
        public float animalSpawnChance;

        /// <summary>動物の上限数（狼を含む総数、出生を含むハードキャップ。基準スケールでの値）。</summary>
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

        /// <summary>
        /// 個体数まわりを、ワールドの適性セル数に比例させて絶対数へ換算する (Demo 5a)。
        /// 確率・速度は密度と無関係なのでそのまま残す。
        ///
        /// 基準を**適性セル数**（suitability &gt; 0）にした理由: 壁・天井・穴のセルを
        /// 分母に入れると、同じ広さの部屋でも家具や間取りで密度がぶれる。
        /// 適性セルは実際にスポーンが起こりうる場所そのものなので、
        /// 「1セルあたり何個体」という意味がそのまま通る。
        /// 実測でも箱庭89.0% / 部屋94.6% と適性率に差があり、総セル基準だと
        /// この差がそのまま密度の誤差になる。
        ///
        /// 整数演算のみで換算する（浮動小数点を挟まない）ため、
        /// 決定論は環境に依らず保たれる。
        /// </summary>
        public SimParams Resolve(int suitableCells)
        {
            var r = this;
            r.plantSpawnCandidates = Scale(plantSpawnCandidates, suitableCells);
            r.plantCap = Scale(plantCap, suitableCells);
            r.animalSpawnCandidates = Scale(animalSpawnCandidates, suitableCells);
            r.animalCap = Scale(animalCap, suitableCells);
            r.animalSpawnCap = Scale(animalSpawnCap, suitableCells);
            r.wolfCap = Scale(wolfCap, suitableCells);
            return r;
        }

        /// <summary>
        /// 基準スケールの値をワールドの適性セル数へ比例換算する。
        /// 0 は「無効」の意味なので 0 のまま返す（テストがスポーンを止めるのに使う）。
        /// 0 より大きい値は最低でも 1 を返し、小さい部屋で完全に止まらないようにする。
        /// </summary>
        static int Scale(int baselineValue, int suitableCells)
        {
            if (baselineValue <= 0)
            {
                return 0;
            }
            long scaled = (long)baselineValue * suitableCells / ReferenceSuitableCells;
            return scaled < 1 ? 1 : (int)scaled;
        }
    }
}
