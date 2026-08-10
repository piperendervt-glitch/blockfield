using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;

namespace SimRunner
{
    /// <summary>
    /// シードごとに独立してシミュレーションを回す。
    ///
    /// 【並列化しても決定論は壊れない】各シードは自前の <see cref="World"/> と
    /// <see cref="BlockField.SimCore.Rng.Mulberry32"/> を持ち、共有状態が無い。
    /// 結果はシード順に並べ直してから集計するので、出力も実行順に依存しない。
    /// </summary>
    public static class Runner
    {
        /// <summary>個体数時系列の間引き間隔。3,000ティックで300行になる。</summary>
        public const int SeriesInterval = 10;

        /// <summary>立ち上がりを最小値の集計から除く期間。Demo 5b 以来の慣習。</summary>
        public const int WarmupTicks = 300;

        public static uint[] MakeSeeds(int count)
        {
            var seeds = new uint[count];
            for (int i = 0; i < count; i++)
            {
                seeds[i] = 1000u + (uint)i * 7919u;
            }
            return seeds;
        }

        public static World MakeWorld(uint seed, int size)
        {
            var tp = TerrainParams.Default;
            tp.seed = seed;
            tp.width = size;
            tp.depth = size;
            tp.maxHeight = 16;
            return World.Create(tp);
        }

        /// <summary>
        /// 条件×シードを並列に回す。progress は完了ごとに呼ばれる（スレッド安全にすること）。
        /// </summary>
        public static List<SeedResult> Run(
            IReadOnlyList<Condition> conditions, uint[] seeds, int ticks, int size,
            int maxParallel, Action<int, int>? progress = null,
            CheckpointWriter? checkpoints = null, int checkpointInterval = 0)
        {
            var jobs = new List<(Condition c, uint seed)>();
            foreach (var c in conditions)
            {
                foreach (uint s in seeds)
                {
                    jobs.Add((c, s));
                }
            }

            var results = new SeedResult[jobs.Count];
            int done = 0;

            Parallel.For(0, jobs.Count,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
                i =>
                {
                    var (c, seed) = jobs[i];
                    results[i] = RunOne(c, seed, ticks, size, checkpoints, checkpointInterval);
                    int n = System.Threading.Interlocked.Increment(ref done);
                    progress?.Invoke(n, jobs.Count);
                });

            var list = new List<SeedResult>(results);
            // 実行順に依存しない出力にする
            list.Sort((a, b) =>
            {
                int byCondition = string.CompareOrdinal(a.Condition, b.Condition);
                return byCondition != 0 ? byCondition : a.Seed.CompareTo(b.Seed);
            });
            return list;
        }

        public static SeedResult RunOne(
            Condition condition, uint seed, int ticks, int size,
            CheckpointWriter? checkpoints = null, int checkpointInterval = 0)
        {
            var world = MakeWorld(seed, size);
            var p = condition.Params;

            var r = new SeedResult
            {
                Condition = condition.Name,
                Seed = seed,
                Ticks = ticks,
                SuitableCells = world.SuitableCellCount,
                MinPlants = int.MaxValue,
                MinHerbivores = int.MaxValue,
                MinWolves = int.MaxValue,
            };

            // ティックループだけを測る。ワールド生成と Collect は含めない (M1 の基準値)。
            // ループ内の集計は個体数カウンタの読み出し（O(1)）だけに留めてあり、
            // 場の走査のような重い処理は入れていない。時間の汚染を避けるため
            long plantSum = 0, herbivoreSum = 0, wolfSum = 0, entitySum = 0, samples = 0;
            var simWatch = System.Diagnostics.Stopwatch.StartNew();

            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(world, world.Rng, p);

                if (t >= WarmupTicks)
                {
                    int herbivores = world.SheepCount + world.PigCount;
                    if (world.PlantCount < r.MinPlants) r.MinPlants = world.PlantCount;
                    if (herbivores < r.MinHerbivores) r.MinHerbivores = herbivores;
                    if (world.WolfCount < r.MinWolves) r.MinWolves = world.WolfCount;

                    plantSum += world.PlantCount;
                    herbivoreSum += herbivores;
                    wolfSum += world.WolfCount;
                    entitySum += world.Entities.Count;
                    samples++;
                }

                // 長時間実験で系列が肥大しないよう、1ランあたり最大2,000点に抑える。
                // 3,000ティックなら従来どおり10ティック間隔、10万ティックなら50ティック間隔
                if (t % Math.Max(SeriesInterval, ticks / 2000) == 0)
                {
                    r.Series.Add((world.TickCount, world.PlantCount,
                        world.SheepCount + world.PigCount, world.WolfCount));
                }

                if (checkpoints != null && checkpointInterval > 0 && world.TickCount % checkpointInterval == 0)
                {
                    checkpoints.Write(condition.Name, world);
                }
            }

