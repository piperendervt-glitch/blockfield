using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>Demo 3 (E1-E5): 植生場・摂食・捕食・繁殖・ログのテスト。</summary>
    public class Demo3Tests
    {
        static TerrainParams WorldParams(uint seed) => new TerrainParams
        {
            seed = seed,
            width = 50,
            depth = 50,
            maxHeight = 16,
            reliefScale = 12f,
            plainsAmplitude = 0.25f,
            mountainAmplitude = 1f,
        };

        /// <summary>スポーンと移動を止めたパラメータ（シナリオを固定配置で検証するため）。</summary>
        static SimParams ScenarioParams()
        {
            var p = SimParams.Default;
            p.plantSpawnCandidates = 0;
            p.animalSpawnCandidates = 0;
            p.moveChance = 0f;
            p.turnChance = 0f;
            return p;
        }

        /// <summary>X方向に len 連続で suitability 1.0 かつ同一高さの行を探す。</summary>
        static (int x, int z) FindFlatRun(World world, int len)
        {
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x + len <= world.Width; x++)
                {
                    bool ok = true;
                    int h = world.GetSurfaceHeight(x, z);
                    for (int i = 0; i < len; i++)
                    {
                        if (world.Suitability.GetAtColumn(x + i, z) < 1f || world.GetSurfaceHeight(x + i, z) != h)
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                    {
                        return (x, z);
                    }
                }
            }
            Assert.Fail("平坦な行が見つからない（テスト前提の不成立）");
            return (0, 0);
        }

        [Test]
        public void M4_VegetationClustersAroundExistingGrass()
        {
            // Demo 3 M4: 場の効果の定量判定。
            //
            // 【Demo 8.5 で測り方が変わった】移行前は「新しく湧いた植物の過半が
            // 既存植物の3セル以内か」を数えていた。草が場になり「新しく湧いた1本」が
            // 存在しなくなったので、同じ主張を連続量で測る:
            // **草の濃いセルの周囲は、薄いセルの周囲より草が濃い**（＝面として固まる）。
            //
            // 成長がロジスティック型（成長率 × 草 × (1-草)）なので草のある所ほど増える。
            // この相関が消えたら自己増殖が壊れている。
            // 1,500ティック回す。400ティックでは草がまだ育ちきらず
            // 「濃いセル」が1つも無かった（実測: 400tで平均0.115・濃い0セル、
            // 1,500tで平均0.246・濃い1,995セル）。ロジスティック成長は
            // 立ち上がりが緩やかなので、移行前より長い窓が要る
            var world = World.Create(WorldParams(5u));
            var p = SimParams.Default;
            p.animalSpawnCandidates = 0;

            for (int t = 0; t < 1500; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }

            double richNeighbours = 0, poorNeighbours = 0;
            int richN = 0, poorN = 0;
            for (int z = 1; z < world.Depth - 1; z++)
            {
                for (int x = 1; x < world.Width - 1; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) <= 0f)
                    {
                        continue;
                    }
                    float v = world.Vegetation.GetAtColumn(x, z);
                    float around =
                        (world.Vegetation.GetAtColumn(x + 1, z) + world.Vegetation.GetAtColumn(x - 1, z)
                         + world.Vegetation.GetAtColumn(x, z + 1) + world.Vegetation.GetAtColumn(x, z - 1)) / 4f;

                    if (v >= 0.2f) { richNeighbours += around; richN++; }
                    else if (v <= 0.05f) { poorNeighbours += around; poorN++; }
                }
            }

            Assert.Greater(richN, 20, "草の濃いセルが少なすぎて判定できない");
            Assert.Greater(poorN, 20, "草の薄いセルが少なすぎて判定できない");
            Assert.Greater(richNeighbours / richN, poorNeighbours / poorN,
                $"草が固まっていない（濃いセルの周囲 {richNeighbours / richN:F3} 対 " +
                $"薄いセルの周囲 {poorNeighbours / poorN:F3}）");
        }

        [Test]
        public void Eating_AdjacentGrassIsConsumed_AndHungerDrops()
        {
            // Demo 8.5: 草が場になったので「植物1本が消えたか」ではなく
            // 「隣のセルの草が減ったか」を見る
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();
            var (x, z) = FindFlatRun(world, 2);
            world.TrySpawn(EntityKind.Sheep, x, z, 0);
            world.Vegetation.SetAtColumn(x + 1, z, 1f);
            float before = world.Vegetation.GetAtColumn(x + 1, z);

            bool eaten = false;
            for (int t = 0; t < 80 && !eaten; t++)
            {
                Simulation.Tick(world, world.Rng, p);
                if (world.Vegetation.GetAtColumn(x + 1, z) < before * 0.5f)
                {
                    eaten = true;
                }
            }

            Assert.IsTrue(eaten, "80ティック以内に隣の草が食べられなかった");
            Assert.AreEqual(1, world.SheepCount, "羊が消えている");
            // hunger は摂食でリセットされている（摂食直後 < 摂食閾値0.5）
            foreach (var e in world.Entities)
            {
                if (e.kind == EntityKind.Sheep)
                {
                    Assert.Less(e.hunger, 0.5f, "摂食後も hunger が高いまま");
                }
            }
        }

        [Test]
        public void Starvation_WithoutPlants_HerbivoreDies()
        {
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();
            var (x, z) = FindFlatRun(world, 1);
            world.TrySpawn(EntityKind.Sheep, x, z, 0);

            for (int t = 0; t < 130; t++) // hungerPerTick 0.01 → 餓死は100ティック目
            {
                Simulation.Tick(world, world.Rng, p);
            }

            Assert.AreEqual(0, world.AnimalCount, "植物ゼロの世界で草食獣が餓死していない");
            Assert.AreEqual(1, world.StarvationCount);
        }

        [Test]
        public void Predation_WolfEatsNearbySheep()
        {
            // Demo 8 で狼の追跡が「視界内の最近接へ直進」から「獲物場の勾配を追う」に
            // 変わり、この筋書きの前提が2つ崩れた:
            // 1. 旧実装の追跡 (ChaseStep) は moveChance を無視して動いていた。
            //    このシナリオは moveChance=0 で移動を止めているため、新実装の狼は
            //    通常の移動規則に従って**一歩も動けない**
            // 2. 3セル離れた状態からの追跡成否は匂いの育ち方に左右され、
            //    シードによって成功したりしなかったりする（実測 6シード中3成功）
            //
            // そこで本テストは**リファクタで保存された機構＝隣接捕食**の検証に絞る。
            // 匂いを辿って接近できることは Demo8Tests の場読みテストと、
            // M5（1000ティックあたり捕食回数が半減しない）で担保する。
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();

            // 狼の空腹速度はこのシナリオのパラメータとして速める。
            // Demo 5b で既定を 0.003 に下げた（狼の全滅対策）結果、狼が捕食モードに
            // 入るのが 167ティック目になり、羊が餓死する100ティックに間に合わなくなった。
            // ここで見たいのは「隣接した空腹の狼が羊を食べる」機構であって
            // 空腹の速さではないので、速めて機構だけを検証する
            p.wolfHungerPerTick = 0.02f;

            var (x, z) = FindFlatRun(world, 4);
            world.TrySpawn(EntityKind.Wolf, x, z, 0);
            world.TrySpawn(EntityKind.Sheep, x + 1, z, 0);

            // 狼は hunger > 0.5（26ティック目）から捕食モードに入る。
            // 羊が餓死する100ティックより前に捕食が起きること
            int predatedAt = -1;
            for (int t = 0; t < 100 && predatedAt < 0; t++)
            {
                Simulation.Tick(world, world.Rng, p);
                if (world.PredationCount > 0)
                {
                    predatedAt = t;
                }
            }

            Assert.GreaterOrEqual(predatedAt, 0, "100ティック以内に隣接する羊が捕食されなかった");
            Assert.AreEqual(0, world.SheepCount, "羊が残っている");
            Assert.AreEqual(0, world.StarvationCount, "餓死が起きている（捕食ではない可能性）");
        }

        [Test]
        public void Breeding_AdjacentFedPair_ProducesChild_AndSetsCooldown()
        {
            // シード1で出生がティック11に決定論的に発生することをハーネスで確認済み
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();
            var (x, z) = FindFlatRun(world, 3);
            world.TrySpawn(EntityKind.Sheep, x, z, 0);
            world.TrySpawn(EntityKind.Sheep, x + 1, z, 0);

            int bornAt = -1;
            for (int t = 0; t < 100 && bornAt < 0; t++)
            {
                Simulation.Tick(world, world.Rng, p);
                if (world.SheepCount >= 3)
                {
                    bornAt = t;
                }
            }

            Assert.GreaterOrEqual(bornAt, 0, "100ティック以内に子が生まれなかった");
            Assert.GreaterOrEqual(world.BirthCount, 1);

            // クールダウン: 出生直後、関与個体（親2＋子）にクールダウンが設定されている
            int withCooldown = 0;
            foreach (var e in world.Entities)
            {
                if (e.IsAnimal && e.breedCooldown > 0)
                {
                    withCooldown++;
                }
            }
            Assert.GreaterOrEqual(withCooldown, 3, "繁殖後のクールダウンが設定されていない");
        }

        [Test]
        public void PopulationLog_OutputsHeaderAndRows()
        {
            var world = World.Create(WorldParams(1u));
            for (int t = 0; t < 50; t++)
            {
                Simulation.Tick(world, world.Rng, SimParams.Default);
            }

            string csv = world.PopulationLog.ToCsv();
            var lines = csv.TrimEnd('\r', '\n').Split('\n');
            Assert.AreEqual(51, lines.Length, "ヘッダ1行＋50ティック分の行が必要");
            Assert.AreEqual("tick,plants,sheep,pigs,wolves", lines[0].TrimEnd('\r'));
        }
    }
}
