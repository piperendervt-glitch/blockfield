using System;
using BlockField.SimCore.Watch;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// L0（滞在の場）の判定。**判定なしの表現作業**なので prereg は無いが、
    /// **カバレッジが常に全域になる実装は、実質カバレッジが無いのと同じ**である。
    /// これは「空の検証」と同型なので、テストで落とす。
    /// </summary>
    public sealed class WatchL0Tests
    {
        const int W = 10, H = 6, D = 10;
        const float Cell = 0.25f;

        /// <summary>
        /// 走査済み領域を x &lt; 6 に限った部屋。x ≥ 6 は**走査外**（隣室・扉の向こう）。
        /// 走査外は常に欠測でなければならない。
        /// </summary>
        static PresenceField Room()
        {
            var scanned = new bool[W * H * D];
            for (int z = 0; z < D; z++)
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        scanned[(z * H + y) * W + x] = x < 6;
            return new PresenceField(W, H, D, Cell, 0f, 0f, 0f, scanned);
        }

        static L0Sample Tracked(int tick, float x, float y, float z, float value = 1f) =>
            new L0Sample(1, tick, x, y, z, value, L0Coverage.ScannedRoom, L0Label.Measured);

        static L0Sample Lost(int tick) =>
            new L0Sample(1, tick, 0f, 0f, 0f, 0f, L0Coverage.None, L0Label.TrackingLost);

        // ================= カバレッジ =================

        /// <summary>
        /// **トラッキングが生きていれば、居ないことも測定された事実になる。**
        /// 頭のセルが 1、走査済みの他のセルは「測定された 0」。
        /// </summary>
        [Test]
        public void TrackedCoverageMakesAbsenceAMeasuredFact()
        {
            var f = Room();
            f.Ingest(Tracked(10, 0.3f, 0.3f, 0.3f));

            Assert.AreEqual(L0Coverage.ScannedRoom, f.Coverage);
            Assert.AreEqual(f.ScannedCells, f.CoveredCells,
                "カバレッジ内セル数が走査済みセル数と違う");

            Assert.IsTrue(f.TryCellOf(0.3f, 0.3f, 0.3f, out int head));
            Assert.AreEqual(1f, f.Value(head), 1e-6f, "頭のセルの値が 1 でない");
            Assert.AreEqual(head, f.OccupiedIndex);

            // 走査済みだが頭が居ないセル: **測定された 0**（欠測ではない）
            int elsewhere = f.Index(3, 2, 5);
            Assert.IsTrue(f.IsScanned(elsewhere));
            Assert.AreEqual(0f, f.Value(elsewhere), 1e-6f, "居ないセルの値が 0 でない");
            Assert.AreEqual(10, f.LastVerified(elsewhere),
                "居ないセルが検証されていない。『居ない』は測定された事実のはず");
        }

        /// <summary>
        /// **カバレッジは常に全域ではない。** 走査外は 1 セルも入らない。
        /// これが無いと、カバレッジという概念が実質存在しないことになる。
        /// </summary>
        [Test]
        public void CoverageIsNeverTheWholeGrid()
        {
            var f = Room();
            f.Ingest(Tracked(1, 0.3f, 0.3f, 0.3f));

            Assert.Less(f.CoveredCells, f.CellCount,
                "カバレッジが格子全体になっている。カバレッジが無いのと同じ");
            Assert.Greater(f.MissingCells, 0, "欠測セルが 1 つも無い");

            int outside = f.Index(8, 2, 5);
            Assert.IsFalse(f.IsScanned(outside), "テストの部屋の作りが壊れている");
            Assert.AreEqual(PresenceField.NeverVerified, f.LastVerified(outside),
                "走査外セルが検証された。走査外は常に欠測のはず");
        }

        /// <summary>
        /// **トラッキングを失うと、部屋全体が欠測へ落ちる。**
        /// 直前値保持もゼロ埋めもしない。
        /// </summary>
        [Test]
        public void LosingTrackingDropsTheWholeRoomToMissing()
        {
            var f = Room();
            f.Ingest(Tracked(5, 0.3f, 0.3f, 0.3f));
            int before = f.CoveredCells;
            Assert.Greater(before, 0);

            f.Ingest(Lost(6));

            Assert.AreEqual(L0Coverage.None, f.Coverage);
            Assert.AreEqual(0, f.CoveredCells, "喪失中なのにカバレッジ内のセルがある");
            Assert.AreEqual(f.CellCount, f.MissingCells, "全セルが欠測になっていない");
            Assert.AreEqual(-1, f.OccupiedIndex, "喪失中なのに値 1 のセルがある");

            // **最終検証ティックは進まない**（古くなるだけ）
            int cell = f.Index(3, 2, 5);
            Assert.AreEqual(5, f.LastVerified(cell),
                "喪失中に最終検証ティックが更新された。欠測を測定として記録している");
            Assert.AreEqual(1, f.StalenessAt(cell), "経過ティックが合わない");
        }

        /// <summary>
        /// 未装着・喪失の**区間**でカバレッジが空集合であり続けること。
        /// 1ティックだけ見て通るのを防ぐ。
        /// </summary>
        [Test]
        public void CoverageStaysEmptyForTheWholeUntrackedInterval()
        {
            var f = Room();
            for (int t = 0; t < 5; t++) f.Ingest(Tracked(t, 0.3f, 0.3f, 0.3f));

            for (int t = 5; t < 40; t++)
            {
                f.Ingest(new L0Sample(1, t, 0f, 0f, 0f, 0f, L0Coverage.None,
                    t < 20 ? L0Label.TrackingLost : L0Label.NotWorn));
                Assert.AreEqual(0, f.CoveredCells, $"t={t} でカバレッジが空でない");
            }

            int cell = f.Index(3, 2, 5);
            Assert.AreEqual(4, f.LastVerified(cell), "区間中に検証が起きている");
            Assert.AreEqual(35, f.StalenessAt(cell), "経過ティックが積み上がっていない");
        }

        /// <summary>
        /// **走査外領域の最終検証ティックは、セッション中に一度も更新されない。**
        /// 装着者が動き回っても変わらないこと（1ティックの確認では足りない）。
        /// </summary>
        [Test]
        public void UnscannedCellsAreNeverVerifiedDuringTheWholeSession()
        {
            var f = Room();
            var rngLike = new[] { 0.1f, 0.6f, 1.1f, 0.4f, 1.4f, 0.9f };

            for (int t = 0; t < 200; t++)
            {
                float x = rngLike[t % rngLike.Length];
                float z = rngLike[(t * 3) % rngLike.Length];
                f.Ingest(Tracked(t, x, 0.3f, z));
            }

            for (int z = 0; z < D; z++)
                for (int y = 0; y < H; y++)
                    for (int x = 6; x < W; x++)
                    {
                        int i = f.Index(x, y, z);
                        Assert.AreEqual(PresenceField.NeverVerified, f.LastVerified(i),
                            $"走査外セル ({x},{y},{z}) が検証された");
                        Assert.AreEqual(int.MaxValue, f.StalenessAt(i),
                            "走査外セルの経過が有限になっている");
                    }
        }

        /// <summary>頭が走査外へ出たら、値 1 のセルは無くなる（0 を書かない）。</summary>
        [Test]
        public void SteppingOutsideTheScannedAreaLeavesNoOccupiedCell()
        {
            var f = Room();
            f.Ingest(Tracked(1, 1.6f, 0.3f, 0.3f));   // x=1.6 → セル 6 = 走査外
            Assert.AreEqual(-1, f.OccupiedIndex, "走査外に値 1 が立った");
            Assert.AreEqual(L0Coverage.ScannedRoom, f.Coverage,
                "トラッキングは生きているのでカバレッジは空にならない");
        }

        // ================= 固定ティック =================

        /// <summary>**20Hz 固定。フレーム駆動にしない。**</summary>
        [Test]
        public void TheTickerIsFixedAtTwentyHertzRegardlessOfFrameRate()
        {
            var a = new L0Ticker();
            for (int i = 0; i < 60; i++) a.Advance(1f / 60f);      // 60fps で 1 秒

            var b = new L0Ticker();
            for (int i = 0; i < 24; i++) b.Advance(1f / 24f);      // 24fps で 1 秒

            Assert.AreEqual(20, a.Tick, "60fps で 1 秒あたり 20 ティックになっていない");
            Assert.AreEqual(20, b.Tick, "24fps で 1 秒あたり 20 ティックになっていない");
            Assert.AreEqual(a.Tick, b.Tick, "フレームレートでティック数が変わっている");
        }

        /// <summary>長い停止のあとに走り出さない。捨てた分は数える。</summary>
        [Test]
        public void TheTickerCapsCatchUpAndCountsWhatItDropped()
        {
            var t = new L0Ticker();
            t.Advance(5f);   // 5 秒ぶん = 100 ティック相当

            Assert.AreEqual(L0Ticker.MaxStepsPerFrame, t.StepsLastFrame,
                "1フレームで上限を超えて消化している");
            Assert.Greater(t.DroppedTicks, 0, "捨てたティックを数えていない");
            Assert.Less(t.Backlog, t.StepSeconds, "積み残しが 1 ティックぶん以上残っている");
        }

        // ================= 記録 → 再生 =================

        /// <summary>
        /// **記録から同じ絵を再生できる**ことの土台。書式は1か所にしか書かないが、
        /// 読みと書きが食い違えば再生は静かにずれるので往復を固定する。
        /// </summary>
        [Test]
        public void TheLogFormatRoundTrips()
        {
            var original = new L0Sample(7, 1234, 1.2345f, -0.5f, 3.75f, 1f,
                L0Coverage.ScannedRoom, L0Label.Measured);

            string line = "08-19 20:00:00.000 I/Unity   (123): " + L0LogFormat.Format(original);
            Assert.IsTrue(L0LogFormat.TryParse(line, out var back), "ログ行を読み戻せない");

            Assert.AreEqual(original.ProducerId, back.ProducerId);
            Assert.AreEqual(original.Tick, back.Tick);
            Assert.AreEqual(original.X, back.X, 1e-3f);
            Assert.AreEqual(original.Y, back.Y, 1e-3f);
            Assert.AreEqual(original.Z, back.Z, 1e-3f);
            Assert.AreEqual(original.Value, back.Value, 1e-3f);
            Assert.AreEqual(original.Coverage, back.Coverage);
            Assert.AreEqual(original.Label, back.Label);

            Assert.IsFalse(L0LogFormat.TryParse("関係のない行", out _),
                "関係ない行を読めてしまっている");
        }

        /// <summary>再生した場が、実時間で作った場と同じ状態になること。</summary>
        [Test]
        public void ReplayingTheLogReproducesTheSameField()
        {
            var live = Room();
            var lines = new System.Collections.Generic.List<string>();

            for (int t = 0; t < 50; t++)
            {
                var s = t is >= 20 and < 30
                    ? Lost(t)
                    : Tracked(t, 0.1f + (t % 5) * 0.25f, 0.3f, 0.3f);
                live.Ingest(s);
                lines.Add(L0LogFormat.Format(s));
            }

            var replay = Room();
            foreach (string line in lines)
            {
                Assert.IsTrue(L0LogFormat.TryParse(line, out var s));
                replay.Ingest(s);
            }

            Assert.AreEqual(live.Tick, replay.Tick);
            Assert.AreEqual(live.Coverage, replay.Coverage);
            Assert.AreEqual(live.OccupiedIndex, replay.OccupiedIndex);
            for (int i = 0; i < live.CellCount; i++)
            {
                Assert.AreEqual(live.LastVerified(i), replay.LastVerified(i),
                    $"セル {i} の最終検証ティックが再生で違う");
                Assert.AreEqual(live.Value(i), replay.Value(i), 1e-6f,
                    $"セル {i} の値が再生で違う");
            }
        }

        /// <summary>プロデューサ識別子がレコードに残ること（後で2つ目を足すため）。</summary>
        [Test]
        public void TheProducerIdSurvivesIntoTheRecord()
        {
            var s = new L0Sample(42, 1, 0f, 0f, 0f, 1f, L0Coverage.ScannedRoom, L0Label.Measured);
            Assert.IsTrue(L0LogFormat.TryParse(L0LogFormat.Format(s), out var back));
            Assert.AreEqual(42, back.ProducerId, "プロデューサ識別子が失われている");
        }
    }
}
