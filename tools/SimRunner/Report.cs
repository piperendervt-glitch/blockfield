using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BlockField.SimCore.Ecology;

namespace SimRunner
{
    /// <summary>
    /// 集計と出力。
    ///
    /// 【自己完結の report.html にする理由】リモートから見るとき、
    /// 画像が別ファイルだと SCP で1枚ずつ落とすか、Web サーバを立てる必要がある。
    /// PNG を data URI で埋め込んでおけば **1ファイル持ち帰るだけ**で全部見られる。
    /// </summary>
    public static class Report
    {
        /// <summary>合算した比（シードごとの平均ではなく、分子分母を足してから割る）。</summary>
        public static double PooledRatio(int aNum, int aDen, int bNum, int bDen)
        {
            if (aDen == 0 || bDen == 0 || bNum == 0)
            {
                return 0;
            }
            return ((double)aNum / aDen) / ((double)bNum / bDen);
        }

        public sealed class Aggregate
        {
            public string Condition = "";
            public int Seeds;
            public double GraveRatio, TrampleRatio, AvoidanceRatio;
            public double GraveDensity, NonGraveDensity, HighTrampleDensity, LowTrampleDensity;
            public int GuildExtinct, WolvesExtinct, PlantsExtinct;
            public double MeanPlants, MeanHerbivores, MeanWolves, MeanCrush;
            public double StarvationPer1000Ticks, PredationPer1000Ticks, BirthsPer1000Ticks;

            // Demo 8.5（植物の場化）の基準値。時間平均・個体あたり・処理時間
            public double MeanPlantsPerTick, MeanHerbivoresPerTick, MeanWolvesPerTick;
            public double MeanEntitiesPerTick;
            public double MeanSheepPerTick, MeanPigPerTick;
            public double StarvationPerAnimalPerKiloTick, PredationPerAnimalPerKiloTick;
            public double VegetationPerSuitableCell;
            public double MsPer1000Ticks;
            public int StabilityViolations;
            public Dictionary<string, double> FieldMean = new();
            public Dictionary<string, double> FieldMax = new();

            // ---- 群れ指標 (Demo 8 第4段 K5)。種名で引く ----
            public Dictionary<string, double> NeighborMean = new();
            public Dictionary<string, double> PairDistanceMedian = new();
            public Dictionary<string, double> ColonyConcentration = new();

            /// <summary>指標が定義できたシード数（種別）。少ないと平均の意味が薄い。</summary>
            public Dictionary<string, int> FlockSeeds = new();

            /// <summary>
            /// 対照との**シードごとの差**の平均 (--control のときだけ埋まる)。
            /// 対応のある比較なので、地形と初期配置の違いが相殺される。
            /// 近傍数は「本条件 − 対照」（群れていれば正）、
            /// ペア距離は「本条件 − 対照」（群れていれば負）。
            /// </summary>
            public Dictionary<string, double> NeighborMeanDelta = new();
            public Dictionary<string, double> PairDistanceMedianDelta = new();

            /// <summary>差を取れたシード数（両方で指標が定義できたシード）。</summary>
            public Dictionary<string, int> ControlPairedSeeds = new();

            /// <summary>対照を並走させたか。</summary>
            public bool HasControl;

            /// <summary>
            /// 狼の全滅を退行とみなす割合の上限。
            ///
            /// 【0 にしない理由】狼の全滅は**死の場も踏み荒らしも切った状態でも
            /// 3/48（約6%）起きる**（Demo 8 第2段の48シード計測）。実測の幅は
            /// 2〜6/48（4〜12.5%）で、これは生態系そのものの性質であって
            /// 退行ではない。0/48 を要求すると夜間バッチが毎晩「不合格」を出し、
            /// 本当の退行が起きたときに気づけなくなる。
            ///
            /// 25% は実測上限 12.5% の倍。ここを超えたら「たまたま」では説明が
            /// つかないので退行として扱う。
            /// </summary>
            public const double WolfExtinctionTolerance = 0.25;

            /// <summary>
            /// 狼の全滅率を評価するのに必要な最小シード数。
            /// これ未満では1件の全滅が許容率を簡単に超えてしまい（2シードなら50%）、
            /// 「率」として意味を持たない。CLAUDE.md の「生態系の判定は最低48シード」と
            /// 同じ理由であり、少ないシードでの実行では狼の項目を評価しない。
            /// </summary>
            public const int MinSeedsForWolfRate = 12;

