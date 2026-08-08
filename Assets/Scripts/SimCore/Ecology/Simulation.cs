using BlockField.SimCore.Rng;
using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// シムティック (Demo 2 D1/D3/D4)。ワールド状態への決定論的な更新の入口。
    /// ティック内の処理順は固定: スポーン（植物→動物）→ 徘徊。
    /// RNG 消費順もこの順で固定されるため、同一シード＋同一ティック数で同一結果になる。
    /// </summary>
    public static class Simulation
    {
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
            SpawnPlants(world, rng, p);
            SpawnAnimals(world, rng, p);
            Wander(world, rng, p);
            world.TickCount++;
        }

        /// <summary>植物スポーン: ランダム候補セルを抽選し、suitability × 乱数 &gt; 閾値 かつ未占有ならスポーン。</summary>
        static void SpawnPlants(World world, Mulberry32 rng, SimParams p)
        {
            if (world.PlantCount >= p.plantCap)
            {
                return;
            }

            for (int i = 0; i < p.plantSpawnCandidates; i++)
            {
                int x = rng.Range(0, world.Width);
                int z = rng.Range(0, world.Depth);
                float suitability = world.Suitability.Get(x, z);

                if (suitability * rng.NextFloat01() > p.plantSpawnThreshold)
                {
                    // GrassTuft : Flower = 3 : 1
                    var kind = rng.Range(0, 4) == 0 ? EntityKind.Flower : EntityKind.GrassTuft;
                    world.TrySpawn(kind, x, z, 0);
                    if (world.PlantCount >= p.plantCap)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>動物スポーン: 低頻度抽選。suitability 1.0 のセルのみ。Sheep : Pig = 1 : 1。</summary>
        static void SpawnAnimals(World world, Mulberry32 rng, SimParams p)
        {
            if (world.AnimalCount >= p.animalCap)
            {
                return;
            }

            for (int i = 0; i < p.animalSpawnCandidates; i++)
            {
                int x = rng.Range(0, world.Width);
                int z = rng.Range(0, world.Depth);

                if (world.Suitability.Get(x, z) >= 1f && rng.NextFloat01() < p.animalSpawnChance)
                {
                    var kind = rng.Range(0, 2) == 0 ? EntityKind.Sheep : EntityKind.Pig;
                    int facing = rng.Range(0, 4);
                    world.TrySpawn(kind, x, z, facing);
                    if (world.AnimalCount >= p.animalCap)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 徘徊 (D4)。動物のみ。処理順はエンティティリスト順（id 昇順）で固定。
        /// - 確率 turnChance でランダム向き変更（移動とは独立）
        /// - 確率 moveChance で facing 方向へ移動試行:
        ///   高低差1以下かつ未占有なら昇降して移動、それ以外（高低差2以上/地形外/占有）は向き変更のみ
        /// </summary>
        static void Wander(World world, Mulberry32 rng, SimParams p)
        {
            for (int i = 0; i < world.Entities.Count; i++)
            {
                var e = world.Entities[i];
                if (!e.IsAnimal)
                {
                    continue;
                }

                if (rng.NextFloat01() < p.turnChance)
                {
                    e.facing = rng.Range(0, 4);
                }

                if (rng.NextFloat01() < p.moveChance)
                {
                    var dir = FacingDirections[e.facing];
                    int nx = e.cell.x + dir.x;
                    int nz = e.cell.z + dir.z;

                    if (!world.InBounds(nx, nz))
                    {
                        e.facing = rng.Range(0, 4);
                    }
                    else
                    {
                        int targetHeight = world.GetSurfaceHeight(nx, nz);
                        var target = new Int3(nx, targetHeight, nz);
                        bool climbable = System.Math.Abs(targetHeight - e.cell.y) <= 1;

                        if (climbable && !world.IsCellOccupied(target))
                        {
                            e.cell = target;
                        }
                        else
                        {
                            e.facing = rng.Range(0, 4);
                        }
                    }
                }

                world.UpdateEntity(i, e);
            }
        }
    }
}
