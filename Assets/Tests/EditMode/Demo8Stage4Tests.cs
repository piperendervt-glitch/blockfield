using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// Demo 8 第4段 段階4a: コロニー場 (K1) と結合行列の器 (K2)。
    ///
    /// 4a は**場を敷くだけ**の段階である。繁殖判定の場化 (4b) も群れ行動 (4c) も
    /// 入っていないので、生態系の振る舞いは 4a の前後で1ビットも変わらないことが
    /// 正しい状態になる。ここのテストはその「変わらなさ」を固定するためにある。
    /// </summary>
    public class Demo8Stage4Tests
    {
        static readonly uint[] k_Seeds = { 12345u, 777u, 20260809u };

        static World MakeDiorama(uint seed)
        {
            var tp = TerrainParams.Default;
            tp.seed = seed;
            tp.width = 50;
            tp.depth = 50;
            tp.maxHeight = 16;
            return World.Create(tp);
        }

        static World Run(uint seed, SimParams p, int ticks)
        {
            var world = MakeDiorama(seed);
            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            return world;
        }

        // ================= K2: 結合行列の器 =================

        /// <summary>
        /// 成長の計算式が移設前と厳密に同じであること。
        ///
        /// 【なぜ具体値で確かめるか】ハッシュ一致は「何も変わっていない」ことしか
        /// 言わない。式のどこに何が掛かるのか（抑制は成長量に掛かり、促進は結果に
        /// 足される）は、値を手で計算して突き合わせないと固定できない。
        /// 3件とも別の経路を通す: 通常 / 抑制が下限に張り付く / 促進が 1.0 で頭打ち。
        ///
        ///   veg=0.20, trample=0.30, death=0.40
        ///     room = 1 - 0.20 = 0.80
        ///     抑制 = 1 - 1.2 × 0.30 = 0.64（下限 0.1 より上）
        ///     成長 = 0.20 + 0.028 × 1.0 × max(0.20, 0.02) × 0.80 × 0.64 = 0.2028672
        ///     促進 = 0.2028672 + 0.05 × 0.40                            = 0.2228672
        ///
        ///   veg=0.05, trample=0.90, death=0.00
        ///     抑制 = 1 - 1.2 × 0.90 = -0.08 → 下限 0.10 に張り付く
        ///     成長 = 0.05 + 0.028 × 1.0 × 0.05 × 0.95 × 0.10 = 0.0501330
        ///     促進なし
        ///
        ///   veg=0.99, trample=0.00, death=1.00
        ///     成長 = 0.99 + 0.028 × 1.0 × 0.99 × 0.01 × 1.0 = 0.9902772
        ///     促進 = 0.9902772 + 0.05 × 1.00 = 1.0402772 → 上限 1.0 で頭打ち
        /// </summary>
        [Test]
        public void Coupling_GrowthFormulaMatchesTheHandComputedValues()
        {
            var cases = new[]
            {
                (veg: 0.20f, trample: 0.30f, death: 0.40f, expected: 0.2228672f),
                (veg: 0.05f, trample: 0.90f, death: 0.00f, expected: 0.0501330f),
                (veg: 0.99f, trample: 0.00f, death: 1.00f, expected: 1.0f),
            };

            foreach (var c in cases)
            {
                var world = MakeDiorama(k_Seeds[0]);
                var p = IsolatedGrowthParams();
                var (x, z) = FindFlatCell(world);

                world.Vegetation.SetAtColumn(x, z, c.veg);
                world.Trample.SetAtColumn(x, z, c.trample);
                world.Death.SetAtColumn(x, z, c.death);

                Simulation.Tick(world, world.Rng, p);

                Assert.AreEqual(c.expected, world.Vegetation.GetAtColumn(x, z), 1e-6f,
                    $"草{c.veg} 踏み{c.trample} 死{c.death} の成長結果が手計算と違う");
            }
        }

        /// <summary>
        /// 促進結合（死の場→植生）が、移設前のスカラー
        /// <see cref="SimParams.deathNutrientGrowth"/> と完全に同値であること。
        ///
        /// 「結合リストから促進を外す」と「係数を0にする」が同じ世界を作るなら、
        /// 結合が旧実装の分岐と同じ場所に同じ形で入っていると言える。
        /// ハッシュで見るのは、式だけでなく**RNG 消費と適用順まで**含めて
        /// 一致していることを要求するためである。
        /// </summary>
        [Test]
        public void Coupling_BoostIsEquivalentToTheLegacyScalar()
        {
            foreach (uint seed in k_Seeds)
            {
                var legacy = SimParams.Default;
                legacy.deathNutrientGrowth = 0f;

                var explicitList = SimParams.Default;
                explicitList.couplings = new[]
                {
                    new FieldCoupling(FieldId.Trample, FieldId.Vegetation,
                        SimParams.Default.trampleSuppression, CouplingForm.GrowthSuppress),
                };

                Assert.AreEqual(
                    Run(seed, legacy, 400).ComputeContentHash(),
                    Run(seed, explicitList, 400).ComputeContentHash(),
                    $"seed {seed}: 促進結合を外した世界と deathNutrientGrowth=0 の世界が違う");
            }
        }

        /// <summary>
        /// 抑制結合（踏み荒らし場→植生）が、移設前のスカラー
        /// <see cref="SimParams.trampleSuppression"/> と完全に同値であること。
        /// </summary>
        [Test]
        public void Coupling_SuppressIsEquivalentToTheLegacyScalar()
        {
            foreach (uint seed in k_Seeds)
            {
                var legacy = SimParams.Default;
                legacy.trampleSuppression = 0f;

                var explicitList = SimParams.Default;
                explicitList.couplings = new[]
                {
                    new FieldCoupling(FieldId.Death, FieldId.Vegetation,
                        SimParams.Default.deathNutrientGrowth, CouplingForm.GrowthBoost),
                };

                Assert.AreEqual(
                    Run(seed, legacy, 400).ComputeContentHash(),
                    Run(seed, explicitList, 400).ComputeContentHash(),
                    $"seed {seed}: 抑制結合を外した世界と trampleSuppression=0 の世界が違う");
            }
        }

        /// <summary>
        /// 既定の結合リストを**明示的に渡しても**既定（null＝スカラーから導く）と
        /// 同じ世界になること。<see cref="SimParams.DefaultCouplings"/> が
        /// 実際に使われている経路と一致していることの確認。
        /// </summary>
        [Test]
        public void Coupling_ExplicitDefaultListMatchesTheImplicitOne()
        {
            var implicitParams = SimParams.Default;
            var explicitParams = SimParams.Default;
            explicitParams.couplings = SimParams.Default.DefaultCouplings();

            Assert.AreEqual(
                Run(k_Seeds[0], implicitParams, 400).ComputeContentHash(),
                Run(k_Seeds[0], explicitParams, 400).ComputeContentHash(),
                "既定の結合を明示的に渡すと世界が変わる（導出の経路がずれている）");
        }

        /// <summary>
        /// <see cref="FieldId"/> の値が場の名前昇順（＝重みの並び・ハッシュの畳み込み順）と
        /// 一致していること。ここがずれると、結合が別の場を指しても誰も気づけない。
        /// </summary>
        [Test]
        public void Coupling_FieldIdOrderMatchesFieldNameOrder()
        {
            var world = MakeDiorama(k_Seeds[0]);
            var ids = (FieldId[])System.Enum.GetValues(typeof(FieldId));

            Assert.AreEqual(EntityWeights.FieldCount, ids.Length,
                "FieldId の数が場の数と合っていない");

            for (int i = 0; i < ids.Length; i++)
            {
                Assert.AreEqual(i, (int)ids[i], $"{ids[i]} の値が名前昇順の位置と違う");
                string name = FieldIds.NameOf(ids[i]);
                Assert.AreEqual(EntityWeights.FieldNames[i], name);
                Assert.AreSame(world.Fields[name], world.GetField(ids[i]),
                    $"{ids[i]} が引く場が名前 '{name}' の場と違う");
            }
        }

        // ================= K1: コロニー場 =================

        /// <summary>
        /// 繁殖が成立すると、**自種のコロニー場だけ**が増えること。
        ///
        /// 他種の場まで濃くなると、場が「誰の集落か」を表さなくなり、
        /// 4c の群れ行動が種を区別できなくなる。
        /// </summary>
        [Test]
        public void Colony_BirthWritesOnlyToTheOwnSpeciesField()
        {
            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                var world = BreedOnce(kind, out _);

                foreach (var other in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
                {
                    var (_, max) = EcologyStats.ColonyStats(world, other);
                    if (other == kind)
                    {
                        Assert.Greater(max, 0f, $"{kind} が繁殖したのに自種のコロニー場が空");
                    }
                    else
                    {
                        Assert.AreEqual(0f, max, $"{kind} の繁殖で {other} のコロニー場が増えた");
                    }
                }
            }
        }

        /// <summary>
        /// 書き込まれるのは**出生セル**であり、量は
        /// <see cref="SimParams.colonyBreedDeposit"/>（上限1.0で飽和）であること。
        ///
        /// 場の更新（拡散・減衰）はティック内で繁殖より**前**に走るので、
        /// 出生したティックの終わりでは書き込んだ値がそのまま残っている。
        /// </summary>
        [Test]
        public void Colony_DepositLandsOnTheBirthCell()
        {
            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Wolf })
            {
                var world = BreedOnce(kind, out var child);
                float atBirthCell = world.Colony(kind).GetAtColumn(child.cell.x, child.cell.z);

                Assert.AreEqual(1f, atBirthCell, 1e-6f,
                    $"{kind}: 出生セル ({child.cell.x},{child.cell.z}) の値が deposit と違う");
            }
        }

        /// <summary>
        /// 狼の繁殖でも狼のコロニー場が増えること（prereg 判断1: 狼も対称に扱う）。
        /// 上の2つに含まれてはいるが、「狼だけ落ちている」という壊れ方が
        /// 一番起きやすいので単独でも見る。
        /// </summary>
        [Test]
        public void Colony_WolfBreedingFillsTheWolfField()
        {
            var world = BreedOnce(EntityKind.Wolf, out _);
            var (mean, max) = EcologyStats.ColonyStats(world, EntityKind.Wolf);

            Assert.Greater(max, 0f, "狼が繁殖したのに狼のコロニー場が空");
            Assert.Greater(mean, 0f);
        }

        /// <summary>
        /// **滞在しているだけで自種の場が濃くなること (4a 追補の第1層)。**
        ///
        /// 拡散と減衰を止めてあるので、値は毎ティックの書き込みの単純な積算になる。
        /// 「1頭 × 10ティック = colonyPresenceDeposit × 10」を厳密に要求することで、
        /// 書き込みが毎ティック1回だけ起きること（二重書き込みも取りこぼしもないこと）を固定する。
        /// </summary>
        [Test]
        public void Colony_PresenceAccumulatesUnderAStandingAnimal()
        {
            const int ticks = 10;

            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                var world = MakeDiorama(k_Seeds[0]);
                var p = SimParams.Default;
                p.animalSpawnChance = 0f;    // 他の個体を湧かせない（他所からの書き込みを排除）
                p.hungerPerTick = 0f;        // 餓死・採餌行動を止める
                p.wolfHungerPerTick = 0f;
                p.moveChance = 0f;           // 動かない → 同じセルに積もる
                p.colonyDiffuse = 0f;        // にじみと減衰を止め、積算そのものを見る
                p.colonyDecay = 0f;

                var (x, z) = FindFlatCell(world);
                Assert.GreaterOrEqual(world.TrySpawn(kind, x, z, 0, p), 0, $"{kind} を置けない");

                for (int t = 0; t < ticks; t++)
                {
                    Simulation.Tick(world, world.Rng, p);
                }

                Assert.AreEqual(ticks * p.colonyPresenceDeposit,
                    world.Colony(kind).GetAtColumn(x, z), 1e-5f,
                    $"{kind}: 滞在 {ticks} ティックの積算が deposit × ティック数と違う");

                foreach (var other in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
                {
                    if (other == kind)
                    {
                        continue;
                    }
                    Assert.AreEqual(0f, EcologyStats.ColonyStats(world, other).max,
                        $"{kind} が居るだけで {other} のコロニー場が増えた");
                }
            }
        }

        /// <summary>
        /// **滞在の書き込みが、場を0から立ち上げていること (4a 追補の主目的)。**
        ///
        /// 【この判定が要る理由 — 4b の自己閉塞】4b は「自セルのコロニー場（自種）が
        /// 閾値以上なら繁殖できる」に置き換える。場が0から始まる以上、
        /// 繁殖イベントだけを書いていると
        ///   最初の繁殖が起きない → 場が立たない → 永久に繁殖しない
        /// という鶏と卵になり、**閾値をどう選んでも解けない**。
        /// 実際 4a の実測では 48シード中 羊28 / 狼33 で痕跡が1セルも立たなかった。
        ///
        /// ここでは滞在の書き込みを 0 にした対照と並べ、
        /// 「立ち上がらない」→「全シードで立つ」に変わったことを固定する。
        /// 対照を並べるのは、単に「場が空でない」だけを見ても
        /// **どちらの層のおかげか**が言えないため。
        /// </summary>
        [Test]
        public void Colony_PresenceIsWhatLiftsTheFieldOffZero()
        {
            const int seedCount = 8;
            const int ticks = 800;

            int Empty(float presenceDeposit)
            {
                int empty = 0;
                var gate = new object();

                System.Threading.Tasks.Parallel.For(0, seedCount, i =>
                {
                    // SimRunner と同じシード列（1000 + i × 7919）
                    var world = MakeDiorama(1000u + (uint)i * 7919u);
                    var p = SimParams.Default;
                    p.colonyPresenceDeposit = presenceDeposit;

                    for (int t = 0; t < ticks; t++)
                    {
                        Simulation.Tick(world, world.Rng, p);
                    }

                    int local = 0;
                    foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
                    {
                        if (EcologyStats.ColonyStats(world, kind).max <= 0f)
                        {
                            local++;
                        }
                    }
                    lock (gate)
                    {
                        empty += local;
                    }
                });
                return empty;
            }

            Assert.Greater(Empty(0f), 0,
                "繁殖だけを書く設定で全ての場が立ってしまった。" +
                "自己閉塞の対照が成立しておらず、この判定が意味を失っている");

            Assert.AreEqual(0, Empty(SimParams.Default.colonyPresenceDeposit),
                $"滞在の書き込みを入れても、{seedCount}シード×{ticks}ティックで" +
                "痕跡が1セルも立たない種がある（4b の閾値判定が自己閉塞する）");
        }

        // 【削除したテスト: Colony_EverythingExceptTheColonyFieldsIsUnchanged (4a)】
        //
        // 「コロニー場を除いたハッシュが、場を足す前と完全一致する」を
        // Unity で測った基準値6件で固定していた（判定 M0b）。4a では
        // 誰もこの場を読まなかったので成り立つ主張だったが、
        // **K3 で場が繁殖確率を決めるようになった時点で前提ごと無くなった**。
        // 場は世界に影響を与えるのが仕事になったので、
        // 「足しても何も変わらない」はもはや望ましい性質ですらない。
        //
        // 生き残る中身（書き込みが乱数を消費しないこと）は
        // Breed_ModulationDoesNotConsumeExtraRandomness が引き継いでいる。
        // 4a 時点の 192/192 一致はチェックリストに記録済み。

        // ================= K3: 繁殖判定の場化 =================

        /// <summary>
        /// **繁殖に相手個体が要らなくなったこと（M1 の実体）。**
        ///
        /// 移行前は隣接4近傍に条件を満たす同種個体がいることが必須だった。
        /// 1頭だけを置いて子が生まれるなら、その要求が消えたと言える。
        /// </summary>
        [Test]
        public void Breed_LoneAnimalBreedsWhenItsColonyFieldIsStrong()
        {
            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                var world = MakeDiorama(k_Seeds[0]);
                var p = SimParams.Default;
                p.animalSpawnChance = 0f;    // 相手になりうる個体を湧かせない
                p.hungerPerTick = 0f;
                p.wolfHungerPerTick = 0f;
                p.moveChance = 0f;

                var (x, z) = FindFlatCell(world);
                Assert.GreaterOrEqual(world.TrySpawn(kind, x, z, 0, p), 0, $"{kind} を置けない");
                world.Colony(kind).SetAtColumn(x, z, 1f);

                int ticks = 0;
                for (; ticks < 2000 && world.BirthCount == 0; ticks++)
                {
                    Simulation.Tick(world, world.Rng, p);
                }

                Assert.Greater(world.BirthCount, 0,
                    $"{kind}: 相手が居ないと繁殖できないままになっている（K3 が効いていない）");
            }
        }

        /// <summary>
        /// **実効確率がミカエリス・メンテン型の変調になっていること。**
        ///
        /// 実効確率 = breedChance × colony / (colony + colonyBreedK) を、
        /// 出生までにかかったティック数から逆算して確かめる。
        /// 場の値を 2 段階に振って、**濃い場のほうが速く産む**ことを見る。
        ///
        /// 【なぜ確率そのものを直接見ないか】確率は内部量で外から読めない。
        /// 待ち時間の期待値 1/p は観測できるので、そちらで押さえる。
        /// 個体を独立に多数回試行して平均を取る（1回では分散が大きすぎる）。
        /// </summary>
        [Test]
        public void Breed_ChanceIsModulatedByTheColonyField()
        {
            // 場の値 → 出生までの平均ティック数
            double MeanTicksToBirth(float colony, int trials)
            {
                double total = 0;
                for (int trial = 0; trial < trials; trial++)
                {
                    var world = MakeDiorama(1000u + (uint)trial * 7919u);
                    var p = SimParams.Default;
                    p.animalSpawnChance = 0f;
                    p.hungerPerTick = 0f;
                    p.wolfHungerPerTick = 0f;
                    p.moveChance = 0f;
                    p.colonyPresenceDeposit = 0f;  // 場を固定したいので滞在の書き込みを止める
                    p.colonyDiffuse = 0f;
                    p.colonyDecay = 0f;

                    var (x, z) = FindFlatCell(world);
                    Assert.GreaterOrEqual(world.TrySpawn(EntityKind.Sheep, x, z, 0, p), 0);
                    world.ColonySheep.SetAtColumn(x, z, colony);

                    int t = 0;
                    for (; t < 20000 && world.BirthCount == 0; t++)
                    {
                        Simulation.Tick(world, world.Rng, p);
                    }
                    total += t;
                }
                return total / trials;
            }

            const int trials = 12;
            double strong = MeanTicksToBirth(1.0f, trials);
            double weak = MeanTicksToBirth(0.25f, trials);

            // 実効確率の比は (1/(1+12)) : (0.25/(0.25+12)) = 0.0769 : 0.0204 ＝ 3.77倍。
            // 待ち時間はその逆比になるので、薄い場のほうが明確に遅いはず。
            // クールダウン等の定数項が乗るので比そのものは緩めに見る
            Assert.Less(strong, weak,
                $"場が濃いほうが遅い（濃 {strong:F0}t / 薄 {weak:F0}t）。変調の向きが逆になっている");
            Assert.Greater(weak / strong, 1.5,
                $"場の濃さで待ち時間が変わっていない（濃 {strong:F0}t / 薄 {weak:F0}t）。" +
                "変調が効いていないか、飽和して差が出ていない");
        }

        /// <summary>
        /// **変調が乱数を追加消費していないこと。**
        ///
        /// 変調は「既存の breedChance 判定の乱数1個と比べる相手を変える」だけの
        /// 実装でなければならない。乱数をもう1つ引くと消費列が変わり、
        /// 4c 以降の掃引で「場の効果」と「乱数列のずれ」が混ざる。
        ///
        /// k を変えれば繁殖の成否は変わるが、**繁殖が一度も起きない条件**では
        /// 消費列が同じになるはず、という形で確かめる。
        /// </summary>
        [Test]
        public void Breed_ModulationDoesNotConsumeExtraRandomness()
        {
            ulong Run(float k)
            {
                var world = MakeDiorama(k_Seeds[0]);
                var p = SimParams.Default;
                p.colonyPresenceDeposit = 0f;  // 場を空のままにする → 実効確率は常に 0
                p.colonyBreedDeposit = 0f;

                for (int t = 0; t < 400; t++)
                {
                    Simulation.Tick(world, world.Rng, p);
                }
                Assert.AreEqual(0, world.BirthCount, "場が空なのに繁殖が起きている");
                return world.ComputeContentHash();
            }

            Assert.AreEqual(Run(12f), Run(400f),
                "場が空で繁殖が起きない条件なのに k で世界が変わった。" +
                "変調が乱数を追加消費している疑いがある");
        }

        /// <summary>
        /// 減衰率（τ）が配線されていること。
        /// τ≈400 は「集落は世代を跨いで残る」という設計そのものなので、
        /// ここが効いていないと 4c の掃引が成立しない。
        /// </summary>
        [Test]
        public void Colony_DecayParameterIsWired()
        {
            float Remaining(float decay)
            {
                var world = MakeDiorama(k_Seeds[0]);
                var p = IsolatedGrowthParams();
                p.colonyDecay = decay;
                p.colonyDiffuse = 0f;

                var (x, z) = FindFlatCell(world);
                world.ColonySheep.SetAtColumn(x, z, 1f);
                for (int t = 0; t < 400; t++)
                {
                    Simulation.Tick(world, world.Rng, p);
                }
                return world.ColonySheep.GetAtColumn(x, z);
            }

            // τ=400 なら 400ティックで 1/e ≈ 0.368 まで落ちる
            float slow = Remaining(0.0025f);
            Assert.AreEqual(0.368f, slow, 0.02f, $"τ≈400 の減衰になっていない（残 {slow:F3}）");

            // 恐怖場と同じ速さ（τ≈33）なら 400ティックではほぼ消える
            Assert.Less(Remaining(0.03f), 0.001f, "減衰率を上げても痕跡が残っている");
        }

        // ---- 補助 ----

        /// <summary>
        /// 成長の計算だけを見るためのパラメータ。
        /// 拡散・減衰・踏み潰し・スポーンを止め、1ティックの結果が
        /// <c>GrowVegetation</c> の出力そのものになるようにする。
        /// </summary>
        static SimParams IsolatedGrowthParams()
        {
            var p = SimParams.Default;
            p.animalSpawnChance = 0f;    // 動物を湧かせない（摂食・踏み跡が混ざらない）
            p.vegetationDiffuse = 0f;    // 成長の直後に走る拡散・減衰を止める
            p.vegetationDecay = 0f;
            p.trampleCrushRate = 0f;     // 踏み潰しによる後段の減算を止める
            p.initialVegetation = 0f;
            return p;
        }

        /// <summary>適性1.0（平坦）のセルを1つ返す。</summary>
        static (int x, int z) FindFlatCell(World world)
        {
            for (int z = 1; z < world.Depth - 1; z++)
            {
                for (int x = 1; x < world.Width - 1; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) >= 1f)
                    {
                        return (x, z);
                    }
                }
            }
            Assert.Fail("平坦な適性セルが見つからない");
            return (0, 0);
        }

        /// <summary>
        /// 指定の種を2頭だけ隣り合わせに置き、1回繁殖させた直後のワールドを返す。
        ///
        /// 野生スポーン・空腹・移動を止めてあるので、繁殖以外の出来事が起きない。
        /// 「コロニー場に何かが書かれた」原因が繁殖であることを、
        /// 実行の側で保証するための舞台づくりである。
        /// </summary>
        static World BreedOnce(EntityKind kind, out Entity child)
        {
            var world = MakeDiorama(k_Seeds[0]);
            var p = SimParams.Default;
            p.animalSpawnChance = 0f;    // 他の個体を湧かせない
            p.hungerPerTick = 0f;        // 空腹で繁殖不能にならないようにする
            p.wolfHungerPerTick = 0f;
            p.moveChance = 0f;           // 2頭が離れないようにする

            var (x, z) = FindFlatCell(world);
            int h = world.GetSurfaceHeight(x, z);

            // 隣に同じ高さの適性セルがある場所を探す（高低差2以上だと繁殖判定が通らない）
            int px = -1, pz = -1;
            for (int cz = 1; cz < world.Depth - 1 && px < 0; cz++)
            {
                for (int cx = 1; cx < world.Width - 2; cx++)
                {
                    if (world.Suitability.GetAtColumn(cx, cz) < 1f) continue;
                    if (world.Suitability.GetAtColumn(cx + 1, cz) < 1f) continue;
                    if (world.GetSurfaceHeight(cx, cz) != world.GetSurfaceHeight(cx + 1, cz)) continue;
                    px = cx;
                    pz = cz;
                    break;
                }
            }
            Assert.GreaterOrEqual(px, 0, $"隣り合う平坦セルが見つからない（({x},{z}) 高さ {h}）");

            Assert.GreaterOrEqual(world.TrySpawn(kind, px, pz, 0, p), 0, "親Aを置けない");
            Assert.GreaterOrEqual(world.TrySpawn(kind, px + 1, pz, 0, p), 0, "親Bを置けない");

            // 繁殖確率 0.2 なので数十ティックで成立する。上限は十分な余裕を取る
            for (int t = 0; t < 500; t++)
            {
                Simulation.Tick(world, world.Rng, p);
                if (world.BirthCount > 0)
                {
                    child = world.Entities[world.Entities.Count - 1];
                    Assert.AreEqual(kind, child.kind, "生まれた個体の種が親と違う");
                    return world;
                }
            }

            Assert.Fail($"{kind} が 500 ティックで繁殖しなかった");
            child = default;
            return world;
        }
    }
}