            /// <summary>
            /// Demo 8 第2段で確立した安定条件（草食獣ギルド≧1 かつ 狼≧1 かつ
            /// 植物≧1、いずれも時間を通した最小値）を48シード規模へ読み替えたもの。
            ///
            /// ギルドと植物の全滅は48シードで一度も観測されていないので 0 を要求する。
            /// 狼だけは <see cref="WolfExtinctionTolerance"/> の許容を設け、
            /// シード数が足りないときは評価しない。
            /// </summary>
            public bool M5Pass =>
                GuildExtinct == 0 && PlantsExtinct == 0 &&
                (Seeds < MinSeedsForWolfRate ||
                 (double)WolvesExtinct / Seeds <= WolfExtinctionTolerance);

            /// <summary>不合格の内訳（レポートとログに理由を出すため）。</summary>
            public string M5Detail
            {
                get
                {
                    var reasons = new List<string>();
                    if (GuildExtinct > 0) reasons.Add($"草食ギルド全滅 {GuildExtinct}/{Seeds}");
                    if (PlantsExtinct > 0) reasons.Add($"植物全滅 {PlantsExtinct}/{Seeds}");
                    if (Seeds >= MinSeedsForWolfRate &&
                        (double)WolvesExtinct / Seeds > WolfExtinctionTolerance)
                    {
                        reasons.Add($"狼全滅 {WolvesExtinct}/{Seeds} が許容 " +
                                    $"{WolfExtinctionTolerance:P0} を超過");
                    }
                    if (reasons.Count > 0)
                    {
                        return string.Join(" / ", reasons);
                    }
                    return Seeds < MinSeedsForWolfRate
                        ? $"合格（シード{Seeds}件のため狼の全滅率は未評価）"
                        : "合格";
                }
            }
        }

