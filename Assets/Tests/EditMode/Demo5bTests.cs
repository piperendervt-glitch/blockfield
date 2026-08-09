using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// Demo 5b: 生態系の安定条件を固定する回帰テスト。
    ///
    /// 【なぜ回帰テストにするか】以後の Demo（Demo 8 第2段以降、Demo 9/10）は
    /// この生態系を判定装置として使う。狼が全滅すると恐怖場が空になり、
    /// 個体数が上限に張り付くと場の情報量が落ちて、創発の判定そのものが成立しなくなる。
    /// パラメータを触ったときに気づけるよう、安定条件をここで固定する。
    ///
    /// 【シード数とティック数の判断】事前登録の M1 は5シード×5,000ティックだが、
    /// テストは**5シード×2,000ティック**に短縮した。
    /// - 調整前は全滅が t=454〜1,744 で起きていたので、2,000ティックあれば
    ///   同種の退行は捕捉できる
    /// - ヘッドレス実測で3シード×2,000ティックが約1秒。5シードでも2秒程度で、
    ///   pre-push ゲートの実用範囲に収まる
    /// 5,000ティックまでの確認はヘッドレスで別途実施し、prereg に記録した。
    /// </summary>
    public class Demo5bTests
    {
        const int k_Ticks = 2000;

        /// <summary>個体数を数え始めるティック（立ち上がりを除く）。</summary>
        const int k_WarmupTicks = 300;

        static readonly uint[] k_Seeds = { 12345u, 777u, 20260809u, 42u, 9001u };

        static World MakeDiorama(uint seed)
        {
            var tp = TerrainParams.Default;
            tp.seed = seed;
            tp.width = 50;
            tp.depth = 50;
            tp.maxHeight = 16;
            return World.Create(tp);
        }

        [Test]
        public void M1_WolvesSurviveOnEverySeed()
        {
            foreach (uint seed in k_Seeds)
            {
                var world = MakeDiorama(seed);
                int minWolves = int.MaxValue;

                for (int t = 0; t < k_Ticks; t++)
                {
                    Simulation.Tick(world, world.Rng);
                    if (t >= k_WarmupTicks && world.WolfCount < minWolves)
                    {
                        minWolves = world.WolfCount;
                    }
                }

                Assert.Greater(world.WolfCount, 0,
                    $"seed {seed}: 狼が全滅した。恐怖場が空になり以後の Demo の判定が成立しない");
                Assert.Greater(minWolves, 0,
                    $"seed {seed}: 狼が一度0になった（最小 {minWolves}）。" +
                    "運良く再スポーンしただけで、安定しているとは言えない");
            }
        }

        [Test]
        public void M2_PopulationsDoNotStayPinnedAtTheirCaps()
        {
            foreach (uint seed in k_Seeds)
            {
                var world = MakeDiorama(seed);
                var caps = SimParams.Default.Resolve(world.SuitableCellCount);

                int plantsAtCap = 0, animalsAtCap = 0, samples = 0;
                for (int t = 0; t < k_Ticks; t++)
                {
                    Simulation.Tick(world, world.Rng);
                    if (t < k_WarmupTicks)
                    {
                        continue;
                    }
                    samples++;
                    if (world.PlantCount >= caps.plantCap) plantsAtCap++;
                    if (world.AnimalCount >= caps.animalCap) animalsAtCap++;
                }

                // 上限に張り付いている時間が半分を超えると、場の値が飽和して
                // 「どこが濃いか」の差が失われる
                float plantRatio = (float)plantsAtCap / samples;
                float animalRatio = (float)animalsAtCap / samples;

                Assert.Less(plantRatio, 0.5f,
                    $"seed {seed}: 植物が上限に張り付いている時間の割合が {plantRatio:P0}");
                Assert.Less(animalRatio, 0.5f,
                    $"seed {seed}: 動物が上限に張り付いている時間の割合が {animalRatio:P0}");
            }
        }

        [Test]
        public void M2_PopulationsDoNotCollapse()
        {
            // 爆発の裏返し。個体数が0付近まで落ちて戻らない状態も判定装置として使えない
            foreach (uint seed in k_Seeds)
            {
                var world = MakeDiorama(seed);
                for (int t = 0; t < k_Ticks; t++)
                {
                    Simulation.Tick(world, world.Rng);
                }

                Assert.Greater(world.PlantCount, 0, $"seed {seed}: 植物が絶滅した");
                Assert.Greater(world.SheepCount + world.PigCount, 0, $"seed {seed}: 草食獣が絶滅した");
            }
        }

        [Test]
        public void M4_TunedParametersRemainDeterministic()
        {
            foreach (uint seed in k_Seeds)
            {
                var a = MakeDiorama(seed);
                var b = MakeDiorama(seed);
                for (int t = 0; t < 300; t++)
                {
                    Simulation.Tick(a, a.Rng);
                    Simulation.Tick(b, b.Rng);
                }
                Assert.AreEqual(a.ComputeContentHash(), b.ComputeContentHash(),
                    $"seed {seed}: 調整後のパラメータで決定論が壊れている");
            }
        }

        [Test]
        public void WolfHungerIsSlowerThanHerbivores()
        {
            // 調整の要。狼の死因は5シードとも100%餓死だったため、空腹の進みを遅くした。
            // ここが元に戻されたら M1 が崩れる
            var p = SimParams.Default;
            Assert.Less(p.wolfHungerPerTick, p.hungerPerTick,
                "狼の空腹進行が草食獣以上になっている（Demo 5b の調整が失われている）");
        }
    }
}
