using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BlockField.SimCore.Ecology;
using SimRunner;

// SimRunner: SimCore をヘッドレスで回し、結果をファイルに残す。
//
// 【なぜ作ったか】検証のたびに使い捨てのハーネスを書いていたため、
// (1) 結果がコンソール出力にしか残らず、セッションをまたぐと失われる
// (2) 逐次実行なので条件を掛け算すると数十分かかる
// (3) 場の様子が数値でしか分からず、けもの道のような「形」を確認できない
// という問題があった。リモート（帰省先から SSH）で回すことを想定し、
// 出力は全てファイルに落とし、report.html 1枚で全部見られるようにしてある。

var opts = Options.Parse(args);
if (opts == null)
{
    Options.PrintUsage();
    return 1;
}

Directory.CreateDirectory(opts.OutDir);

var seeds = Runner.MakeSeeds(opts.Seeds);
var conditions = opts.Conditions;

Console.WriteLine($"SimRunner: {conditions.Count} 条件 × {seeds.Length} シード × {opts.Ticks} ティック " +
                  $"({opts.Size}x{opts.Size}) 並列度 {opts.Parallel}");
Console.WriteLine($"  条件: {string.Join(", ", conditions.Select(c => c.Name))}");
Console.WriteLine($"  出力: {Path.GetFullPath(opts.OutDir)}");

// チェックポイント（長時間実験の途中経過）。場の名前を得るために空のワールドを1つ作る
CheckpointWriter? checkpoints = null;
if (opts.CheckpointInterval > 0)
{
    var probe = Runner.MakeWorld(seeds[0], opts.Size);
    checkpoints = new CheckpointWriter(Path.Combine(opts.OutDir, "checkpoints.csv"), probe.Fields.Keys);
    Console.WriteLine($"  チェックポイント: {opts.CheckpointInterval} ティックごとに checkpoints.csv へ追記");
}

var sw = Stopwatch.StartNew();
int lastPercent = -1;
var results = Runner.Run(conditions, seeds, opts.Ticks, opts.Size, opts.Parallel,
    (done, total) =>
    {
        int percent = done * 100 / total;
        if (percent / 10 != lastPercent / 10)
        {
            lastPercent = percent;
            // 進捗はリダイレクト先のファイルでも読めるよう改行で出す
            Console.WriteLine($"  ... {done}/{total} ({percent}%) {sw.Elapsed.TotalSeconds:F0}s");
        }
    },
    checkpoints, opts.CheckpointInterval);
sw.Stop();
checkpoints?.Dispose();

Console.WriteLine($"シミュレーション完了: {sw.Elapsed.TotalSeconds:F1} 秒 " +
                  $"({sw.Elapsed.TotalSeconds / (conditions.Count * seeds.Length):F2} 秒/ラン)");

var aggregates = Report.Aggregate_(results);

Report.WritePopulationCsv(Path.Combine(opts.OutDir, "population.csv"), results);
Report.WriteSummaryJson(Path.Combine(opts.OutDir, "summary.json"),
    aggregates, results, opts.Ticks, opts.Size, sw.Elapsed.TotalSeconds);

// 代表シードの画像。全シード出すと report.html が巨大になるので先頭 opts.Images 個
var imageDir = Path.Combine(opts.OutDir, "images");
Directory.CreateDirectory(imageDir);
var embedded = new List<(string caption, string dataUri)>();

foreach (var condition in conditions)
{
    foreach (uint seed in seeds.Take(opts.Images))
    {
        // 画像用にもう一度回す。結果を保持し続けるとメモリを食うため
        // （48シード×5場のワールドを全部持つと数百MBになる）
        var world = Runner.MakeWorld(seed, opts.Size);
        for (int t = 0; t < opts.Ticks; t++)
        {
            Simulation.Tick(world, world.Rng, condition.Params);
        }

        var terrain = Heatmap.RenderTerrain(world, out int tw, out int th);
        Save($"{condition.Name}_seed{seed}_terrain", $"{condition.Name} / seed {seed} / 地形と生き物",
            tw, th, terrain);

        foreach (string name in world.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (name == SuitabilityField.FieldName)
            {
                continue; // 静的な場なので毎回出す意味が薄い
            }
            var field = (ScalarField)world.Fields[name];
            var rgb = Heatmap.RenderField(world, field, out int fw, out int fh);
            Save($"{condition.Name}_seed{seed}_{name}", $"{condition.Name} / seed {seed} / {name}",
                fw, fh, rgb);
        }
    }
}

