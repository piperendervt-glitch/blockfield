// L0a（観測）の実測用。**逆投影だけを行う。**
//
// しないこと: 補間、平滑化、予測、直前値保持、ゼロ埋め、無効レイの切り捨て、
// レイキャスト、格子化、履歴の蓄積、座標変換、通行可能性の判定。
//
// **無効レイを落とさない。** 点群にすると返らなかったレイは消えるが、
// 「値が無い」と「距離が無限」は別の測定結果である。
//
// 【必ず図を出す】数値だけの報告は、**対象を取り違えていても同じ形で出てくる**。
// 1回の測定につき RGB / 深度（無効レイは黒）/ 測定箇所の重ね描き の3枚を保存する。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using Intel.RealSense;
using RsStream = Intel.RealSense.Stream;

int width = 848, height = 480, fps = 30, frames = 30;
string name = "shot";
string outDir = Path.Combine("..", "..", "docs", "measurements", "l0a");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "-w") width = int.Parse(args[++i]);
    else if (args[i] == "-h") height = int.Parse(args[++i]);
    else if (args[i] == "-f") fps = int.Parse(args[++i]);
    else if (args[i] == "-n") frames = int.Parse(args[++i]);
    else if (args[i] == "-name") name = args[++i];
    else if (args[i] == "-out") outDir = args[++i];
}
Directory.CreateDirectory(outDir);

using var pipeline = new Pipeline();
var cfg = new Config();
cfg.EnableStream(RsStream.Depth, width, height, Format.Z16, fps);
cfg.EnableStream(RsStream.Color, 640, 480, Format.Rgb8, 30);
using var profile = pipeline.Start(cfg);

var dev = profile.Device;
Console.WriteLine($"デバイス: {dev.Info[CameraInfo.Name]}  シリアル={dev.Info[CameraInfo.SerialNumber]}");
Console.WriteLine($"ファーム: {dev.Info[CameraInfo.FirmwareVersion]}  USB={dev.Info[CameraInfo.UsbTypeDescriptor]}");

var vsp = profile.GetStream(RsStream.Depth).As<VideoStreamProfile>();
var intr = vsp.GetIntrinsics();
Console.WriteLine($"深度: {vsp.Width}x{vsp.Height} @ {vsp.Framerate}Hz");
Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
    "内部パラメータ: fx={0:F3} fy={1:F3} ppx={2:F3} ppy={3:F3} model={4}",
    intr.fx, intr.fy, intr.ppx, intr.ppy, intr.model));

float depthScale = 0.001f;
foreach (var s in profile.Device.Sensors)
    if (s.Is(Extension.DepthSensor)) { depthScale = s.DepthScale; break; }
Console.WriteLine($"深度スケール: {depthScale} m/unit");
Console.WriteLine();

int W = vsp.Width, H = vsp.Height, rays = W * H;
var buf = new ushort[rays];
byte[] rgb = null;
int rgbW = 0, rgbH = 0;

long validTotal = 0, frameCount = 0;
var invalidByZone = new long[9];
var raysByZone = new long[9];
var sw = Stopwatch.StartNew();
var proc = Process.GetCurrentProcess();
TimeSpan cpu0 = proc.TotalProcessorTime;

for (int f = 0; f < frames; f++)
{
    using var fs = pipeline.WaitForFrames();
    using var depth = fs.DepthFrame;
    depth.CopyTo(buf);
    frameCount++;

    int valid = 0;
    for (int y = 0; y < H; y++)
    {
        int zy = y * 3 / H;
        for (int x = 0; x < W; x++)
        {
            int zone = zy * 3 + x * 3 / W;
            raysByZone[zone]++;
            if (buf[y * W + x] == 0) { invalidByZone[zone]++; continue; }
            valid++;
        }
    }
    validTotal += valid;

    if (f == frames - 1)
    {
        using var color = fs.ColorFrame;
        if (color != null)
        {
            rgbW = color.Width; rgbH = color.Height;
            rgb = new byte[rgbW * rgbH * 3];
            color.CopyTo(rgb);
        }
    }
}
sw.Stop();
TimeSpan cpu = proc.TotalProcessorTime - cpu0;
double sec = sw.Elapsed.TotalSeconds;

