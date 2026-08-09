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

        /// <summary>この値以上の恐怖場を「避ける対象」とみなす (Demo 8 第2段 M3 の測定用)。</summary>
        const float k_FearRelevantThreshold = 0.05f;
        const int k_BreedCooldownTicks = 20;
        // k_WolfSightRadius は Demo 8 H3 で削除した。
        // 「視界半径」という個体側の概念そのものが、獲物場の拡散距離に置き換わったため
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

                // 死の場が養分として植物スポーンを後押しする (Demo 8 第2段 I2)。
                // 案A（スポーン重みに直接掛ける）を採った。案B（植生場の deposit を
                // 増やす）は場を経由するぶん間接的で効果が出るまで遅く、
                // 「墓場に草が茂る」という因果が読み取りにくいため。
                // ここは死が生を生む経路そのものなので、直接的な方が意図が伝わる
                float nutrient = 1f + p.deathNutrientBoost * world.Death.GetAtColumn(x, z);
                float weight = suitability * Math.Max(vegetation, p.vegetationFloor) * nutrient;

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
                    DepositDeath(world, p, e.cell, starved: true);
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
                        // 場読み: 「餌に寄りたい」と「危険を避けたい」を1つのスコアで合成する
                        e.facing = FindForagingFacing(world, p, e.cell, e.facing);
                        MoveHerbivore(world, rng, p, ref e);
                    }
                }
                else
                {
                    // 満腹: ランダム徘徊。ただし**危険は空腹でなくても避ける** (Demo 8 第2段 I3)。
                    // 第1段では恐怖場を摂食モードのときしか読まず、読む時間が半分しか
                    // なかったことが迂回行動を定量できなかった最大の原因だった
                    if (rng.NextFloat01() < p.turnChance)
                    {
                        e.facing = rng.Range(0, 4);
                    }
                    e.facing = FindWanderFacing(world, p, e.cell, e.facing);
                    MoveHerbivore(world, rng, p, ref e);
                }

                // 獲物場への書き込み (Demo 8 H3)。移動後の位置に匂いを残す。
                // 狼はこの痕跡だけを頼りに追跡する（個体を探す処理は無くなった）
                if (world.InBounds(e.cell.x, e.cell.z))
                {
                    world.Prey.Deposit(e.cell, p.preyDeposit);
                }

                world.UpdateEntity(i, e);
            }

            world.RemoveEntities(dead);
        }

        /// <summary>
        /// 狼 (E3 → Demo 8 H3): hunger 進行 → 餓死 / 捕食モード（獲物場の勾配を追う。
        /// 隣接に草食獣がいれば捕食）/ 見つからなければランダム徘徊。
        ///
        /// Demo 8 で「視界内の全個体を走査して最近接を選ぶ」処理を獲物場の読み出しに
        /// 置き換えた。狼は獲物の位置を知らず、匂いの濃い方へ進むだけである。
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

                // 狼は草食獣より空腹の進みが遅い (Demo 5b)。捕食者は大きな獲物を稀に食べる
                e.hunger += p.wolfHungerPerTick;
                if (e.hunger >= 1f)
                {
                    dead.Add(e.id);
                    world.StarvationCount++;
                    DepositDeath(world, p, e.cell, starved: true);
                    world.UpdateEntity(i, e);
                    continue;
                }

                if (e.hunger > k_HungerActionThreshold)
                {
                    // 隣接の草食獣は捕食する。この判定だけは個体を見る（4近傍の固定順、O(1)）
                    int preyIndex = FindAdjacentHerbivore(world, e.cell, dead);
                    if (preyIndex >= 0)
                    {
                        var prey = world.Entities[preyIndex];
                        dead.Add(prey.id);
                        world.PredationCount++;
                        DepositDeath(world, p, prey.cell, starved: false);
                        e.hunger = 0f;
                    }
                    else
                    {
                        // 場読み: 獲物場の濃い方へ向く（決定論、RNG不使用）。
                        // 匂いが全く無ければ現在の向きのまま＝ランダム徘徊に落ちる
                        e.facing = FindPreyGradientFacing(world, e.cell, e.facing);
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

                // 恐怖場への書き込み (Demo 8 H1)。移動後の位置に危険の痕跡を残す。
                // これが積もって「けもの道」になり、草食獣の迂回行動を生む
                if (world.InBounds(e.cell.x, e.cell.z))
                {
                    world.Fear.Deposit(e.cell, p.fearDeposit);
                }

                // 診断用の統計（導出値、ハッシュ非対象）: 何歩歩いたか
                if (e.cell != world.Entities[i].cell)
                {
                    world.WolfStepCount++;
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

        /// <summary>
        /// 草食獣の採餌方向 (Demo 8 H2)。4近傍を
        /// スコア = w_veg × 植生場 − w_fear × 恐怖場 で評価し、最大のセルへ向く。
        ///
        /// 【設計意図】2つの場を1つのスコアに合成することで、
        /// 「腹が減っているが危険な場所にある草」という葛藤が表現できる。
        /// 濃い草があっても、そこに狼の痕跡が濃く残っていれば近寄らない。
        /// w_fear を w_veg より大きくしてあるので、迷ったら安全側に倒れる。
        /// 個体は狼を見ておらず、場に残された痕跡だけを読んでいる。
        ///
        /// 同値は固定順の先勝ち。RNG は使わない（＝決定論）。
        /// 場読みの振る舞いを直接検証できるよう public にしている。
        /// </summary>
        public static int FindForagingFacing(World world, SimParams p, Int3 cell, int currentFacing)
        {
            int best = currentFacing;
            float bestScore = 0f;
            bool found = false;

            for (int f = 0; f < FacingDirections.Length; f++)
            {
                var dir = FacingDirections[f];
                int nx = cell.x + dir.x;
                int nz = cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }

                float score = p.herbivoreVegetationWeight * world.Vegetation.GetAtColumn(nx, nz)
                    - p.herbivoreFearWeight * world.Fear.GetAtColumn(nx, nz);

                if (!found || score > bestScore)
                {
                    bestScore = score;
                    best = f;
                    found = true;
                }
            }

            // スコアが全て負（草が無く危険だけ）でも**最大の方向を選ぶ**。
            // ここで「魅力が無いから向きを変えない」としてしまうと、
            // 危険地帯のど真ん中にいるときに限って回避が働かず、
            // 恐怖場を入れた意味が無くなる（実測で M2 が不成立になった原因）。
            // 負の中の最大＝最も危険が薄い方向へ逃げる、が正しい振る舞い
            return found ? best : currentFacing;
        }

        /// <summary>
        /// 死んだ場所に痕跡を残す (Demo 8 第2段 I1)。
        /// 餓死は死骸がそのまま残るので大きく、被食は肉が持ち去られるので小さい。
        /// 死因で量を変えることで「どんな死が起きたか」まで場が記憶する。
        /// </summary>
        static void DepositDeath(World world, SimParams p, Int3 cell, bool starved)
        {
            if (!world.InBounds(cell.x, cell.z))
            {
                return;
            }
            world.Death.Deposit(cell, starved ? p.deathDepositStarved : p.deathDepositPredated);
        }

        /// <summary>
        /// 満腹時の徘徊方向 (Demo 8 第2段 I3)。
        /// 今の向きを基本にしつつ、恐怖場の濃い方向だけは避ける。
        ///
        /// スコア = (今の向きなら wanderBias) − w_fear × 恐怖場。
        /// 恐怖が薄いうちは今の向きが勝つ（＝従来どおりのランダム徘徊）が、
        /// 恐怖が wanderBias / w_fear を超えると向きを変える。
        /// 「空腹でなくても危険は避ける」を、徘徊の性質を壊さずに入れるための形。
        /// </summary>
        public static int FindWanderFacing(World world, SimParams p, Int3 cell, int currentFacing)
        {
            const float wanderBias = 0.15f;

            int best = currentFacing;
            float bestScore = float.NegativeInfinity;
            for (int f = 0; f < FacingDirections.Length; f++)
            {
                var dir = FacingDirections[f];
                int nx = cell.x + dir.x;
                int nz = cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                float score = (f == currentFacing ? wanderBias : 0f)
                    - p.herbivoreFearWeight * world.Fear.GetAtColumn(nx, nz);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }
            return best;
        }

        /// <summary>
        /// 草食獣の移動。実際に動いたときだけ、恐怖場の高い方へ動いたか低い方へ動いたかを数える
        /// (Demo 8 第2段 M3 の指標)。
        ///
        /// 「恐怖場が高いセルにいた割合」で測ると、動かなかったティックも、
        /// 危険な場所で捕食されて消えた個体の分も混ざって回避の効果が埋もれる。
        /// **移動が起きた瞬間だけ**を見れば、その1歩が危険を避ける方向だったかを直接測れる。
        /// </summary>
        static void MoveHerbivore(World world, Mulberry32 rng, SimParams p, ref Entity e)
        {
            var from = e.cell;
            float fearFrom = world.Fear.GetAtColumn(from.x, from.z);

            // 近傍に意味のある恐怖があるときだけ数える。
            // 恐怖場は全体の1%未満にしか立たないので、全ての移動を数えると
            // 「避けようのない移動」に薄められて効果が見えなくなる（実測で差1%以下）
            bool nearFear = fearFrom >= k_FearRelevantThreshold;
            if (!nearFear)
            {
                foreach (var d in FacingDirections)
                {
                    int nx = from.x + d.x, nz = from.z + d.z;
                    if (world.InBounds(nx, nz) && world.Fear.GetAtColumn(nx, nz) >= k_FearRelevantThreshold)
                    {
                        nearFear = true;
                        break;
                    }
                }
            }

            TryMove(world, rng, p, ref e, allowRandomTurnOnBlock: true);

            if (e.cell == from || !nearFear)
            {
                return;
            }

            float fearTo = world.Fear.GetAtColumn(e.cell.x, e.cell.z);
            if (fearTo < fearFrom)
            {
                world.HerbivoreMovesAwayFromFear++;
            }
            else if (fearTo > fearFrom)
            {
                world.HerbivoreMovesTowardFear++;
            }
        }

        /// <summary>
        /// 狼の追跡方向 (Demo 8 H3)。獲物場の4近傍値が最大の方向へ向く。
        /// 全て0（匂いが届いていない）なら現在の向きを維持し、通常の徘徊に落ちる。
        ///
        /// これが「半径6セル以内の全個体を走査して最近接を選ぶ」処理の置き換えである。
        /// 個体数に依らず4近傍を見るだけなので O(1)。
        /// 場読みの振る舞いを直接検証できるよう public にしている。
        /// </summary>
        public static int FindPreyGradientFacing(World world, Int3 cell, int currentFacing)
        {
            int best = currentFacing;
            float bestValue = 0f;
            for (int f = 0; f < FacingDirections.Length; f++)
            {
                var dir = FacingDirections[f];
                int nx = cell.x + dir.x;
                int nz = cell.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                float v = world.Prey.GetAtColumn(nx, nz);
                if (v > bestValue)
                {
                    bestValue = v;
                    best = f;
                }
            }
            return bestValue > 0f ? best : currentFacing;
        }

        /// <summary>
        /// 隣接（4近傍、高低差1以下）の草食獣のインデックス。無ければ -1。
        /// 捕食の成立判定だけは個体を見る必要があるが、走査するのは4セルだけである。
        /// </summary>
        static int FindAdjacentHerbivore(World world, Int3 from, HashSet<int> excludeIds)
        {
            foreach (var dir in FacingDirections)
            {
                int nx = from.x + dir.x;
                int nz = from.z + dir.z;
                if (!world.InBounds(nx, nz))
                {
                    continue;
                }
                var cell = new Int3(nx, world.GetSurfaceHeight(nx, nz), nz);
                if (Math.Abs(cell.y - from.y) > 1)
                {
                    continue;
                }
                if (!world.TryGetEntityIndexAt(cell, out int index))
                {
                    continue;
                }
                var e = world.Entities[index];
                if (e.IsHerbivore && !excludeIds.Contains(e.id))
                {
                    return index;
                }
            }
            return -1;
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
