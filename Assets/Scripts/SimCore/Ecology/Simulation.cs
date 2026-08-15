using System;
using System.Collections.Generic;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// シムティック (Demo 2 D1/D3/D4 + Demo 3 E1-E4)。ワールド状態への決定論的な更新の入口。
    ///
    /// ティック内の処理順は固定（Demo 8 第4段 4a で実際の実装に合わせて書き直した。
    /// 「植物スポーン」は Demo 8.5 で草が場になったときに消えている）:
    ///   パラメータの解決 → (最初のティックのみ) 草の初期値 →
    ///   プレイヤー操作適用 → 動物スポーン →
    ///   植生の成長と結合の適用 → 全ての場の更新（拡散・減衰） →
    ///   草食獣（摂食/餓死/移動）→ 狼（餓死/捕食/追跡）→ 踏み潰し → 繁殖 →
    ///   コロニー場への滞在の書き込み → 個体数の記録 → ティック加算
    ///
    /// このうち **RNG を消費するのは 動物スポーン → 草食獣 → 狼 → 繁殖 の4つだけ**で、
    /// 場の更新・踏み潰し・コロニー場への書き込み・草の初期値・プレイヤー操作は消費しない。
    /// 消費順がこの順で固定されるため、同一シード＋同一ティック数で同一結果になる。
    /// </summary>
    public static class Simulation
    {
        // 空腹・繁殖の定数 (Demo 3)。可変にする必要が出たものは SimParams へ昇格済み
        const float k_HungerActionThreshold = 0.5f;
        const float k_BreedCost = 0.3f;

        /// <summary>この値以上の恐怖場を「避ける対象」とみなす (Demo 8 第2段 M3 の測定用)。</summary>
        const float k_FearRelevantThreshold = 0.05f;
        const int k_BreedCooldownTicks = 20;
        // k_WolfSightRadius は Demo 8 H3 で削除した。
        // 「視界半径」という個体側の概念そのものが、獲物場の拡散距離に置き換わったため
        const float k_WolfSpawnShare = 0.15f;

        /// <summary>facing (0..3) → 移動方向 (+X, +Z, -X, -Z)。</summary>
        public static readonly Int3[] FacingDirections =
        {
            new Int3(1, 0, 0),
            new Int3(0, 0, 1),
            new Int3(-1, 0, 0),
            new Int3(0, 0, -1),
        };

        public static void Tick(World world, Mulberry32 rng)
        {
            Tick(world, rng, SimParams.Default);
        }

        public static void Tick(World world, Mulberry32 rng, SimParams p)
        {
            // Demo 5a: 個体数まわりはワールドの広さ（適性セル数）に比例させる。
            // 呼び出し側は基準スケールの値を渡すだけでよい。整数演算なので決定論は不変
            p = p.Resolve(world.SuitableCellCount);

            // 最初のティックで草の初期値を入れる (Demo 8.5、暫定対処)。
            // World.Create ではなくここで行うのは、初期値が SimParams にあり
            // World は TerrainParams しか受け取らないため。
            // RNG を消費しないので決定論は保たれる
            if (world.TickCount == 0)
            {
                SeedInitialVegetation(world, p);
            }

            // Demo 4 F2: プレイヤー操作はティック先頭で適用（RNG非消費 → リプレイ決定論を保つ）
            world.ApplyPendingActions();

            SpawnAnimals(world, rng, p);
            UpdateVegetation(world, p);
            UpdateHerbivores(world, rng, p);
            UpdateWolves(world, rng, p);
            CrushTrampledGrass(world, p);
            Breed(world, rng, p);
            DepositColonyPresence(world, p);

            world.PopulationLog.Record(world);
            world.TickCount++;
        }

        /// <summary>
        /// 草の成長 (Demo 8.5 K1。移行前の SpawnPlants の置き換え)。
        ///
        /// 移行前は「毎ティック数セルを抽選し、確率 suitability × max(植生, 床値) で
        /// 植物 Entity を1つ湧かせる」だった。植物が場になったので、
        /// **抽選をやめて全ての適性セルの植生場を増やす**形になる。
        /// 抽選が無くなったぶん RNG を消費しない。
        ///
        /// 成長量 = 成長率 × 適性 × max(現在の草, 自然発生率) × **(1 - 現在の草)** × 踏み荒らしの抑制
        ///
        /// max(現在の草, 自然発生率) の形は移行前と同じで、
        /// 「草のある所ほど増える（自己増殖）／無い所でもごく稀に生える」を表す。
        ///
        /// 【(1 - 現在の草) が要る理由 — 設計上の落とし穴】
        /// 最初は素直に `成長率 × 草` としたが、これは**破綻する**。
        /// 成長も減衰(<see cref="SimParams.vegetationDecay"/>)もどちらも草の量に
        /// 比例するため、両者の比だけで結果が決まり、**内部の釣り合い点が無い**:
        ///   成長率 > 減衰率 → 際限なく増えて世界が草で埋まる
        ///   成長率 < 減衰率 → 消滅する
        /// 実測でも 0.02 で平均0.134、0.05 で平均0.907（全セルが草）と、
        /// 0.02〜0.05 のあいだで振る舞いが切り替わってしまった。
        ///
        /// 移行前にこれが起きなかったのは <see cref="SimParams.plantCap"/> が
        /// **暗黙の安定装置**だったからである。本数に上限があるので、
        /// 抽選確率をいくら上げても総量は頭打ちになっていた。
        /// 場にすると本数が無いのでその歯止めも消える。
        ///
        /// そこでロジスティック型（混み合うほど増えにくい）にした。
        /// 釣り合い点は 草* = 1 - 減衰率 / (成長率 × 適性 × 踏み荒らしの抑制) で、
        /// **成長率で狙った密度に調整できる**ようになる。
        ///
        /// <see cref="SimParams.plantSpawnCandidates"/> と
        /// <see cref="SimParams.plantCap"/> はここでは使わない
        /// （抽選も本数の上限も存在しなくなったため）。
        /// </summary>
        /// <summary>
        /// 草の初期値を適性セルへ入れる (Demo 8.5、暫定対処)。
        ///
        /// ロジスティック成長は立ち上がりが緩やかで、0から始めると平衡に
        /// 達するまで約1,500ティックかかる。実機セッションは5分＝約400ティック
        /// しかなく、その時点では草が表示閾値に届かない
        /// （実機で草が1つも見えなかった原因のひとつ）。
        ///
        /// 適性に比例させるのは、適性0のセル（壁・穴）に草を置かないため。
        /// **RNG を消費しない**ので決定論は保たれる。
        /// </summary>
        static void SeedInitialVegetation(World world, SimParams p)
        {
            if (p.initialVegetation <= 0f)
            {
                return;
            }

            var cells = world.SuitableCellIndices;
            for (int i = 0; i < cells.Length; i++)
            {
                int c = cells[i];

                // **既に草があるセルは触らない。**
                // 初期化は最初のティックで走るため、World.Create のあとに
                // 呼び出し側が置いた草（テストの舞台づくりや、将来の
                // セーブデータ読み込み）を上書きしてしまう。
                // 「まだ何も無いセルに初期値を入れる」という意味に限定する
                if (world.Vegetation.GetByIndex(c) > 0f)
                {
                    continue;
                }

                float v = world.Suitability.GetByIndex(c) * p.initialVegetation;
                world.Vegetation.SetByIndex(c, v > 1f ? 1f : v);
            }
        }

        /// <summary>
        /// 解決済みの結合 (Demo 8 第4段 K2)。<see cref="FieldId"/> を場の参照に
        /// 変換したもの。セルのループの中で識別子から場を引き直さないための形。
        /// </summary>
        readonly struct BoundCoupling
        {
            public readonly ScalarField source;
            public readonly float coefficient;

            public BoundCoupling(ScalarField source, float coefficient)
            {
                this.source = source;
                this.coefficient = coefficient;
            }
        }

        /// <summary>
        /// 指定の場を target とする結合のうち、形が一致するものを場の参照に解決する
        /// (Demo 8 第4段 K2)。**並び順は <see cref="SimParams.couplings"/> のまま**で、
        /// これがそのまま適用順＝浮動小数の演算順になる。
        /// </summary>
        static BoundCoupling[] BindCouplings(
            World world, FieldCoupling[] couplings, FieldId target, CouplingForm form)
        {
            int n = 0;
            for (int i = 0; i < couplings.Length; i++)
            {
                if (couplings[i].target == target && couplings[i].form == form)
                {
                    n++;
                }
            }

            var bound = new BoundCoupling[n];
            int k = 0;
            for (int i = 0; i < couplings.Length; i++)
            {
                var c = couplings[i];
                if (c.target == target && c.form == form)
                {
                    bound[k++] = new BoundCoupling(world.GetField(c.source), c.coefficient);
                }
            }
            return bound;
        }

        static void GrowVegetation(World world, SimParams p)
        {
            // 【結合行列 (Demo 8 第4段 K2)】「死の場が育てる」「踏み荒らしが抑える」を
            // ここに直書きするのをやめ、**自分 (植生) を target とする結合を全て適用する**
            // という1つの規則にした。場が増えても触るのは SimParams の結合リストだけになる。
            //
            // 移設は純粋なリファクタリングであり、計算式も適用順も変えていない
            // （判定 M0a: 48シードで ContentHash が移設前と完全一致）。
            var couplings = p.ResolveCouplings();
            var suppressors = BindCouplings(
                world, couplings, FieldId.Vegetation, CouplingForm.GrowthSuppress);
            var boosters = BindCouplings(
                world, couplings, FieldId.Vegetation, CouplingForm.GrowthBoost);

            bool grow = p.vegetationGrowth > 0f;

            // 移設前の `bool nutrient = p.deathNutrientGrowth > 0f` の一般化。
            // 係数0の促進結合は値を1ビットも変えない（current + 0×source = current）ので、
            // 全ての促進が0なら成長と合わせて丸ごと飛ばせる
            bool boost = false;
            for (int i = 0; i < boosters.Length; i++)
            {
                if (boosters[i].coefficient > 0f)
                {
                    boost = true;
                    break;
                }
            }
            if (!grow && !boost)
            {
                return;
            }

            // 【走査の最適化 (Demo 8.5 段階3)】
            // 成長と養分は当初それぞれ全セルを回していたが、どちらも
            // **近傍を読まず自セルだけで完結する**ので、1周にまとめても
            // 結果は1ビットも変わらない（順序が問題になるのは近傍を読むとき）。
            // さらに適性セルの索引列を使い、「適性を引いて0なら飛ばす」を省く。
            // 索引は (z, x) の走査順のままなので演算順も変わらない。
            var cells = world.SuitableCellIndices;
            var vegetation = world.Vegetation;
            var suitabilityField = world.Suitability;

            for (int i = 0; i < cells.Length; i++)
            {
                int c = cells[i];
                float current = vegetation.GetByIndex(c);

                if (grow)
                {
                    float room = 1f - current;
                    if (room > 0f)
                    {
                        // 抑制結合: 成長量に (1 - 係数 × source) を掛ける
                        // （踏み荒らし→植生がこれ。Demo 8 第3段 J1）。
                        // 下限を設けるのは、一度踏まれた筋が二度と草の生えない
                        // 不可逆な傷になるのを避けるため。
                        // 初期値 1f から掛けていくのは、結合が1本のときに
                        // 1f × s = s と厳密に一致する（IEEE754 で1倍は誤差なし）ため
                        float suppression = 1f;
                        for (int k = 0; k < suppressors.Length; k++)
                        {
                            float s = 1f - suppressors[k].coefficient
                                * suppressors[k].source.GetByIndex(c);
                            if (s < p.trampleSuppressionFloor)
                            {
                                s = p.trampleSuppressionFloor;
                            }
                            suppression *= s;
                        }

                        float grown = current + p.vegetationGrowth * suitabilityField.GetByIndex(c)
                            * Math.Max(current, p.vegetationFloor) * room * suppression;
                        current = grown > 1f ? 1f : grown;
                    }
                }

                // 促進結合: 係数 × source をそのまま足す
                // （死の場→植生の養分がこれ。Demo 8 第2段 I2）
                for (int k = 0; k < boosters.Length; k++)
                {
                    float coefficient = boosters[k].coefficient;
                    if (coefficient <= 0f)
                    {
                        continue;
                    }
                    float source = boosters[k].source.GetByIndex(c);
                    if (source > 0f)
                    {
                        float fed = current + coefficient * source;
                        current = fed > 1f ? 1f : fed;
                    }
                }

                vegetation.SetByIndex(c, current);
            }
        }

        /// <summary>動物スポーン: suitability 1.0 のセルのみ。狼は低頻度（Sheep:Pig = 1:1、Wolf は別枠上限）。</summary>
        static void SpawnAnimals(World world, Mulberry32 rng, SimParams p)
        {
            // 野生スポーンは animalSpawnCap で止まり、animalCap までの余裕は出生（繁殖）用
            if (world.AnimalCount >= p.animalSpawnCap)
            {
                return;
            }

            for (int i = 0; i < p.animalSpawnCandidates; i++)
            {
                int x = rng.Range(0, world.Width);
                int z = rng.Range(0, world.Depth);

                if (world.Suitability.GetAtColumn(x, z) >= 1f && rng.NextFloat01() < p.animalSpawnChance)
                {
                    EntityKind kind;
                    if (rng.NextFloat01() < k_WolfSpawnShare)
                    {
                        if (world.WolfCount >= p.wolfCap)
                        {
                            continue; // 狼枠が埋まっている場合はこの候補は不発
                        }
                        kind = EntityKind.Wolf;
                    }
                    else
                    {
                        kind = rng.Range(0, 2) == 0 ? EntityKind.Sheep : EntityKind.Pig;
                    }

                    int facing = rng.Range(0, 4);
                    // 野生スポーンはパラメータの初期重みを個体へ写す (Demo 8 第3段 J2)
                    world.TrySpawn(kind, x, z, facing, p);
                    if (world.AnimalCount >= p.animalSpawnCap)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 場の更新 (E1): 植物存在セルへ書き込み → 全ての場を更新（拡散・減衰）。
        /// 更新ループは World.UpdateFields が場の種類を知らずに回す（Demo 4.5 作業1）。
        /// </summary>
        static void UpdateVegetation(World world, SimParams p)
        {
            // 移行前はここで「植物 Entity のセルに vegetationDeposit を書く」
            // ループを回していた。植物が場になったので、その間接は消えて
            // 場が自分で育つ形になった (Demo 8.5 K1)
            // 成長と養分は GrowVegetation に統合した（どちらも自セルだけで完結するため）
            GrowVegetation(world, p);
            world.UpdateFields(p);
        }

        /// <summary>
        /// 通った跡を残す (Demo 8 第3段 J1)。**実際に移動したときだけ**書く。
        /// 立ち止まっている個体が同じセルを踏み固め続けると、
        /// 「通り道」ではなく「滞留した場所」が濃くなり、けもの道にならない。
        /// </summary>
        static void DepositTrample(World world, SimParams p, Int3 cell)
        {
            if (!world.InBounds(cell.x, cell.z))
            {
                return;
            }
            world.Trample.Deposit(cell, p.trampleDeposit);
        }

        /// <summary>
        /// 踏み潰し (Demo 8 第3段 J1 → Demo 8.5 段階2 で場化)。
        /// 踏み荒らし場が閾値を超えたセルの**植生場を掛け算で減らす**。
        ///
        /// 【実装した理由】スポーン抑制だけだと「新しく生えない」だけになり、
        /// 既にある草が消えるのを草の寿命（＝草食獣に食われるまで）待つことになる。
        /// けもの道が見えるまでの時間が M1（実機5分セッションでの目視）に対して
        /// 長すぎる。踏み潰しを入れると、通行が始まってから数十ティックで
        /// 筋が見え始める。「歩いたら草が消える」は直感にも合う。
        ///
        /// 【場化で変わったこと】移行前は植物 Entity を走査して確率で消していた。
        /// 場になると「1本消す」が成立しないので、その期待値を連続量で表した
        /// 掛け算に置き換えた。個体を1つも見なくなり、処理は個体数に依存しない
        /// O(セル数) になった（Demo 8.5 M1 に寄与）。RNG も消費しない。
        ///
        /// <see cref="World.TrampleCrushCount"/> の意味も変わった。
        /// 「消した植物の本数」ではなく「草を削ったセルの延べ数」である。
        /// </summary>
        static void CrushTrampledGrass(World world, SimParams p)
        {
            if (p.trampleCrushRate <= 0f)
            {
                return;
            }

            // 全セルを回す（適性0のセルにも拡散で草が届くので対象から外せない）。
            // 平坦インデックスで回して GetAtColumn の境界チェックを省く
            float keep = 1f - p.trampleCrushRate;
            var vegetation = world.Vegetation;
            var trample = world.Trample;
            int n = vegetation.Length;
            int crushed = 0;

            for (int i = 0; i < n; i++)
            {
                if (trample.GetByIndex(i) < p.trampleCrushThreshold)
                {
                    continue;
                }
                float v = vegetation.GetByIndex(i);
                if (v <= 0f)
                {
                    continue;
                }
                vegetation.SetByIndex(i, v * keep);
                crushed++;
            }
            world.TrampleCrushCount += crushed;
        }

        /// <summary>
        /// 草食獣 (E2): hunger 進行 → 餓死 / 摂食（隣接植物）/ 植生場勾配の場読み移動 / 通常徘徊。
        /// 削除はフェーズ末尾でまとめて適用する（イテレーション中のインデックス安定のため）。
        /// </summary>
        static void UpdateHerbivores(World world, Mulberry32 rng, SimParams p)
        {
            var dead = new HashSet<int>();

            for (int i = 0; i < world.Entities.Count; i++)
            {
                var e = world.Entities[i];
                if (!e.IsHerbivore)
                {
                    continue;
                }

                e.hunger += p.hungerPerTick;
                if (e.hunger >= 1f)
                {
                    dead.Add(e.id);
                    world.StarvationCount++;
                    DepositDeath(world, p, e.cell, starved: true);
                    world.UpdateEntity(i, e);
                    continue;
                }

                if (e.hunger > k_HungerActionThreshold)
                {
                    // 摂食モード: 自セル＋4近傍の「草のあるセル」を固定順で探す
                    // (Demo 8.5 段階1)。探すのは植物 Entity ではなく**場の値**
                    bool grazed = TryGraze(world, p, e.cell, out float taken);

                    // 診断用の統計。導出値なので ContentHash には含めず、RNG も消費しない
                    world.FeedAttemptCount++;

                    if (grazed)
                    {
                        world.FeedSuccessCount++;

                        // 食べた**量に比例して**回復する。草の薄いセルでは
                        // 満腹にならない — これが摂食を連続量にした意味そのもの
                        e.hunger -= taken * p.grazeRecovery;
                        if (e.hunger < 0f)
                        {
                            e.hunger = 0f;
                        }
                    }
                    else
                    {
                        // 場読み: 「餌に寄りたい」と「危険を避けたい」を1つのスコアで合成する
                        e.facing = FindForagingFacing(world, e.forageWeights, e.cell, e.facing);
                        MoveHerbivore(world, rng, p, ref e);
                    }
                }
                else
                {
                    // 満腹: ランダム徘徊。ただし**危険は空腹でなくても避ける** (Demo 8 第2段 I3)。
                    // 第1段では恐怖場を摂食モードのときしか読まず、読む時間が半分しか
                    // なかったことが迂回行動を定量できなかった最大の原因だった
                    if (rng.NextFloat01() < p.turnChance)
                    {
                        e.facing = rng.Range(0, 4);
                    }
                    e.facing = FindWanderFacing(world, e.wanderWeights, e.cell, e.facing);
                    MoveHerbivore(world, rng, p, ref e);
                }

                // 獲物場への書き込み (Demo 8 H3)。移動後の位置に匂いを残す。
                // 狼はこの痕跡だけを頼りに追跡する（個体を探す処理は無くなった）
                if (world.InBounds(e.cell.x, e.cell.z))
                {
                    world.Prey.Deposit(e.cell, p.preyDeposit);
                }

                world.UpdateEntity(i, e);
            }

            world.RemoveEntities(dead);
        }

        /// <summary>
        /// 狼 (E3 → Demo 8 H3): hunger 進行 → 餓死 / 捕食モード（獲物場の勾配を追う。
        /// 隣接に草食獣がいれば捕食）/ 見つからなければランダム徘徊。
        ///
        /// Demo 8 で「視界内の全個体を走査して最近接を選ぶ」処理を獲物場の読み出しに
        /// 置き換えた。狼は獲物の位置を知らず、匂いの濃い方へ進むだけである。
        /// </summary>
        static void UpdateWolves(World world, Mulberry32 rng, SimParams p)
        {
            var dead = new HashSet<int>();

            for (int i = 0; i < world.Entities.Count; i++)
            {
                var e = world.Entities[i];
                if (e.kind != EntityKind.Wolf)
                {
                    continue;
                }

                // 狼は草食獣より空腹の進みが遅い (Demo 5b)。捕食者は大きな獲物を稀に食べる
                e.hunger += p.wolfHungerPerTick;
                if (e.hunger >= 1f)
                {
                    dead.Add(e.id);
                    world.StarvationCount++;
                    DepositDeath(world, p, e.cell, starved: true);
                    world.UpdateEntity(i, e);
                    continue;
                }

                if (e.hunger > k_HungerActionThreshold)
                {
                    // 隣接の草食獣は捕食する。この判定だけは個体を見る（4近傍の固定順、O(1)）
                    int preyIndex = FindAdjacentHerbivore(world, e.cell, dead);
                    if (preyIndex >= 0)
                    {
                        var prey = world.Entities[preyIndex];
                        dead.Add(prey.id);
                        world.PredationCount++;
                        DepositDeath(world, p, prey.cell, starved: false);
                        e.hunger = 0f;
                    }
                    else
                    {
                        // 場読み: 獲物場の濃い方へ向く（決定論、RNG不使用）。
                        // 匂いが全く無ければ現在の向きのまま＝ランダム徘徊に落ちる
                        e.facing = FindPreyGradientFacing(world, e.forageWeights, e.cell, e.facing);
                        TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);
                    }
                }
                else
                {
                    if (rng.NextFloat01() < p.turnChance)
                    {
                        e.facing = rng.Range(0, 4);
                    }

                    // 満腹時は重みを読んで向きを決める (Demo 8 第4段 K4)。
                    // 移行前の狼は徘徊で場を一切読まなかったので、このままでは
                    // **狼だけが群れられない**（prereg 判断1 は狼のパックを狙っている）。
                    // 草食獣の徘徊と同じ形に揃えた。RNG は消費しない。
                    //
                    // 【w_colony=0 でも完全に同一にはならない — 世界の縁だけ】
                    // 狼の徘徊時の重みはコロニー場以外すべて0なので、盤面の内側では
                    // 現在の向きが必ず勝ち（wanderBias 0.15 対 0）挙動は変わらない。
                    // ただし**現在の向きが盤外を指しているとき**、移行前は向きを保ったまま
                    // TryMove に入って弾かれていたのに対し、いまは盤内の方向へ向き直る
                    // （草食獣と同じ振る舞い）。この1点だけで乱数列がずれるので、
                    // w_colony=0 の世界は 4b とハッシュ一致しない。
                    // 実測の差（48シード）: 草食獣 15.32→16.06 / 狼 5.69→4.96 /
                    // 捕食 1.196→1.064 — いずれも 4a の基準値側へ寄る。
                    // M4 の対照は同じコードで w だけを変えるので、この差は判定に影響しない
                    e.facing = FindWanderFacing(world, e.wanderWeights, e.cell, e.facing);
                    TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);
                }

                // 恐怖場への書き込み (Demo 8 H1)。移動後の位置に危険の痕跡を残す。
                // これが積もって「けもの道」になり、草食獣の迂回行動を生む
                if (world.InBounds(e.cell.x, e.cell.z))
                {
                    world.Fear.Deposit(e.cell, p.fearDeposit);
                }

                if (e.cell != world.Entities[i].cell)
                {
                    // 診断用の統計（導出値、ハッシュ非対象）: 何歩歩いたか
                    world.WolfStepCount++;

                    // 通った跡を残す (Demo 8 第3段 J1)。狼も草を踏む
                    DepositTrample(world, p, e.cell);
                }

                world.UpdateEntity(i, e);
            }

            world.RemoveEntities(dead);
        }

        /// <summary>
        /// 繁殖 (E4 → Demo 8 第4段 K3 で場化)。自種の生活圏に居る個体が子を1つ産む。
        ///
        /// 【K3 で何が変わったか】移行前は「隣接4近傍に条件を満たす同種個体がいるか」を
        /// 走査していた（<c>FindBreedPartner</c>）。それを
        /// **自セルの自種コロニー場による繁殖確率の変調**に置き換えた。
        ///   実効確率 = breedChance × colony / (colony + <see cref="SimParams.colonyBreedK"/>)
        /// 個体は相手の位置を知らず、自分の足元の濃さだけを見る。
        /// 獲物場で狼の視界走査を消したのと同じ形の置換である。
        ///
        /// 可否のゲート（colony ≥ 閾値）ではなく確率の変調にしたのは、
        /// **ゲート方式が実測で破綻したため**である（詳細は
        /// <see cref="SimParams.colonyBreedK"/>）。場は時間積分なので、
        /// 旧判定が要求していた「同時性」を 0/1 の判定には落とせない。
        ///
        /// 生態的な意味は変わっている: 有性生殖（相手が要る）から
        /// **場を介した繁殖**（生活圏の濃さが確率を決める）になった。
        /// 相手個体が居なくなったので、繁殖コストは産んだ個体にだけ課される。
        ///
        /// 【判定と RNG 消費の順】個体ごとに id 昇順で、
        ///   1. クールダウン &gt; 0 なら 1 減らして終わり（RNG 非消費）
        ///   2. 自分の hunger が <see cref="SimParams.breedHungerMax"/>（既定 0.4）以上なら不成立
        ///   3. 自セルの自種コロニー場を読んで実効確率を作る（**場読み**、RNG 非消費）
        ///   4. その実効確率で抽選 ← **最初の RNG 消費**（変調しても消費は1個のまま）
        ///   5. <see cref="SimParams.animalCap"/> の判定
        ///   6. 隣接空きセルを固定順で探し、見つかるまで <see cref="World.TrySpawn"/>
        ///      （子の向きに 1 回ずつ RNG を消費する。**試行ごとに消費する**ので、
        ///       塞がっている方向があるとその分だけ余分に消費される）
        /// この順序は乱数列そのものなので、判定を1つ入れ替えるだけで世界が別物になる。
        /// K3 は 3〜4 を入れ替えたので、**移行の前後で ContentHash は比較できない**
        /// （移行前は相手のいる個体しか抽選しなかったが、いまは資格のある個体が
        /// 全員抽選するので消費数そのものが変わる。prereg で確定済み）。
        ///
        /// 成立すると産んだ個体に hunger +0.3 と 20 ティックのクールダウン、
        /// 子にも 20 ティックのクールダウン（即時繁殖の防止）が入る。
        ///
        /// 【消えた非対称】移行前はペアの低い id 側だけが処理し、相手（高い id 側）は
        /// 同じティックのループでこの後に訪問されて入ったばかりのクールダウンが 1 減る、
        /// という 1 ティックのずれがあった。相手という概念が無くなったので
        /// **この非対称も消えた**（prereg に「本段では修正しない」と書いた既存挙動だが、
        /// 修正したのではなく前提ごと無くなった）。
        ///
        /// ループは繁殖前の個体数までしか回さないので、このティックで生まれた子は
        /// 走査対象にならない（新生児は次ティックから行動する）。
        /// </summary>
        static void Breed(World world, Mulberry32 rng, SimParams p)
        {
            int countBeforeBirths = world.Entities.Count;
            for (int i = 0; i < countBeforeBirths; i++)
            {
                var e = world.Entities[i];
                if (!e.IsAnimal)
                {
                    continue;
                }

                if (e.breedCooldown > 0)
                {
                    e.breedCooldown--;
                    world.UpdateEntity(i, e);
                    continue;
                }

                if (e.hunger >= p.breedHungerMax)
                {
                    continue;
                }

                // 場読み (Demo 8 第4段 K3)。相手個体の探索はここで消えた。
                // 個体が見るのは自分の足元の自種コロニー場の濃さだけである。
                //
                // 可否のゲートではなく**確率の変調**にしてある。ゲート方式は
                // 実測で破綻した（単独個体が自分の痕跡で繁殖する／閾値を上げると
                // 双安定になる。SimParams.colonyBreedK のコメント参照）。
                // 場は時間積分なので、旧判定が要求していた「同時性」を
                // 0/1 の判定には落とせない。
                //
                // 乱数は**追加で消費しない**。既存の breedChance 判定の1個を
                // そのまま使い、比べる相手の確率を変調するだけである
                float colony = world.Colony(e.kind).GetAtColumn(e.cell.x, e.cell.z);
                float breedChance = p.breedChance * colony / (colony + p.colonyBreedK);

                if (rng.NextFloat01() >= breedChance)
                {
                    continue;
                }

                if (world.AnimalCount >= p.animalCap)
                {
                    continue;
                }

                // 隣接空きセル（高低差1以下）を固定順で探す
                int childId = -1;
                for (int f = 0; f < FacingDirections.Length && childId < 0; f++)
                {
                    var dir = FacingDirections[f];
                    int nx = e.cell.x + dir.x;
                    int nz = e.cell.z + dir.z;
                    if (!world.InBounds(nx, nz))
                    {
                        continue;
                    }
                    if (Math.Abs(world.GetSurfaceHeight(nx, nz) - e.cell.y) > 1)
                    {
                        continue;
                    }
                    // 親の重みをそのまま継承する (Demo 8 第3段 J2)。
                    // 変異は入れない — 本段は構造の移管までで、進化本体は実装しない。
                    // 変異を入れるならこの1行に乱数を足すだけで済む形にしてある
                    childId = world.TrySpawn(
                        e.kind, nx, nz, rng.Range(0, 4), e.forageWeights, e.wanderWeights);
                }

                if (childId < 0)
                {
                    continue;
                }

                world.BirthCount++;

                // 新生児にもクールダウン（即時繁殖の防止）
                int childIndex = world.Entities.Count - 1;
                var child = world.Entities[childIndex];
                child.breedCooldown = k_BreedCooldownTicks;

                // 変異 (Demo 8 第4.5段 K1)。**向き(TrySpawn の中)の直後**に置く。
                // prereg で「RNG 消費順は facing → 変異」に固定した順序である
                MutateChildWeights(ref child, rng, p);

                world.UpdateEntity(childIndex, child);

                // コロニー場への書き込み (Demo 8 第4段 K1)。出生セルへ自種の場だけを書く。
                // 死の場・踏み荒らし場と同じく「何が起きた場所か」を空間が覚える形で、
                // ここに残るのは**次の世代が生まれた**という出来事である。
                // 4a では誰も読まない（読むのは 4b の繁殖判定と 4c の群れ行動）
                DepositColony(world, p, child.kind, child.cell);

                // 繁殖コスト＋クールダウン。**産んだ個体にだけ**課す (Demo 8 第4段 K3)。
                // 場化で相手個体が居なくなったので、旧実装が親Bへ課していた分は
                // 課す対象そのものを失った。近傍の同種を1体選んで課す案もあったが、
                // それは消したはずの個体探索を復活させることになり本末転倒である。
                // 結果として1回の出生あたりの繁殖コストは 0.6 → 0.3 に半減する。
                // これは生態への実質的な変更なので M2（餓死率・個体数）で監視する
                e.hunger += k_BreedCost;
                e.breedCooldown = k_BreedCooldownTicks;
                world.UpdateEntity(i, e);
            }
        }

        /// <summary>
        /// 子の重みに変異を加える (Demo 8 第4.5段 K1)。
        /// 採餌時と徘徊時の**両方**の重みが対象（prereg K3）。
        ///
        /// 【乱数の消費 — 決定論の要】
        /// 変異が無効（<see cref="SimParams.mutationRate"/> か
        /// <see cref="SimParams.mutationSigma"/> が 0）のときは**1個も引かない**。
        /// 引いて捨てる形にすると、変異を入れる前の世界と ContentHash が
        /// 一致しなくなり、「変異なしなら完全に同じ」という M 判定の前提が壊れる。
        ///
        /// 有効なときは **1成分あたり必ず3個**引く（消費数が分岐に依存しない）:
        ///   1個目 = 変異するかの抽選 / 2〜3個目 = ガウス乱数の種
        /// 「変異しない成分は引かない」にすると、抽選の結果で消費数が変わり、
        /// 同じシードでも個体の履歴が違えば乱数列がずれる。
        /// 引いてから捨てることで、消費数が
        /// **2（採餌・徘徊）× 9成分 × 3 = 54個/出生**に固定される。
        ///
        /// 【なぜ Box-Muller か】極座標法（Marsaglia）は棄却を伴うので
        /// 消費数が可変になり、上の固定を壊す。Box-Muller は
        /// 一様乱数2個から必ず1個の正規乱数が出る（2個目の sin 側は捨てる。
        /// 捨てた値を次回に持ち越すとキャッシュが状態になり、
        /// ContentHash に含まれない隠れ状態を作ってしまう）。
        /// </summary>
        static void MutateChildWeights(ref Entity child, Mulberry32 rng, SimParams p)
        {
            if (p.mutationRate <= 0f || p.mutationSigma <= 0f)
            {
                return;
            }
            // マスクは種で解決してから両モードへ渡す（自種コロニーの添字が種で違う）
            int mask = EntityWeights.ResolveMutationMask(p.mutationFieldMask, child.kind);
            MutateWeights(ref child.forageWeights, rng, p, mask);
            MutateWeights(ref child.wanderWeights, rng, p, mask);
        }

        static void MutateWeights(ref EntityWeights w, Mulberry32 rng, SimParams p, int mask)
        {
            for (int i = 0; i < EntityWeights.FieldCount; i++)
            {
                // 3個とも**必ず**引く。抽選に落ちても、マスクで外れていても引く
                // （上のコメント / SimParams.mutationFieldMask 参照）
                float roll = rng.NextFloat01();
                float u1 = rng.NextFloat01();
                float u2 = rng.NextFloat01();

                if (roll >= p.mutationRate || (mask & (1 << i)) == 0)
                {
                    continue;
                }
                w.SetByIndex(i, w[i] + NextGaussian(u1, u2) * p.mutationSigma);
            }
        }

        /// <summary>
        /// 一様乱数2個から標準正規乱数を1個作る（Box-Muller）。
        ///
        /// <see cref="Mulberry32.NextFloat01"/> は [0,1) で **0 を返しうる**ので、
        /// log(0) = -∞ を避けるため 1-u1 を取る（(0,1] になる）。
        /// これを忘れると稀に NaN が重みへ入り、その個体以降の行動が壊れる。
        /// </summary>
        static float NextGaussian(float u1, float u2)
        {
            double r = Math.Sqrt(-2.0 * Math.Log(1.0 - u1));
            return (float)(r * Math.Cos(2.0 * Math.PI * u2));
        }

        /// <summary>
        /// 繁殖が成立した場所に痕跡を残す (Demo 8 第4段 K1)。
        ///
        /// **RNG を消費しない。** 消費すると乱数列が変わって世界そのものが別物になり、
        /// 「場を1枚足しただけ」という 4a の前提が崩れる（判定 M0b は
        /// コロニー場を除いた部分のハッシュが移設前と完全一致することで確認する）。
        ///
        /// 書き込むのは**自種の場だけ**である。他種のコロニー場を読む「盗聴」は
        /// 器（重み）だけ作って重み0で寝かせてあるが、書き込みまで混ぜてしまうと
        /// 場そのものが「誰の集落か」を表さなくなる（prereg 判断2）。
        /// </summary>
        static void DepositColony(World world, SimParams p, EntityKind kind, Int3 cell)
        {
            if (!world.InBounds(cell.x, cell.z))
            {
                return;
            }
            world.Colony(kind).Deposit(cell, p.colonyBreedDeposit);
        }

        /// <summary>
        /// 存在の痕跡 (Demo 8 第4段 4a 追補)。生きている動物が、そのティックの
        /// 最終位置の自種コロニー場へ <see cref="SimParams.colonyPresenceDeposit"/> を書く。
        ///
        /// 【なぜ二層にしたか】4b で消した <c>FindBreedPartner</c> が見ていたのは
        /// 「隣に相手が**存在するか**」である。繁殖イベントだけを覚える場は
        /// 置換元と意味がずれており、その症状が「場が0から立ち上がらない」
        /// 自己閉塞だった（4a 実測: 48シード中 羊28 / 狼33 で痕跡ゼロ）。
        /// 存在の痕跡こそが置換元と揃った空間統計である。
        ///
        /// 【なぜ独立したパスにするか】獲物場・恐怖場のように移動処理の中へ埋めると、
        /// 草食獣と狼で書く場所が二手に分かれ、「全ての動物が等しく書く」ことが
        /// 読み取れなくなる。存在の痕跡は種によらず同じ規則なので、
        /// 種ごとの更新が全て終わったあとに1つのループで書く。
        ///
        /// 【RNG を消費しない】ティック内のどこに置いても乱数列は動かない。
        /// 繁殖の**後**に置いてあるので、その位置は
        ///   - 生きて1ティックを終えた個体の最終位置になる
        ///     （このティックに死んだ個体は既に取り除かれている）
        ///   - 新生児も出生セルへ書く（繁殖 deposit 1.0 が既にあるので飽和して変わらない）
        /// 上限 1.0 で飽和するため、書き込み順に依存しない
        /// （個体は id 昇順の固定順で走査するので、いずれにせよ決定論的）。
        /// </summary>
        static void DepositColonyPresence(World world, SimParams p)
        {
            if (p.colonyPresenceDeposit <= 0f)
            {
                return;
            }

            var entities = world.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (!e.IsAnimal)
                {
                    continue;
                }
                if (!world.InBounds(e.cell.x, e.cell.z))
                {
                    continue;
                }
                world.Colony(e.kind).Deposit(e.cell, p.colonyPresenceDeposit);
            }
        }

        /// <summary>
        /// 自セル＋4近傍（固定順）で草を食む (Demo 8.5 段階1 / K2)。
        /// 食べられたら true と**実際に減らせた量**を返す。
        ///
        /// 【個体を探さなくなった】移行前は植物 Entity を走査し、
        /// 同じ植物を2頭が同時に食べないよう `alreadyEaten` の HashSet を
        /// 持ち回っていた。場からの減算はその集合を要らなくする —
        /// 2頭目は「1頭目が食べ残した分」を得るだけで、破綻しない。
        /// 個体側が持つ状態がひとつ減った（Demo 8.5 M1 に寄与）。
        ///
        /// 【中間状態の扱い】段階1では植物 Entity がまだ存在し、
        /// <see cref="UpdateVegetation"/> が毎ティック植生場へ書き込む。
        /// 食べたセルに植物が残っていると翌ティックに場が回復してしまい、
        /// 「食べても減らない」無限の餌場になる。それを避けるため、
        /// **草を食んだセルに植物 Entity がいれば取り除く**。
        /// 植物の増減（スポーン・上限・除去）は移行前と同じ経済のまま保たれ、
        /// 段階1で変わるのは hunger の回復のしかただけになる。
        /// </summary>
        static bool TryGraze(World world, SimParams p, Int3 from, out float taken)
        {
            if (TryGrazeColumn(world, p, from.x, from.z, from.y, out taken))
            {
                return true;
            }
            foreach (var dir in FacingDirections)
            {
                if (TryGrazeColumn(world, p, from.x + dir.x, from.z + dir.z, from.y, out taken))
                {
                    return true;
                }
            }
            taken = 0f;
            return false;
        }

        static bool TryGrazeColumn(
            World world, SimParams p, int x, int z, int fromY, out float taken)
        {
            taken = 0f;
            if (!world.InBounds(x, z))
            {
                return false;
            }
            if (Math.Abs(world.GetSurfaceHeight(x, z) - fromY) > 1)
            {
                return false;
            }
            if (world.Vegetation.GetAtColumn(x, z) < p.grazeThreshold)
            {
                return false;
            }

            taken = world.Vegetation.Consume(x, z, p.grazeBite);
            if (taken <= 0f)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 草食獣の採餌方向 (Demo 8 H2)。4近傍を
        /// スコア = w_veg × 植生場 − w_fear × 恐怖場 で評価し、最大のセルへ向く。
        ///
        /// 【設計意図】2つの場を1つのスコアに合成することで、
        /// 「腹が減っているが危険な場所にある草」という葛藤が表現できる。
        /// 濃い草があっても、そこに狼の痕跡が濃く残っていれば近寄らない。
        /// w_fear を w_veg より大きくしてあるので、迷ったら安全側に倒れる。
        /// 個体は狼を見ておらず、場に残された痕跡だけを読んでいる。
        ///
        /// 同値は固定順の先勝ち。RNG は使わない（＝決定論）。
        /// 場読みの振る舞いを直接検証できるよう public にしている。
        ///
        /// 【Demo 8 第3段 J2】重みは <see cref="SimParams"/> ではなく**個体**が持つ。
        /// スコアの計算は <see cref="EntityWeights.Score"/> の
        /// 「各場について 重み × 場の値 を合計」という一般形になり、
        /// 場が増えてもこの関数は触らずに済む。
        /// 重み0の項は 0×値 = 0 で加算も厳密なので、移管しても値は変わらない。
        /// </summary>
        public static int FindForagingFacing(
            World world, in EntityWeights weights, Int3 cell, int currentFacing)
        {
            int best = currentFacing;
            float bestScore = 0f;
            bool found = false;

            for (int f = 0; f < FacingDirections.Length; f++)
            {
                var dir = FacingDirections[f];
                int nx = cell.x + dir.x;
                int nz = cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }

                float score = weights.Score(world, nx, nz);

                if (!found || score > bestScore)
                {
                    bestScore = score;
                    best = f;
                    found = true;
                }
            }

            // スコアが全て負（草が無く危険だけ）でも**最大の方向を選ぶ**。
            // ここで「魅力が無いから向きを変えない」としてしまうと、
            // 危険地帯のど真ん中にいるときに限って回避が働かず、
            // 恐怖場を入れた意味が無くなる（実測で M2 が不成立になった原因）。
            // 負の中の最大＝最も危険が薄い方向へ逃げる、が正しい振る舞い
            return found ? best : currentFacing;
        }

        /// <summary>
        /// 死んだ場所に痕跡を残す (Demo 8 第2段 I1)。
        /// 餓死は死骸がそのまま残るので大きく、被食は肉が持ち去られるので小さい。
        /// 死因で量を変えることで「どんな死が起きたか」まで場が記憶する。
        /// </summary>
        static void DepositDeath(World world, SimParams p, Int3 cell, bool starved)
        {
            if (!world.InBounds(cell.x, cell.z))
            {
                return;
            }
            world.Death.Deposit(cell, starved ? p.deathDepositStarved : p.deathDepositPredated);
        }

        /// <summary>
        /// 満腹時の徘徊方向 (Demo 8 第2段 I3)。
        /// 今の向きを基本にしつつ、恐怖場の濃い方向だけは避ける。
        ///
        /// スコア = (今の向きなら wanderBias) − w_fear × 恐怖場。
        /// 恐怖が薄いうちは今の向きが勝つ（＝従来どおりのランダム徘徊）が、
        /// 恐怖が wanderBias / w_fear を超えると向きを変える。
        /// 「空腹でなくても危険は避ける」を、徘徊の性質を壊さずに入れるための形。
        /// </summary>
        public static int FindWanderFacing(
            World world, in EntityWeights weights, Int3 cell, int currentFacing)
        {
            const float wanderBias = 0.15f;

            int best = currentFacing;
            float bestScore = float.NegativeInfinity;
            for (int f = 0; f < FacingDirections.Length; f++)
            {
                var dir = FacingDirections[f];
                int nx = cell.x + dir.x;
                int nz = cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                float score = (f == currentFacing ? wanderBias : 0f)
                    + weights.Score(world, nx, nz);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }
            return best;
        }

        /// <summary>
        /// 草食獣の移動。実際に動いたときだけ、恐怖場の高い方へ動いたか低い方へ動いたかを数える
        /// (Demo 8 第2段 M3 の指標)。
        ///
        /// 「恐怖場が高いセルにいた割合」で測ると、動かなかったティックも、
        /// 危険な場所で捕食されて消えた個体の分も混ざって回避の効果が埋もれる。
        /// **移動が起きた瞬間だけ**を見れば、その1歩が危険を避ける方向だったかを直接測れる。
        /// </summary>
        static void MoveHerbivore(World world, Mulberry32 rng, SimParams p, ref Entity e)
        {
            var from = e.cell;
            float fearFrom = world.Fear.GetAtColumn(from.x, from.z);

            // 近傍に意味のある恐怖があるときだけ数える。
            // 恐怖場は全体の1%未満にしか立たないので、全ての移動を数えると
            // 「避けようのない移動」に薄められて効果が見えなくなる（実測で差1%以下）
            bool nearFear = fearFrom >= k_FearRelevantThreshold;
            if (!nearFear)
            {
                foreach (var d in FacingDirections)
                {
                    int nx = from.x + d.x, nz = from.z + d.z;
                    if (world.InBounds(nx, nz) && world.Fear.GetAtColumn(nx, nz) >= k_FearRelevantThreshold)
                    {
                        nearFear = true;
                        break;
                    }
                }
            }

            TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);

            if (e.cell == from)
            {
                return;
            }

            // 通った跡を残す (Demo 8 第3段 J1)
            DepositTrample(world, p, e.cell);

            if (!nearFear)
            {
                return;
            }

            float fearTo = world.Fear.GetAtColumn(e.cell.x, e.cell.z);
            if (fearTo < fearFrom)
            {
                world.HerbivoreMovesAwayFromFear++;
            }
            else if (fearTo > fearFrom)
            {
                world.HerbivoreMovesTowardFear++;
            }
        }

        /// <summary>
        /// 狼の追跡方向 (Demo 8 H3)。獲物場の4近傍値が最大の方向へ向く。
        /// 全て0（匂いが届いていない）なら現在の向きを維持し、通常の徘徊に落ちる。
        ///
        /// これが「半径6セル以内の全個体を走査して最近接を選ぶ」処理の置き換えである。
        /// 個体数に依らず4近傍を見るだけなので O(1)。
        /// 場読みの振る舞いを直接検証できるよう public にしている。
        /// </summary>
        public static int FindPreyGradientFacing(
            World world, in EntityWeights weights, Int3 cell, int currentFacing)
        {
            int best = currentFacing;
            float bestValue = 0f;
            for (int f = 0; f < FacingDirections.Length; f++)
            {
                var dir = FacingDirections[f];
                int nx = cell.x + dir.x;
                int nz = cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                float v = weights.Score(world, nx, nz);
                if (v > bestValue)
                {
                    bestValue = v;
                    best = f;
                }
            }
            return bestValue > 0f ? best : currentFacing;
        }

        /// <summary>
        /// 隣接（4近傍、高低差1以下）の草食獣のインデックス。無ければ -1。
        /// 捕食の成立判定だけは個体を見る必要があるが、走査するのは4セルだけである。
        /// </summary>
        static int FindAdjacentHerbivore(World world, Int3 from, HashSet<int> excludeIds)
        {
            foreach (var dir in FacingDirections)
            {
                int nx = from.x + dir.x;
                int nz = from.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                var cell = new Int3(nx, world.GetSurfaceHeight(nx, nz), nz);
                if (Math.Abs(cell.y - from.y) > 1)
                {
                    continue;
                }
                if (!world.TryGetEntityIndexAt(cell, out int index))
                {
                    continue;
                }
                var e = world.Entities[index];
                if (e.IsHerbivore && !excludeIds.Contains(e.id))
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>通常の移動判定 (Demo 2 D4): moveChance で facing 方向へ、高低差1以下・未占有なら移動。</summary>
        static void TryMove(World world, Mulberry32 rng, SimParams p, ref Entity e, bool allowRandomTurnOnBlock)
        {
            if (rng.NextFloat01() >= p.moveChance)
            {
                return;
            }

            var dir = FacingDirections[e.facing];
            int nx = e.cell.x + dir.x;
            int nz = e.cell.z + dir.z;

            if (!world.InBounds(nx, nz))
            {
                if (allowRandomTurnOnBlock)
                {
                    e.facing = rng.Range(0, 4);
                }
                return;
            }

            int targetHeight = world.GetSurfaceHeight(nx, nz);
            var target = new Int3(nx, targetHeight, nz);
            bool climbable = Math.Abs(targetHeight - e.cell.y) <= 1;

            // 塞ぐのは動物だけ (Demo 8.5 段階3)。植物が場になると
            // 「草が通行を妨げる」は成立しないので、動物は草の上を歩ける。
            // movementBlockVegetation は移行前の阻害を再現する診断用の入口（既定 0＝無効）
            bool blocked = world.IsCellBlockedByAnimal(target)
                || (p.movementBlockVegetation > 0f
                    && world.Vegetation.GetAtColumn(nx, nz) >= p.movementBlockVegetation);

            if (climbable && !blocked)
            {
                e.cell = target;
            }
            else if (allowRandomTurnOnBlock)
            {
                e.facing = rng.Range(0, 4);
            }
        }
    }
}
