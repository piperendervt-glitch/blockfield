using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>Demo 4 (F1/F2/F4): 出所属性・イベントログ・リプレイ・場フィードバックのテスト。</summary>
    public class Demo4Tests
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

        static SimParams FrozenParams()
        {
            var p = SimParams.Default;
            p.plantSpawnCandidates = 0;
            p.animalSpawnCandidates = 0;
            p.moveChance = 0f;
            p.turnChance = 0f;
            return p;
        }

        // ---- F1: 出所属性 ----

        [Test]
        public void Origin_SetGet_Roundtrip_AndAirNormalization()
        {
            var grid = new VoxelGrid();
            var cell = new Int3(3, 2, 1);

            grid.SetBlock(cell, BlockId.Stone, BlockOrigin.Player);
            Assert.AreEqual(BlockOrigin.Player, grid.GetOrigin(cell));

            grid.SetBlock(cell, BlockId.Grass, BlockOrigin.Ecology);
            Assert.AreEqual(BlockOrigin.Ecology, grid.GetOrigin(cell));

            // Air 化すると origin は Terrain に正規化される（経緯によらず同ハッシュ）
            grid.SetBlock(cell, BlockId.Air, BlockOrigin.Player);
            Assert.AreEqual(BlockOrigin.Terrain, grid.GetOrigin(cell));

            // 未生成チャンクは Terrain
            Assert.AreEqual(BlockOrigin.Terrain, grid.GetOrigin(new Int3(999, 0, 999)));
        }

        [Test]
        public void TrySetBlockEcology_RejectsPlayerCells_AcceptsOthers()
        {
            var grid = new VoxelGrid();
            var playerCell = new Int3(0, 0, 0);
            var terrainCell = new Int3(1, 0, 0);

            grid.SetBlock(playerCell, BlockId.Stone, BlockOrigin.Player);
            grid.SetBlock(terrainCell, BlockId.Dirt, BlockOrigin.Terrain);

            Assert.IsFalse(grid.TrySetBlockEcology(playerCell, BlockId.Grass), "Player セルへの生態系書き込みが通ってしまった");
            Assert.AreEqual(BlockId.Stone, grid.Get(playerCell), "拒否時に状態が変わっている");
            Assert.AreEqual(BlockOrigin.Player, grid.GetOrigin(playerCell));

            Assert.IsTrue(grid.TrySetBlockEcology(terrainCell, BlockId.Grass));
            Assert.AreEqual(BlockId.Grass, grid.Get(terrainCell));
            Assert.AreEqual(BlockOrigin.Ecology, grid.GetOrigin(terrainCell));
        }

        [Test]
        public void ContentHash_ChangesWithOrigin()
        {
            var a = new VoxelGrid();
            var b = new VoxelGrid();
            a.SetBlock(new Int3(0, 0, 0), BlockId.Stone, BlockOrigin.Terrain);
            b.SetBlock(new Int3(0, 0, 0), BlockId.Stone, BlockOrigin.Player);

            Assert.AreNotEqual(a.ComputeContentHash(), b.ComputeContentHash(),
                "同一ブロック・異なる出所でハッシュが同じ（origin がハッシュに含まれていない）");
        }

        // ---- M2: 固定レイヤー ----

        [Test]
        public void M2_PlayerPlacedBlock_SurvivesEcologyTicks()
        {
            var world = World.Create(WorldParams(1u));
            var cell = new Int3(25, world.GetSurfaceHeight(25, 25), 25);
            world.EnqueuePlayerAction(SimEventType.PlayerPlace, cell, BlockId.Stone);

            for (int t = 0; t < 300; t++)
            {
                Simulation.Tick(world, world.Rng, SimParams.Default);
            }

            Assert.AreEqual(BlockId.Stone, world.Grid.Get(cell), "プレイヤー設置ブロックが変化した");
            Assert.AreEqual(BlockOrigin.Player, world.Grid.GetOrigin(cell), "出所属性が変化した");
            Assert.AreEqual(0f, world.Suitability.GetAtColumn(25, 25), "Player ブロックが表層のセルの suitability が 0 でない");
            Assert.IsFalse(world.Grid.TrySetBlockEcology(cell, BlockId.Air), "生態系書き込み口が Player セルを書き換えられてしまう");
        }

        // ---- M3: リプレイ決定論 ----

        [Test]
        public void M3_ReplayWithEvents_MatchesSequentialRun()
        {
            var tp = WorldParams(7u);
            var sp = SimParams.Default;

            // 逐次実行: ランダムな Place/Break/BreakPlant を投入（有効・無効混在）
            var worldA = World.Create(tp);
            var actionRng = new Mulberry32(0xAC7104u);
            int enqueued = 0;
            for (int t = 0; t < 60; t++)
            {
                if (t % 3 == 0 && enqueued < 20)
                {
                    int roll = actionRng.Range(0, 3);
                    var type = roll == 0 ? SimEventType.PlayerPlace
                        : roll == 1 ? SimEventType.PlayerBreak
                        : SimEventType.PlayerBreakPlant;
                    var cell = new Int3(actionRng.Range(0, 50), actionRng.Range(0, 16), actionRng.Range(0, 50));
                    worldA.EnqueuePlayerAction(type, cell, BlockId.Stone);
                    enqueued++;
                }

                // 有効な PlayerBreakPlant を確実に1件含める（実在の植物セルを狙う）
                if (t == 30)
                {
                    foreach (var e in worldA.Entities)
                    {
                        if (e.IsPlant)
                        {
                            worldA.EnqueuePlayerAction(SimEventType.PlayerBreakPlant, e.cell, BlockId.Air);
                            break;
                        }
                    }
                }

                Simulation.Tick(worldA, worldA.Rng, sp);
            }

            int applied = 0;
            bool plantBreakApplied = false;
            foreach (var e in worldA.EventLog.Events)
            {
                if (e.applied) applied++;
                if (e.applied && e.type == SimEventType.PlayerBreakPlant) plantBreakApplied = true;
            }
            Assert.GreaterOrEqual(worldA.EventLog.Events.Count, 20, "全操作がログに残っていない");
            Assert.Greater(applied, 0, "有効操作が1つもない（テスト前提が弱い）");
            Assert.Less(applied, worldA.EventLog.Events.Count, "無効操作が1つもない（テスト前提が弱い）");
            Assert.IsTrue(plantBreakApplied, "有効な PlayerBreakPlant がイベント列に含まれていない");

            // リプレイ: f(シード, イベントログ)
            var events = new List<SimEvent>(worldA.EventLog.Events);
            var worldB = World.Replay(tp, sp, events, 60);
            Assert.AreEqual(worldA.ComputeContentHash(), worldB.ComputeContentHash(),
                "同一シード＋同一イベント列のリプレイでハッシュが不一致");
        }

        [Test]
        public void M3_ReplayWithoutEvents_MatchesPlainRun()
        {
            var tp = WorldParams(7u);
            var sp = SimParams.Default;

            var worldA = World.Create(tp);
            for (int t = 0; t < 60; t++)
            {
                Simulation.Tick(worldA, worldA.Rng, sp);
            }

            var worldB = World.Replay(tp, sp, new List<SimEvent>(), 60);
            Assert.AreEqual(worldA.ComputeContentHash(), worldB.ComputeContentHash());
        }

        // ---- M4: 場フィードバック ----

        [Test]
        public void M4_BreakingPlantBlocks_LowersVegetationAndNearbySpawns()
        {
            var tp = WorldParams(5u);
            var sp = SimParams.Default;

            var control = World.Create(tp);
            var test = World.Create(tp);
            for (int t = 0; t < 120; t++)
            {
                Simulation.Tick(control, control.Rng, sp);
                Simulation.Tick(test, test.Rng, sp);
            }

            // test 側: 最初の5植物の下のブロックを Break
            var broken = new List<Int3>();
            foreach (var e in test.Entities)
            {
                if (e.IsPlant && broken.Count < 5)
                {
                    test.EnqueuePlayerAction(SimEventType.PlayerBreak,
                        new Int3(e.cell.x, e.cell.y - 1, e.cell.z), BlockId.Air);
                    broken.Add(new Int3(e.cell.x, e.cell.y - 1, e.cell.z));
                }
            }
            Assert.AreEqual(5, broken.Count, "テスト前提: 植物が5つ以上あること");

            // 適用ティック直後、破壊列の植生場が対照より十分低い（×0.5適用）
            Simulation.Tick(control, control.Rng, sp);
            Simulation.Tick(test, test.Rng, sp);
            float vegControl = 0f, vegTest = 0f;
            foreach (var b in broken)
            {
                vegControl += control.Vegetation.GetAtColumn(b.x, b.z);
                vegTest += test.Vegetation.GetAtColumn(b.x, b.z);
            }
            Assert.Less(vegTest, vegControl * 0.75f, $"植生場が半減していない (ctrl={vegControl:F2}, test={vegTest:F2})");

            // 100ティックの近傍新規スポーン数（破壊列そのものはセル占有解放の交絡があるため除外）
            int controlNear = 0, testNear = 0;
            var prevIds = new HashSet<int>();
            for (int t = 0; t < 100; t++)
            {
                controlNear += TickAndCountNearSpawns(control, sp, broken, prevIds);
                testNear += TickAndCountNearSpawns(test, sp, broken, prevIds);
            }

            Assert.Less(testNear, controlNear,
                $"破壊後の近傍スポーンが対照より少なくない (ctrl={controlNear}, test={testNear})");
        }

        static int TickAndCountNearSpawns(World w, SimParams sp, List<Int3> centers, HashSet<int> scratch)
        {
            scratch.Clear();
            foreach (var e in w.Entities)
            {
                if (e.IsPlant) scratch.Add(e.id);
            }
            Simulation.Tick(w, w.Rng, sp);

            int near = 0;
            foreach (var e in w.Entities)
            {
                if (!e.IsPlant || scratch.Contains(e.id)) continue;
                bool onBrokenColumn = false;
                foreach (var c in centers)
                {
                    if (c.x == e.cell.x && c.z == e.cell.z)
                    {
                        onBrokenColumn = true;
                        break;
                    }
                }
                if (onBrokenColumn) continue;
                foreach (var c in centers)
                {
                    if (System.Math.Max(System.Math.Abs(c.x - e.cell.x), System.Math.Abs(c.z - e.cell.z)) <= 3)
                    {
                        near++;
                        break;
                    }
                }
            }
            return near;
        }

        [Test]
        public void PlayerBreakPlant_RemovesPlantOnly_TerrainUnchanged()
        {
            var world = World.Create(WorldParams(1u));
            var p = FrozenParams();
            var (x, z) = FindFlatRunFor(world, 1);
            int h = world.GetSurfaceHeight(x, z);
            var plantCell = new Int3(x, h, z);
            var surfaceCell = new Int3(x, h - 1, z);
            var surfaceBlock = world.Grid.Get(surfaceCell);

            Assert.GreaterOrEqual(world.TrySpawn(EntityKind.GrassTuft, x, z, 0), 0);

            // 1ティック回して植生場に書き込ませてから破壊
            Simulation.Tick(world, world.Rng, p);
            float vegBefore = world.Vegetation.GetAtColumn(x, z);
            Assert.Greater(vegBefore, 0f, "テスト前提: 植生場に書き込みがあること");

            world.EnqueuePlayerAction(SimEventType.PlayerBreakPlant, plantCell, BlockId.Air);
            Simulation.Tick(world, world.Rng, p);

            Assert.AreEqual(0, world.PlantCount, "植物が消えていない");
            Assert.AreEqual(surfaceBlock, world.Grid.Get(surfaceCell), "地形が変更された（植物のみ消えるはず）");
            Assert.AreEqual(h, world.GetSurfaceHeight(x, z), "表層高さが変わった");
            Assert.Less(world.Vegetation.GetAtColumn(x, z), vegBefore * 0.75f, "植生場が半減していない");

            var buffer = new List<Int3>();
            Assert.IsFalse(world.ConsumeDirtyChunks(buffer), "地形不変更なのに DirtyChunks が積まれた");
        }

        static (int x, int z) FindFlatRunFor(World world, int len)
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
            Assert.Fail("平坦な行が見つからない");
            return (0, 0);
        }

        // ---- 無効操作・DirtyChunks ----

        [Test]
        public void InvalidActions_DoNotChangeState_ButAreLogged()
        {
            var world = World.Create(WorldParams(1u));
            var p = FrozenParams();

            int h = world.GetSurfaceHeight(10, 10);
            var airCell = new Int3(10, h + 2, 10);     // Air（Break 無効）
            var solidCell = new Int3(10, h - 1, 10);   // 非Air（Place 無効）
            var solidBlock = world.Grid.Get(solidCell);

            world.EnqueuePlayerAction(SimEventType.PlayerBreak, airCell, BlockId.Air);
            world.EnqueuePlayerAction(SimEventType.PlayerPlace, solidCell, BlockId.Stone);
            Simulation.Tick(world, world.Rng, p);

            Assert.AreEqual(BlockId.Air, world.Grid.Get(airCell), "無効な Break で状態が変わった");
            Assert.AreEqual(solidBlock, world.Grid.Get(solidCell), "無効な Place で状態が変わった");
            Assert.AreEqual(2, world.EventLog.Events.Count);
            Assert.IsFalse(world.EventLog.Events[0].applied);
            Assert.IsFalse(world.EventLog.Events[1].applied);

            var buffer = new List<Int3>();
            Assert.IsFalse(world.ConsumeDirtyChunks(buffer), "無効操作で DirtyChunks が積まれた");
        }

        [Test]
        public void DirtyChunks_MarkedOnAppliedAction()
        {
            var world = World.Create(WorldParams(1u));
            var p = FrozenParams();

            var cell = new Int3(25, world.GetSurfaceHeight(25, 25), 25);
            world.EnqueuePlayerAction(SimEventType.PlayerPlace, cell, BlockId.Stone);
            Simulation.Tick(world, world.Rng, p);

            var buffer = new List<Int3>();
            Assert.IsTrue(world.ConsumeDirtyChunks(buffer), "適用操作で DirtyChunks が積まれていない");
            CollectionAssert.Contains(buffer, VoxelGrid.WorldToChunk(cell));

            // 取り出し後はクリアされている
            Assert.IsFalse(world.ConsumeDirtyChunks(buffer));
        }
    }
}