void Save(string fileStem, string caption, int w, int h, byte[] rgb)
{
    byte[] png = Png.Encode(w, h, rgb);
    File.WriteAllBytes(Path.Combine(imageDir, fileStem + ".png"), png);
    embedded.Add((caption, "data:image/png;base64," + Convert.ToBase64String(png)));
}

Report.WriteHtml(Path.Combine(opts.OutDir, "report.html"),
    aggregates, results, embedded, opts.Ticks, opts.Size, sw.Elapsed.TotalSeconds,
    "SimRunner " + string.Join(' ', args));

// 前回との比較（回帰検知）
int exitCode = 0;
if (!string.IsNullOrEmpty(opts.ComparePath))
{
    if (!File.Exists(opts.ComparePath))
    {
        Console.WriteLine($"\n比較対象が見つかりません: {opts.ComparePath}（比較をスキップ）");
    }
    else
    {
        var prev = Compare.Load(opts.ComparePath, out string? loadError);
        if (prev == null)
        {
            Console.WriteLine($"\n比較対象を読めません: {loadError}（比較をスキップ）");
        }
        else
        {
            string diffPath = Path.Combine(opts.OutDir, "diff_report.html");
            var status = Compare.WriteDiffHtml(
                diffPath, opts.ComparePath, prev, aggregates, results, opts.Ticks, opts.Size);
            Console.WriteLine($"\n差分レポート: {diffPath}");

            switch (status)
            {
                case Compare.DeterminismStatus.BrokenSameCode:
                    // コードが同一なのにハッシュが違う。本物の破れ
                    Console.WriteLine("!!! 決定論の破れを検出: ContentHash が前回と一致しません !!!");
                    Console.WriteLine("!!! コミットは前回と同一です。f(シード, イベントログ) が壊れています !!!");
                    exitCode = 2;
                    break;

                case Compare.DeterminismStatus.MismatchUnknownCode:
                    Console.WriteLine("!!! ContentHash が前回と一致しません !!!");
                    Console.WriteLine("!!! コミットを取得できず、実装変更によるものか区別できません !!!");
                    exitCode = 2;
                    break;

                case Compare.DeterminismStatus.ChangedWithCode:
                    // 実装を変えたのなら想定内。ここで exit 2 にすると、
                    // 段階的な移行の期間に毎回警報が鳴って本物の破れを見逃す
                    Console.WriteLine("ContentHash が前回と一致しませんが、コミットも変わっています" +
                                      "（実装を変更したのであれば想定どおり）");
                    break;
            }
        }
    }
}

Console.WriteLine();
foreach (var a in aggregates)
{
    Console.WriteLine($"[{a.Condition}] M5={a.M5Detail} " +
                      $"墓場比={a.GraveRatio:F3} 踏跡比={a.TrampleRatio:F3} " +
                      $"迂回={a.AvoidanceRatio * 100:F1}% " +
                      $"全滅(ギルド/狼/植物)={a.GuildExtinct}/{a.WolvesExtinct}/{a.PlantsExtinct} of {a.Seeds}");
    if (!a.M5Pass)
    {
        exitCode = Math.Max(exitCode, 1);
    }
}
Console.WriteLine();
Console.WriteLine($"出力:");
Console.WriteLine($"  {Path.Combine(opts.OutDir, "report.html")}  ← これ1枚で全部見られる");
Console.WriteLine($"  {Path.Combine(opts.OutDir, "summary.json")}");
Console.WriteLine($"  {Path.Combine(opts.OutDir, "population.csv")}");
Console.WriteLine($"  {imageDir}\\*.png ({embedded.Count} 枚)");
if (opts.CheckpointInterval > 0)
{
    Console.WriteLine($"  {Path.Combine(opts.OutDir, "checkpoints.csv")}");
}

