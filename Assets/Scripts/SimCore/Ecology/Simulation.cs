using System;
using System.Collections.Generic;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// シムティック (Demo 2 D1/D3/D4 + Demo 3 E1-E4)。ワールド状態への決定論的な更新の入口。
    /// ティック内の処理順は固定:
    /// プレイヤー操作適用 → 植物スポーン → 動物スポーン → 植生場更新 →
    /// 草食獣（摂食/餓死/移動）→ 狼（捕食）→ 繁殖。
    /// RNG 消費順もこの順で固定されるため、同一シード＋同一ティック数で同一結果になる。
    /// </summary>
    public static class Simulation
    {
        // 空腹・繁殖の定数 (Demo 3)。可変にする必要が出たものは SimParams へ昇格済み
        const float k_HungerActionThreshold = 0.5f;
        const float k_BreedCost = 0.3f;
        const int k_BreedCooldownTicks = 20;
        const int k_WolfSightRadius = 6;
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

            // Demo 4 F2: プレイヤー操作はティック先頭で適用（RNG非消費 → リプレイ決定論を保つ）
            world.ApplyPendingActions();

            SpawnPlants(world, rng, p);
            SpawnAnimals(world, rng, p);
            UpdateVegetation(world, p);
            UpdateHerbivores(world, rng, p);
            UpdateWolves(world, rng, p);
            Breed(world, rng, p);

            world.PopulationLog.Record(world);
            world.TickCount++;
        }

        /// <summary>
        /// 植物スポーン (E1): スポーン確率 = suitability × max(植生場, 床値)。
        /// 場が繁殖の主体 — 既存植物の近傍（植生場が高い）ほど高確率、無からの発生は稀。
        /// </summary>
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
                float suitability = world.Suitability.GetAtColumn(x, z);
                float vegetation = world.Vegetation.GetAtColumn(x, z);
                float weight = suitability * Math.Max(vegetation, p.vegetationFloor);

                if (rng.NextFloat01() < weight)
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
                    world.TrySpawn(kind, x, z, facing);
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
            foreach (var e in world.Entities)
            {
                if (e.IsPlant)
                {
                    // エンティティのセルは表層の上のセル＝表面場の対象セル
                    world.Vegetation.Deposit(e.cell, p.vegetationDeposit);
                }
            }
            world.UpdateFields(p);
        }

        /// <summary>
        /// 草食獣 (E2): hunger 進行 → 餓死 / 摂食（隣接植物）/ 植生場勾配の場読み移動 / 通常徘徊。
        /// 削除はフェーズ末尾でまとめて適用する（イテレーション中のインデックス安定のため）。
        /// </summary>
        static void UpdateHerbivores(World world, Mulberry32 rng, SimParams p)
        {
            var dead = new HashSet<int>();
            var eatenPlants = new HashSet<int>();

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
                    world.UpdateEntity(i, e);
                    continue;
                }

                if (e.hunger > k_HungerActionThreshold)
                {
                    // 摂食モード: 自セル＋4近傍の植物を固定順で探す
                    int plantIndex = FindAdjacentPlant(world, e.cell, eatenPlants);

                    // 診断用の統計。導出値なので ContentHash には含めず、RNG も消費しない
                    world.FeedAttemptCount++;

                    if (plantIndex >= 0)
                    {
                        world.FeedSuccessCount++;
                        eatenPlants.Add(world.Entities[plantIndex].id);
                        dead.Add(world.Entities[plantIndex].id);
                        e.hunger = 0f; // 植生場は据え置き＝痕跡は残る
                    }
                    else
                    {
                        // 場読み: 植生場の4近傍勾配が最大の方向へ向く（決定論、RNG不使用）
                        int bestFacing = FindVegetationGradientFacing(world, e.cell, e.facing);
                        e.facing = bestFacing;
                        TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);
                    }
                }
                else
                {
                    // 満腹: 従来のランダム徘徊
                    if (rng.NextFloat01() < p.turnChance)
                    {
                        e.facing = rng.Range(0, 4);
                    }
                    TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);
                }

                world.UpdateEntity(i, e);
            }

            world.RemoveEntities(dead);
        }

        /// <summary>
        /// 狼 (E3): hunger 進行 → 餓死 / 捕食モード（視界内最近接の草食獣へ1セル/ティック接近、隣接で捕食）/
        /// 見つからなければランダム徘徊。
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

                e.hunger += p.hungerPerTick;
                if (e.hunger >= 1f)
                {
                    dead.Add(e.id);
                    world.StarvationCount++;
                    world.UpdateEntity(i, e);
                    continue;
                }

                if (e.hunger > k_HungerActionThreshold)
                {
                    int preyIndex = FindNearestHerbivore(world, e.cell, k_WolfSightRadius, dead);
                    if (preyIndex >= 0)
                    {
                        var prey = world.Entities[preyIndex];
                        int dx = prey.cell.x - e.cell.x;
                        int dz = prey.cell.z - e.cell.z;

                        if (Math.Abs(dx) + Math.Abs(dz) == 1 && Math.Abs(prey.cell.y - e.cell.y) <= 1)
                        {
                            // 隣接 → 捕食
                            dead.Add(prey.id);
                            world.PredationCount++;
                            e.hunger = 0f;
                        }
                        else
                        {
                            // 1セル/ティックで接近（主軸→副軸の順に試行、RNG不使用）
                            ChaseStep(world, ref e, dx, dz);
                        }
                    }
                    else
                    {
                        if (rng.NextFloat01() < p.turnChance)
                        {
                            e.facing = rng.Range(0, 4);
                        }
                        TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);
                    }
                }
                else
                {
                    if (rng.NextFloat01() < p.turnChance)
                    {
                        e.facing = rng.Range(0, 4);
                    }
                    TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);
                }

                world.UpdateEntity(i, e);
            }

            world.RemoveEntities(dead);
        }

        /// <summary>
        /// 繁殖 (E4): 隣接する同種の動物ペア（双方 hunger &lt; 0.3、クールダウン0）が
        /// 確率 0.1 で隣接空きセルに子をスポーン。親は hunger +0.3、20ティックのクールダウン。
        /// ペアは低い id 側だけが処理する（二重判定防止）。新生児は次ティックから行動。
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

                int partnerIndex = FindBreedPartner(world, e, p.breedHungerMax);
                if (partnerIndex < 0)
                {
                    continue;
                }

                if (rng.NextFloat01() >= p.breedChance)
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
                    childId = world.TrySpawn(e.kind, nx, nz, rng.Range(0, 4));
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
                world.UpdateEntity(childIndex, child);

                // 親双方に繁殖コスト＋クールダウン
                e.hunger += k_BreedCost;
                e.breedCooldown = k_BreedCooldownTicks;
                world.UpdateEntity(i, e);

                var partner = world.Entities[partnerIndex];
                partner.hunger += k_BreedCost;
                partner.breedCooldown = k_BreedCooldownTicks;
                world.UpdateEntity(partnerIndex, partner);
            }
        }

        /// <summary>
        /// 繁殖相手: 隣接（4近傍、高低差1以下）の同種で、双方の条件（hunger&lt;0.3、クールダウン0）を
        /// 満たす個体。ペアの二重処理を防ぐため相手の id が自分より大きい場合のみ成立。
        /// </summary>
        static int FindBreedPartner(World world, Entity e, float breedHungerMax)
        {
            foreach (var dir in FacingDirections)
            {
                int nx = e.cell.x + dir.x;
                int nz = e.cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                var cell = new Int3(nx, world.GetSurfaceHeight(nx, nz), nz);
                if (Math.Abs(cell.y - e.cell.y) > 1)
                {
                    continue;
                }
                if (!world.TryGetEntityIndexAt(cell, out int index))
                {
                    continue;
                }
                var partner = world.Entities[index];
                if (partner.kind == e.kind
                    && partner.id > e.id
                    && partner.hunger < breedHungerMax
                    && partner.breedCooldown == 0)
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>自セル＋4近傍（固定順）にある未捕食の植物のインデックスを返す（無ければ -1）。</summary>
        static int FindAdjacentPlant(World world, Int3 cell, HashSet<int> alreadyEaten)
        {
            if (TryPlantAtColumn(world, cell.x, cell.z, cell.y, alreadyEaten, out int index))
            {
                return index;
            }
            foreach (var dir in FacingDirections)
            {
                if (TryPlantAtColumn(world, cell.x + dir.x, cell.z + dir.z, cell.y, alreadyEaten, out index))
                {
                    return index;
                }
            }
            return -1;
        }

        static bool TryPlantAtColumn(World world, int x, int z, int fromY, HashSet<int> alreadyEaten, out int index)
        {
            index = -1;
            if (!world.InBounds(x, z))
            {
                return false;
            }
            var cell = new Int3(x, world.GetSurfaceHeight(x, z), z);
            if (Math.Abs(cell.y - fromY) > 1)
            {
                return false;
            }
            if (!world.TryGetEntityIndexAt(cell, out int i))
            {
                return false;
            }
            var e = world.Entities[i];
            if (!e.IsPlant || alreadyEaten.Contains(e.id))
            {
                return false;
            }
            index = i;
            return true;
        }

        /// <summary>植生場の4近傍値が最大の方向（同値は固定順の先勝ち、全て0なら現facing維持）。</summary>
        static int FindVegetationGradientFacing(World world, Int3 cell, int currentFacing)
        {
            int best = currentFacing;
            float bestValue = -1f;
            for (int f = 0; f < FacingDirections.Length; f++)
            {
                var dir = FacingDirections[f];
                int nx = cell.x + dir.x;
                int nz = cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                float v = world.Vegetation.GetAtColumn(nx, nz);
                if (v > bestValue)
                {
                    bestValue = v;
                    best = f;
                }
            }
            return bestValue > 0f ? best : currentFacing;
        }

        /// <summary>半径内（XZ距離）で最も近い草食獣のインデックス（同距離は小さい id 優先）。無ければ -1。</summary>
        static int FindNearestHerbivore(World world, Int3 from, int radius, HashSet<int> excludeIds)
        {
            int best = -1;
            int bestDistSq = radius * radius + 1;
            for (int i = 0; i < world.Entities.Count; i++)
            {
                var e = world.Entities[i];
                if (!e.IsHerbivore || excludeIds.Contains(e.id))
                {
                    continue;
                }
                int dx = e.cell.x - from.x;
                int dz = e.cell.z - from.z;
                int distSq = dx * dx + dz * dz;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>狼の追跡1歩: 主軸（絶対値の大きい軸、同値はX優先）→副軸の順に移動を試す。</summary>
        static void ChaseStep(World world, ref Entity e, int dx, int dz)
        {
            int primary = Math.Abs(dx) >= Math.Abs(dz)
                ? (dx > 0 ? 0 : 2)   // +X / -X
                : (dz > 0 ? 1 : 3);  // +Z / -Z
            int secondary = Math.Abs(dx) >= Math.Abs(dz)
                ? (dz > 0 ? 1 : 3)
                : (dx > 0 ? 0 : 2);

            if (TryStep(world, ref e, primary))
            {
                return;
            }
            if ((dx != 0 && dz != 0) || primary != secondary)
            {
                TryStep(world, ref e, secondary);
            }
        }

        static bool TryStep(World world, ref Entity e, int facing)
        {
            var dir = FacingDirections[facing];
            int nx = e.cell.x + dir.x;
            int nz = e.cell.z + dir.z;
            if (!world.InBounds(nx, nz))
            {
                return false;
            }
            int h = world.GetSurfaceHeight(nx, nz);
            var target = new Int3(nx, h, nz);
            if (Math.Abs(h - e.cell.y) > 1 || world.IsCellOccupied(target))
            {
                return false;
            }
            e.facing = facing;
            e.cell = target;
            return true;
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

            if (climbable && !world.IsCellOccupied(target))
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
