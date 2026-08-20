using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// **パネルの行が箱に収まることを固定する。**
    ///
    /// 【なぜテストにするか】一度直すだけでは足りない。**項目を足すたびに
    /// 下の行が箱から押し出されていた**のが、装着中に確認できなかった3件
    /// （凡例 / 描画数 / 格子と確からしさ）の共通の原因である。
    /// 9行あったが箱は 620×240 で 6行ぶんしか無かった。
    /// **足すことが壊すことになっていた。**
    ///
    /// これがあれば、次に項目を足したとき**ビルドが通らない**。
    /// `WatchPanelLogParityTests`（パネルの値はログにも出す）と同じ形。
    /// </summary>
    public sealed class WatchPanelFitsTests
    {
        /// <summary>Unity UI Text の行送りの見積り（フォント高に対する係数）。</summary>
        const float k_LineSpacing = 1.2f;

        /// <summary>
        /// 1行の文字数の上限。水槽パネルで**行が右へ伸びて視野から見切れ、
        /// 現在値が読めないので比較そのものが成立しなかった**（2026-08-18）ときに
        /// 置いた目安をそのまま使う。
        /// </summary>
        const int k_MaxCharsPerLine = 62;

        /// <summary>`{...}` の展開後の見積り文字数（数値・短い語を想定）。</summary>
        const int k_FieldWidth = 8;

        static string PanelPath => Path.Combine(
            UnityEngine.Application.dataPath, "Watch", "Scripts", "WatchPanel.cs");

        static string BootstrapPath => Path.Combine(
            UnityEngine.Application.dataPath, "Scripts", "Editor", "WatchSceneBootstrap.cs");

        /// <summary>パネル本文の各行（`\n` 区切り）の見積り文字数を返す。</summary>
        static int[] EstimateLines(string source, out int lineCount)
        {
            // m_Text.text = ... ; の塊を取り出す
            var m = Regex.Match(source, @"m_Text\.text\s*=\s*(.*?);\s*$",
                RegexOptions.Singleline | RegexOptions.Multiline);
            Assert.IsTrue(m.Success, "パネル本文の代入が見つからない");

            string expr = m.Groups[1].Value;
            // 文字列リテラルだけを連結する（+ や変数名は落とす）
            var sb = new System.Text.StringBuilder();
            foreach (Match lit in Regex.Matches(expr, @"\$?""((?:[^""\\]|\\.)*)"""))
            {
                sb.Append(lit.Groups[1].Value);
            }
            string joined = sb.ToString();

            // 補間フィールドを固定幅に置き換えてから行へ割る
            joined = Regex.Replace(joined, @"\{[^{}]*\}", new string('x', k_FieldWidth));
            string[] lines = joined.Split(new[] { @"\n" }, System.StringSplitOptions.None);

            lineCount = lines.Length;
            var widths = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++) widths[i] = lines[i].Length;
            return widths;
        }

        [Test]
        public void ThePanelFitsInsideItsBox()
        {
            string panel = Regex.Replace(File.ReadAllText(PanelPath), @"//.*", "");
            string boot = File.ReadAllText(BootstrapPath);

            var size = Regex.Match(boot, @"sizeDelta = new Vector2\(([\d.]+)f, ([\d.]+)f\)");
            Assert.IsTrue(size.Success, "パネルの箱の寸法が読めない");
            float boxHeight = float.Parse(size.Groups[2].Value);

            var font = Regex.Match(boot, @"uiText\.fontSize = (\d+)");
            Assert.IsTrue(font.Success, "フォントサイズが読めない");
            float fontSize = int.Parse(font.Groups[1].Value);

            var margin = Regex.Match(boot, @"offsetMin = new Vector2\([\d.]+f, ([\d.]+)f\)");
            float pad = margin.Success ? float.Parse(margin.Groups[1].Value) : 0f;

            float usable = boxHeight - pad * 2f;
            float lineHeight = fontSize * k_LineSpacing;
            int capacity = (int)(usable / lineHeight);

            EstimateLines(panel, out int lineCount);

            Assert.LessOrEqual(lineCount, capacity,
                $"パネルが {lineCount} 行だが、箱 {boxHeight}px（余白 {pad}×2）に" +
                $" フォント {fontSize}px で入るのは {capacity} 行まで。" +
                "**はみ出した行は装着中に読めない。** 行を減らすか、警告行に集約すること");
        }

        [Test]
        public void NoPanelLineRunsOffToTheRight()
        {
            string panel = Regex.Replace(File.ReadAllText(PanelPath), @"//.*", "");
            int[] widths = EstimateLines(panel, out _);

            for (int i = 0; i < widths.Length; i++)
            {
                Assert.LessOrEqual(widths[i], k_MaxCharsPerLine,
                    $"{i + 1} 行目が約 {widths[i]} 文字。上限 {k_MaxCharsPerLine} 文字。" +
                    "**右へ伸びると視野から見切れ、現在値が読めない**");
            }
        }

        /// <summary>
        /// **警告は1行に集約されていること。** 検査を足しても行数が増えない形を保つ。
        /// </summary>
        [Test]
        public void WarningsAreAggregatedIntoOneLine()
        {
            string panel = File.ReadAllText(PanelPath);
            Assert.IsTrue(panel.Contains("string Warnings()"), "警告の集約が無い");
            Assert.IsTrue(panel.Contains("異常なし"), "異常が無いときの表示が無い");
            Assert.IsTrue(Regex.IsMatch(panel, @"w\.Count}件"),
                "警告が多いときに件数を出していない。**黙って落とさない**");

            // 警告行は本文に1つだけ
            int occurrences = Regex.Matches(panel, @"\$""警告 ").Count;
            Assert.AreEqual(2, occurrences,
                $"警告行が {occurrences} 箇所。場が無いときと通常時の 2 箇所のはず");
        }
    }
}
