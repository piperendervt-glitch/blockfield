using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// **水槽モードの描画はアンカー基準でなければならない**、という規約をテストで守る。
    ///
    /// 【なぜ規約が要るか】部屋座標をワールドへ移す行列を、粒子・クラゲ・デバッグ表示が
    /// それぞれ独立に組み立てていた。**同じ組み立てを3回書けば3回間違えられる**。
    /// 実際に2種類の誤りが同時に起きていた:
    ///   1. 主軸ヨーを戻す回転の符号が3か所とも逆（2×ヨー = 75.6° ずれ）
    ///   2. アンカーが一度も適用されていなかった（実質ワールド座標で描いていた）
    /// 2 のせいで **HMD を被り直すたびに座標がずれ**、実機観測が汚染されていた。
    /// 同種の現象は susuwatari-mirror (2026-08-05)、blockfield VR (2026-08-13) でも
    /// 起きており、対策は「空間アンカー化」だったが、**新しい描画を足すたびに
    /// 規約から外れられる**状態だったので再発した。
    ///
    /// 【守り方】書き込み経路を1本に絞って例外を機械的に禁じる。
    /// 生態系の <c>VoxelGrid.TrySetBlockEcology</c>（CLAUDE.md の固定レイヤー原則）と同型。
    /// 意味解析は要らず「呼んだかどうか」だけで決まるのが要点。
    /// </summary>
    public class AquariumRenderingConventionTests
    {
        const string k_Dir = "Aquarium/Scripts";
        const string k_Allowed = "AnchorSpaceRenderer.cs";

        static string[] SourceFiles()
        {
            string dir = Path.Combine(Application.dataPath, "Aquarium", "Scripts");
            Assert.IsTrue(Directory.Exists(dir), $"走査対象が見つからない: {dir}");
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 3, "走査対象のソースが少なすぎる");
            return files;
        }

        /// <summary>コメントを除いた行を返す（規約の説明文で落ちないように）。</summary>
        static IEnumerable<(string file, int line, string text)> CodeLines()
        {
            foreach (string file in SourceFiles())
            {
                string src = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", "",
                    RegexOptions.Singleline);
                var lines = src.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string text = Regex.Replace(lines[i], @"//.*$", "");
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    yield return (Path.GetFileName(file), i + 1, text);
                }
            }
        }

        /// <summary>
        /// **描画の入口は1つだけ。** <c>Graphics.DrawMesh*</c> を呼んで良いのは
        /// <see cref="BlockField.Aquarium.AnchorSpaceRenderer"/> のみ。
        /// </summary>
        [Test]
        public void OnlyTheAnchorSpaceRendererMayDraw()
        {
            var violations = new List<string>();
            int scanned = 0;
            foreach (var (file, line, text) in CodeLines())
            {
                scanned++;
                if (file == k_Allowed) continue;
                if (Regex.IsMatch(text, @"Graphics\s*\.\s*DrawMesh"))
                {
                    violations.Add($"{file}:{line} {text.Trim()}");
                }
            }

            Assert.Greater(scanned, 200, "走査行数が少なすぎる（コメント除去が過剰）");
            CollectionAssert.IsEmpty(violations,
                $"{k_Dir} で描画して良いのは {k_Allowed} だけ。" +
                "空間行列を各所で組み立てると、アンカーの適用漏れや回転の符号ミスが" +
                "同じ数だけ起こる（実際に3か所すべてで起きた）:\n  "
                + string.Join("\n  ", violations));
        }

        /// <summary>
        /// **アンカー未確定のとき identity へ落ちてはならない。**
        ///
        /// 以前は各 View が <c>m_AnchorSpace != null ? ... : Matrix4x4.identity</c> と
        /// 書いており、アンカーが配線されていなくても**黙って**ワールド座標で描いていた。
        /// 静かに壊れるので、実機で見るまで誰も気づけなかった。
        /// </summary>
        [Test]
        public void NoSilentFallbackToWorldSpace()
        {
            var violations = new List<string>();
            foreach (var (file, line, text) in CodeLines())
            {
                if (!Regex.IsMatch(text, @"Matrix4x4\s*\.\s*identity")) continue;
                // 「戻り値の初期化」は許す。三項演算子のフォールバックだけを禁じる
                if (Regex.IsMatch(text, @"\?.*Matrix4x4\s*\.\s*identity"))
                {
                    violations.Add($"{file}:{line} {text.Trim()}");
                }
            }

            CollectionAssert.IsEmpty(violations,
                "アンカーが無いときに identity へ落とすと、座標のずれが見えなくなる。" +
                "描かずにエラーを出すこと:\n  " + string.Join("\n  ", violations));
        }

        /// <summary>
        /// **カメラに触れて良いのも入口だけ。**
        ///
        /// 描画の入口を集約しても、**回転を引数で受け取る形なら呼ぶ側が
        /// 間違った座標系の回転を渡せる**。実際、粒子のビルボードはカメラの
        /// ワールド回転をそのまま部屋座標の行列に入れており、アンカーを
        /// 正しく適用した瞬間に約 89° ずれて平面が横を向いた。
        /// 位置だけ守って回転を守っていなかった、という穴だった。
        ///
        /// カメラの姿勢はワールド座標の量なので、部屋座標へ直す責任は
        /// <see cref="BlockField.Aquarium.AnchorSpaceRenderer.TryGetBillboardRotation"/>
        /// が持つ。呼ぶ側はカメラを見に行かない。
        /// </summary>
        [Test]
        public void OnlyTheAnchorSpaceRendererMayReadTheCamera()
        {
            var violations = new List<string>();
            foreach (var (file, line, text) in CodeLines())
            {
                if (file == k_Allowed) continue;
                if (Regex.IsMatch(text, @"\bCamera\s*\.\s*(main|current|allCameras)\b")
                    || Regex.IsMatch(text, @"\bCamera\s+\w+\s*[=;)]"))
                {
                    violations.Add($"{file}:{line} {text.Trim()}");
                }
            }

            CollectionAssert.IsEmpty(violations,
                "カメラの姿勢はワールド座標の量。部屋座標の行列へそのまま入れると" +
                "アンカーの姿勢だけ余計に回る。部屋座標へ直した回転を " +
                $"{k_Allowed} からもらうこと:\n  " + string.Join("\n  ", violations));
        }

        /// <summary>
        /// 規約の対象ファイル自体が存在すること（改名で空振りにならないように）。
        /// </summary>
        [Test]
        public void TheSingleEntryPointExists()
        {
            bool found = false;
            foreach (string f in SourceFiles())
            {
                if (Path.GetFileName(f) == k_Allowed) found = true;
            }
            Assert.IsTrue(found, $"{k_Allowed} が無い。規約テストが空振りしている");
        }
    }
}
