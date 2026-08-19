using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// **パネルに出す値はログにも出す**（CLAUDE.md）— L0 側。
    /// `AquariumPanelLogParityTests` と同じ形。装着中のユーザーは数値を読み上げられず、
    /// セッション後に転記もできないので、パネルにしか無い数値は事実上取得できない。
    /// </summary>
    public sealed class WatchPanelLogParityTests
    {
        /// <summary>パネルにしか無くてよいもの。**理由を書けるものだけ**。</summary>
        static readonly Dictionary<string, string> k_PanelOnly = new Dictionary<string, string>
        {
            { "Status", "焼き込み待ちの文言。場ができる前だけ出る。できたあとは走査済セル数がログにある" },
        };

        static string Dir => Path.Combine(UnityEngine.Application.dataPath, "Watch", "Scripts");

        [Test]
        public void EveryPanelFieldAlsoAppearsInTheLog()
        {
            string panelPath = Path.Combine(Dir, "WatchPanel.cs");
            string fieldPath = Path.Combine(Dir, "WatchField.cs");
            Assert.IsTrue(File.Exists(panelPath), $"走査対象が見つからない: {panelPath}");
            Assert.IsTrue(File.Exists(fieldPath), $"走査対象が見つからない: {fieldPath}");

            string panel = Regex.Replace(File.ReadAllText(panelPath), @"//.*", "");
            string log = File.ReadAllText(fieldPath);

            var referenced = new HashSet<string>();
            foreach (Match brace in Regex.Matches(panel, @"\{([^{}]+)\}"))
            {
                foreach (Match member in Regex.Matches(brace.Groups[1].Value,
                    @"\b(?:f|ticker|pos|m_Head|m_Field|m_View|m_Space|BuildStamp)\.([A-Za-z_]\w*)"))
                {
                    referenced.Add(member.Groups[1].Value);
                }
            }
            Assert.Greater(referenced.Count, 8,
                $"パネルから拾えたメンバが {referenced.Count} 個しかない。走査が壊れている");

            var missing = new List<string>();
            foreach (string name in referenced)
            {
                if (k_PanelOnly.ContainsKey(name)) continue;
                if (!log.Contains(name)) missing.Add(name);
            }

            CollectionAssert.IsEmpty(missing,
                "パネルにあってログに無い項目: " + string.Join(", ", missing) +
                "\n装着中のユーザーは読み上げられないので、セッション後に取得できない");
        }

        /// <summary>刻印とアンカー識別子がログにあること。</summary>
        [Test]
        public void TheStampAndAnchorAreLoggedNotOnlyShownOnThePanel()
        {
            string log = File.ReadAllText(Path.Combine(Dir, "WatchField.cs"));
            string panel = File.ReadAllText(Path.Combine(Dir, "WatchPanel.cs"));

            Assert.IsTrue(panel.Contains("BuildStamp.Text"), "パネルに刻印が出ていない");
            Assert.IsTrue(panel.Contains("AnchorIdentity"), "パネルにアンカー識別子が出ていない");
            Assert.IsTrue(Regex.IsMatch(log, @"Debug\.Log\([^;]*BuildStamp\.Text"),
                "ログに刻印を出していない");
            Assert.IsTrue(Regex.IsMatch(log, @"Debug\.Log\([^;]*AnchorIdentity"),
                "ログにアンカー識別子を出していない");
        }

        /// <summary>
        /// **描画は 1 ファイルからしか呼ばない。** 系列2 で確立した規約を L0 にも掛ける
        /// （`AnchorSpaceRenderer` の grep ゲートと同型）。
        /// </summary>
        [Test]
        public void OnlyTheSpaceRendererDrawsOrTouchesTheCamera()
        {
            var violations = new List<string>();
            foreach (string file in Directory.GetFiles(Dir, "*.cs"))
            {
                if (Path.GetFileName(file) == "WatchSpaceRenderer.cs") continue;
                string src = Regex.Replace(File.ReadAllText(file), @"//.*", "");
                foreach (string raw in src.Split('\n'))
                {
                    if (Regex.IsMatch(raw, @"Graphics\.DrawMesh"))
                        violations.Add($"{Path.GetFileName(file)}: Graphics.DrawMesh — {raw.Trim()}");
                    if (Regex.IsMatch(raw, @"\bCamera\b"))
                        violations.Add($"{Path.GetFileName(file)}: Camera 直参照 — {raw.Trim()}");
                }
            }
            CollectionAssert.IsEmpty(violations,
                "描画とカメラ参照は WatchSpaceRenderer だけに閉じること:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void EveryExclusionHasAReason()
        {
            foreach (var kv in k_PanelOnly)
            {
                Assert.Greater(kv.Value.Length, 10, $"{kv.Key} の除外理由が短すぎる: {kv.Value}");
            }
        }
    }
}
