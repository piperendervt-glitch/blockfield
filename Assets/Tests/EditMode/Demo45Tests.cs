using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>Demo 4.5 (G1/G2): 観測データと多層ハイトマップ化のテスト。</summary>
    public class Demo45Tests
    {
        const float k_Cell = 0.04f;

        /// <summary>床(y=0, 2m×2m) ＋ 机(y=0.8, x,z=[0.5,1.5]) の合成メッシュ。</summary>
        static (float[] verts, int[] tris) BuildFloorAndTable()
        {
            var v = new List<float>();
            var t = new List<int>();
            AddQuad(v, t, 0f, 0f, 2f, 2f, 0f);
            AddQuad(v, t, 0.5f, 0.5f, 1.5f, 1.5f, 0.8f);
            return (v.ToArray(), t.ToArray());
        }

        /// <summary>
        /// 上向き（法線 +Y）の水平四角形。巻き順 (0,2,1)/(0,3,2) で ny &gt; 0 になる
        /// （検証: e1×e2 の Y 成分が正）。積もり面として検出されるべき面に使う。
        /// </summary>
        static void AddQuad(List<float> v, List<int> t, float x0, float z0, float x1, float z1, float y)
        {
            int b = v.Count / 3;
            v.AddRange(new[] { x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1 });
            t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
        }

        /// <summary>
        /// 下向き（法線 -Y）の水平四角形。天井の裏側を模す。
        /// 積もり面として検出されてはならない（初版は絶対値判定で誤検出していた）。
        /// </summary>
        static void AddDownwardQuad(List<float> v, List<int> t, float x0, float z0, float x1, float z1, float y)
        {
            int b = v.Count / 3;
            v.AddRange(new[] { x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1 });
            t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        /// <summary>x 方向に rise だけ立ち上がる傾斜面（法線Yが閾値未満になる急勾配）。</summary>
        static void AddSlope(List<float> v, List<int> t, float x0, float z0, float w, float d, float rise)
        {
            int b = v.Count / 3;
            v.AddRange(new[] { x0, 0f, z0, x0 + w, rise, z0, x0 + w, rise, z0 + d, x0, 0f, z0 + d });
            t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        [Test]
        public void Heightmap_FloorOnlyCell_HasOneSurface()
        {
            var (verts, tris) = BuildFloorAndTable();
            var obs = MultiLayerHeightmap.Build(verts, tris, k_Cell, 0f, 0f, 50, 50);

            // (0.3, 0.2) → セル (7, 5): 机の外なので床のみ
            Assert.AreEqual(1, obs.GetHitCount(7, 5), "床のみのセルは1面のはず");
            Assert.AreEqual(0, obs.GetHit(7, 5, 0).cellY, "床は cellY=0");
        }

        [Test]
        public void Heightmap_TableCell_HasTwoSurfaces()
        {
            var (verts, tris) = BuildFloorAndTable();
            var obs = MultiLayerHeightmap.Build(verts, tris, k_Cell, 0f, 0f, 50, 50);

            // (1.0, 1.0) → セル (25, 25): 机上と床の2面
            Assert.AreEqual(2, obs.GetHitCount(25, 25), "机のあるセルは2面のはず");

            // 面は高さ昇順で保持される（床 → 机上）
            var lower = obs.GetHit(25, 25, 0);
            var upper = obs.GetHit(25, 25, 1);
            Assert.AreEqual(0, lower.cellY, "下の面は床 (cellY=0)");
            Assert.AreEqual(20, upper.cellY, "上の面は机 (0.8m / 0.04m = cellY=20)");

            // フロアIDは上から数える（0 = 最上面 = 机上）
            Assert.AreEqual(0, upper.floorId, "最上面のフロアIDは0");
            Assert.AreEqual(1, lower.floorId, "その下のフロアIDは1");

            // 確定事項①: ワールド高さも保持されている（参考値、地形合成には使わない）
            Assert.AreEqual(0.8f, upper.worldY, 1e-4f);
        }

        [Test]
        public void Heightmap_SteepSlope_IsNotASurface()
        {
            var v = new List<float>();
            var t = new List<int>();
            AddQuad(v, t, 0f, 0f, 2f, 2f, 0f);
            AddSlope(v, t, 1.6f, 0.1f, 0.3f, 0.3f, 1.2f); // 法線Y < 0.5 の急勾配

            var obs = MultiLayerHeightmap.Build(v.ToArray(), t.ToArray(), k_Cell, 0f, 0f, 50, 50);

            // (1.7, 0.2) → セル (42, 5): 傾斜面の真下だが、積もり面は床のみ
            Assert.AreEqual(1, obs.GetHitCount(42, 5), "傾斜面は積もり面にならない");
            Assert.AreEqual(0, obs.GetHit(42, 5, 0).cellY);
        }

        [Test]
        public void Heightmap_DownwardFacingSurface_IsRejected()
        {
            // 実機で天井が積もり面として検出された過検出の回帰テスト。
            // 下向き面（天井の裏）は法線条件で除外されなければならない。
            var v = new List<float>();
            var t = new List<int>();
            AddQuad(v, t, 0f, 0f, 2f, 2f, 0f);              // 床（上向き）
            AddDownwardQuad(v, t, 0f, 0f, 2f, 2f, 2.0f);    // 天井の裏（下向き）

            var obs = MultiLayerHeightmap.Build(v.ToArray(), t.ToArray(), k_Cell, 0f, 0f, 50, 50,
                null, out var stats);

            Assert.AreEqual(1, obs.GetHitCount(25, 25), "下向き面が積もり面として検出された");
            Assert.AreEqual(0, obs.GetHit(25, 25, 0).cellY, "残るのは床のみ");
            Assert.Greater(stats.RejectedByNormal, 0, "法線条件での除外がカウントされていない");
        }

        [Test]
        public void Heightmap_WallAndCeilingLabels_AreRejected()
        {
            // ラベル除外: WallFace / Ceiling の面は積もり面にしない
            var v = new List<float>();
            var t = new List<int>();
            AddQuad(v, t, 0f, 0f, 2f, 2f, 0f);      // 床（Floor 相当）
            AddQuad(v, t, 0f, 0f, 2f, 2f, 1.0f);    // 中間の面（WallFace とラベルする）

            // 高さ 1.0 付近を WallFace、それ以外を Floor として解決する
            SurfaceLabel Resolver(float wx, float wy, float wz)
                => wy > 0.5f ? SurfaceLabel.WallFace : SurfaceLabel.Floor;

            var obs = MultiLayerHeightmap.Build(v.ToArray(), t.ToArray(), k_Cell, 0f, 0f, 50, 50,
                Resolver, out var stats);

            Assert.AreEqual(1, obs.GetHitCount(25, 25), "WallFace ラベルの面が除外されていない");
            Assert.AreEqual(SurfaceLabel.Floor, obs.GetHit(25, 25, 0).label);
            Assert.Greater(stats.RejectedByLabel, 0, "ラベル除外がカウントされていない");
        }

        [Test]
        public void Heightmap_SurfaceCountPerCell_IsCapped()
        {
            // セルあたり上限 (MaxSurfacesPerCell=3)。高い順に上位N面が残る
            var v = new List<float>();
            var t = new List<int>();
            for (int i = 0; i < 6; i++)
            {
                AddQuad(v, t, 0f, 0f, 2f, 2f, i * 0.5f); // 0.0 / 0.5 / ... / 2.5 の6面
            }

            var obs = MultiLayerHeightmap.Build(v.ToArray(), t.ToArray(), k_Cell, 0f, 0f, 50, 50,
                null, out var stats);

            Assert.AreEqual(MultiLayerHeightmap.MaxSurfacesPerCell, obs.GetHitCount(25, 25),
                "セルあたりの面数が上限を超えている");
            Assert.Greater(stats.TruncatedByCap, 0, "上限での切り捨てがカウントされていない");

            // 高い順に残るので、最上面は 2.5m
            var top = obs.GetHit(25, 25, obs.GetHitCount(25, 25) - 1);
            Assert.AreEqual(2.5f, top.worldY, 1e-4f, "上位N面が残っていない");
        }

        [Test]
        public void Heightmap_Stats_ReportsSurfaceCountDistribution()
        {
            // ログ設計の補強: 1面 / 2面 / 3面以上 の分布が取れること
            var (verts, tris) = BuildFloorAndTable();
            MultiLayerHeightmap.Build(verts, tris, k_Cell, 0f, 0f, 50, 50, null, out var stats);

            Assert.Greater(stats.CellsWith1, 0, "1面のセルが数えられていない（床のみの領域）");
            Assert.Greater(stats.CellsWith2, 0, "2面のセルが数えられていない（机の領域）");
            Assert.AreEqual(0, stats.CellsWith3Plus, "床＋机だけなので3面以上は無いはず");
        }

        [Test]
        public void M4_SameMesh_ProducesIdenticalObservationHash()
        {
            // Demo 4.5 M4 の部品テスト:
            // 同一メッシュ → 同一 RoomObservation（地形合成の入力が bit-exact に一致する）
            var (verts, tris) = BuildFloorAndTable();
            var a = MultiLayerHeightmap.Build(verts, tris, k_Cell, 0f, 0f, 50, 50);
            var b = MultiLayerHeightmap.Build(verts, tris, k_Cell, 0f, 0f, 50, 50);

            Assert.AreEqual(a.ComputeContentHash(), b.ComputeContentHash(),
                "同一メッシュから異なる観測データが生成された");
            Assert.AreEqual(a.CountHits(), b.CountHits());
        }

        [Test]
        public void M4_DifferentMesh_ProducesDifferentObservationHash()
        {
            var (vertsA, trisA) = BuildFloorAndTable();
            var a = MultiLayerHeightmap.Build(vertsA, trisA, k_Cell, 0f, 0f, 50, 50);

            // 机の高さを変えたメッシュ
            var v = new List<float>();
            var t = new List<int>();
            AddQuad(v, t, 0f, 0f, 2f, 2f, 0f);
            AddQuad(v, t, 0.5f, 0.5f, 1.5f, 1.5f, 0.6f);
            var b = MultiLayerHeightmap.Build(v.ToArray(), t.ToArray(), k_Cell, 0f, 0f, 50, 50);

            Assert.AreNotEqual(a.ComputeContentHash(), b.ComputeContentHash(),
                "異なる地形なのに観測ハッシュが同一");
        }

        [Test]
        public void Observation_RecordedIntoEventLog_WithPayloadIndex()
        {
            var world = World.Create(new TerrainParams
            {
                seed = 1u, width = 50, depth = 50, maxHeight = 16,
                reliefScale = 12f, plainsAmplitude = 0.25f, mountainAmplitude = 1f,
            });

            var (verts, tris) = BuildFloorAndTable();
            var obs = MultiLayerHeightmap.Build(verts, tris, k_Cell, 0f, 0f, 50, 50);
            world.RecordObservation(obs);

            Assert.AreEqual(1, world.EventLog.Events.Count);
            var e = world.EventLog.Events[0];
            Assert.AreEqual(SimEventType.Observation, e.type);
            Assert.AreEqual(0, e.payloadIndex, "payload は付随テーブルの先頭を指すはず");
            Assert.IsTrue(e.applied);

            // SimEvent 本体は太らせず、payload は並列テーブルから引く
            Assert.AreSame(obs, world.EventLog.GetObservation(e.payloadIndex));
        }

        [Test]
        public void Replay_CarriesObservationsForward()
        {
            var tp = new TerrainParams
            {
                seed = 3u, width = 50, depth = 50, maxHeight = 16,
                reliefScale = 12f, plainsAmplitude = 0.25f, mountainAmplitude = 1f,
            };
            var sp = SimParams.Default;

            var worldA = World.Create(tp);
            var (verts, tris) = BuildFloorAndTable();
            worldA.RecordObservation(MultiLayerHeightmap.Build(verts, tris, k_Cell, 0f, 0f, 50, 50));
            for (int t = 0; t < 10; t++)
            {
                Simulation.Tick(worldA, worldA.Rng, sp);
            }

            var worldB = World.Replay(tp, sp, worldA.EventLog, 10);

            // 観測データが引き継がれ、内容が一致する（地形合成そのものは G3）
            Assert.AreEqual(1, worldB.EventLog.Observations.Count, "観測データが引き継がれていない");
            Assert.AreEqual(
                worldA.EventLog.Observations[0].ComputeContentHash(),
                worldB.EventLog.Observations[0].ComputeContentHash(),
                "引き継いだ観測データのハッシュが不一致");

            // 観測は現時点でワールド状態を変えないため、従来どおりハッシュも一致する
            Assert.AreEqual(worldA.ComputeContentHash(), worldB.ComputeContentHash());
        }

        // ---- G3: 雪積もり合成 ----

        /// <summary>床(cellY=0)全面 ＋ 中央 20x20 に机(cellY=20) の観測を作る。</summary>
        static RoomObservation BuildFloorAndTableObservation(float tableWorldY = 0.82f, float floorWorldY = 0.01f)
        {
            var obs = new RoomObservation(40, 40, k_Cell, 0f, 0f);
            for (int z = 0; z < 40; z++)
            {
                for (int x = 0; x < 40; x++)
                {
                    bool table = x >= 10 && x < 30 && z >= 10 && z < 30;
                    if (table)
                    {
                        obs.AddHit(x, z, new SurfaceHit(20, tableWorldY, 0, SurfaceLabel.Table));
                    }
                    obs.AddHit(x, z, new SurfaceHit(0, floorWorldY, table ? 1 : 0, SurfaceLabel.Floor));
                }
            }
            return obs;
        }

        [Test]
        public void Snowfall_OnlyTopmostSurface_IsCovered()
        {
            var obs = BuildFloorAndTableObservation();
            var result = SnowfallComposer.Compose(obs, SnowfallParams.Default);

            // 机のあるセル: 机上 (cellY=20 の上) にだけ積もる
            int aboveTable = CountColumn(result.Grid, 20, 20, 21, 24);
            int aboveFloorUnderTable = CountColumn(result.Grid, 20, 20, 1, 4);
            Assert.Greater(aboveTable, 0, "机の上に積もっていない");
            Assert.AreEqual(0, aboveFloorUnderTable,
                "机の下の床に積もっている（表面場は最上面のみ — prereg 論点1 決定(d)）");

            // 机の外のセル: 床の上に積もる
            Assert.Greater(CountColumn(result.Grid, 2, 2, 1, 4), 0, "床のみのセルに積もっていない");
        }

        static int CountColumn(BlockField.SimCore.Voxel.VoxelGrid grid, int x, int z, int y0, int y1)
        {
            int n = 0;
            for (int y = y0; y <= y1; y++)
            {
                if (grid.Get(new BlockField.SimCore.Voxel.Int3(x, y, z)) != BlockField.SimCore.Voxel.BlockId.Air)
                {
                    n++;
                }
            }
            return n;
        }

        [Test]
        public void Snowfall_LayerCount_StaysWithinRange()
        {
            var p = SnowfallParams.Default;
            var noise = new ValueNoise(p.seed);

            int min = int.MaxValue, max = int.MinValue;
            for (int z = 0; z < 66; z++)
            {
                for (int x = 0; x < 81; x++)
                {
                    int layers = SnowfallComposer.ComputeLayers(p, noise, x, z);
                    if (layers < min) min = layers;
                    if (layers > max) max = layers;
                }
            }

            Assert.AreEqual(p.minLayers, min, "最小層数が既定 (1) と違う");
            Assert.AreEqual(p.maxLayers, max, "最大層数が既定 (4) に届いていない — 起伏が出ない");

            // 全域が同じ層数だと「薄い起伏」にならない（実測: 1層54% / 4層19%）
            var result = SnowfallComposer.Compose(BuildFloorAndTableObservation(), p);
            int distinct = 0;
            foreach (var count in result.LayerHistogram)
            {
                if (count > 0) distinct++;
            }
            Assert.GreaterOrEqual(distinct, 3, "層数のばらつきが乏しい（起伏が平坦）");
        }

        [Test]
        public void Snowfall_IgnoresWorldY_AndIsDeterministic()
        {
            var p = SnowfallParams.Default;

            // 同一の cellY 列で worldY だけ変えても地形は一致しなければならない。
            // これは M4 の構造的保証（リプレイ経路から float 幾何演算を排除する）の検証。
            var a = SnowfallComposer.Compose(BuildFloorAndTableObservation(0.82f, 0.01f), p);
            var b = SnowfallComposer.Compose(BuildFloorAndTableObservation(999f, -999f), p);

            Assert.AreEqual(a.Grid.ComputeContentHash(), b.Grid.ComputeContentHash(),
                "worldY を変えると地形が変わる — SnowfallComposer が worldY を読んでいる");
            Assert.AreEqual(a.BlockCount, b.BlockCount);
        }

        [Test]
        public void Snowfall_DifferentSeed_ProducesDifferentTerrain()
        {
            var obs = BuildFloorAndTableObservation();

            var p1 = SnowfallParams.Default;
            var p2 = SnowfallParams.Default;
            p2.seed = 777u;

            var a = SnowfallComposer.Compose(obs, p1);
            var b = SnowfallComposer.Compose(obs, p2);

            Assert.AreNotEqual(a.Grid.ComputeContentHash(), b.Grid.ComputeContentHash(),
                "シードを変えても地形が同じ");
            Assert.AreEqual(a.SurfaceCount, b.SurfaceCount, "積もり面の数はシードに依存しない");
        }
    }
}
