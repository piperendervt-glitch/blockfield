using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// **刻印は常に HEAD と一致しなければならない。**
    ///
    /// 【一致しない刻印は、刻印が無いのより悪い】間違ったものを信じさせるためである。
    /// 2026-08-19 に、実機の刻印が `099d9e5` を指しているのに HEAD が `f550da9` という
    /// 状態が起きた。原因は2つの構造バグで、どちらもここで固定する。
    ///
    /// 1. **`BuildStamp.txt` が git 追跡下にあった。** 刻印は自分を含むコミットより
    ///    前に書かれるので、**常に1つ前のコミットを指す**（循環）
    /// 2. **刻印を `ensureScene()` より前に書いていた。** シーン生成はアセットを作るので、
    ///    先に刻むと**生成物が未コミットでも clean と称する**
    /// </summary>
    public sealed class BuildStampTests
    {
        static string BuildScriptPath => Path.Combine(
            UnityEngine.Application.dataPath, "Scripts", "Editor", "BuildScript.cs");

        static string ProjectRoot => Directory.GetParent(
            UnityEngine.Application.dataPath).FullName;

        /// <summary>
        /// **刻印はシーン生成のあとに書く。** 前に書くと、生成されたシーンが
        /// 未コミットでも `+dirty` が付かない。
        /// </summary>
        [Test]
        public void TheStampIsWrittenAfterTheSceneIsGenerated()
        {
            string src = File.ReadAllText(BuildScriptPath);

            int ensure = src.IndexOf("ensureScene();", System.StringComparison.Ordinal);
            int stamp = src.IndexOf("WriteBuildStamp(scenePath);", System.StringComparison.Ordinal);

            Assert.Greater(ensure, 0, "ensureScene() の呼び出しが見つからない");
            Assert.Greater(stamp, 0, "WriteBuildStamp() の呼び出しが見つからない");
            Assert.Greater(stamp, ensure,
                "刻印をシーン生成より前に書いている。生成物が未コミットでも clean と称する");
        }

        /// <summary>**未コミットの変更を必ず刻む。** APK がどのコミットとも一致しない事実を隠さない。</summary>
        [Test]
        public void TheStampReportsAnUncommittedTree()
        {
            string src = File.ReadAllText(BuildScriptPath);
            Assert.IsTrue(Regex.IsMatch(src, @"status --porcelain"),
                "未コミットの判定をしていない");
            Assert.IsTrue(src.Contains("+dirty"),
                "未コミットのときに刻印へ印を付けていない");
        }

        /// <summary>
        /// **刻印ファイルを追跡しない。** 追跡すると、刻印を含むコミットは
        /// 刻印が指すコミットの次になり、**構造的に一致しなくなる**。
        /// </summary>
        [Test]
        public void TheStampFileIsNotTracked()
        {
            string gitignore = Path.Combine(ProjectRoot, ".gitignore");
            Assert.IsTrue(File.Exists(gitignore), $".gitignore が無い: {gitignore}");

            string text = File.ReadAllText(gitignore);
            Assert.IsTrue(text.Contains("Assets/Resources/BuildStamp.txt"),
                "BuildStamp.txt が .gitignore に無い。追跡すると刻印は常に1つ前のコミットを指す");
            Assert.IsTrue(text.Contains("Assets/Resources/BuildStamp.txt.meta"),
                "BuildStamp.txt.meta が .gitignore に無い");

            // 追跡されたままなら .gitignore は効かない。索引に残っていないことを見る
            string indexPath = Path.Combine(ProjectRoot, ".git", "index");
            if (!File.Exists(indexPath)) return;   // git 管理外での実行（CI 等）は素通し

            // .git/index はバイナリだが、パスは素の文字列で入っている
            byte[] raw = File.ReadAllBytes(indexPath);
            string asText = System.Text.Encoding.ASCII.GetString(raw);
            Assert.IsFalse(asText.Contains("Assets/Resources/BuildStamp.txt"),
                "BuildStamp.txt がまだ git の索引にある。" +
                "git rm --cached Assets/Resources/BuildStamp.txt を実行すること");
        }

        /// <summary>刻印にシーン名・ブランチ・HEAD がすべて入っていること。</summary>
        [Test]
        public void TheStampCarriesSceneBranchAndHead()
        {
            string src = File.ReadAllText(BuildScriptPath);
            Assert.IsTrue(src.Contains("rev-parse --abbrev-ref HEAD"), "ブランチ名を取っていない");
            Assert.IsTrue(src.Contains("rev-parse --short HEAD"), "HEAD を取っていない");
            Assert.IsTrue(Regex.IsMatch(src, @"GetFileNameWithoutExtension\(scenePath\)"),
                "シーン名を取っていない");
        }
    }
}