Console.WriteLine($"=== {frames} フレーム / {sec:F2} 秒 ===");
Console.WriteLine($"実効レート: {frameCount / sec:F1} Hz（要求 {fps}Hz）");
Console.WriteLine($"1フレームのレイ数: {rays}");
Console.WriteLine($"有効レイの比率: {100.0 * validTotal / (rays * (double)frameCount):F1}%");
Console.WriteLine($"1フレームあたりの有効点数: {validTotal / frameCount}");
Console.WriteLine($"CPU: {cpu.TotalSeconds / sec * 100:F1}%（1コア換算 / {Environment.ProcessorCount} コア）");
Console.WriteLine($"USB 帯域（深度のみ、16bit）: {rays * 2.0 * frameCount / sec * 8 / 1e6:F0} Mbps");
Console.WriteLine();
Console.WriteLine("視野の区画ごとの無効レイ率（左上→右下）:");
for (int r = 0; r < 3; r++)
{
    var cells = new string[3];
    for (int c = 0; c < 3; c++)
        cells[c] = $"{100.0 * invalidByZone[r * 3 + c] / raysByZone[r * 3 + c],5:F1}%";
    Console.WriteLine("  " + string.Join(" ", cells));
}

// ---- 中央帯の深度プロファイル ----
const int bandRows = 11;
int y0 = H / 2 - bandRows / 2;
var centre = new float[W];
var scratch = new float[bandRows];
for (int x = 0; x < W; x++)
{
    int n = 0;
    for (int k = 0; k < bandRows; k++)
    {
        ushort z = buf[(y0 + k) * W + x];
        if (z != 0) scratch[n++] = z * depthScale;
    }
    if (n == 0) { centre[x] = 0f; continue; }      // 無効は 0 のまま持つ
    Array.Sort(scratch, 0, n);
    centre[x] = scratch[n / 2];
}

float bg = 0f; int bgN = 0;
foreach (float d in centre) if (d > 0.1f) { bg += d; bgN++; }
bg = bgN > 0 ? bg / bgN : 0f;

int runStart = -1, bestStart = -1, bestLen = 0;
for (int x = 0; x < W; x++)
{
    bool near = centre[x] > 0.1f && centre[x] < bg - 0.20f;
    if (near) { if (runStart < 0) runStart = x; }
    else
    {
        if (runStart >= 0 && x - runStart > bestLen) { bestLen = x - runStart; bestStart = runStart; }
        runStart = -1;
    }
}

Console.WriteLine();
Console.WriteLine("=== 中央帯の深度プロファイル ===");
Console.WriteLine($"中央帯の平均深度（背景の目安）: {bg:F3} m");
float runDist = 0f, runWidthM = 0f; int runValid = 0;
if (bestLen > 0)
{
    float dsum = 0f;
    for (int x = bestStart; x < bestStart + bestLen; x++)
        if (centre[x] > 0.1f) { dsum += centre[x]; runValid++; }
    runDist = runValid > 0 ? dsum / runValid : 0f;
    runWidthM = bestLen * runDist / intr.fx;
    Console.WriteLine($"最も長い「手前の連続区間」: 列 {bestStart}〜{bestStart + bestLen - 1}");
    Console.WriteLine($"  画素幅 {bestLen} px / 距離 {runDist:F3} m → **実寸 {runWidthM * 100:F1} cm**");
    Console.WriteLine($"  その区間の有効レイ: {runValid}/{bestLen}");
}
else
{
    Console.WriteLine("手前に飛び出している連続区間は見つからなかった");
}

// ================= 図を出す =================
// **数値だけの報告は、対象を取り違えていても同じ形で出てくる。**
float MinM = 0.2f, MaxM = 4.0f;

Color Heat(float m)
{
    // 近い=暖色 / 遠い=寒色。**無効レイは黒**
    if (m <= 0f) return Color.Black;
    float t = Math.Clamp((m - MinM) / (MaxM - MinM), 0f, 1f);
    // 赤 → 黄 → 緑 → 水 → 青
    float[] stops = { 0f, 0.25f, 0.5f, 0.75f, 1f };
    Color[] cols = { Color.FromArgb(220, 40, 40), Color.FromArgb(240, 200, 40),
                     Color.FromArgb(60, 200, 60), Color.FromArgb(40, 200, 220),
                     Color.FromArgb(40, 60, 220) };
    for (int i = 0; i < stops.Length - 1; i++)
    {
        if (t > stops[i + 1]) continue;
        float u = (t - stops[i]) / (stops[i + 1] - stops[i]);
        return Color.FromArgb(
            (int)(cols[i].R + (cols[i + 1].R - cols[i].R) * u),
            (int)(cols[i].G + (cols[i + 1].G - cols[i].G) * u),
            (int)(cols[i].B + (cols[i + 1].B - cols[i].B) * u));
    }
    return cols[^1];
}

