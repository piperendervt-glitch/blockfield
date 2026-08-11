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

        // ---- 段階4: 表示 (K3) ----

        /// <summary>
        /// 草の高さが場の値で決まること。段階の境界は実測分布の分位点から
        /// 決めてある（事前登録の目安 0.2/0.5/0.8 は植生場が最大0.345 にしか
        /// ならないため使えなかった）。
        /// </summary>
        [Test]
        public void Display_GrassHeightFollowsTheFieldValue()
        {
            float low = GrassView.Step(2).threshold;
            float mid = GrassView.Step(1).threshold;
            float high = GrassView.Step(0).threshold;

            Assert.Less(low, mid, "低段の閾値が中段以上になっている");
            Assert.Less(mid, high, "中段の閾値が高段以上になっている");

            Assert.AreEqual(-1, GrassView.StepFor(low - 0.001f), "最低閾値未満で草が描かれている");
            Assert.AreEqual(2, GrassView.StepFor(low));
            Assert.AreEqual(2, GrassView.StepFor(mid - 0.001f));
            Assert.AreEqual(1, GrassView.StepFor(mid));
            Assert.AreEqual(1, GrassView.StepFor(high - 0.001f));
            Assert.AreEqual(0, GrassView.StepFor(high));
            Assert.AreEqual(0, GrassView.StepFor(1f));
        }

        /// <summary>
        /// **3段階すべてが実際に出ること。**
        ///
        /// エディタ確認で草が1つも見えなかった原因の半分がこれだった。
        /// 事前登録の目安（0.2 / 0.5 / 0.8）は植生場が最大 0.345 にしか
        /// ならないことと噛み合っておらず、上2段が永久に使われなかった。
        /// 閾値を触ったときに同じ失敗を繰り返さないよう、
        /// 「どの段階にも実際にセルが入る」ことを固定する。
        /// </summary>
        [Test]
        public void Display_AllThreeHeightsActuallyAppear()
        {
            var world = MakeDiorama(12345u);
            for (int t = 0; t < 1500; t++)
            {
                Simulation.Tick(world, world.Rng, SimParams.Default);
            }

            var counts = new int[GrassView.StepCount];
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    int step = GrassView.StepFor(world.Vegetation.GetAtColumn(x, z));
                    if (step >= 0)
                    {
                        counts[step]++;
                    }
                }
            }

            for (int i = 0; i < counts.Length; i++)
            {
                Assert.Greater(counts[i], 10,
                    $"高さ段階 {i}（閾値 {GrassView.Step(i).threshold:F2}）に " +
                    $"{counts[i]} セルしか入らない。段階が実質使われていない");
            }
        }

        [Test]
        public void Display_TallerGrassIsActuallyTaller()
        {
            // 段階0が最も高い（StepFor は上から探すため）
            float tall = GrassView.Step(0).height;
            float mid = GrassView.Step(1).height;
            float low = GrassView.Step(2).height;

            Assert.Greater(tall, mid, "高い段階が中段より低い");
            Assert.Greater(mid, low, "中段が低い段階より低い");
            Assert.Greater(low, 0f);
            Assert.LessOrEqual(tall, 1f, "草がブロックより高い");
        }

        /// <summary>
        /// 描画対象のセル数が現実的な範囲に収まること。
        /// 全セルに草を描くと重く、閾値0.2 が高すぎると何も見えない。
        /// 実測（3000t、適性2,225セル）で数百セルになるのが妥当。
        /// </summary>
        [Test]
        public void Display_GrassCoversAReasonableFractionOfCells()
        {
            var world = MakeDiorama(12345u);
            for (int t = 0; t < 3000; t++)
            {
                Simulation.Tick(world, world.Rng, SimParams.Default);
            }

            int drawn = 0;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (GrassView.StepFor(world.Vegetation.GetAtColumn(x, z)) >= 0)
                    {
                        drawn++;
                    }
                }
            }

            Assert.Greater(drawn, 20, $"草が {drawn} セルしか描かれない（閾値が高すぎる）");
            Assert.Less(drawn, world.SuitableCellCount,
                "全ての適性セルに草が描かれている（濃淡が読めない）");
        }

        /// <summary>
        /// **起動直後の状態で草が描画されること。**
        ///
        /// 実機で草が1つも見えなかった二次原因の再発防止。
        /// `FieldOverlayView.Current` の初期値が `Fear` だったため、
        /// 「オーバーレイ表示中は草を隠す」判定が**起動時から成立**し、
        /// 左手Yで巡回して None に到達するまで草が永久に描画されなかった
        /// （実機ログ: 「草=0セル」が3分続き、None への切替直後に「草=108セル」）。
        ///
        /// 初期値を `Fear` に戻すとこのテストが落ちる。
        /// </summary>
        [Test]
        public void Display_GrassIsVisibleInTheStartupState()
        {
            // 起動直後は通常モードで、場のオーバーレイは何も出ていないのが正しい
            Assert.AreEqual(FieldOverlayView.Layer.None, DefaultOverlayLayer(),
                "場のオーバーレイの初期値が None でない。" +
                "起動時から『オーバーレイ表示中』とみなされ、草が描画されなくなる");
        }

        /// <summary>
        /// <see cref="FieldOverlayView.Current"/> の初期値。
        /// MonoBehaviour を生成せずに読むため、既定値を持つインスタンスから取る。
        /// </summary>
        static FieldOverlayView.Layer DefaultOverlayLayer()
        {
            var go = new UnityEngine.GameObject("overlay-probe");
            try
            {
                return go.AddComponent<FieldOverlayView>().Current;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// 通常モードでは、場のオーバーレイの状態にかかわらず草を隠さないこと。
        /// オーバーレイは診断モード限定の機能なので、
        /// 「オーバーレイが出ているか」だけで判定すると通常モードで草が消える。
        /// </summary>
        [Test]
        public void Display_GrassIsNotHiddenInNormalMode()
        {
            var go = new UnityEngine.GameObject("grass-probe");
            try
            {
                var grass = go.AddComponent<GrassView>();
                var overlay = go.AddComponent<FieldOverlayView>();
                var roomView = go.AddComponent<RoomTerrainView>();

                grass.fieldOverlay = overlay;
                grass.roomView = roomView;

                Assert.AreEqual(RoomTerrainView.ViewMode.Normal, roomView.Mode,
                    "表示モードの初期値が通常モードでない");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// **表示基準値が実測分布と乖離していないこと。**
        ///
        /// 同じ不具合を2回起こしている:
        /// - Demo 8 第2段: 死の場が灰色に見えた（不透明度が生値に比例していた）
        /// - Demo 8.5 段階4: 植生場が全面均一な緑に見えた
        ///   （ロジスティック成長で釣り合い点が 0.29 になったのに基準が 0.90 のままで、
        ///   90%点との比が 0.24 まで落ちて濃淡が潰れた）
        ///
        /// どちらも「τや釣り合い点を変えたのに表示の正規化を直さなかった」ことが原因。
        /// 目視に頼らずここで捕まえる。
        ///
        /// 【1シードから24シードの合算に変えた理由 (Demo 8 第4段 4a)】
        /// コロニー場は繁殖の痕跡で、繁殖は 1000ティックあたり 7.25 回しか起きない。
        /// 48シードのうち痕跡が1セルも立たないシードが 豚 11 / 羊 28 / 狼 33 あり、
        /// **1シードでは分布を測れない**（表示基準値が正しくても標本が空になる）。
        /// CLAUDE.md の「生態系の判定は最低48シード」と同じ理由で、
        /// 標本の足りない場を除外するのではなく、標本のほうを増やす。
        /// シードは互いに独立なので並列に回してよい（決定論は各ワールド内で閉じている）。
        /// </summary>
        [Test]
        public void Display_FieldScalesMatchTheMeasuredDistribution()
        {
            const int seedCount = 24;
            const int ticks = 1500;

            var pooled = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<float>>();
            var gate = new object();

            System.Threading.Tasks.Parallel.For(0, seedCount, i =>
            {
                // SimRunner と同じシード列（1000 + i × 7919）。実測の裏取りと母集団を揃える
                var world = MakeDiorama(1000u + (uint)i * 7919u);
                for (int t = 0; t < ticks; t++)
                {
                    Simulation.Tick(world, world.Rng, SimParams.Default);
                }

                var local = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<float>>();
                foreach (var kv in world.Fields)
                {
                    if (kv.Key == SuitabilityField.FieldName || kv.Value is not ScalarField field)
                    {
                        continue; // 適性場は静的で表示対象外
                    }

                    // 基準値の根拠と同じ母集団（0.02 以上のセル）で 90%点を取る
                    var values = new System.Collections.Generic.List<float>();
                    for (int j = 0; j < field.Length; j++)
                    {
                        float v = field.GetByIndex(j);
                        if (v >= 0.02f)
                        {
                            values.Add(v);
                        }
                    }
                    local[kv.Key] = values;
                }

                lock (gate)
                {
                    foreach (var kv in local)
                    {
                        if (!pooled.TryGetValue(kv.Key, out var list))
                        {
                            pooled[kv.Key] = list = new System.Collections.Generic.List<float>();
                        }
                        list.AddRange(kv.Value);
                    }
                }
            });

            foreach (var kv in pooled)
            {
                var values = kv.Value;
                Assert.Greater(values.Count, 20,
                    $"{kv.Key}: {seedCount}シード合算でも 0.02以上のセルが {values.Count} しかなく、" +
                    "分布を判定できない");

                values.Sort();
                float p90 = values[(int)(values.Count * 0.90)];
                float scale = EcologyStats.FieldDisplayScale(kv.Key);

                Assert.IsTrue(EcologyStats.DisplayScaleMatchesDistribution(kv.Key, p90),
                    $"{kv.Key}: 表示基準値 {scale:F2} が実測の90%点 {p90:F3} と乖離している" +
                    $"（比 {p90 / scale:F2}、許容 0.5〜2.0、{seedCount}シード合算）。" +
                    "小さすぎると濃淡が潰れ、大きすぎると全部が最大の濃さに張り付く");
            }
        }

        // ---- 段階0が既存の挙動を変えていないこと ----

        /// <summary>
        /// 成長率が効いていること（段階3で配線した）。
        /// 草の成長は抽選ではなく場の値の増加になったので、
        /// 成長率を上げれば草の総量が増える。
        /// </summary>
        [Test]
        public void Stage3_GrowthParameterIsWired()
        {
            var slow = SimParams.Default;
            slow.vegetationGrowth = SimParams.Default.vegetationGrowth * 0.5f;
            var fast = SimParams.Default;
            fast.vegetationGrowth = SimParams.Default.vegetationGrowth * 2f;

            float slowGrass = GrassAfter(slow);
            float fastGrass = GrassAfter(fast);

            Assert.Greater(fastGrass, slowGrass,
                $"成長率を上げても草が増えない（遅 {slowGrass:F1} / 速 {fastGrass:F1}）");
        }

        /// <summary>
        /// 成長がロジスティック型であること（Demo 8.5 段階3 の設計上の要）。
        ///
        /// 素直な `成長率 × 草` は破綻する。成長も減衰も草の量に比例するため
        /// 比だけで結果が決まり、**内部の釣り合い点が無い**（成長率 &gt; 減衰率で
        /// 世界が草で埋まり、下回れば消滅する）。移行前は plantCap が
        /// 暗黙の安定装置だった。
        ///
        /// (1 - 草) を掛けることで釣り合い点 1 - 減衰率/成長率 ができる。
        /// ここでは「成長率を倍にしても草が上限に張り付かない」ことで
        /// その性質を固定する。
        /// </summary>
        [Test]
        public void Stage3_GrowthIsLogisticSoTheWorldDoesNotFillWithGrass()
        {
            var fast = SimParams.Default;
            fast.vegetationGrowth = SimParams.Default.vegetationGrowth * 2f;

            var world = MakeDiorama(12345u);
            for (int t = 0; t < 1500; t++)
            {
                Simulation.Tick(world, world.Rng, fast);
            }

            // 釣り合い点は 1 - 0.02/0.056 = 0.64。上限1.0には達しない
            float perCell = world.VegetationTotal / world.SuitableCellCount;
            Assert.Less(perCell, 0.9f,
                $"成長率を倍にしたら草が上限に張り付いた（適性セルあたり {perCell:F3}）。" +
                "ロジスティック型 (1 - 草) が効いていない");
            Assert.Greater(perCell, 0.1f, "草が育っていない");
        }

        static float GrassAfter(SimParams p, uint seed = 12345u, int ticks = 800)
        {
            var world = MakeDiorama(seed);
            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            return world.VegetationTotal;
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

        /// <summary>
        /// 診断用の通行阻害が既定で無効であること (Demo 8.5 段階3)。
        ///
        /// これは移行前の「植物が通行不可だった」副作用を再現して
        /// 切り分けるための入口であり、通常の実行では使わない。
        /// 既定で有効になっていると、廃止したはずの阻害が復活して
        /// 生態系の指標が静かにずれる。
        /// </summary>
        [Test]
        public void Stage3_MovementBlockingIsDisabledByDefault()
        {
            Assert.AreEqual(0f, SimParams.Default.movementBlockVegetation, 1e-6f,
                "診断用の通行阻害が既定で有効になっている");
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