        public static List<Aggregate> Aggregate_(List<SeedResult> results)
        {
            var list = new List<Aggregate>();
            foreach (var group in results.GroupBy(r => r.Condition).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var rs = group.ToList();
                var a = new Aggregate { Condition = group.Key, Seeds = rs.Count };

                // Demo 8.5: 分子は「本数」ではなく草の量（植生場の合計）。
                // セルごとの平均を出してから比を取る（合算してから割る）
                int gc = rs.Sum(r => r.GraveCells), oc = rs.Sum(r => r.NonGraveCells);
                double gg = rs.Sum(r => r.GraveGrass), og = rs.Sum(r => r.NonGraveGrass);
                a.GraveDensity = gc > 0 ? gg / gc : 0;
                a.NonGraveDensity = oc > 0 ? og / oc : 0;
                a.GraveRatio = a.NonGraveDensity > 0 ? a.GraveDensity / a.NonGraveDensity : 0;

                int hc = rs.Sum(r => r.HighTrampleCells), lc = rs.Sum(r => r.LowTrampleCells);
                double hg = rs.Sum(r => r.HighTrampleGrass), lg = rs.Sum(r => r.LowTrampleGrass);
                a.HighTrampleDensity = hc > 0 ? hg / hc : 0;
                a.LowTrampleDensity = lc > 0 ? lg / lc : 0;
                a.TrampleRatio = a.LowTrampleDensity > 0 ? a.HighTrampleDensity / a.LowTrampleDensity : 0;

                long away = rs.Sum(r => (long)r.MovesAwayFromFear);
                long toward = rs.Sum(r => (long)r.MovesTowardFear);
                a.AvoidanceRatio = away + toward > 0 ? (double)away / (away + toward) : 0;

                a.GuildExtinct = rs.Count(r => r.GuildExtinct);
                a.WolvesExtinct = rs.Count(r => r.WolvesExtinct);
                a.PlantsExtinct = rs.Count(r => r.PlantsExtinct);

                a.MeanPlants = rs.Average(r => (double)r.Plants);
                a.MeanHerbivores = rs.Average(r => (double)(r.Sheep + r.Pigs));
                a.MeanWolves = rs.Average(r => (double)r.Wolves);
                a.MeanCrush = rs.Average(r => (double)r.TrampleCrush);

                // 率にしておかないとティック数の違う実行どうしを比べられない
                double ticks = rs[0].Ticks;
                a.StarvationPer1000Ticks = rs.Average(r => r.Starvation * 1000.0 / ticks);
                a.PredationPer1000Ticks = rs.Average(r => r.Predation * 1000.0 / ticks);
                a.BirthsPer1000Ticks = rs.Average(r => r.Births * 1000.0 / ticks);

                a.MeanPlantsPerTick = rs.Average(r => r.MeanPlantsPerTick);
                a.MeanHerbivoresPerTick = rs.Average(r => r.MeanHerbivoresPerTick);
                a.MeanWolvesPerTick = rs.Average(r => r.MeanWolvesPerTick);
                a.MeanEntitiesPerTick = rs.Average(r => r.MeanEntitiesPerTick);
                a.MeanSheepPerTick = rs.Average(r => r.MeanSheepPerTick);
                a.MeanPigPerTick = rs.Average(r => r.MeanPigPerTick);
                a.StarvationPerAnimalPerKiloTick = rs.Average(r => r.StarvationPerAnimalPerKiloTick);
                a.PredationPerAnimalPerKiloTick = rs.Average(r => r.PredationPerAnimalPerKiloTick);
                a.VegetationPerSuitableCell = rs.Average(r => r.VegetationPerSuitableCell);
                a.MsPer1000Ticks = rs.Average(r => r.SimMilliseconds * 1000.0 / ticks);
                a.StabilityViolations = rs.Count(r => r.StabilityViolated);

                foreach (string name in rs[0].FieldMean.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    a.FieldMean[name] = rs.Average(r => r.FieldMean[name]);
                    a.FieldMax[name] = rs.Max(r => r.FieldMax[name]);
                }

                // 群れ指標 (Demo 8 第4段 K5)。
                // 指標が未定義のシード（その種が居なかった）は平均から外す。
                // 0 で埋めると「絶滅していた」が「群れていなかった」として混ざる
                a.HasControl = rs.Any(r => r.Control != null);
                foreach (var (_, species) in Runner.FlockSpecies)
                {
                    var withMetric = rs.Where(r => r.NeighborMean.ContainsKey(species)).ToList();
                    a.FlockSeeds[species] = withMetric.Count;
                    if (withMetric.Count > 0)
                    {
                        a.NeighborMean[species] = withMetric.Average(r => r.NeighborMean[species]);
                    }

                    var withPair = rs.Where(r => r.PairDistanceMedian.ContainsKey(species)).ToList();
                    if (withPair.Count > 0)
                    {
                        a.PairDistanceMedian[species] = withPair.Average(r => r.PairDistanceMedian[species]);
                    }

                    var withConc = rs.Where(r => r.ColonyConcentration.ContainsKey(species)).ToList();
                    if (withConc.Count > 0)
                    {
                        a.ColonyConcentration[species] = withConc.Average(r => r.ColonyConcentration[species]);
                    }

                    if (!a.HasControl)
                    {
                        continue;
                    }

                    // 対応のある差。**両方で定義できたシードだけ**を使う
                    var paired = rs.Where(r =>
                        r.Control != null &&
                        r.NeighborMean.ContainsKey(species) &&
                        r.Control.NeighborMean.ContainsKey(species)).ToList();
                    a.ControlPairedSeeds[species] = paired.Count;
                    if (paired.Count > 0)
                    {
                        a.NeighborMeanDelta[species] = paired.Average(
                            r => r.NeighborMean[species] - r.Control!.NeighborMean[species]);
                    }

                    var pairedDist = rs.Where(r =>
                        r.Control != null &&
                        r.PairDistanceMedian.ContainsKey(species) &&
                        r.Control.PairDistanceMedian.ContainsKey(species)).ToList();
                    if (pairedDist.Count > 0)
                    {
                        a.PairDistanceMedianDelta[species] = pairedDist.Average(
                            r => r.PairDistanceMedian[species] - r.Control!.PairDistanceMedian[species]);
                    }
                }

                list.Add(a);
            }
            return list;
        }

