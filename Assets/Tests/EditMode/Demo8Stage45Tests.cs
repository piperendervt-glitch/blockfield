using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// Demo 8 第4.5段: 進化本体の K1（変異）と K2（観察）。
    ///
    /// 本段は**器だけ**を入れる段階である。既定は変異なし
    /// （<see cref="SimParams.mutationRate"/> = 0）なので、
    /// 第4段の世界と1ビットも変わらないことが正しい状態になる。
    /// E1/E2 の実験（変異を有効にした掃引）はまだ行っていない。
    /// </summary>
    public class Demo8Stage45Tests
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

        static SimParams WithMutation(float rate, float sigma)
        {
            var p = SimParams.Default;
            p.mutationRate = rate;
            p.mutationSigma = sigma;
            return p;
        }

        // ================= K1: 変異 =================

        /// <summary>
        /// **既定（変異なし）では世界が1ビットも変わらないこと。**
        ///
        /// M 判定の前提そのもの。変異の実装が「無効でも乱数を引いて捨てる」形に
        /// なっていると、乱数列がずれてここが落ちる。
        /// </summary>
        [Test]
        public void Mutation_DisabledLeavesTheWorldBitIdentical()
        {
            foreach (uint seed in k_Seeds)
            {
                var baseline = Run(seed, SimParams.Default, 600);

                // rate=0（sigma だけ与える）と sigma=0（rate だけ与える）の
                // どちらでも「変異なし」に落ちること
                var rateZero = Run(seed, WithMutation(0f, 0.2f), 600);
                var sigmaZero = Run(seed, WithMutation(1f, 0f), 600);

                Assert.AreEqual(baseline.ComputeContentHash(), rateZero.ComputeContentHash(),
                    $"seed {seed}: mutationRate=0 なのに世界が変わった（乱数を引いている？）");
                Assert.AreEqual(baseline.ComputeContentHash(), sigmaZero.ComputeContentHash(),
                    $"seed {seed}: mutationSigma=0 なのに世界が変わった");
            }
        }

        /// <summary>
        /// **変異が実際に重みを動かすこと。**
        ///
        /// 全個体が同一の初期値から始まるので、変異が無ければ集団の分散は
        /// 恒久的に 0 である。0 でなくなったなら変異が効いている。
        /// </summary>
        [Test]
        public void Mutation_ActuallyChangesWeights()
        {
            var world = Run(k_Seeds[0], WithMutation(1f, 0.2f), 1500);

            Assert.Greater(world.BirthCount, 0, "繁殖が起きていない（変異を観察できない）");

            bool anyVariance = false;
            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                foreach (bool wandering in new[] { false, true })
                {
                    var (_, variance, count) = EcologyStats.SpeciesWeightStats(world, kind, wandering);
                    if (count == 0)
                    {
                        continue;
                    }
                    for (int i = 0; i < EntityWeights.FieldCount; i++)
                    {
                        if (variance[i] > 0f)
                        {
                            anyVariance = true;
                        }
                    }
                }
            }
            Assert.IsTrue(anyVariance, "変異を有効にしたのに集団の重みの分散が全て0");

            // 変異なしなら分散は0のまま、が対になる要求
            var noMutation = Run(k_Seeds[0], SimParams.Default, 1500);
            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                var (_, variance, count) = EcologyStats.SpeciesWeightStats(noMutation, kind, wandering: true);
                if (count == 0)
                {
                    continue;
                }
                for (int i = 0; i < EntityWeights.FieldCount; i++)
                {
                    Assert.AreEqual(0f, variance[i], 1e-9f,
                        $"{kind}: 変異なしなのに重み {EntityWeights.FieldNames[i]} に分散がある");
                }
            }
        }

        /// <summary>
        /// **変異が決定論的であること。** 同一シード・同一パラメータなら
        /// 変異列まで含めて同じ世界に到達する。
        /// </summary>
        [Test]
        public void Mutation_IsDeterministic()
        {
            foreach (uint seed in k_Seeds)
            {
                var p = WithMutation(1f, 0.2f);
                Assert.AreEqual(
                    Run(seed, p, 800).ComputeContentHash(),
                    Run(seed, p, 800).ComputeContentHash(),
                    $"seed {seed}: 同一条件の2回の実行が違う世界になった");
            }
        }

        /// <summary>
        /// **乱数の消費数が分岐に依存しないこと。**
        ///
        /// 変異率だけを変えると「どの成分が変異するか」は変わるが、
        /// 消費する乱数の**個数**は変わってはいけない
        /// （1成分あたり必ず3個）。個数が変われば、以降の乱数列が
        /// ずれて世界そのものが別物になる。
        ///
        /// 直接数えられないので、**同じ乱数列を消費していれば
        /// 出生数と個体数の推移が一致する**という形で確かめる。
        /// rate=0.5 と rate=1.0 は変異の中身が違うだけで消費数は同じなので、
        /// 「最初の出生が起きるティック」は一致するはずである。
        /// </summary>
        [Test]
        public void Mutation_RngConsumptionDoesNotDependOnTheRoll()
        {
            int FirstBirthTick(float rate)
            {
                var world = MakeDiorama(k_Seeds[0]);
                var p = WithMutation(rate, 0.2f);
                for (int t = 0; t < 2000; t++)
                {
                    Simulation.Tick(world, world.Rng, p);
                    if (world.BirthCount > 0)
                    {
                        return t;
                    }
                }
                return -1;
            }

            int half = FirstBirthTick(0.5f);
            int full = FirstBirthTick(1.0f);

            Assert.GreaterOrEqual(half, 0, "2000ティックで繁殖が起きなかった");
            Assert.AreEqual(full, half,
                "変異率を変えたら最初の出生時刻が動いた。" +
                "抽選に落ちた成分で乱数を引いていない（消費数が分岐に依存している）疑いがある");
        }

        // ================= K3: 変異成分の選択（マスク） =================

        /// <summary>
        /// **既定のマスクは全成分**で、マスクを導入する前と挙動が変わらないこと。
        /// 追記3 の M 判定（sigma=0.1 が生態指標を壊さない）は全成分変異での
        /// 結果なので、既定が変わるとその判定が無効になる。
        /// </summary>
        [Test]
        public void MutationMask_DefaultsToEveryField()
        {
            Assert.AreEqual(EntityWeights.AllFieldsMask, SimParams.Default.mutationFieldMask);
            Assert.AreEqual((1 << EntityWeights.FieldCount) - 1, EntityWeights.AllFieldsMask);

            // 全ビットのマスクを明示しても、既定のまま走らせた世界と一致する
            var explicitMask = WithMutation(1f, 0.2f);
            explicitMask.mutationFieldMask = EntityWeights.AllFieldsMask;
            Assert.AreEqual(
                Run(k_Seeds[0], WithMutation(1f, 0.2f), 800).ComputeContentHash(),
                Run(k_Seeds[0], explicitMask, 800).ComputeContentHash());
        }

        /// <summary>
        /// **マスクで外した成分は1ミリも動かないこと。**
        ///
        /// E1 の中心要求（prereg K3「1次元のみ開放」）。他の成分まで動くと、
        /// 観察された重みの移動がどの形質の淘汰によるものか分けられなくなる。
        /// </summary>
        [Test]
        public void MutationMask_LeavesUnselectedComponentsExactlyAtTheInitialValue()
        {
            var p = WithMutation(1f, 0.2f);
            p.mutationFieldMask = 1 << 8;   // vegetation だけ開放（名前昇順の最後）
            var world = Run(k_Seeds[0], p, 1500);

            Assert.Greater(world.BirthCount, 0, "繁殖が起きていない（変異を観察できない）");

            bool vegetationMoved = false;
            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                foreach (bool wandering in new[] { false, true })
                {
                    var (_, variance, count) = EcologyStats.SpeciesWeightStats(world, kind, wandering);
                    if (count == 0)
                    {
                        continue;
                    }
                    for (int i = 0; i < EntityWeights.FieldCount; i++)
                    {
                        if (i == 8)
                        {
                            if (variance[i] > 0f) vegetationMoved = true;
                            continue;
                        }
                        Assert.AreEqual(0f, variance[i], 1e-9f,
                            $"{kind}({(wandering ? "徘徊" : "採餌")}): " +
                            $"マスクで外した重み {EntityWeights.FieldNames[i]} に分散が出た");
                    }
                }
            }
            Assert.IsTrue(vegetationMoved, "開放した vegetation にすら分散が出ていない");
        }

        /// <summary>
        /// **マスクを変えても乱数の消費数が変わらないこと。**
        ///
        /// マスクで外した成分でも3個引いて捨てる設計（SimParams.mutationFieldMask）。
        /// これが守られていれば、マスクの違う実験どうしが同じ乱数列に乗り、
        /// 重みが分岐するまでの世界の進行が一致する。
        /// 消費数を直接数えられないので、既存テストと同じく
        /// 「最初の出生が起きるティック」の一致で見る。
        /// </summary>
        [Test]
        public void MutationMask_DoesNotChangeRngConsumption()
        {
            int FirstBirthTick(int mask)
            {
                var world = MakeDiorama(k_Seeds[0]);
                var p = WithMutation(1f, 0.2f);
                p.mutationFieldMask = mask;
                for (int t = 0; t < 2000; t++)
                {
                    Simulation.Tick(world, world.Rng, p);
                    if (world.BirthCount > 0)
                    {
                        return t;
                    }
                }
                return -1;
            }

            int all = FirstBirthTick(EntityWeights.AllFieldsMask);
            int one = FirstBirthTick(EntityWeights.SelfColonyBit);
            int none = FirstBirthTick(0);

            Assert.GreaterOrEqual(all, 0, "2000ティックで繁殖が起きなかった");
            Assert.AreEqual(all, one, "マスクを絞ったら最初の出生時刻が動いた（消費数が変わっている）");
            Assert.AreEqual(all, none, "マスク0でも消費数は同じでなければならない");
        }

        /// <summary>
        /// **自種コロニービットが種ごとに正しい成分へ解決されること。**
        ///
        /// 添字の固定マスクでは「各個体が自分の分だけ」を表せない。
        /// 羊に colony-sheep、豚に colony-pig が対応し、
        /// 他種のコロニー重み（盗聴）には触れないことを固定する。
        /// </summary>
        [Test]
        public void MutationMask_SelfColonyBitResolvesPerSpecies()
        {
            Assert.AreEqual(0, EntityWeights.SelfColonyIndex(EntityKind.Pig));
            Assert.AreEqual(1, EntityWeights.SelfColonyIndex(EntityKind.Sheep));
            Assert.AreEqual(2, EntityWeights.SelfColonyIndex(EntityKind.Wolf));

            // 名前昇順の並びと一致していること（並びが変わったらここで落ちる）
            Assert.AreEqual(ColonyField.NameFor(EntityKind.Pig), EntityWeights.FieldNames[0]);
            Assert.AreEqual(ColonyField.NameFor(EntityKind.Sheep), EntityWeights.FieldNames[1]);
            Assert.AreEqual(ColonyField.NameFor(EntityKind.Wolf), EntityWeights.FieldNames[2]);

            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                int resolved = EntityWeights.ResolveMutationMask(EntityWeights.SelfColonyBit, kind);
                Assert.AreEqual(1 << EntityWeights.SelfColonyIndex(kind), resolved,
                    $"{kind}: 自種コロニー以外のビットが立っている");
            }

            // 実体ビットと併用できる
            int combined = EntityWeights.ResolveMutationMask(
                EntityWeights.SelfColonyBit | (1 << 8), EntityKind.Sheep);
            Assert.AreEqual((1 << 1) | (1 << 8), combined);
        }

        /// <summary>
        /// **自種コロニーだけを開放すると、動くのは自種の成分だけであること。**
        /// マスクの解決が実際の変異処理まで届いていることの確認。
        /// </summary>
        [Test]
        public void MutationMask_SelfColonyOnlyMovesTheOwnSpeciesComponent()
        {
            var p = WithMutation(1f, 0.2f);
            p.mutationFieldMask = EntityWeights.SelfColonyBit;
            var world = Run(k_Seeds[0], p, 1500);

            Assert.Greater(world.BirthCount, 0, "繁殖が起きていない（変異を観察できない）");

            bool anySelfMoved = false;
            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                int self = EntityWeights.SelfColonyIndex(kind);
                foreach (bool wandering in new[] { false, true })
                {
                    var (_, variance, count) = EcologyStats.SpeciesWeightStats(world, kind, wandering);
                    if (count == 0)
                    {
                        continue;
                    }
                    for (int i = 0; i < EntityWeights.FieldCount; i++)
                    {
                        if (i == self)
                        {
                            if (variance[i] > 0f) anySelfMoved = true;
                            continue;
                        }
                        Assert.AreEqual(0f, variance[i], 1e-9f,
                            $"{kind}({(wandering ? "徘徊" : "採餌")}): " +
                            $"自種以外の重み {EntityWeights.FieldNames[i]} が動いた（盗聴の重みは温存する）");
                    }
                }
            }
            Assert.IsTrue(anySelfMoved, "自種コロニー重みに分散が出ていない");
        }

        /// <summary>
        /// **wolfCap = 0 なら狼が一度も生まれないこと。**
        ///
        /// E1 の「狼を初期条件から外す」の実装。走行中ずっと存在しないことが
        /// 要求であり、途中で湧くと条件が走行中に変質する。
        /// 繁殖には既存個体が要るので、野生スポーンさえ止まれば発生経路は無い。
        /// </summary>
        [Test]
        public void WolfCapZero_KeepsWolvesOutForTheWholeRun()
        {
            var p = WithMutation(1f, 0.1f);
            p.wolfCap = 0;

            foreach (uint seed in k_Seeds)
            {
                var world = MakeDiorama(seed);
                for (int t = 0; t < 3000; t++)
                {
                    Simulation.Tick(world, world.Rng, p);
                    Assert.AreEqual(0, world.WolfCount,
                        $"seed {seed}: wolfCap=0 なのに tick {t} で狼が存在する");
                }
                Assert.Greater(world.SheepCount + world.PigCount, 0,
                    $"seed {seed}: 草食獣まで消えている（狼を外した影響の確認以前の問題）");
            }
        }

        // ================= K2: 観察 =================

        /// <summary>
        /// 認知範囲指標が手計算と一致すること。
        ///
        /// 時間深度 = Σ|w|×τ、空間半径 = Σ|w|×L。
        /// τ = 1/減衰率、L = sqrt(パス数 × 拡散率 / 4 / 減衰率)。
        /// </summary>
        [Test]
        public void CognitiveRange_MatchesTheHandComputedValues()
        {
            var p = SimParams.Default;

            // 恐怖場だけに重み 2.0 を持つ個体。
            // τ = 1/0.03 = 33.333、L = sqrt(1 × 0.1 / 4 / 0.03) = sqrt(0.8333) = 0.9129
            var w = new EntityWeights { fear = 2f };
            var (t, s) = EcologyStats.CognitiveRange(w, p);

            Assert.AreEqual(2f * (1f / 0.03f), t, 1e-3f, "時間深度が Σ|w|×τ と違う");
            Assert.AreEqual(2f * 0.91287f, s, 1e-3f, "空間半径が Σ|w|×L と違う");

            // **負の重みも「読んでいる」ので絶対値で効く**
            var negative = new EntityWeights { fear = -2f };
            var (tn, sn) = EcologyStats.CognitiveRange(negative, p);
            Assert.AreEqual(t, tn, 1e-5f, "負の重みが時間深度に寄与していない");
            Assert.AreEqual(s, sn, 1e-5f, "負の重みが空間半径に寄与していない");

            // 適性場は静的なので τ も L も 0（履歴を持たない）
            var suitabilityOnly = new EntityWeights { suitability = 5f };
            var (ts, ss) = EcologyStats.CognitiveRange(suitabilityOnly, p);
            Assert.AreEqual(0f, ts, 1e-6f, "静的な場が時間深度に寄与している");
            Assert.AreEqual(0f, ss, 1e-6f, "静的な場が空間半径に寄与している");
        }

        /// <summary>
        /// τ・L の表が場のパラメータと対応していること（添字のずれの検出）。
        /// </summary>
        [Test]
        public void CognitiveRange_TauAndReachMatchTheFieldParameters()
        {
            var p = SimParams.Default;
            var expectedTau = new Dictionary<string, float>
            {
                ["colony-pig"] = 1f / p.colonyDecay,
                ["colony-sheep"] = 1f / p.colonyDecay,
                ["colony-wolf"] = 1f / p.colonyDecay,
                [DeathField.FieldName] = 1f / p.deathDecay,
                [FearField.FieldName] = 1f / p.fearDecay,
                [PreyField.FieldName] = 1f / p.preyDecay,
                [SuitabilityField.FieldName] = 0f,
                [TrampleField.FieldName] = 1f / p.trampleDecay,
                [VegetationField.FieldName] = 1f / p.vegetationDecay,
            };

            for (int i = 0; i < EntityWeights.FieldCount; i++)
            {
                string name = EntityWeights.FieldNames[i];
                Assert.AreEqual(expectedTau[name], EcologyStats.FieldTau(i, p), 1e-3f,
                    $"{name} の τ が場のパラメータと合わない（添字がずれている？）");
            }

            // コロニー場の L は設計コメントの実測値 1.4 セル
            int colony = System.Array.IndexOf(EntityWeights.FieldNames, "colony-sheep");
            Assert.AreEqual(1.414f, EcologyStats.FieldReach(colony, p), 0.01f);

            // 獲物場は 3パスで L≈2.4 セル
            int prey = System.Array.IndexOf(EntityWeights.FieldNames, PreyField.FieldName);
            Assert.AreEqual(2.449f, EcologyStats.FieldReach(prey, p), 0.01f);
        }

        /// <summary>
        /// 種ごとの重み統計が、個体が居ないときに count=0 を返すこと。
        /// 0 を平均値として混ぜないための約束。
        /// </summary>
        [Test]
        public void WeightStats_ReportZeroCountWhenSpeciesIsAbsent()
        {
            var world = MakeDiorama(k_Seeds[0]);   // ティックを回さない＝個体が居ない

            foreach (var kind in new[] { EntityKind.Sheep, EntityKind.Pig, EntityKind.Wolf })
            {
                var (_, _, count) = EcologyStats.SpeciesWeightStats(world, kind, wandering: false);
                Assert.AreEqual(0, count, $"{kind}: 個体が居ないのに count が 0 でない");

                var (_, _, cogCount) = EcologyStats.SpeciesCognitiveRange(world, kind, SimParams.Default);
                Assert.AreEqual(0, cogCount, $"{kind}: 個体が居ないのに認知範囲が数えられている");
            }
        }

        /// <summary>
        /// 種ごとの重み統計が**徘徊時の重みも**返すこと。
        ///
        /// 群れ重み (colonySelfWeight) は徘徊時の重みにしか入らないので、
        /// 採餌側だけ見ていると E1 の対象が観察できない。
        /// </summary>
        [Test]
        public void WeightStats_CoverTheWanderingWeightsWhereTheColonyWeightLives()
        {
            var world = Run(k_Seeds[0], SimParams.Default, 600);

            int colonySheep = System.Array.IndexOf(EntityWeights.FieldNames, "colony-sheep");

            var (wanderMean, _, wanderCount) =
                EcologyStats.SpeciesWeightStats(world, EntityKind.Sheep, wandering: true);
            var (forageMean, _, forageCount) =
                EcologyStats.SpeciesWeightStats(world, EntityKind.Sheep, wandering: false);

            Assert.Greater(wanderCount, 0, "羊が居ない");
            Assert.AreEqual(SimParams.Default.colonySelfWeight, wanderMean[colonySheep], 1e-5f,
                "徘徊時の重みに群れ重みが入っていない");
            Assert.AreEqual(0f, forageMean[colonySheep], 1e-5f,
                "採餌時の重みに群れ重みが入っている（第4段 K4 の設計と違う）");
            Assert.AreEqual(wanderCount, forageCount);
        }
    }
}