            // 最終状態も1点残す。ただし最後のティックが既に間隔と一致していれば
            // 二重に書かない（同じ tick の行が2つ並ぶと集計時に重複する）
            if (checkpoints != null && checkpointInterval > 0 &&
                world.TickCount % checkpointInterval != 0)
            {
                checkpoints.Write(condition.Name, world);
            }

            simWatch.Stop();
            r.SimMilliseconds = simWatch.Elapsed.TotalMilliseconds;

            if (samples > 0)
            {
                r.MeanPlantsPerTick = (double)plantSum / samples;
                r.MeanHerbivoresPerTick = (double)herbivoreSum / samples;
                r.MeanWolvesPerTick = (double)wolfSum / samples;
                r.MeanEntitiesPerTick = (double)entitySum / samples;
            }

            if (r.MinPlants == int.MaxValue) r.MinPlants = world.PlantCount;
            if (r.MinHerbivores == int.MaxValue) r.MinHerbivores = world.SheepCount + world.PigCount;
            if (r.MinWolves == int.MaxValue) r.MinWolves = world.WolfCount;

            Collect(world, r);
            return r;
        }

        static void Collect(World world, SeedResult r)
        {
            r.Plants = world.PlantCount;
            r.Sheep = world.SheepCount;
            r.Pigs = world.PigCount;
            r.Wolves = world.WolfCount;
            r.Starvation = world.StarvationCount;
            r.Predation = world.PredationCount;
            r.Births = world.BirthCount;
            r.TrampleCrush = world.TrampleCrushCount;
            r.MovesAwayFromFear = world.HerbivoreMovesAwayFromFear;
            r.MovesTowardFear = world.HerbivoreMovesTowardFear;
            r.ContentHash = world.ComputeContentHash();

            foreach (var kv in world.Fields)
            {
                if (kv.Value is ScalarField sf)
                {
                    var (mean, max) = EcologyStats.FieldStats(sf);
                    r.FieldMean[kv.Key] = mean;
                    r.FieldMax[kv.Key] = max;
                }
            }

            var (high, low) = EcologyStats.TrampleQuartileThresholds(world);
            r.TrampleQuartileHigh = high;
            r.TrampleQuartileLow = low;

            // 個体あたりの餓死・捕食。分母は延べ生存動物ティック数
            // （PopulationLog が毎ティック記録している）
            r.StarvationPerAnimalPerKiloTick = EcologyStats.StarvationPerAnimalPerKiloTick(world);
            long animalTicks = 0;
            var log = world.PopulationLog;
            for (int i = 0; i < log.Count; i++)
            {
                animalTicks += log.GetSample(i).Animals;
            }
            r.PredationPerAnimalPerKiloTick = animalTicks > 0
                ? 1000.0 * world.PredationCount / animalTicks
                : 0;

            // 適性セルあたりの植生場。植物を場にしたあとも比較できる唯一の指標
            double vegSum = 0;
            int suitable = 0;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) <= 0f)
                    {
                        continue;
                    }
                    suitable++;
                    vegSum += world.Vegetation.GetAtColumn(x, z);
                }
            }
            r.VegetationPerSuitableCell = suitable > 0 ? vegSum / suitable : 0;

            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) <= 0f)
                    {
                        continue;
                    }
                    if (world.Death.GetAtColumn(x, z) >= EcologyStats.GraveyardThreshold) r.GraveCells++;
                    else r.NonGraveCells++;

                    float tv = world.Trample.GetAtColumn(x, z);
                    if (tv >= high) r.HighTrampleCells++;
                    else if (tv <= low) r.LowTrampleCells++;
                }
            }

            foreach (var e in world.Entities)
            {
                if (!e.IsPlant)
                {
                    continue;
                }
                if (world.Death.GetAtColumn(e.cell.x, e.cell.z) >= EcologyStats.GraveyardThreshold) r.GravePlants++;
                else r.NonGravePlants++;

                float tv = world.Trample.GetAtColumn(e.cell.x, e.cell.z);
                if (tv >= high) r.HighTramplePlants++;
                else if (tv <= low) r.LowTramplePlants++;
            }
        }
    }
}
