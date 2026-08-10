using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SimRunner
{
    /// <summary>
    /// 前回の summary.json と突き合わせて回帰を検知する。
    ///
    /// 【最優先は ContentHash の一致】コードを変えていないのにハッシュが変われば、
    /// それは決定論 f(シード, イベントログ) が破れたということであり、
    /// 本プロジェクトの前提そのものが崩れる。指標が多少動くより深刻なので、
    /// 差分レポートの最上部に最も目立つ形で出す。
    ///
    /// System.Text.Json は .NET のランタイムに同梱されており NuGet 復元が要らない
    /// （回線の細いリモートで詰まらないため。SimRunner.csproj のコメント参照）。
    /// </summary>
    public static class Compare
    {
        /// <summary>
        /// 実行時の git HEAD。summary.json に残しておくと、ハッシュ不一致が
        /// 「実装を変えたから（想定内）」なのか「変えていないのに壊れた（本物の破れ）」
        /// なのかを機械的に区別できる。
        ///
        /// Demo 8.5 のように段階的に実装を変える期間は、ハッシュ不一致が
        /// 何度も起きる。そのたびに警報が鳴ると本物の破れを見逃すため、
        /// この区別が要る。git が無い環境では空文字になり、
        /// その場合は従来どおり「区別できない」扱いにする。
        /// </summary>
        public static string CurrentCommit()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                {
                    return "";
                }
                string outText = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                return proc.ExitCode == 0 ? outText : "";
            }
            catch
            {
                return "";
            }
        }

        public sealed class Previous
        {
            public int Ticks, Size, Seeds;
            public string Commit = "";
            public Dictionary<string, Dictionary<string, double>> Numbers = new();
            public Dictionary<string, bool> M5Pass = new();
            /// <summary>"条件|シード" → ハッシュ文字列。</summary>
            public Dictionary<string, string> Hashes = new();
            public Dictionary<string, Dictionary<string, double>> FieldMean = new();
        }

        public static Previous? Load(string path, out string? error)
        {
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var p = new Previous
                {
                    Ticks = root.GetProperty("ticks").GetInt32(),
                    Size = root.GetProperty("size").GetInt32(),
                    Seeds = root.GetProperty("seeds").GetInt32(),
                    Commit = root.TryGetProperty("commit", out var c0) ? (c0.GetString() ?? "") : "",
                };

                foreach (var c in root.GetProperty("conditions").EnumerateArray())
                {
                    string name = c.GetProperty("name").GetString() ?? "?";
                    var nums = new Dictionary<string, double>();
                    foreach (var prop in c.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            nums[prop.Name] = prop.Value.GetDouble();
                        }
                    }
                    p.Numbers[name] = nums;
                    p.M5Pass[name] = c.TryGetProperty("m5Pass", out var m5) && m5.ValueKind == JsonValueKind.True;

                    var fm = new Dictionary<string, double>();
                    if (c.TryGetProperty("fieldMean", out var fmEl))
                    {
                        foreach (var prop in fmEl.EnumerateObject())
                        {
                            fm[prop.Name] = prop.Value.GetDouble();
                        }
                    }
                    p.FieldMean[name] = fm;
                }

                if (root.TryGetProperty("contentHashes", out var hashes))
                {
                    foreach (var h in hashes.EnumerateArray())
                    {
                        string key = h.GetProperty("condition").GetString() + "|" + h.GetProperty("seed").GetUInt32();
                        p.Hashes[key] = h.GetProperty("hash").GetString() ?? "";
                    }
                }
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>指標の向き。差分に色を付けるために使う。</summary>
        enum Direction
        {
            /// <summary>増えると悪い（絶滅数など）。</summary>
            LowerIsBetter,
            /// <summary>良し悪しが一意でない。大きく動いたときだけ注意を促す。</summary>
            Neutral,
        }

        static readonly (string key, string label, Direction dir, string fmt)[] k_Metrics =
        {
            ("guildExtinct", "草食ギルド全滅", Direction.LowerIsBetter, "F0"),
            ("wolvesExtinct", "狼全滅", Direction.LowerIsBetter, "F0"),
            ("plantsExtinct", "植物全滅", Direction.LowerIsBetter, "F0"),
            ("starvationPer1000Ticks", "餓死 /1000t", Direction.Neutral, "F2"),
            ("predationPer1000Ticks", "捕食 /1000t", Direction.Neutral, "F2"),
            ("birthsPer1000Ticks", "出生 /1000t", Direction.Neutral, "F2"),
            ("meanPlants", "植物数", Direction.Neutral, "F1"),
            ("meanHerbivores", "草食獣数", Direction.Neutral, "F1"),
            ("meanWolves", "狼数", Direction.Neutral, "F1"),
            ("meanTrampleCrush", "踏み潰し", Direction.Neutral, "F0"),
            ("graveyardRatio", "墓場の植物密度比", Direction.Neutral, "F3"),
            ("trampleRatio", "踏跡の植物密度比", Direction.Neutral, "F3"),
            ("fearAvoidanceRatio", "迂回率", Direction.Neutral, "F3"),
        };

        /// <summary>相対変化がこれを超えたら「大きく動いた」とみなす。</summary>
        const double k_SignificantChange = 0.10;

        /// <summary>決定論の照合結果。終了コードとコンソール表示を分けるために使う。</summary>
        public enum DeterminismStatus
        {
            /// <summary>一致した、または照合できなかった。</summary>
            Ok,

            /// <summary>不一致だが、コミットも変わっている（実装を変えたのなら想定内）。</summary>
            ChangedWithCode,

            /// <summary>コードは同一なのに不一致。本物の決定論の破れ。</summary>
            BrokenSameCode,

            /// <summary>不一致だが、コミットを取得できず区別できない。</summary>
            MismatchUnknownCode,
        }

        public static DeterminismStatus WriteDiffHtml(
            string path, string previousPath, Previous prev,
            List<Report.Aggregate> current, List<SeedResult> results,
            int ticks, int size)
        {
            // ハッシュの突き合わせ。条件が揃っていないと比較自体が無意味なので先に判定する
            bool comparable = prev.Ticks == ticks && prev.Size == size;
            var mismatches = new List<(string condition, uint seed, string before, string after)>();
            int compared = 0;

            if (comparable)
            {
                foreach (var r in results)
                {
                    string key = r.Condition + "|" + r.Seed;
                    if (!prev.Hashes.TryGetValue(key, out string? before))
                    {
                        continue;
                    }
                    compared++;
                    string after = r.ContentHash.ToString("X16");
                    if (before != after)
                    {
                        mismatches.Add((r.Condition, r.Seed, before, after));
                    }
                }
            }

            var status = DeterminismStatus.Ok;

            var sb = new StringBuilder();
            sb.Append("""
<!doctype html>
<meta charset="utf-8">
<title>SimRunner diff</title>
<style>
 :root { color-scheme: light dark; }
 body { font-family: system-ui, "Segoe UI", sans-serif; margin: 2rem auto; max-width: 1000px;
        padding: 0 1rem; line-height: 1.6; }
 h1 { font-size: 1.5rem; } h2 { font-size: 1.15rem; margin-top: 2.2rem; }
 table { border-collapse: collapse; width: 100%; margin: 0.8rem 0; font-size: 0.9rem; }
 th, td { border: 1px solid #8884; padding: 0.35rem 0.6rem; text-align: right; }
 th:first-child, td:first-child { text-align: left; }
 thead th { background: #8882; }
 .meta { font-size: 0.85rem; opacity: 0.75; }
 .worse { color: #d33; font-weight: bold; }
 .better { color: #197; font-weight: bold; }
 .moved { color: #c70; }
 .banner { border-radius: 8px; padding: 1rem 1.25rem; margin: 1.5rem 0; }
 .alarm { background: #d33; color: #fff; }
 .alarm h2 { margin-top: 0; color: #fff; font-size: 1.3rem; }
 .ok { background: #1972; border: 1px solid #1975; }
 .warn { background: #c703; border: 1px solid #c706; }
 code { background: #8882; padding: 0.1rem 0.3rem; border-radius: 3px; }
</style>

""");
            sb.Append("<h1>SimRunner 差分レポート</h1>\n");
            sb.Append($"<p class=meta>今回: {ticks} ティック / {size}×{size} / " +
                      $"{results.Select(r => r.Seed).Distinct().Count()} シード<br>" +
                      $"前回: <code>{Esc(previousPath)}</code>（{prev.Ticks} ティック / " +
                      $"{prev.Size}×{prev.Size} / {prev.Seeds} シード）</p>\n");

            // --- 決定論。最上部に置く ---
            if (!comparable)
            {
                sb.Append("<div class='banner warn'><h2>ContentHash は比較していません</h2>" +
                          "<p>ティック数または世界のサイズが前回と違うため、ハッシュを比べても意味がありません。" +
                          "決定論の確認をしたい場合は同じ条件で実行してください。</p></div>\n");
            }
            else if (mismatches.Count > 0)
            {
                // コードが変わっていれば不一致は想定内。変わっていないなら本物の破れ。
                // この区別ができないと、実装を段階的に変えている期間に警報が鳴り続け、
                // 本物の破れを見逃す
                string nowCommit = CurrentCommit();
                bool codeChanged = nowCommit.Length > 0 && prev.Commit.Length > 0 && nowCommit != prev.Commit;
                bool codeSame = nowCommit.Length > 0 && prev.Commit.Length > 0 && nowCommit == prev.Commit;

                sb.Append(codeChanged
                    ? "<div class='banner warn'><h2>ContentHash が変わりました（コードも変わっています）</h2>"
                    : "<div class='banner alarm'><h2>⚠ 決定論が破れています</h2>");
                sb.Append($"<p><b>{mismatches.Count} / {compared} シードで ContentHash が前回と一致しません。</b></p>");

                status = codeChanged ? DeterminismStatus.ChangedWithCode
                    : codeSame ? DeterminismStatus.BrokenSameCode
                    : DeterminismStatus.MismatchUnknownCode;

                if (codeChanged)
                {
                    sb.Append($"<p>前回の実行以降にコミットが変わっています" +
                              $"（<code>{prev.Commit[..Math.Min(8, prev.Commit.Length)]}</code> → " +
                              $"<code>{nowCommit[..Math.Min(8, nowCommit.Length)]}</code>）。" +
                              "シミュレーションのルールを変更したのであれば、この不一致は想定どおりです。" +
                              "<b>心当たりが無い場合は、その差分が本当に挙動を変えるものだったかを確認してください。</b></p>");
                }
                else if (codeSame)
                {
                    sb.Append($"<p><b>コードは前回と同一です（<code>{nowCommit[..Math.Min(8, nowCommit.Length)]}</code>）。" +
                              "つまりこれは実装変更によるものではありません。</b> " +
                              "f(シード, イベントログ) という本プロジェクトの前提が崩れたということなので、" +
                              "他の指標の差分より先にこれを調べてください。</p>");
                }
                else
                {
                    sb.Append("<p>コミットを取得できなかったため、実装変更によるものか区別できません。" +
                              "コードを変更していないのにここが不一致になるなら、" +
                              "f(シード, イベントログ) という前提が崩れたということです。</p>");
                }
                sb.Append("<table><thead><tr><th>条件</th><th>シード</th><th>前回</th><th>今回</th></tr></thead><tbody>");
                foreach (var (c, s, before, after) in mismatches.Take(30))
                {
                    sb.Append($"<tr><td>{Esc(c)}</td><td>{s}</td><td><code>{before}</code></td>" +
                              $"<td><code>{after}</code></td></tr>");
                }
                sb.Append("</tbody></table>");
                if (mismatches.Count > 30)
                {
                    sb.Append($"<p>ほか {mismatches.Count - 30} 件</p>");
                }
                sb.Append("</div>\n");
            }
            else if (compared == 0)
            {
                sb.Append("<div class='banner warn'><h2>ContentHash を照合できませんでした</h2>" +
                          "<p>前回の summary.json に一致する条件・シードの記録がありません。</p></div>\n");
            }
            else
            {
                sb.Append($"<div class='banner ok'><h2>決定論 OK</h2>" +
                          $"<p>{compared} シードすべてで ContentHash が前回と一致しました。</p></div>\n");
            }

            // --- M5 ---
            sb.Append("<h2>M5（生態系の安定条件）</h2>\n");
            sb.Append("<p class=meta>草食獣ギルドと植物の全滅は 0/シード を要求する（48シードで一度も観測されていないため）。" +
                      $"狼だけは許容 {Report.Aggregate.WolfExtinctionTolerance:P0} を設ける — " +
                      "狼の全滅は死の場も踏み荒らしも切った状態でも 3/48（約6%）起きる生態系そのものの性質であり、" +
                      "0 を要求すると毎回不合格になって本当の退行に気づけなくなるため。</p>\n");
            sb.Append("<table><thead><tr><th>条件</th><th>前回</th><th>今回</th><th>判定</th><th>内訳</th></tr></thead><tbody>\n");
            foreach (var a in current)
            {
                bool hadPrev = prev.M5Pass.TryGetValue(a.Condition, out bool before);
                string beforeText = hadPrev ? (before ? "合格" : "不合格") : "—";
                string verdict;
                if (!hadPrev) verdict = "<span class=meta>前回なし</span>";
                else if (before && !a.M5Pass) verdict = "<span class=worse>退行</span>";
                else if (!before && a.M5Pass) verdict = "<span class=better>改善</span>";
                else verdict = "変化なし";
                string nowClass = a.M5Pass ? "" : " class=worse";
                sb.Append($"<tr><td>{Esc(a.Condition)}</td><td>{beforeText}</td>" +
                          $"<td{nowClass}>{(a.M5Pass ? "合格" : "不合格")}</td><td>{verdict}</td>" +
                          $"<td class=meta>{Esc(a.M5Detail)}</td></tr>\n");
            }
            sb.Append("</tbody></table>\n");

            // --- 指標の差分 ---
            foreach (var a in current)
            {
                sb.Append($"<h2>指標の差分 — {Esc(a.Condition)}</h2>\n");
                if (!prev.Numbers.TryGetValue(a.Condition, out var before))
                {
                    sb.Append("<p class=meta>前回にこの条件がありません。</p>\n");
                    continue;
                }

                var now = CurrentNumbers(a);
                sb.Append("<table><thead><tr><th>指標</th><th>前回</th><th>今回</th><th>差</th><th>変化率</th></tr></thead><tbody>\n");
                foreach (var (key, label, dir, fmt) in k_Metrics)
                {
                    if (!before.TryGetValue(key, out double b) || !now.TryGetValue(key, out double n))
                    {
                        continue;
                    }
                    double delta = n - b;
                    double rel = Math.Abs(b) > 1e-9 ? delta / Math.Abs(b) : (Math.Abs(delta) > 1e-9 ? double.NaN : 0);
                    string cls = Classify(dir, delta, rel);
                    string relText = double.IsNaN(rel) ? "—" : (rel * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
                    sb.Append($"<tr><td>{Esc(label)}</td><td>{b.ToString(fmt, CultureInfo.InvariantCulture)}</td>" +
                              $"<td>{n.ToString(fmt, CultureInfo.InvariantCulture)}</td>" +
                              $"<td{cls}>{delta.ToString("+" + fmt + ";-" + fmt + ";0", CultureInfo.InvariantCulture)}</td>" +
                              $"<td{cls}>{relText}</td></tr>\n");
                }
                sb.Append("</tbody></table>\n");

                // 場の平均
                if (prev.FieldMean.TryGetValue(a.Condition, out var fmBefore) && fmBefore.Count > 0)
                {
                    sb.Append("<table><thead><tr><th>場（平均）</th><th>前回</th><th>今回</th><th>変化率</th></tr></thead><tbody>\n");
                    foreach (var kv in a.FieldMean)
                    {
                        if (!fmBefore.TryGetValue(kv.Key, out double b))
                        {
                            continue;
                        }
                        double rel = Math.Abs(b) > 1e-9 ? (kv.Value - b) / Math.Abs(b) : double.NaN;
                        string cls = !double.IsNaN(rel) && Math.Abs(rel) >= k_SignificantChange ? " class=moved" : "";
                        string relText = double.IsNaN(rel) ? "—" : (rel * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
                        sb.Append($"<tr><td>{Esc(kv.Key)}</td><td>{b:F4}</td><td>{kv.Value:F4}</td>" +
                                  $"<td{cls}>{relText}</td></tr>\n");
                    }
                    sb.Append("</tbody></table>\n");
                }
            }

            sb.Append($"<p class=meta>赤=悪化 / 橙={k_SignificantChange * 100:F0}%以上の変化 / 緑=改善。" +
                      "指標の多くは良し悪しが一意でないため、橙は「注意して見る」印であって不合格ではない。</p>\n");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return status;
        }

        static Dictionary<string, double> CurrentNumbers(Report.Aggregate a) => new()
        {
            ["guildExtinct"] = a.GuildExtinct,
            ["wolvesExtinct"] = a.WolvesExtinct,
            ["plantsExtinct"] = a.PlantsExtinct,
            ["starvationPer1000Ticks"] = a.StarvationPer1000Ticks,
            ["predationPer1000Ticks"] = a.PredationPer1000Ticks,
            ["birthsPer1000Ticks"] = a.BirthsPer1000Ticks,
            ["meanPlants"] = a.MeanPlants,
            ["meanHerbivores"] = a.MeanHerbivores,
            ["meanWolves"] = a.MeanWolves,
            ["meanTrampleCrush"] = a.MeanCrush,
            ["graveyardRatio"] = a.GraveRatio,
            ["trampleRatio"] = a.TrampleRatio,
            ["fearAvoidanceRatio"] = a.AvoidanceRatio,
        };

        static string Classify(Direction dir, double delta, double rel)
        {
            if (Math.Abs(delta) < 1e-9)
            {
                return "";
            }
            if (dir == Direction.LowerIsBetter)
            {
                return delta > 0 ? " class=worse" : " class=better";
            }
            return !double.IsNaN(rel) && Math.Abs(rel) >= k_SignificantChange ? " class=moved" : "";
        }

        static string Esc(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