// 終了コード: 0=問題なし / 1=M5 不合格 / 2=決定論の破れ。
// バッチから成否を判定できるようにする
return exitCode;

sealed class Options
{
    public int Seeds = 48;
    public int Ticks = 3000;
    public int Size = 50;
    public int Parallel = Math.Max(1, Environment.ProcessorCount - 2);
    public int Images = 1;
    public string OutDir = "";
    public string ComparePath = "";
    public int CheckpointInterval;
    public List<Condition> Conditions = new();

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        var names = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a)
            {
                case "--seeds": if (!int.TryParse(Next(), out o.Seeds)) return null; break;
                case "--ticks": if (!int.TryParse(Next(), out o.Ticks)) return null; break;
                case "--size": if (!int.TryParse(Next(), out o.Size)) return null; break;
                case "--parallel": if (!int.TryParse(Next(), out o.Parallel)) return null; break;
                case "--images": if (!int.TryParse(Next(), out o.Images)) return null; break;
                case "--out": o.OutDir = Next() ?? ""; break;
                case "--compare": o.ComparePath = Next() ?? ""; break;
                case "--checkpoint-interval":
                    if (!int.TryParse(Next(), out o.CheckpointInterval)) return null;
                    break;
                case "--conditions":
                    string? list = Next();
                    if (list == null) return null;
                    names.AddRange(list.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "-h": case "--help": return null;
                default:
                    Console.Error.WriteLine($"不明な引数: {a}");
                    return null;
            }
        }

        if (names.Count == 0)
        {
            names.Add(Condition.Default.Name);
        }
        foreach (string n in names)
        {
            if (!Condition.All.TryGetValue(n.Trim(), out var c))
            {
                Console.Error.WriteLine($"不明な条件: {n}");
                return null;
            }
            o.Conditions.Add(c);
        }

        if (o.Seeds < 1 || o.Ticks < 1 || o.Size < 4 || o.Parallel < 1)
        {
            return null;
        }

        if (string.IsNullOrEmpty(o.OutDir))
        {
            // 実行時刻でディレクトリを分ける。上書きで前回の結果を失わないため
            o.OutDir = Path.Combine("runs", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        }
        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
使い方: dotnet run -c Release --project tools/SimRunner -- [オプション]

  --seeds N        シード数（既定 48。開発中の反復は 5 でよい）
  --ticks N        ティック数（既定 3000）
  --size N         箱庭の一辺（既定 50）
  --parallel N     並列度（既定 論理コア数-2）
  --conditions a,b 条件をカンマ区切りで（既定 default）
  --images N       画像を出す代表シード数（既定 1。条件ごとに再実行するので増やすと遅い）
  --out DIR        出力先（既定 runs/日時）
  --compare PATH   前回の summary.json と比較し diff_report.html を出す
  --checkpoint-interval N   N ティックごとに checkpoints.csv へ途中経過を追記

終了コード:
  0 問題なし / 1 M5（生態系の安定条件）不合格 / 2 決定論の破れ（ContentHash 不一致）

条件:
  default        既定パラメータ
  trample-off    踏み荒らしの効果のみ無効（書き込みは残す）
  nutrient-off   死の場の養分効果のみ無効
  fear-off       草食獣が恐怖場を読まない

例:
  # 開発中の反復（5シード、数秒）
  dotnet run -c Release --project tools/SimRunner -- --seeds 5 --ticks 2000

  # 最終判定（48シード、対照つき）
  dotnet run -c Release --project tools/SimRunner -- --conditions default,trample-off

  # 前回と比較して回帰を見る
  dotnet run -c Release --project tools/SimRunner -- --compare runs/nightly_20260810/summary.json

  # 長時間実験（途中経過つき）
  dotnet run -c Release --project tools/SimRunner -- --seeds 5 --ticks 100000 --checkpoint-interval 2000
""");
    }
}
