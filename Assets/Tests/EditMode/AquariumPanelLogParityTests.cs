using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// **パネルに出す値はログにも出す**（CLAUDE.md）。
    ///
    /// 【なぜテストにするか】装着中のユーザーは数値を読み上げられず、セッション後に
    /// 転記もできないので、パネルにしか無い数値は**事実上取得できない**。
    /// 規約として書いてあったが**3回破った**（Demo 4.5 の M6 FPS、Demo 5a の密度指標、
    /// 2026-08-19 のビルド刻印）。規約を書き直しても4回目が起きる。
    ///
    /// 【書き込み先を制限してテストで固定する】`AnchorSpaceRenderer` 以外から
    /// `Graphics.DrawMesh*` を呼ばせない grep ゲートと同じ形にする。
    /// パネルが参照するメンバの集合が、ログ側のソースに現れることを assert する。
    /// </summary>
    public sealed class AquariumPanelLogParityTests
    {
        /// <summary>
        /// パネルにしか無くてよいもの。**理由を書けるものだけ**を入れる。
        /// 「面倒だから」で足さない。
        /// </summary>
        static readonly Dictionary<string, string> k_PanelOnly = new Dictionary<string, string>
        {
            // 値そのもの（沈降比・復元係数・傘径・プリセット名）はログに出ている。
            // 「選択肢の何番目か」は値と Choices 配列から復元できるので、パネル専用でよい
            { "BellIndex", "傘径の選択肢番号。値 BellDiameter はログにある" },
            { "SinkIndex", "沈降比の選択肢番号。値 SinkRatio はログにある" },
            { "RightingIndex", "復元の選択肢番号。値 RightingGain はログにある" },
            { "PresetIndex", "粒子プリセットの選択肢番号。名前 Name はログにある" },
        };

        [Test]
        public void EveryPanelFieldAlsoAppearsInTheLog()
        {
            string dir = Path.Combine(UnityEngine.Application.dataPath, "Aquarium", "Scripts");
            string panelPath = Path.Combine(dir, "AquariumPanel.cs");
            string flowPath = Path.Combine(dir, "AquariumFlow.cs");
            Assert.IsTrue(File.Exists(panelPath), $"走査対象が見つからない: {panelPath}");
            Assert.IsTrue(File.Exists(flowPath), $"走査対象が見つからない: {flowPath}");

            string panel = Regex.Replace(File.ReadAllText(panelPath), @"//.*", "");
            string log = File.ReadAllText(flowPath);

            // 補間 {...} の中で参照しているメンバ名を集める
            var referenced = new HashSet<string>();
            foreach (Match brace in Regex.Matches(panel, @"\{([^{}]+)\}"))
            {
                foreach (Match member in Regex.Matches(brace.Groups[1].Value,
                    @"\b(?:body|m_Jelly|m_Flow|m_Particles|field|preset|g|BuildStamp)\.([A-Za-z_]\w*)"))
                {
                    referenced.Add(member.Groups[1].Value);
                }
            }
            Assert.Greater(referenced.Count, 20,
                $"パネルから拾えたメンバが {referenced.Count} 個しかない。走査が壊れている");

            var missing = new List<string>();
            foreach (string name in referenced)
            {
                if (k_PanelOnly.ContainsKey(name)) continue;
                if (!log.Contains(name)) missing.Add(name);
            }

            CollectionAssert.IsEmpty(missing,
                "パネルにあってログに無い項目: " + string.Join(", ", missing) +
                "\n装着中のユーザーはこれを読み上げられないので、セッション後に取得できない。" +
                "ログへ出すか、理由を書いて k_PanelOnly に入れること");
        }

        /// <summary>
        /// **ビルド刻印がログにあること。** どのビルドのセッションかをログ単体で
        /// 確定できないと、あとから結果を突き合わせられない。
        /// 2026-08-19 のセッションで実際に漏れていた。
        /// </summary>
        [Test]
        public void TheBuildStampIsLoggedNotOnlyShownOnThePanel()
        {
            string dir = Path.Combine(UnityEngine.Application.dataPath, "Aquarium", "Scripts");
            string log = File.ReadAllText(Path.Combine(dir, "AquariumFlow.cs"));
            string panel = File.ReadAllText(Path.Combine(dir, "AquariumPanel.cs"));

            Assert.IsTrue(panel.Contains("BuildStamp.Text"), "パネルに刻印が出ていない");
            Assert.IsTrue(Regex.IsMatch(log, @"Debug\.Log\([^;]*BuildStamp\.Text"),
                "ログに刻印を出していない。パネルにしか無い値は装着中に取得できない");
        }

        /// <summary>k_PanelOnly の除外に理由が書かれていること。空文字での素通しを防ぐ。</summary>
        [Test]
        public void EveryExclusionHasAReason()
        {
            foreach (var kv in k_PanelOnly)
            {
                Assert.IsNotEmpty(kv.Value, $"{kv.Key} の除外理由が空");
                Assert.Greater(kv.Value.Length, 10, $"{kv.Key} の除外理由が短すぎる: {kv.Value}");
            }
        }
    }
}
