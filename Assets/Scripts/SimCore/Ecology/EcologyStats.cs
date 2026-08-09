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
        /// 密度の推定が雑音に埋もれるので、実測で決めた（3000t・5シード）:
        ///   閾値 0.05 → 35セル、比 1.01（ただし5シード中3つが 0.00。標本不足）
        ///   閾値 0.02 → 103セル、比 0.86（ゼロのシード無し）  ← 採用
        ///   閾値 0.01 → 205セル、比 0.64（薄いセルが混ざり効果が薄まる）
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
        /// 実測で養分係数を0にした対照でも比は 0.33 しかない。
        /// したがって判定は**対照 (deathNutrientBoost=0) との比較**で行う:
        ///   k=0  → 0.33（交絡だけの値。ここが原点）
        ///   k=20 → 0.86（採用値。約2.6倍まで回復）
        /// k を上げれば 1.0 は超えられるが（k=160 で 1.03）、狼の最小個体数が0になり
        /// Demo 5b の安定条件が壊れるため採らなかった。
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