        public static void WritePopulationCsv(string path, List<SeedResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("condition,seed,tick,plants,herbivores,wolves");
            foreach (var r in results)
            {
                foreach (var (tick, plants, herbivores, wolves) in r.Series)
                {
                    sb.Append(r.Condition).Append(',').Append(r.Seed).Append(',').Append(tick).Append(',')
                      .Append(plants).Append(',').Append(herbivores).Append(',').Append(wolves).Append('\n');
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static void WriteSummaryJson(
            string path, List<Aggregate> aggregates, List<SeedResult> results,
            int ticks, int size, double elapsedSeconds)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"ticks\": {ticks},\n");
            sb.Append($"  \"size\": {size},\n");
            sb.Append($"  \"seeds\": {results.Select(r => r.Seed).Distinct().Count()},\n");
            sb.Append($"  \"elapsedSeconds\": {N(elapsedSeconds)},\n");
            // 実行時のコミット。ハッシュ不一致が「実装変更によるもの」か
            // 「本物の決定論の破れ」かを次回の比較で区別するために残す
            sb.Append($"  \"commit\": \"{Compare.CurrentCommit()}\",\n");
            sb.Append("  \"conditions\": [\n");
            for (int i = 0; i < aggregates.Count; i++)
            {
                var a = aggregates[i];
                sb.Append("    {\n");
                sb.Append($"      \"name\": \"{a.Condition}\",\n");
                sb.Append($"      \"seeds\": {a.Seeds},\n");
                sb.Append($"      \"graveyardPlantDensity\": {N(a.GraveDensity)},\n");
                sb.Append($"      \"nonGraveyardPlantDensity\": {N(a.NonGraveDensity)},\n");
                sb.Append($"      \"graveyardRatio\": {N(a.GraveRatio)},\n");
                sb.Append($"      \"highTramplePlantDensity\": {N(a.HighTrampleDensity)},\n");
                sb.Append($"      \"lowTramplePlantDensity\": {N(a.LowTrampleDensity)},\n");
                sb.Append($"      \"trampleRatio\": {N(a.TrampleRatio)},\n");
                sb.Append($"      \"fearAvoidanceRatio\": {N(a.AvoidanceRatio)},\n");
                sb.Append($"      \"guildExtinct\": {a.GuildExtinct},\n");
                sb.Append($"      \"wolvesExtinct\": {a.WolvesExtinct},\n");
                sb.Append($"      \"plantsExtinct\": {a.PlantsExtinct},\n");
                sb.Append($"      \"meanPlants\": {N(a.MeanPlants)},\n");
                sb.Append($"      \"meanHerbivores\": {N(a.MeanHerbivores)},\n");
                sb.Append($"      \"meanWolves\": {N(a.MeanWolves)},\n");
                sb.Append($"      \"meanTrampleCrush\": {N(a.MeanCrush)},\n");
                sb.Append($"      \"m5Pass\": {(a.M5Pass ? "true" : "false")},\n");
                sb.Append($"      \"starvationPer1000Ticks\": {N(a.StarvationPer1000Ticks)},\n");
                sb.Append($"      \"predationPer1000Ticks\": {N(a.PredationPer1000Ticks)},\n");
                sb.Append($"      \"birthsPer1000Ticks\": {N(a.BirthsPer1000Ticks)},\n");
                sb.Append($"      \"meanPlantsPerTick\": {N(a.MeanPlantsPerTick)},\n");
                sb.Append($"      \"meanHerbivoresPerTick\": {N(a.MeanHerbivoresPerTick)},\n");
                sb.Append($"      \"meanWolvesPerTick\": {N(a.MeanWolvesPerTick)},\n");
                sb.Append($"      \"meanEntitiesPerTick\": {N(a.MeanEntitiesPerTick)},\n");
                sb.Append($"      \"meanSheepPerTick\": {N(a.MeanSheepPerTick)},\n");
                sb.Append($"      \"meanPigPerTick\": {N(a.MeanPigPerTick)},\n");
                sb.Append($"      \"starvationPerAnimalPerKiloTick\": {N(a.StarvationPerAnimalPerKiloTick)},\n");
                sb.Append($"      \"predationPerAnimalPerKiloTick\": {N(a.PredationPerAnimalPerKiloTick)},\n");
                sb.Append($"      \"vegetationPerSuitableCell\": {N(a.VegetationPerSuitableCell)},\n");
                sb.Append($"      \"msPer1000Ticks\": {N(a.MsPer1000Ticks)},\n");
                sb.Append($"      \"stabilityViolations\": {a.StabilityViolations},\n");
                sb.Append("      \"fieldMean\": {");
                sb.Append(string.Join(", ", a.FieldMean.Select(kv => $"\"{kv.Key}\": {N(kv.Value)}")));
                sb.Append("},\n");
                sb.Append("      \"fieldMax\": {");
                sb.Append(string.Join(", ", a.FieldMax.Select(kv => $"\"{kv.Key}\": {N(kv.Value)}")));
                sb.Append("},\n");

                // 群れ指標 (Demo 8 第4段 K5)。4c の M4 はここを見る
                sb.Append("      \"flock\": {\n");
                sb.Append($"        \"neighborRadius\": {N(EcologyStats.FlockNeighborRadius)},\n");
                sb.Append("        \"neighborMean\": {");
                sb.Append(string.Join(", ", a.NeighborMean.Select(kv => $"\"{kv.Key}\": {N(kv.Value)}")));
                sb.Append("},\n");
                sb.Append("        \"pairDistanceMedian\": {");
                sb.Append(string.Join(", ", a.PairDistanceMedian.Select(kv => $"\"{kv.Key}\": {N(kv.Value)}")));
                sb.Append("},\n");
                sb.Append("        \"colonyConcentrationTop10\": {");
                sb.Append(string.Join(", ", a.ColonyConcentration.Select(kv => $"\"{kv.Key}\": {N(kv.Value)}")));
                sb.Append("},\n");
                sb.Append("        \"seeds\": {");
                sb.Append(string.Join(", ", a.FlockSeeds.Select(kv => $"\"{kv.Key}\": {kv.Value}")));
                sb.Append("},\n");
                sb.Append($"        \"hasControl\": {(a.HasControl ? "true" : "false")}");
                if (a.HasControl)
                {
                    sb.Append(",\n");
                    // 対応のある差（本条件 − 対照）。群れていれば
                    // 近傍数の差は正、ペア距離の差は負になる
                    sb.Append("        \"neighborMeanDeltaVsControl\": {");
                    sb.Append(string.Join(", ", a.NeighborMeanDelta.Select(kv => $"\"{kv.Key}\": {N(kv.Value)}")));
                    sb.Append("},\n");
                    sb.Append("        \"pairDistanceMedianDeltaVsControl\": {");
                    sb.Append(string.Join(", ", a.PairDistanceMedianDelta.Select(kv => $"\"{kv.Key}\": {N(kv.Value)}")));
                    sb.Append("},\n");
                    sb.Append("        \"pairedSeeds\": {");
                    sb.Append(string.Join(", ", a.ControlPairedSeeds.Select(kv => $"\"{kv.Key}\": {kv.Value}")));
                    sb.Append("}\n");
                }
                else
                {
                    sb.Append('\n');
                }
                sb.Append("      }\n");
                sb.Append(i == aggregates.Count - 1 ? "    }\n" : "    },\n");
            }
            sb.Append("  ],\n");

            // 羊と豚の内訳はシードごとに残す。集計値だけだと
            // 「何シードで豚が優勢か」という符号検定ができない
            sb.Append("  \"speciesBySeed\": [\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.Append($"    {{\"condition\": \"{r.Condition}\", \"seed\": {r.Seed}, " +
                          $"\"sheepMean\": {N(r.MeanSheepPerTick)}, \"pigMean\": {N(r.MeanPigPerTick)}}}");
                sb.Append(i == results.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ],\n");

            // 群れ指標をシードごとに残す (Demo 8 第4段 K5)。
            // 集計値だけだと 4c の「対照との比で有意に高い」を後から検定できない。
            // 対照を並走させていれば control 側も同じ行に入るので、
            // **対応のある検定（シードごとの差の符号）**がそのまま組める
            sb.Append("  \"flockBySeed\": [\n");
            {
                var rows = new List<string>();
                foreach (var r in results)
                {
                    foreach (var (_, species) in Runner.FlockSpecies)
                    {
                        if (!r.NeighborMean.ContainsKey(species))
                        {
                            continue;   // その種が居なかったシード
                        }
                        var row = new StringBuilder();
                        row.Append($"    {{\"condition\": \"{r.Condition}\", \"seed\": {r.Seed}, " +
                                   $"\"species\": \"{species}\", " +
                                   $"\"neighborMean\": {N(r.NeighborMean[species])}, " +
                                   $"\"pairDistanceMedian\": " +
                                   $"{N(r.PairDistanceMedian.TryGetValue(species, out double pdm) ? pdm : 0)}, " +
                                   $"\"colonyConcentrationTop10\": " +
                                   $"{N(r.ColonyConcentration.TryGetValue(species, out double cc) ? cc : 0)}");
                        if (r.Control != null && r.Control.NeighborMean.ContainsKey(species))
                        {
                            row.Append($", \"controlNeighborMean\": {N(r.Control.NeighborMean[species])}");
                            if (r.Control.PairDistanceMedian.TryGetValue(species, out double cpd))
                            {
                                row.Append($", \"controlPairDistanceMedian\": {N(cpd)}");
                            }
                        }
                        row.Append('}');
                        rows.Add(row.ToString());
                    }
                }
                sb.Append(string.Join(",\n", rows));
                sb.Append(rows.Count > 0 ? "\n" : "");
            }
            sb.Append("  ],\n");

            // 決定論の追跡用にシードごとのハッシュも残す
            sb.Append("  \"contentHashes\": [\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                // hashExColony はコロニー場（Demo 8 第4段 K1）を除いた部分。
                // 場を足した前後で「他は何も変わっていない」を照合するために残す
                sb.Append($"    {{\"condition\": \"{r.Condition}\", \"seed\": {r.Seed}, " +
                          $"\"hash\": \"{r.ContentHash:X16}\", " +
                          $"\"hashExColony\": \"{r.ContentHashExcludingColony:X16}\"}}");
                sb.Append(i == results.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ]\n}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string N(double v) =>
            double.IsFinite(v) ? v.ToString("0.######", CultureInfo.InvariantCulture) : "null";

        public static void WriteHtml(
            string path, List<Aggregate> aggregates, List<SeedResult> results,
            List<(string caption, string dataUri)> images,
            int ticks, int size, double elapsedSeconds, string commandLine)
        {
            var sb = new StringBuilder();
            sb.Append("""
<!doctype html>
<meta charset="utf-8">
<title>SimRunner report</title>
<style>
 :root { color-scheme: light dark; }
 body { font-family: system-ui, "Segoe UI", sans-serif; margin: 2rem auto; max-width: 1100px;
        padding: 0 1rem; line-height: 1.6; }
 h1 { font-size: 1.5rem; } h2 { font-size: 1.2rem; margin-top: 2.5rem; }
 table { border-collapse: collapse; width: 100%; margin: 1rem 0; font-size: 0.9rem; }
 th, td { border: 1px solid #8884; padding: 0.35rem 0.6rem; text-align: right; }
 th:first-child, td:first-child { text-align: left; }
 thead th { background: #8882; }
 .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 1rem; }
 .card { border: 1px solid #8884; border-radius: 6px; padding: 0.5rem; }
 .card img { width: 100%; image-rendering: pixelated; display: block; border-radius: 3px; }
 .card p { margin: 0.4rem 0 0; font-size: 0.85rem; }
 .meta { font-size: 0.85rem; opacity: 0.75; }
 .bad { color: #d33; font-weight: bold; }
 code { background: #8882; padding: 0.1rem 0.3rem; border-radius: 3px; }
</style>

""");
            sb.Append($"<h1>SimRunner report</h1>\n");
            sb.Append($"<p class=meta>{results.Select(r => r.Seed).Distinct().Count()} シード × {ticks} ティック / " +
                      $"{size}×{size} / 所要 {elapsedSeconds:F1} 秒<br>" +
                      $"<code>{Escape(commandLine)}</code></p>\n");

            sb.Append("<h2>条件ごとの集計</h2>\n<table><thead><tr>" +
                      "<th>条件</th><th>シード</th><th>植物</th><th>草食</th><th>狼</th>" +
                      "<th>踏み潰し</th><th>ギルド全滅</th><th>狼全滅</th><th>植物全滅</th>" +
                      "</tr></thead><tbody>\n");
            foreach (var a in aggregates)
            {
                sb.Append($"<tr><td>{Escape(a.Condition)}</td><td>{a.Seeds}</td>" +
                          $"<td>{a.MeanPlants:F0}</td><td>{a.MeanHerbivores:F1}</td><td>{a.MeanWolves:F1}</td>" +
                          $"<td>{a.MeanCrush:F0}</td>" +
                          $"<td{Flag(a.GuildExtinct)}>{a.GuildExtinct}/{a.Seeds}</td>" +
                          $"<td>{a.WolvesExtinct}/{a.Seeds}</td>" +
                          $"<td{Flag(a.PlantsExtinct)}>{a.PlantsExtinct}/{a.Seeds}</td></tr>\n");
            }
            sb.Append("</tbody></table>\n");

            sb.Append("<h2>場の指標</h2>\n<table><thead><tr>" +
                      "<th>条件</th><th>墓場 植物密度</th><th>それ以外</th><th>比</th>" +
                      "<th>踏跡上位25%</th><th>下位25%</th><th>比</th><th>迂回率</th>" +
                      "</tr></thead><tbody>\n");
            foreach (var a in aggregates)
            {
                sb.Append($"<tr><td>{Escape(a.Condition)}</td>" +
                          $"<td>{a.GraveDensity * 100:F2}%</td><td>{a.NonGraveDensity * 100:F2}%</td>" +
                          $"<td>{a.GraveRatio:F3}</td>" +
                          $"<td>{a.HighTrampleDensity * 100:F2}%</td><td>{a.LowTrampleDensity * 100:F2}%</td>" +
                          $"<td>{a.TrampleRatio:F3}</td>" +
                          $"<td>{a.AvoidanceRatio * 100:F1}%</td></tr>\n");
            }
            sb.Append("</tbody></table>\n");
            sb.Append("<p class=meta>比は合算値（シードごとの平均ではなく、分子分母を足してから割る）。" +
                      "墓場の比は 1.0 ではなく対照条件と比べること — 餓死は餌の乏しい場所で起きるため。" +
                      "迂回率は 50% でなく w_fear=0 の対照（約55%）と比べること。</p>\n");

            sb.Append("<h2>場の平均 / 最大</h2>\n<table><thead><tr><th>条件</th>");
            var fieldNames = aggregates.Count > 0
                ? aggregates[0].FieldMean.Keys.ToList()
                : new List<string>();
            foreach (string n in fieldNames)
            {
                sb.Append($"<th>{Escape(n)}</th>");
            }
            sb.Append("</tr></thead><tbody>\n");
            foreach (var a in aggregates)
            {
                sb.Append($"<tr><td>{Escape(a.Condition)}</td>");
                foreach (string n in fieldNames)
                {
                    sb.Append($"<td>{a.FieldMean[n]:F4} / {a.FieldMax[n]:F2}</td>");
                }
                sb.Append("</tr>\n");
            }
            sb.Append("</tbody></table>\n");

            // 群れ指標 (Demo 8 第4段 K5)。4c の M4 はこの表を見る
            bool anyControl = aggregates.Any(a => a.HasControl);
            sb.Append("<h2>群れ指標</h2>\n<table><thead><tr>" +
                      "<th>条件</th><th>種</th><th>同種近傍数<br>(半径3)</th><th>ペア距離<br>中央値</th>" +
                      "<th>コロニー場<br>集中度(上位10%)</th><th>標本<br>シード</th>");
            if (anyControl)
            {
                sb.Append("<th>近傍数の差<br>(本−対照)</th><th>ペア距離の差<br>(本−対照)</th>");
            }
            sb.Append("</tr></thead><tbody>\n");
            foreach (var a in aggregates)
            {
                foreach (var (_, species) in Runner.FlockSpecies)
                {
                    if (!a.NeighborMean.TryGetValue(species, out double nm))
                    {
                        continue;
                    }
                    a.PairDistanceMedian.TryGetValue(species, out double pd);
                    a.ColonyConcentration.TryGetValue(species, out double cc);
                    a.FlockSeeds.TryGetValue(species, out int fs);

                    sb.Append($"<tr><td>{Escape(a.Condition)}</td><td>{species}</td>" +
                              $"<td>{nm:F4}</td><td>{pd:F2}</td><td>{cc:F4}</td>" +
                              $"<td>{fs}/{a.Seeds}</td>");
                    if (anyControl)
                    {
                        if (a.HasControl && a.NeighborMeanDelta.TryGetValue(species, out double dn))
                        {
                            a.PairDistanceMedianDelta.TryGetValue(species, out double dp);
                            sb.Append($"<td>{dn:+0.0000;-0.0000;0}</td><td>{dp:+0.00;-0.00;0}</td>");
                        }
                        else
                        {
                            sb.Append("<td>—</td><td>—</td>");
                        }
                    }
                    sb.Append("</tr>\n");
                }
            }
            sb.Append("</tbody></table>\n");
            sb.Append("<p class=meta>群れていれば<b>近傍数は大きく・ペア距離は小さく</b>なる。" +
                      "一様分布の目安はペア距離 ≈ 0.5214 × 一辺（50セルなら約26.1）。" +
                      "集中度は一様なら 0.1。" +
                      "差は同一シードで対にして取った平均（地形と初期配置が相殺される）。" +
                      "コロニー場の集中度は出生位置の +X バイアスにも反応するので記録項目であり、" +
                      "群れの判定は近傍数とペア距離で行う。</p>\n");
            if (!anyControl)
            {
                sb.Append("<p class=meta>対照を並走させるには <code>--control</code> を付ける。</p>\n");
            }

            if (images.Count > 0)
            {
                sb.Append("<h2>地形と場（代表シード）</h2>\n<div class=grid>\n");
                foreach (var (caption, uri) in images)
                {
                    sb.Append($"<div class=card><img src=\"{uri}\" alt=\"{Escape(caption)}\">" +
                              $"<p>{Escape(caption)}</p></div>\n");
                }
                sb.Append("</div>\n");
            }

            sb.Append("<h2>個体数の推移</h2>\n");
            sb.Append("<p class=meta>全系列は population.csv にある。ここは代表シードの折れ線。</p>\n");
            foreach (var group in results.GroupBy(r => r.Condition).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var first = group.OrderBy(r => r.Seed).First();
                sb.Append($"<h3>{Escape(group.Key)} / seed {first.Seed}</h3>\n");
                sb.Append(Sparkline(first));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string Flag(int count) => count > 0 ? " class=bad" : "";

        /// <summary>
        /// 個体数の折れ線を SVG で描く。外部ライブラリを使わないのは、
        /// report.html を1ファイルで完結させるため（CDN が引けない環境でも見える）。
        /// </summary>
        static string Sparkline(SeedResult r)
        {
            const int w = 1000, h = 180, pad = 24;
            if (r.Series.Count == 0)
            {
                return "<p class=meta>系列なし</p>";
            }

            long maxTick = r.Series[^1].tick;
            int maxValue = 1;
            foreach (var (_, plants, herbivores, wolves) in r.Series)
            {
                maxValue = Math.Max(maxValue, Math.Max(plants, Math.Max(herbivores, wolves)));
            }

            string Path_(Func<(long, int, int, int), int> pick, string color)
            {
                var sb = new StringBuilder($"<path fill=none stroke=\"{color}\" stroke-width=1.5 d=\"");
                for (int i = 0; i < r.Series.Count; i++)
                {
                    var s = r.Series[i];
                    double x = pad + (w - 2 * pad) * (maxTick > 0 ? (double)s.tick / maxTick : 0);
                    double y = h - pad - (h - 2 * pad) * ((double)pick((s.tick, s.plants, s.herbivores, s.wolves)) / maxValue);
                    sb.Append(i == 0 ? 'M' : 'L').Append(x.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(' ').Append(y.ToString("F1", CultureInfo.InvariantCulture)).Append(' ');
                }
                return sb.Append("\"/>").ToString();
            }

            var svg = new StringBuilder();
            svg.Append($"<svg viewBox=\"0 0 {w} {h}\" style=\"width:100%;height:auto;border:1px solid #8884;border-radius:6px\">");
            svg.Append($"<text x=\"{pad}\" y=\"14\" font-size=\"11\" fill=\"currentColor\" opacity=\"0.7\">" +
                       $"最大 {maxValue} / tick 0–{maxTick}　緑=植物 橙=草食 赤=狼</text>");
            svg.Append(Path_(s => s.Item2, "#3c3"));
            svg.Append(Path_(s => s.Item3, "#f90"));
            svg.Append(Path_(s => s.Item4, "#e33"));
            svg.Append("</svg>");
            return svg.ToString();
        }

        static string Escape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
