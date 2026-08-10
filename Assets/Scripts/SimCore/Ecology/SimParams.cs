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

        // --- Demo 8 第1段: 恐怖場と獲物場 ---
        // 3つの場の違いは deposit 量と τ（拡散率・減衰率）だけで、
        // τ の違いがそのまま「その情報がどれくらい長く価値を持つか」を表す。
        //   植生 decay 0.02  … 植物は動かないので痕跡は長持ちしてよい
        //   恐怖 decay 0.03  … 危険は移動するので古い情報は価値が下がる
        //   獲物 decay 0.05  … 鮮度重視。獲物は動くので古い匂いは価値が下がる。
        //     一度 0.015 まで遅くしたが（匂いの到達距離を稼ぐため）、
        //     場が広がりすぎて情報量が落ちたため 0.05 に戻し、
        //     代わりに拡散を絞って局所化した。詳細は PreyField のコメント参照

        /// <summary>恐怖場: 狼が通過セルへ毎ティック書き込む量（上限1.0）。</summary>
        public float fearDeposit;

        /// <summary>恐怖場: 4近傍平均へ寄せる拡散率。小さめ＝「道」の形を保つ。</summary>
        public float fearDiffuse;

        /// <summary>恐怖場: 毎ティックの減衰率（τ中）。</summary>
        public float fearDecay;

        /// <summary>獲物場: 草食獣が通過セルへ毎ティック書き込む量（上限1.0）。</summary>
        public float preyDeposit;

        /// <summary>獲物場: 拡散率。大きめ＝匂いが広がって狼が遠くから方向を掴める。</summary>
        public float preyDiffuse;

        /// <summary>獲物場: 毎ティックの減衰率。</summary>
        public float preyDecay;

        /// <summary>
        /// 獲物場: 1ティックあたりの拡散パス数。
        /// 匂いが届く距離は概ね sqrt(D/decay)（D は1パスあたり拡散率/4）で決まる。
        /// 1パスでは1セル程度しか届かず、狼が方向を掴めない（実測で確認）。
        /// パスを重ねて到達距離を伸ばす。
        /// </summary>
        public int preyDiffusePasses;

        // --- Demo 8 第2段: 死の場 ---

        /// <summary>
        /// 死の場: 餓死した個体が残す量。死骸がそのまま残るので大きい。
        /// </summary>
        public float deathDepositStarved;

        /// <summary>
        /// 死の場: 捕食された個体が残す量。
        /// **餓死より小さい**理由: 食べられた個体は肉が持ち去られるので、
        /// その場に残る養分は餓死体より少ない。死因で量を変えることで
        /// 「どんな死が起きたか」まで場が記憶する。
        /// </summary>
        public float deathDepositPredated;

        /// <summary>死の場: 拡散率。死骸は動かないので最小限にする。</summary>
        public float deathDiffuse;

        /// <summary>死の場: 拡散パス数。広げたいときは拡散率でなくこちらを増やす。</summary>
        public int deathDiffusePasses;

        /// <summary>
        /// 死の場: 毎ティックの減衰率（τ特大＝長期記憶）。
        /// 植生0.02・恐怖0.03・獲物0.05 より桁違いに遅い。土に還った養分は長く残る。
        /// </summary>
        public float deathDecay;

        /// <summary>
        /// 死の場が植物スポーンを後押しする強さ (Demo 8 第2段 I2)。
        /// スポーン重み = suitability × max(植生, 床値) × (1 + k × 死の場)。
        /// これが「死骸が養分になる」経路そのもの。
        /// </summary>
        public float deathNutrientBoost;

        /// <summary>草食獣の移動評価: 植生場の重み（餌に寄る強さ）。</summary>
        public float herbivoreVegetationWeight;

        /// <summary>
        /// 草食獣の移動評価: 恐怖場の重み（危険を避ける強さ）。
        /// 植生の重みより大きくして危険回避を優先させる。両者の差が
        /// 「腹は減っているが危険な場所にある草」という葛藤の強さになる。
        /// </summary>
        public float herbivoreFearWeight;

        /// <summary>
        /// 狼の移動評価: 獲物場の重み。
        /// 狼は獲物場だけを追うので、他の場の重みは0。
        /// </summary>
        public float wolfPreyWeight;

        // --- 踏み荒らし場 (Demo 8 第3段 J1) ---

        /// <summary>動物が移動先のセルに残す踏み跡の量。</summary>
        public float trampleDeposit;

        /// <summary>踏み荒らし場の拡散率。踏み跡は歩いた筋そのものであるべきなので最小。</summary>
        public float trampleDiffuse;

        /// <summary>
        /// 踏み荒らし場の拡散パス数。**1から増やさないこと。**
        /// 死の場と同じく総量が「書き込み量 × τ」で頭打ちなので、
        /// 広げるほど1セルあたりの値が下がり、道の形が消える（第2段の実測）。
        /// </summary>
        public int trampleDiffusePasses;

        /// <summary>
        /// 踏み荒らし場の減衰率（τ中 ≈50ティック）。
        /// 「踏まれた草が回復する速さ」。恐怖0.03よりやや遅く、死0.003より大幅に速い。
        /// 通行が続くかぎり道は残り、通らなくなれば草が戻る。
        /// </summary>
        public float trampleDecay;

        /// <summary>
        /// 踏み荒らしが植物のスポーンを抑える強さ (Demo 8 第3段 J1)。
        /// スポーン重みに (1 - k × 踏み荒らし場) を掛ける。0未満にはクランプせず、
        /// <see cref="trampleSuppressionFloor"/> で下限を設けて回復の余地を残す。
        /// </summary>
        public float trampleSuppression;

        /// <summary>
        /// 踏み荒らしによる抑制の下限。完全に0にすると、一度踏まれた筋が
        /// 二度と草の生えない不可逆な傷になる。踏まれなくなれば戻る余地を残す。
        /// </summary>
        public float trampleSuppressionFloor;

        /// <summary>
        /// この値を超えた踏み荒らし場のセルで、既存の植物が踏み潰される確率
        /// (Demo 8 第3段 J1)。0 にすると「新しく生えない」だけになり、
        /// けもの道が見えるまでに植物の寿命ぶんの時間がかかる。
        /// </summary>
        public float trampleCrushThreshold;

        /// <summary>踏み潰しが起きる毎ティック確率（閾値を超えたセルの植物に対して）。</summary>
        public float trampleCrushChance;

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

        /// <summary>空腹の毎ティック進行量（1.0 で餓死）。草食獣に適用。</summary>
        public float hungerPerTick;

        /// <summary>
        /// 狼の空腹の毎ティック進行量 (Demo 5b)。
        ///
        /// 草食獣と分けた理由: 診断で**狼の死因が5シードとも100%餓死**（捕食されるわけでも
        /// 場が読めないわけでもない）と判明した。匂いは常に感知できていた（感知不能0.0%）ので、
        /// 律速は「方向は分かるが捕らえきれない」こと。狼は空腹閾値0.5を超えてから
        /// 餓死までの猶予が50ティックしかなく、その間に獲物へ追いつけずに死んでいた。
        ///
        /// 捕食者は大きな獲物を稀に食べる（草を少しずつ食べる草食獣とは食事の間隔が違う）ので、
        /// 空腹の進みを遅くするのが素直な表現になる。上限や場を触るより副作用が小さい。
        /// </summary>
        public float wolfHungerPerTick;

        /// <summary>繁殖可能な空腹上限（双方これ未満で繁殖候補になる）。</summary>
        public float breedHungerMax;

        /// <summary>隣接ペア成立時の毎ティック繁殖確率。</summary>
        public float breedChance;

        public static SimParams Default => new SimParams
        {
            // 10 だと植物が上限に張り付き続け（実測61%の時間）、場としての情報量が落ちる。
            // 5 にすると張り付きは34%まで下がり、平均個体数は189で維持される (Demo 5b)
            plantSpawnCandidates = 5,
            plantCap = 200,
            vegetationDeposit = 0.3f,
            vegetationDiffuse = 0.15f,
            vegetationDecay = 0.02f,
            vegetationFloor = 0.02f,
            fearDeposit = 0.5f,
            fearDiffuse = 0.1f,
            fearDecay = 0.03f,
            // 獲物場は「匂いが届く距離」で決まる。L = sqrt(passes*diffuse/4 / decay) ≈ 2.4セル。
            //
            // 当初は旧実装の視界半径6セルに合わせて L≈7.3 にしたが、実機で
            // 「平地のほとんどが青くなる」＝勾配が平坦化して場としての情報量が落ちる
            // 状態になった（実測: 高値セルが全体の92.5%）。
            // 掃引の結果このパラメータに変更した。高値面積 92.5% → 15.8% と局所化しつつ、
            // 捕食率は置換前の82%を維持している（M5 の基準50%を上回る）。
            preyDeposit = 0.3f,
            preyDiffuse = 0.4f,
            preyDiffusePasses = 3,
            preyDecay = 0.05f,
            deathDepositStarved = 1f,
            deathDepositPredated = 0.3f,
            deathDiffuse = 0.02f,
            deathDiffusePasses = 1,
            deathDecay = 0.003f,
            // k=1 では効果が測定できなかった（死の場は全体の1%未満にしか立たず、
            // 平均値が0.005程度なので重みがほぼ1倍のまま）。
            // 掃引の結果 20。48シード実測で墓場セルの植物密度の比が
            // 対照(k=0)の 0.348 から 0.523 へ（約1.5倍）。k=0/4/20 で単調に上がる。
            // ただし 1.0 は超えない（墓場はもともと餌の乏しい土地なので不利を背負う）
            deathNutrientBoost = 20f,
            herbivoreVegetationWeight = 1f,
            herbivoreFearWeight = 1.5f,
            wolfPreyWeight = 1f,

            // 踏み荒らし (Demo 8 第3段)。到達距離 L = sqrt(1 × 0.02/4 / 0.02) = 0.5セル
            // ＝ 実質にじまない。踏み跡は歩いた筋そのもの
            trampleDeposit = 0.35f,
            trampleDiffuse = 0.02f,
            trampleDiffusePasses = 1,
            trampleDecay = 0.02f,
            trampleSuppression = 1.2f,
            trampleSuppressionFloor = 0.1f,
            // 0.35 では届かない。3000t の実測分布は 中央値0.024 / 90%点0.257 /
            // 最大1.0 で、0.35 以上は適性セルの6.2%しかなく踏み潰しが年に数回になる
            // （3シード×3000tで15件）。0.10 は「通行のある域」23.5% に相当し、
            // 375件／3シードと目に見える頻度になる。植物総数は 205 のまま変わらない
            trampleCrushThreshold = 0.10f,
            trampleCrushChance = 0.02f,
            animalSpawnCandidates = 2,
            animalSpawnChance = 0.5f,
            animalCap = 30,
            animalSpawnCap = 20,
            wolfCap = 4,
            moveChance = 0.5f,
            turnChance = 0.2f,
            hungerPerTick = 0.01f,
            // 狼は草食獣の約1/3の速さで空腹になる (Demo 5b)。
            // 0.01 のままだと5シード中3で全滅していた（死因は100%餓死）
            wolfHungerPerTick = 0.003f,
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