void DrawLegend(Graphics g, int imgW, int imgH)
{
    // **図の中に凡例を書く。** 別の場所にある説明は見ないで済む形にする
    int barW = 260, barH = 16, x0 = 10, yb = imgH - 46;
    using var back = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
    g.FillRectangle(back, 0, imgH - 56, imgW, 56);
    for (int i = 0; i < barW; i++)
    {
        float m = MinM + (MaxM - MinM) * i / (barW - 1f);
        using var b = new SolidBrush(Heat(m));
        g.FillRectangle(b, x0 + i, yb, 1, barH);
    }
    using var f2 = new Font("Consolas", 10f);
    using var w = new SolidBrush(Color.White);
    g.DrawString($"{MinM:F1}m", f2, w, x0 - 2, yb + barH + 1);
    g.DrawString($"{(MinM + MaxM) / 2:F1}m", f2, w, x0 + barW / 2 - 12, yb + barH + 1);
    g.DrawString($"{MaxM:F1}m", f2, w, x0 + barW - 20, yb + barH + 1);
    g.DrawString("近い←→遠い", f2, w, x0 + barW + 12, yb + 1);

    using var black = new SolidBrush(Color.Black);
    g.FillRectangle(black, x0 + barW + 110, yb, barH, barH);
    using var pen = new Pen(Color.White, 1f);
    g.DrawRectangle(pen, x0 + barW + 110, yb, barH, barH);
    g.DrawString("黒 = 無効レイ（値が返らなかった）", f2, w, x0 + barW + 132, yb + 1);
}

string Save(Bitmap bmp, string suffix)
{
    string path = Path.GetFullPath(Path.Combine(outDir, $"{name}_{suffix}.png"));
    bmp.Save(path, ImageFormat.Png);
    return path;
}

// (a) RGB
string pathRgb = "(色フレームが取れなかった)";
if (rgb != null)
{
    using var bmp = new Bitmap(rgbW, rgbH + 56);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.Black);
        for (int y = 0; y < rgbH; y++)
            for (int x = 0; x < rgbW; x++)
            {
                int i = (y * rgbW + x) * 3;
                bmp.SetPixel(x, y, Color.FromArgb(rgb[i], rgb[i + 1], rgb[i + 2]));
            }
        using var f2 = new Font("Consolas", 11f);
        using var back = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        g.FillRectangle(back, 0, rgbH, rgbW, 56);
        using var w = new SolidBrush(Color.White);
        g.DrawString($"RGB  {name}  何に向いていたか", f2, w, 8, rgbH + 6);
        g.DrawString($"深度は別図（{name}_depth.png）", f2, w, 8, rgbH + 26);
    }
    pathRgb = Save(bmp, "rgb");
}

// (b) 深度（無効レイは黒）
string pathDepth;
{
    using var bmp = new Bitmap(W, H + 56);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.Black);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                bmp.SetPixel(x, y, Heat(buf[y * W + x] * depthScale));
        DrawLegend(g, W, H + 56);
    }
    pathDepth = Save(bmp, "depth");
}

// (c) 測定に使った帯と、幅を出した区間を重ねる
string pathMarked;
{
    using var bmp = new Bitmap(W, H + 56);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.Black);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                bmp.SetPixel(x, y, Heat(buf[y * W + x] * depthScale));

        using var band = new Pen(Color.White, 1f);
        g.DrawRectangle(band, 0, y0, W - 1, bandRows);      // 測定に使った帯
        using var f2 = new Font("Consolas", 11f);
        using var w2 = new SolidBrush(Color.White);
        g.DrawString("← 測定に使った帯（この 11 行の中央値）", f2, w2, 6, y0 - 18);

        if (bestLen > 0)
        {
            using var run = new Pen(Color.Magenta, 2f);
            g.DrawRectangle(run, bestStart, y0 - 30, bestLen, bandRows + 60);
            g.DrawString($"幅を出した区間 {bestLen}px = {runWidthM * 100:F1}cm @ {runDist:F2}m",
                f2, new SolidBrush(Color.Magenta), Math.Max(2, bestStart - 40), y0 + 46);
        }
        else
        {
            g.DrawString("手前の連続区間は見つからなかった", f2,
                new SolidBrush(Color.Magenta), 6, y0 + 46);
        }
        DrawLegend(g, W, H + 56);
    }
    pathMarked = Save(bmp, "marked");
}

Console.WriteLine();
Console.WriteLine("=== 図（**何を測ったかを目で確かめる**）===");
Console.WriteLine($"  RGB   : {pathRgb}");
Console.WriteLine($"  深度  : {pathDepth}");
Console.WriteLine($"  測定箇所: {pathMarked}");
