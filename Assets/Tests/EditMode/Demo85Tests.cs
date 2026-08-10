using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// Demo 8.5: 植物の場化。
    ///
    /// 段階0（準備）の時点では、追加した API とパラメータが
    /// **既存の挙動を一切変えていないこと**を固定するのが主な役目。
    /// 段階1以降でここに摂食・成長・踏み潰しのテストが増える。
    /// </summary>
    public class Demo85Tests
    {
        static World MakeDiorama(uint seed)
        {
            var tp = TerrainParams.Default;
            tp.seed = seed;
            tp.width = 50;
            tp.depth = 50;
            tp.maxHeight = 16;
            return World.Create(tp);
        }

        // ---- ScalarField.Consume の契約 ----

        [Test]
        public void Consume_ReturnsTheAmountActuallyTaken()
        {
            var world = MakeDiorama(1u);
            world.Vegetation.SetAtColumn(10, 10, 0.8f);

            float taken = world.Vegetation.Consume(10, 10, 0.5f);

            Assert.AreEqual(0.5f, taken, 1e-6f, "要求量を全て取れるはずの場面で取れていない");
            Assert.AreEqual(0.3f, world.Vegetation.GetAtColumn(10, 10), 1e-6f, "場が減っていない");
        }

        [Test]
        public void Consume_IsLimitedByWhatIsThere()
        {
            // 「そこにあるだけ食べる」。草の薄いセルでは部分的にしか食べられず、
            // 回復も少ない — これが摂食を連続量にする意味そのもの
            var world = MakeDiorama(2u);
            world.Vegetation.SetAtColumn(5, 5, 0.2f);

            float taken = world.Vegetation.Consume(5, 5, 0.5f);

            Assert.AreEqual(0.2f, taken, 1e-6f, "そこにある量を超えて取れている");
            Assert.AreEqual(0f, world.Vegetation.GetAtColumn(5, 5), 1e-6f, "食べ尽くしたのに残っている");
        }

        [Test]
        public void Consume_NeverGoesNegative()
        {
            var world = MakeDiorama(3u);
            world.Vegetation.SetAtColumn(7, 7, 0f);

            Assert.AreEqual(0f, world.Vegetation.Consume(7, 7, 1f), 1e-6f);
            Assert.AreEqual(0f, world.Vegetation.GetAtColumn(7, 7), 1e-6f);
            Assert.GreaterOrEqual(world.Vegetation.GetAtColumn(7, 7), 0f, "場が負になっている");
        }

        [Test]
        public void Consume_IgnoresNonPositiveRequests()
        {
            var world = MakeDiorama(4u);
            world.Vegetation.SetAtColumn(3, 3, 0.4f);

            Assert.AreEqual(0f, world.Vegetation.Consume(3, 3, 0f), 1e-6f);
            Assert.AreEqual(0f, world.Vegetation.Consume(3, 3, -1f), 1e-6f);
            Assert.AreEqual(0.4f, world.Vegetation.GetAtColumn(3, 3), 1e-6f, "取らないはずの呼び出しで場が動いた");
        }

        [Test]
        public void Consume_WorksOnEveryField()
        {
            // 摂食は植生場にしか使わないが、API は ScalarField にあるので
            // 他の場でも同じ契約で動くこと（将来 踏み潰しの減算などで使う）
            var world = MakeDiorama(5u);
            world.Trample.SetAtColumn(12, 12, 0.6f);
            Assert.AreEqual(0.25f, world.Trample.Consume(12, 12, 0.25f), 1e-6f);
            Assert.AreEqual(0.35f, world.Trample.GetAtColumn(12, 12), 1e-6f);
        }

        // ---- 段階1: 摂食の場化 (K2) ----

        /// <summary>
        /// 摂食を1ティックで起こすための舞台。
        ///
        /// `hunger` は internal でしか書けないので、代わりに
        /// **1ティックで空腹になるパラメータ**を渡して摂食モードへ入れる。
        /// スポーンは止めて、見たい1頭以外が場を動かさないようにする。
        /// </summary>
        static SimParams GrazeScenario(float hungerPerTick = 0.9f)
        {
            var p = SimParams.Default;
            p.hungerPerTick = hungerPerTick;
            p.plantSpawnCandidates = 0;
            p.animalSpawnChance = 0f;
            return p;
        }

        /// <summary>3×3 を同じ値で埋める。拡散で中央が薄まらないようにするため。</summary>
        static void FillVegetation(World world, int x, int z, float value)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    world.Vegetation.SetAtColumn(x + dx, z + dz, value);
                }
            }
        }

        /// <summary>
        /// 草の茂ったセルでは移行前と同じだけ回復すること。
        /// grazeBite × grazeRecovery = 1.0 に設計した意図の検証。
        /// </summary>
        [Test]
        public void Grazing_OnRichGrassFullyRestoresHunger()
        {
            var world = MakeDiorama(11u);
            var p = GrazeScenario();
            int x = 25, z = 25;

            FillVegetation(world, x, z, 1f);
            Assert.GreaterOrEqual(world.TrySpawn(EntityKind.Sheep, x, z, 0, p), 0, "前提: 羊が湧くこと");

            float before = world.Vegetation.GetAtColumn(x, z);
            Simulation.Tick(world, world.Rng, p);

            Assert.AreEqual(0f, OnlySheep(world).hunger, 1e-5f,
                "草が十分あるのに満腹まで回復していない");
            Assert.Less(world.Vegetation.GetAtColumn(x, z), before, "植生場が減っていない");
        }

        /// <summary>
        /// 草の薄いセルでは部分的にしか回復しないこと。
        /// **これが摂食を連続量にした意味そのもの**であり、
        /// 移行前の「1本食べたら hunger=0」との最大の違い。
        /// </summary>
        [Test]
        public void Grazing_OnThinGrassOnlyPartiallyRestoresHunger()
        {
            var world = MakeDiorama(12u);
            var p = GrazeScenario();
            p.grazeThreshold = 0.05f; // 薄い草でも食べられる状況にする
            int x = 25, z = 25;

            FillVegetation(world, x, z, 0.1f);
            world.TrySpawn(EntityKind.Sheep, x, z, 0, p);
            Simulation.Tick(world, world.Rng, p);

            // 0.1 しか無いので回復は 0.1 × 2.0 = 0.2 程度。0.9 から 0.7 台に留まる
            float hunger = OnlySheep(world).hunger;
            Assert.Greater(hunger, 0.5f, $"薄い草で満腹になっている（hunger={hunger:F3}）");
            Assert.Less(hunger, 0.9f, "全く回復していない");
        }

        /// <summary>
        /// 閾値未満のセルは食べられないこと。
        /// これが無いと、拡散でにじんだだけの薄い痕跡まで餌場になり、
        /// 餓死が消える（実測: 閾値0.05 で餓死率が基準の 1/4.4）。
        /// </summary>
        [Test]
        public void Grazing_IgnoresCellsBelowTheThreshold()
        {
            var world = MakeDiorama(13u);
            var p = GrazeScenario();
            int x = 25, z = 25;

            FillVegetation(world, x, z, p.grazeThreshold * 0.5f);
            world.TrySpawn(EntityKind.Sheep, x, z, 0, p);
            Simulation.Tick(world, world.Rng, p);

            Assert.AreEqual(p.hungerPerTick, OnlySheep(world).hunger, 1e-5f,
                "閾値未満の草を食べて回復している");
        }

        /// <summary>
        /// 2頭が同じセルを食んでも破綻しないこと。
        /// 移行前は `alreadyEaten` の HashSet で二重摂食を防いでいたが、
        /// 場からの減算では2頭目が「食べ残し」を得るだけで済む。
        /// 個体側の状態がひとつ減った（M1 に寄与）。
        /// </summary>
        [Test]
        public void Grazing_TwoHerbivoresShareOneCellWithoutBreaking()
        {
            var world = MakeDiorama(14u);
            var p = GrazeScenario();
            p.grazeThreshold = 0.05f;
            int x = 25, z = 25;

            // 中央だけに草を置き、両隣の羊が同じセルを食む状況を作る
            world.Vegetation.SetAtColumn(x, z, 0.6f);
            Assert.GreaterOrEqual(world.TrySpawn(EntityKind.Sheep, x - 1, z, 0, p), 0);
            Assert.GreaterOrEqual(world.TrySpawn(EntityKind.Sheep, x + 1, z, 0, p), 0);

            Simulation.Tick(world, world.Rng, p);

            Assert.GreaterOrEqual(world.Vegetation.GetAtColumn(x, z), 0f, "植生場が負になっている");
            Assert.AreEqual(2, world.SheepCount, "羊が消えている");

            // 少なくとも1頭は食べられている（共有そのものは成立している）
            float minHunger = float.MaxValue;
            foreach (var e in world.Entities)
            {
                if (e.kind == EntityKind.Sheep && e.hunger < minHunger)
                {
                    minHunger = e.hunger;
                }
            }
            Assert.Less(minHunger, p.hungerPerTick, "どちらの羊も食べていない");
        }

        static Entity OnlySheep(World world)
        {
            foreach (var e in world.Entities)
            {
                if (e.kind == EntityKind.Sheep)
                {
                    return e;
                }
            }
            Assert.Fail("羊がいない");
            return default;
        }

        // ---- 段階0が既存の挙動を変えていないこと ----

        /// <summary>
        /// 成長率だけはまだどこからも読まれていない（段階3で配線する）。
        /// 値を変えても世界は1ビットも変わらないはず。
        ///
        /// 摂食の3つ（bite / recovery / threshold）は段階1で配線済みなので、
        /// ここでは対象外。<see cref="Stage1_GrazingParametersAreWired"/> が
        /// 逆に「効いていること」を固定する。
        /// </summary>
        [Test]
        public void Stage3_GrowthParameterIsNotWiredYet()
        {
            var changed = SimParams.Default;
            changed.vegetationGrowth = 0.99f;

            Assert.AreEqual(HashAfter(SimParams.Default), HashAfter(changed),
                "vegetationGrowth が既に挙動へ影響している（配線は段階3のはず）");
        }

        /// <summary>
        /// 摂食のパラメータが実際に効いていること。
        /// 「配線したつもりで読まれていない」を防ぐ。
        /// </summary>
        [Test]
        public void Stage1_GrazingParametersAreWired()
        {
            ulong baseline = HashAfter(SimParams.Default);

            var thinner = SimParams.Default;
            thinner.grazeBite = 0.1f;
            Assert.AreNotEqual(baseline, HashAfter(thinner), "grazeBite が読まれていない");

            var weaker = SimParams.Default;
            weaker.grazeRecovery = 0.5f;
            Assert.AreNotEqual(baseline, HashAfter(weaker), "grazeRecovery が読まれていない");

            var picky = SimParams.Default;
            picky.grazeThreshold = 0.99f;
            Assert.AreNotEqual(baseline, HashAfter(picky), "grazeThreshold が読まれていない");
        }

        static ulong HashAfter(SimParams p, uint seed = 12345u, int ticks = 300)
        {
            var world = MakeDiorama(seed);
            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            return world.ComputeContentHash();
        }

        [Test]
        public void Stage0_DefaultsAreConsistentWithTheMigrationPlan()
        {
            var p = SimParams.Default;

            // 一口 × 回復係数 = 1.0。草が十分あるセルでは移行前の
            // 「植物1本で hunger=0」と同等の回復になるよう設計している
            Assert.AreEqual(1f, p.grazeBite * p.grazeRecovery, 1e-6f,
                "一口と回復係数の積が1.0でない。草の茂ったセルでの回復量が移行前と揃わない");

            // 摂食閾値は段階1〜2では中間状態専用の暫定値（0.70）。
            // 移行前の餌場（植物のあるセルの植生場 0.76〜0.98）と揃えるための値で、
            // 低くすると拡散でにじんだ薄い場所まで餌場になり餓死が消える
            // （実測: 閾値0.05 で餌場が植物の7.6倍、餓死率が基準の 1/4.4）。
            // 段階3で植生場が「草そのもの」になったら 0.05 付近へ戻す
            Assert.Greater(p.grazeThreshold, 0f);
            Assert.LessOrEqual(p.grazeThreshold, 1f);
        }
    }
}
