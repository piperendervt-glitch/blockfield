namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 生態系の健全性指標 (Demo 5a の診断表示)。UnityEngine 非依存の純関数。
    ///
    /// 【なぜ必要か】実機判定を目測に頼っていたため、「植物が少ない」「餓死が多い」を
    /// 数値で言えなかった。スケールが変わっても比較できる形（密度・率）で出す。
    ///
    /// 【表示と真実の分離】ここは World を**読むだけ**で、一切書き換えない。
    /// 入力はいずれも導出値（ContentHash に含まれない統計）なので、
    /// この計算が決定論に影響することはない。
    /// </summary>
    public static class EcologyStats
    {
        /// <summary>
        /// 箱庭 (50x50, seed=12345, 適性2,225セル) をヘッドレス3,000ティック走らせた実測値。
        /// Demo 3 まで「観察できる生態系」として成立していた水準であり、
        /// 部屋スケールでの目標値になる。実機パネルに並べて比較する。
        /// </summary>
        public static class DioramaReference
        {
            /// <summary>植物密度（適性セルに対する割合）。実測 200 / 2,225。</summary>
            public const float PlantDensity = 0.0899f;

            /// <summary>動物密度（適性セルに対する割合）。実測 30 / 2,225。</summary>
            public const float AnimalDensity = 0.0135f;

            /// <summary>1個体・1000ティックあたりの餓死数。実測 餓死69 / 延べ生存ティック。</summary>
            public const float StarvationPerAnimalPerKiloTick = 0.939f;

            /// <summary>摂食成功率（成功1,108 / 試行11,380）。</summary>
            public const float FeedSuccessRate = 0.0974f;

            /// <summary>
            /// 上の値に達するまでのティック数。5分のセッション（約300ティック）では
            /// まだ立ち上がり途中なので、そのまま比べると低く出る。
            /// 参考: 箱庭の t300 実測は 植物1.71% / 動物0.94% / 摂食成功率0.025。
            /// </summary>
            public const int SettledTicks = 3000;
        }

        /// <summary>
        /// 表示のときに「濃い」とみなす場の値 (Demo 8 第2段)。
        /// この値以上を最大の濃さで描く。
        ///
        /// 【なぜ場ごとに変えるのか】場によって値の桁が違う。
        /// 3,000ティック時点で 0.02 以上のセルの分布（seed 12345 実測）:
        ///   死  : 73セル   中央値 0.037 / 90%点 0.100 / 最大 0.955
        ///   恐怖: 206セル  中央値 0.082 / 90%点 0.183 / 最大 1.000
        ///   獲物: 1602セル 中央値 0.075 / 90%点 0.188 / 最大 0.765
        ///   植生: 1579セル 中央値 0.169 / 90%点 0.930 / 最大 0.980
        /// 死の場は τ が大きいぶん1セルの値が小さく溜まるため、
        /// 共通のスケールで描くとほぼ見えない（エディタ確認で「灰色に見える」と
        /// 報告された原因。不透明度が生値に比例していたので中央値のセルは
        /// alpha=7/255 ＝ 不透明度3%になり、下の地形が透けていた）。
        ///
        /// 最大値で正規化する手も取らない。死の場は飽和した数セル（0.955）と
        /// 大多数の薄いセル（0.037）の差が25倍あり、最大で割ると大多数が潰れる。
        /// 各場の**90%点**を基準にした固定値を使う。
        /// </summary>
        public static float FieldDisplayScale(string fieldName) => fieldName switch
        {
            DeathField.FieldName => 0.10f,
            FearField.FieldName => 0.20f,
            PreyField.FieldName => 0.20f,
            // 踏み荒らし: deposit 0.35 / τ≈50 で、通行のある筋は 0.3〜1.0 に達する。
            // 恐怖場より濃く出るので基準も高めに取る
            TrampleField.FieldName => 0.35f,
            _ => 0.90f, // 植生
        };

        /// <summary>
        /// 場の値を表示の濃さ 0〜1 に写す (Demo 8 第2段)。
        /// 平方根を通すのは、薄いセルの差を潰さないため。
        /// 線形だと 0.02 と 0.05 の差が濃さ 0.2 と 0.5 の差にしかならず、
        /// 墓場の広がりが読めない。
        /// </summary>
        public static float FieldDisplayIntensity(float value, float scale)
        {
            if (scale <= 0f)
            {
                return 0f;
            }
            float t = value / scale;
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return (float)System.Math.Sqrt(t);
        }

        /// <summary>場の平均値と最大値 (Demo 8 H4)。</summary>
        public static (float mean, float max) FieldStats(ScalarField field)
        {
            float sum = 0f, max = 0f;
            int n = field.Length;
            for (int i = 0; i < n; i++)
            {
                float v = field.GetByIndex(i);
                sum += v;
                if (v > max) max = v;
            }
            return (n > 0 ? sum / n : 0f, max);
        }

        /// <summary>
        /// 草食獣が恐怖場のどれくらい濃い所にいるか (Demo 8 M2 の指標)。
        /// 「草食獣のいるセルの恐怖場の平均 ÷ 場全体の平均」で、1.0 未満なら薄い所を選んでいる。
        ///
        /// 【注意】この比だけでは回避の効果を切り分けられない。恐怖の濃い所にいた個体は
        /// 捕食されて消えるため、回避していなくても生き残りは薄い所に偏る。
        /// 実測でも w_fear=0 の対照が 0.34〜0.72 と 1.0 を大きく下回った。
        /// あくまで「今どのくらい危険な場所にいるか」の目安として読むこと。
        /// </summary>
        public static float HerbivoreFearExposure(World world)
        {
            var (fieldMean, _) = FieldStats(world.Fear);
            if (fieldMean <= 0f)
            {
                return 0f;
            }

            float sum = 0f;
            int n = 0;
            foreach (var e in world.Entities)
            {
                if (!e.IsHerbivore)
                {
                    continue;
                }
                sum += world.Fear.GetAtColumn(e.cell.x, e.cell.z);
                n++;
            }
            return n > 0 ? sum / n / fieldMean : 0f;
        }

        /// <summary>
        /// 狼の移動距離あたりの捕食成功率 (Demo 8 M5 の指標)。
        /// 狼が何歩歩いて1匹捕らえたか＝場読みでの追跡がどれだけ効率的か。
        /// 1000歩あたりの捕食回数で返す。
        /// </summary>
        public static float PredationPerKiloWolfStep(World world) =>
            world.WolfStepCount > 0 ? 1000f * world.PredationCount / world.WolfStepCount : 0f;

        /// <summary>
        /// 迂回行動の指標 (Demo 8 第2段 M3)。
        /// 草食獣が実際に動いた1歩のうち、恐怖場の低い方へ向かった割合。
        /// 0.5 が「避けても寄りもしない」で、それより大きければ避けている。
        ///
        /// 移動が起きた瞬間だけを数えるので、動かなかったティックの希釈も、
        /// 危険地帯で捕食されて消えた個体による生存者バイアスも入らない。
        /// 第1段の「恐怖場が高いセルにいた割合」はこの2つに埋もれて符号が
        /// 一定しなかったため、指標そのものを作り直したもの。
        /// </summary>
        public static float FearAvoidanceRatio(World world)
        {
            int total = world.HerbivoreMovesAwayFromFear + world.HerbivoreMovesTowardFear;
            return total > 0 ? (float)world.HerbivoreMovesAwayFromFear / total : 0f;
        }

        /// <summary>
        /// 「墓場」とみなす死の場の下限。
        ///
        /// 【0.02 の根拠】この閾値は「墓場が何セルになるか」を決める。標本が小さいと
        /// 密度の推定が雑音に埋もれるので、実測で決めた（3000t・5シード・k=20）:
        ///   閾値 0.05 → 35セル、比 1.01（ただし5シード中3つが 0.00。標本不足）
        ///   閾値 0.02 → 103セル、比 0.86（ゼロのシード無し）  ← 採用
        ///   閾値 0.01 → 205セル、比 0.64（薄いセルが混ざり効果が薄まる）
        /// （この5シードの比 0.86 自体もばらつきが大きい。48シードでは 0.523）
        /// 0.02 が「安定して測れる最小の標本数」と「効果の濃さ」の折り合う点。
        ///
        /// なお拡散のパス数を増やして墓場を広げる手（Demo 8 第1段で確立した原則）は
        /// ここでは効かない。死の総量は「死者数 × τ」で頭打ちなので、広げるほど
        /// 1セルあたりの値が閾値を割り、かえって墓場が狭くなる（実測 passes 1→32 で
        /// 35セル→3セル）。だから広げるのではなく閾値を場の実寸に合わせた。
        /// </summary>
        public const float GraveyardThreshold = 0.02f;

        /// <summary>
        /// 養分効果の指標 (Demo 8 第2段 M2)。
        /// **墓場セル**（死の場が <see cref="GraveyardThreshold"/> 以上）と
        /// **それ以外のセル**で植物密度を比べ、(墓場, それ以外) を返す。
        ///
        /// 【この比の読み方 — 1.0 が基準ではない】
        /// 事前登録では「墓場の方が高ければ養分効果あり」としていたが、これは成立しない。
        /// 餓死は**餌の乏しい場所で起きる**ので、墓場はもともと植物の少ない土地に偏る。
        /// 実測で養分係数を0にした対照でも比は 0.35 しかない。
        /// したがって判定は**対照 (deathNutrientBoost=0) との比較**で行う。
        ///
        /// 48シード×3,000ティックの実測（少ないシードでは全く当てにならない指標なので、
        /// 必ずこの規模で測ること）:
        ///   k=0  → 0.348（交絡だけの値。ここが原点）
        ///   k=4  → 0.442
        ///   k=20 → 0.523（採用値。約1.5倍）
        /// **1.0 は超えない**。養分効果は交絡による不利を半分ほど埋めるにとどまる。
        /// k をさらに上げても狼の全滅が増える側に振れるだけで、1.0 には届かない。
        ///
        /// 事前登録の当初案「上位25%セルと下位25%セル」も棄却した。死の場は全体の
        /// 数%にしか立たないため上位25%の閾値がほぼ0になり、墓場でないセルまで
        /// 「上位」に入って比較が成立しなかった（実測で比0.05〜1.08と符号が定まらず）。
        ///
        /// まだ墓場が1つも無ければ (0, 0) を返す。
        /// </summary>
        public static (float graveyard, float elsewhere) PlantDensityByDeathField(World world)
        {
            int graveCells = 0, otherCells = 0;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) <= 0f)
                    {
                        continue; // そもそも植物が湧けないセルは分母から外す
                    }
                    if (world.Death.GetAtColumn(x, z) >= GraveyardThreshold) graveCells++;
                    else otherCells++;
                }
            }
            if (graveCells == 0)
            {
                return (0f, 0f);
            }

            int gravePlants = 0, otherPlants = 0;
            foreach (var e in world.Entities)
            {
                if (!e.IsPlant)
                {
                    continue;
                }
                if (world.Death.GetAtColumn(e.cell.x, e.cell.z) >= GraveyardThreshold) gravePlants++;
                else otherPlants++;
            }

            return (
                (float)gravePlants / graveCells,
                otherCells > 0 ? (float)otherPlants / otherCells : 0f);
        }

        /// <summary>
        /// 踏み荒らしの効果の指標 (Demo 8 第3段 M2)。
        /// 踏み荒らし場の**上位25%セルと下位25%セル**で植物密度を比べ、
        /// (上位, 下位) を返す。踏み荒らしが効いていれば上位の方が低い。
        ///
        /// 【死の場と違って四分位が使える理由】死の場は世界の数%にしか立たないため
        /// 上位25%の閾値がほぼ0になって比較が成立しなかったが、踏み荒らし場は
        /// 動物が歩いたセル全てに書かれるので広く分布する。四分位で十分に分かれる。
        /// 実際に分かれているかは <see cref="TrampleQuartileThresholds"/> で確認できる。
        ///
        /// 分母からは適性0のセル（そもそも植物が湧けない）を除く。
        /// </summary>
        public static (float trampled, float quiet) PlantDensityByTrample(World world)
        {
            var (high, low) = TrampleQuartileThresholds(world);
            if (high <= 0f)
            {
                return (0f, 0f); // まだ誰も歩いていない
            }

            int highCells = 0, lowCells = 0;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) <= 0f)
                    {
                        continue;
                    }
                    float v = world.Trample.GetAtColumn(x, z);
                    if (v >= high) highCells++;
                    else if (v <= low) lowCells++;
                }
            }
            if (highCells == 0 || lowCells == 0)
            {
                return (0f, 0f);
            }

            int highPlants = 0, lowPlants = 0;
            foreach (var e in world.Entities)
            {
                if (!e.IsPlant)
                {
                    continue;
                }
                float v = world.Trample.GetAtColumn(e.cell.x, e.cell.z);
                if (v >= high) highPlants++;
                else if (v <= low) lowPlants++;
            }

            return ((float)highPlants / highCells, (float)lowPlants / lowCells);
        }

        /// <summary>
        /// 踏み荒らし場の上位25%・下位25%の閾値（適性セルのみ）。
        /// 両者が十分に離れていなければ M2 の比較は意味を持たないので、
        /// 指標と一緒に確認できるよう公開する。
        /// </summary>
        public static (float high, float low) TrampleQuartileThresholds(World world)
        {
            var values = new System.Collections.Generic.List<float>();
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) > 0f)
                    {
                        values.Add(world.Trample.GetAtColumn(x, z));
                    }
                }
            }
            if (values.Count < 4)
            {
                return (0f, 0f);
            }
            values.Sort();
            return (values[values.Count * 3 / 4], values[values.Count / 4]);
        }

        /// <summary>
        /// 個体が持つ重みの平均 (Demo 8 第3段 J2)。
        /// 進化本体はまだ無いので全個体が同じ値になるはずで、
        /// **ここがばらついたら継承か初期化が壊れている**という監視の意味を持つ。
        /// 将来「集団が進化したか」を見る窓口でもある。
        /// 返すのは場の名前昇順の配列（<see cref="EntityWeights.FieldNames"/> と対応）。
        /// </summary>
        public static (float[] mean, float[] variance, int count) AnimalForageWeightStats(World world)
        {
            var mean = new float[EntityWeights.FieldCount];
            var variance = new float[EntityWeights.FieldCount];
            int n = 0;

            foreach (var e in world.Entities)
            {
                if (!e.IsAnimal)
                {
                    continue;
                }
                n++;
                for (int i = 0; i < EntityWeights.FieldCount; i++)
                {
                    mean[i] += e.forageWeights[i];
                }
            }
            if (n == 0)
            {
                return (mean, variance, 0);
            }
            for (int i = 0; i < EntityWeights.FieldCount; i++)
            {
                mean[i] /= n;
            }

            foreach (var e in world.Entities)
            {
                if (!e.IsAnimal)
                {
                    continue;
                }
                for (int i = 0; i < EntityWeights.FieldCount; i++)
                {
                    float d = e.forageWeights[i] - mean[i];
                    variance[i] += d * d;
                }
            }
            for (int i = 0; i < EntityWeights.FieldCount; i++)
            {
                variance[i] /= n;
            }
            return (mean, variance, n);
        }

        /// <summary>植物密度 = 植物数 / 適性セル数。</summary>
        public static float PlantDensity(World world) =>
            world.SuitableCellCount > 0 ? (float)world.PlantCount / world.SuitableCellCount : 0f;

        /// <summary>動物密度 = 動物数 / 適性セル数。</summary>
        public static float AnimalDensity(World world) =>
            world.SuitableCellCount > 0 ? (float)world.AnimalCount / world.SuitableCellCount : 0f;

        /// <summary>
        /// 摂食成功率 = 成功回数 / 試行回数（累計）。
        /// 「空腹になった個体が実際に食べ物にありつけた割合」であり、
        /// 餓死の絶対数より直接的に「食べ物が足りているか」を表す。
        /// </summary>
        public static float FeedSuccessRate(World world) =>
            world.FeedAttemptCount > 0 ? (float)world.FeedSuccessCount / world.FeedAttemptCount : 0f;

        /// <summary>
        /// 直近 <paramref name="window"/> 回ぶんの摂食成功率。
        /// 呼び出し側が前回の累計値を覚えておき、その差分を渡す
        /// （World に窓を持たせると状態が増えるので、窓は表示側の責務にする）。
        /// </summary>
        public static float FeedSuccessRateDelta(int successDelta, int attemptDelta) =>
            attemptDelta > 0 ? (float)successDelta / attemptDelta : 0f;

        /// <summary>
        /// 1個体・1000ティックあたりの餓死数。
        ///
        /// 分母は「平均個体数 × 経過ティック数」＝**延べ生存ティック数**。
        /// 餓死の絶対数はスケールと個体数に比例して増えるため、そのままでは
        /// 広さの違う世界どうしを比べられない。個体あたりに正規化して初めて
        /// 「1匹が飢えて死にやすいか」を比較できる。
        /// 1000倍するのは、1ティックあたりだと桁が小さすぎて読めないため。
        /// </summary>
        public static float StarvationPerAnimalPerKiloTick(World world)
        {
            var log = world.PopulationLog;
            if (log.Count == 0)
            {
                return 0f;
            }

            long animalTicks = 0;
            for (int i = 0; i < log.Count; i++)
            {
                animalTicks += log.GetSample(i).Animals;
            }
            if (animalTicks <= 0)
            {
                return 0f;
            }
            return 1000f * world.StarvationCount / animalTicks;
        }
    }
}
