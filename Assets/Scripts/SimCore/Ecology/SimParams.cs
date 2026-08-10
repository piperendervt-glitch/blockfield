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

        // --- Demo 8.5: 植物の場化 ---
        // ここから下は段階0の時点では**まだ使われない**。
        // 摂食（段階1）→ 踏み潰しと養分（段階2）→ スポーンの置換（段階3）の
        // 順に配線していく。先に置くのは、既存の挙動を一切変えないまま
        // パラメータの意味と既定値を確定させ、段階ごとの差分を小さく保つため。

        /// <summary>
        /// 草食獣が1ティックに食べようとする植生場の量（Demo 8.5 K2）。
        /// 実際に食べられるのはそのセルにある分までで、
        /// <see cref="ScalarField.Consume"/> が返す「実際に減った量」が回復の元になる。
        ///
        /// 【0.5 の根拠】移行前は「植物1本を食べたら hunger=0」だった。
        /// 植生場は上限1.0で、草の茂ったセルはおおむね 0.5〜1.0 に達している
        /// （48シード実測の適性セルあたり平均は 0.216 だが、これは草の無いセルを
        /// 含めた平均であり、植物のあるセルの値ではない）。
        /// 一口 0.5 なら、茂ったセルでは移行前と同等の回復量になり、
        /// 痩せたセルでは部分的にしか回復しない。
        /// </summary>
        public float grazeBite;

        /// <summary>
        /// 食べた植生場1.0あたりの hunger 回復量（Demo 8.5 K2）。
        /// 1回の摂食で hunger が満タンぶん回復する（＝移行前の hunger=0 相当）ためには
        /// grazeBite × grazeRecovery ≧ 1.0 が要る。
        ///
        /// 【2.0 の根拠】grazeBite 0.5 との積がちょうど 1.0 になる。
        /// 「草が十分あるセルでは移行前と同じだけ回復し、
        /// 草が薄いセルでは比例して回復が減る」という設計。
        /// 餓死率が基準（2.126/個体/1000t）から外れるようならここを調整する。
        /// </summary>
        public float grazeRecovery;

        /// <summary>
        /// この値以上の植生場があるセルを「草がある」とみなす（Demo 8.5 K2）。
        /// 摂食対象を探すときの閾値。
        ///
        /// 【0.165 は実測で決めた値】
        /// この閾値は「餌場がどれだけ希少か」を決める。
        /// 段階1〜2の中間状態では植生場が「草そのもの」ではなく植物の周囲に
        /// にじんだ痕跡だったため 0.70 を使っていた（移行前の餌場は実質
        /// veg ≧ 0.76 だった）。段階3で植生場が草そのものになったので測り直した。
        ///
        /// 0.05 まで下げると餌場が適性セルの 99.5% になり「どこでも食べられる」
        /// 状態になって、餓死率が基準の 1/4 に落ちた。
        /// 掃引の実測（5シード×3000t、草食の目標中心18.78 / 餓死の目標中心2.126）:
        ///   0.150 → 餌場41.5% 草食16.6 餓死1.531
        ///   0.165 → 餌場33.3% 草食17.8 餓死1.770  ← 採用（中心に最も近い）
        ///   0.185 → 餌場26.2% 草食15.0 餓死2.012
        ///   0.250 → 餌場 7.6% 草食16.8 餓死3.288（移行前と同じ面積比だが上振れ）
        ///
        /// 移行前と同じ「餌場7%」にしても等価にならないのは、1回に食べられる量が
        /// 違うため（移行前は1本で満腹、移行後は 0.2 程度で部分回復）。
        /// 面積比ではなく生態の指標で合わせるのが正しい。
        /// </summary>
        public float grazeThreshold;

        /// <summary>
        /// 植生場の成長率（Demo 8.5 K1、段階3で使用）。
        /// 毎ティック、適性セルの植生場を
        /// 適性 × (床値 + 現在値) × 養分 × 踏み × この係数 だけ増やす。
        /// スポーン抽選（離散）を成長（連続）に置き換えるための係数。
        /// </summary>
        public float vegetationGrowth;

        /// <summary>
        /// ワールド生成時に適性セルへ入れておく草の量。
        /// 実際の初期値は `suitability × この値`（適性0のセルは0のまま）。
        /// **既定は 0**（従来どおりゼロから立ち上がる）。
        ///
        /// 【0 に戻した経緯 (2026-08-11)】実機で草が見えなかったとき、
        /// 「5分のセッションでは草が表示閾値に届かない」ことへの暫定対処として
        /// 0.13 を既定にした。しかし:
        /// - **ティック1から場が全面に広がる状態は、世界が立ち上がっていく
        ///   過程の観察を損なう。** これは本プロジェクトが見ようとしているもの
        ///   （場が育ち、痕跡が積もる過程）そのものを潰してしまう
        /// - 原因調査の結果、草が見えない真因は表示の最終段にあり、
        ///   閾値到達を早める必要は無かった（270ティックで108セル生成の実績）
        ///
        /// パラメータ自体は残す。「平衡状態から始めたい」場面
        /// （Demo 6 の不在中進行のテストなど）で使えるため。
        ///
        /// **RNG を消費しない**（suitability からの決定的な計算のみ）ので
        /// 決定論は保たれる。既に草があるセルは触らない。
        /// </summary>
        public float initialVegetation;

        /// <summary>
        /// この値以上の草があるセルを通行不可にする。**既定は 0（無効）。**
        ///
        /// 【診断用。Demo 8.5 の分離計測で占有索引の副作用を特定するために追加】
        /// 移行前は植物 Entity が占有索引に入っていたため、動物は植物のセルへ
        /// 入れなかった。これは「1セル1エンティティ」という実装上の制約から
        /// 生じた副作用であって、意図された仕様ではない。
        /// 植物が場になるとその阻害は消える。
        ///
        /// この入口で移行前の阻害を再現して測ったところ、狼の個体数が
        /// 6.2（阻害なし）→ 2.2（適性セルの48.6%を阻害）と目標値を跨いで動き、
        /// **占有索引からの植物の除外が狼の増加の主因**であることが確定した。
        /// 同時に、同じ面積を塞いでも「散らばった点」と「連続した塊」では
        /// 効果が全く違うことも分かった（7.6%の塊では狼5.8 のまま）。
        ///
        /// 通常の実行では使わない。Demo 9 以降で「深い草は歩きにくい」を
        /// 正式な機構として採用する判断があれば、この入口を使える。
        /// </summary>
        public float movementBlockVegetation;

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
        /// 死の場が草を育てる強さ (Demo 8 第2段 I2 → Demo 8.5 段階2 で場化)。
        /// 毎ティック、植生場に `この係数 × 死の場` を加える。
        ///
        /// 【移行前とは単位も意味も違う】移行前は
        /// スポーン重み = 適性 × max(植生, 床値) × (1 + k × 死の場) で、
        /// **抽選に当たりやすくする**係数だった（k=20）。
        /// 抽選には plantCap の上限があるため、実質は「どこに湧くか」の
        /// **再配分**であって総量は増えない。
        ///
        /// 成長率に移すと再配分ではなく**純増**になる。同じ k=20 を使うと
        /// 死の場0.05のセルが毎ティック +1.0 され、草が無限に湧く。
        /// 単位が変わった以上、値は測り直す必要がある。
        /// 第2段で k≧40 が狼を絶滅させた実績もあるため慎重に選ぶ。
        /// </summary>
        public float deathNutrientGrowth;

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
        /// 踏み潰しが働き始める踏み荒らし場の下限 (Demo 8 第3段 J1)。
        /// これを超えたセルで草が削られる。0 にすると「新しく生えない」だけになり、
        /// けもの道が見えるまでに草の寿命ぶんの時間がかかる。
        /// </summary>
        public float trampleCrushThreshold;

        /// <summary>
        /// 踏み潰しによる植生場の毎ティック減少率 (Demo 8.5 段階2 で意味が変わった)。
        ///
        /// 【もとは確率だった】移行前は `trampleCrushChance` という名前で、
        /// 「閾値を超えたセルにある植物 Entity をこの確率で消す」ものだった。
        /// 消えると植生場への書き込み（vegetationDeposit）が止まり、
        /// 場が減衰で薄れる、という**間接的な**効果である。
        ///
        /// 植物が場になると「1本消す」が成立しないため、
        /// **その期待値を連続量で表した掛け算**に置き換えた:
        ///   植生場 ×= (1 - このレート)
        /// 名前を Chance から Rate に変えたのは、意味が確率でなくなったため。
        /// 名前が嘘をつくと段階3以降で読む人を誤らせる。
        ///
        /// RNG を使わない形を選んだ。踏み潰しが他の乱数列に干渉しなくなり、
        /// 変更の切り分けが楽になる。処理も個体数に依存しない O(セル数) になった。
        /// </summary>
        public float trampleCrushRate;

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

            // Demo 8.5（植物の場化）。段階0では未使用
            grazeBite = 0.5f,
            grazeRecovery = 2f,
            // 段階3で実測から決め直した。0.05 では餌場が適性セルの99.5%になり
            // 「どこでも食べられる」状態になって餓死が基準の1/4に落ちた。
            // 0.165 は餌場33%で、草食獣17.8・餓死率1.770 と合格範囲の中心に最も近い
            grazeThreshold = 0.165f,
            // ロジスティック成長の係数。釣り合いは 1 - 減衰率/成長率 = 1 - 0.02/0.028 = 0.29 で、
            // 摂食に食われるぶん実測は 0.217 に落ち着く（移行前の 0.2118 と一致）
            vegetationGrowth = 0.028f,
            initialVegetation = 0f, // 既定は0。経緯はフィールドのコメント参照
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
            // Demo 8.5 段階2 で成長率へ移した。単位が変わったので測り直した値
            // （抽選の倍率 20 とは無関係）。死の場0.5のセルで毎ティック +0.025、
            // 減衰0.02 との釣り合いで植生1.0 に達する＝濃い墓場は草で覆われる。
            // 死の場0.05（墓場の下限）なら +0.0025 で釣り合いは 0.125 に留まる
            deathNutrientGrowth = 0.05f,
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
            trampleCrushRate = 0.02f,
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
