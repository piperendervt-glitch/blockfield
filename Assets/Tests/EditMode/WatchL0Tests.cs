using System;
using BlockField.SimCore.Fluid;
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
        const int W = 10, D = 10;
        const float Cell = 0.25f;

        /// <summary>
        /// 走査済み領域を x &lt; 6 に限った部屋。x ≥ 6 は**走査外**（隣室・扉の向こう）。
        /// 走査外は常に欠測でなければならない。
        /// </summary>
        static PresenceField Room()
        {
            var scanned = new bool[W * D];
            var floorY = new float[W * D];
            for (int z = 0; z < D; z++)
                for (int x = 0; x < W; x++)
                    scanned[z * W + x] = x < 6;
            return new PresenceField(W, D, Cell, 0f, 0f, scanned, floorY);
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

            Assert.IsTrue(f.TryCellOf(0.3f, 0.3f, out int head));
            Assert.AreEqual(1f, f.Value(head), 1e-6f, "頭のセルの値が 1 でない");
            Assert.AreEqual(head, f.OccupiedIndex);

            // 走査済みだが頭が居ないセル: **測定された 0**（欠測ではない）
            int elsewhere = f.Index(3, 5);
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

            int outside = f.Index(8, 5);
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
            int cell = f.Index(3, 5);
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

            int cell = f.Index(3, 5);
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
                for (int x = 6; x < W; x++)
                {
                    int i = f.Index(x, z);
                    Assert.AreEqual(PresenceField.NeverVerified, f.LastVerified(i),
                        $"走査外セル ({x},{z}) が検証された");
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

        /// <summary>
        /// **高さは軸ではなく属性である。** 同じ床セルの上で高さだけ変えても
        /// セルは変わらず、属性だけが変わる（座っている・倒れているを将来ここで表す）。
        /// </summary>
        [Test]
        public void HeightIsAnAttributeOfTheCellNotAnAxis()
        {
            var f = Room();

            f.Ingest(Tracked(1, 0.3f, 1.60f, 0.3f));
            int standing = f.OccupiedIndex;
            Assert.GreaterOrEqual(standing, 0, "足元のセルが立っていない");
            Assert.AreEqual(1.60f, f.HeightAt(standing), 1e-4f, "立位の高さが属性に入っていない");

            f.Ingest(Tracked(2, 0.3f, 0.85f, 0.3f));
            Assert.AreEqual(standing, f.OccupiedIndex,
                "高さを変えたらセルが変わった。高さが軸になっている");
            Assert.AreEqual(0.85f, f.HeightAt(standing), 1e-4f, "座位の高さに更新されていない");

            // 一度も居ていないセルの高さは NaN（0 ではない）
            int never = f.Index(4, 4);
            Assert.IsTrue(float.IsNaN(f.HeightAt(never)),
                "居たことのないセルの高さが 0 になっている。欠測と 0 を混ぜている");
        }

        /// <summary>床の高さを引いた値が属性になること（机の上と床では基準が違う）。</summary>
        [Test]
        public void TheHeightAttributeIsMeasuredFromThatCellsFloor()
        {
            var scanned = new bool[W * D];
            var floorY = new float[W * D];
            for (int i = 0; i < scanned.Length; i++) scanned[i] = true;
            floorY[Index2(2, 2)] = 0.70f;      // 机の上（セル 2,2 だけ床が高い）
            var f = new PresenceField(W, D, Cell, 0f, 0f, scanned, floorY);

            // セル (1,1) は床（floorY=0）
            f.Ingest(Tracked(1, 0.3f, 1.60f, 0.3f));
            Assert.AreEqual(Index2(1, 1), f.OccupiedIndex, "テストのセル計算が合っていない");
            Assert.AreEqual(1.60f, f.HeightAt(f.OccupiedIndex), 1e-4f);

            // セル (2,2) は机の上（floorY=0.70）。**同じ頭の高さでも属性は変わる**
            f.Ingest(Tracked(2, 0.6f, 1.60f, 0.6f));
            Assert.AreEqual(Index2(2, 2), f.OccupiedIndex, "テストのセル計算が合っていない");
            Assert.AreEqual(1.60f - 0.70f, f.HeightAt(f.OccupiedIndex), 1e-4f,
                "床の高さを引いていない");
        }

        static int Index2(int x, int z) => z * W + x;

        // ================= 走査済みの定義 =================

        /// <summary>
        /// **部屋の中央が走査済みであること。**
        ///
        /// 「走査外の最終検証ティックが更新されない」判定と**対**になる。
        /// 片方だけだと、**全域が走査外でも通ってしまう**（空の検証と同型）。
        ///
        /// 以前の定義「メッシュから 6 セル以内」では、部屋の中央が
        /// 表面から離れるため走査外になり、実機で足元の印が出なかった（2026-08-19）。
        /// </summary>
        [Test]
        public void TheMiddleOfTheRoomIsScanned()
        {
            // 12x6x12 の格子に、x,z ∈ [2,9] の床だけを置く（部屋の内側）
            var g = new FlowGrid(12, 6, 12, 0.08f, 0f, 0f, 0f);
            for (int z = 2; z <= 9; z++)
                for (int x = 2; x <= 9; x++)
                    g.SetSolid(x, 0, z, true);

            int n = FloorMask.Fold(g, out var scanned, out var floorY);

            Assert.AreEqual(8 * 8, n, "走査済みの床セル数が床の広さと違う");

            // **中央**（部屋のどの面からも離れている）
            Assert.IsTrue(scanned[5 * 12 + 5], "部屋の中央が走査外になっている");
            Assert.IsTrue(scanned[6 * 12 + 6], "部屋の中央が走査外になっている");
            Assert.AreEqual(0.08f, floorY[5 * 12 + 5], 1e-6f, "床の高さが固体セルの上面でない");

            // 床の外（隣室・扉の向こうに相当）は走査外
            Assert.IsFalse(scanned[0], "床の無い列が走査済みになっている");
            Assert.IsFalse(scanned[11 * 12 + 11], "床の無い列が走査済みになっている");
        }

        /// <summary>
        /// **縁を封じた格子を渡すと全列が走査済みになる。** これを踏むと
        /// カバレッジが実質存在しなくなるので、踏んだことが分かる形で残す。
        /// </summary>
        [Test]
        public void SealedBordersWouldMakeEverythingScanned()
        {
            var g = new FlowGrid(12, 6, 12, 0.08f, 0f, 0f, 0f);
            for (int z = 2; z <= 9; z++)
                for (int x = 2; x <= 9; x++)
                    g.SetSolid(x, 0, z, true);
            FlowBoundaryBaker.SealBorders(g);          // 流れ場の格子と同じ状態

            int n = FloorMask.Fold(g, out var scanned, out _);

            Assert.AreEqual(scanned.Length, n,
                "縁を封じても全列が走査済みにならなかった。前提の理解が違う");
            Assert.IsTrue(scanned[0],
                "縁の封じが床として拾われていない。WatchField はメッシュだけを焼くこと");
        }

        // ================= 床の境界ポリゴン =================

        /// <summary>
        /// **走査済み = 床の境界ポリゴンの内側。**
        ///
        /// 「部屋の内側」を近似で作ろうとして2回外している
        /// （1回目は狭すぎて中央が走査外、2回目は広すぎて壁の外まで含んだ）。
        /// 判定は3つ揃って初めて意味を持つ:
        /// **中央が走査済み** / **壁の外側が走査外** / 走査外は検証されない。
        /// 片方だけだと、全域が走査済みでも全域が走査外でも通ってしまう。
        /// </summary>
        [Test]
        public void OnlyTheInsideOfTheFloorPolygonIsScanned()
        {
            // 2.0 x 2.0 m の正方形の部屋（部屋座標 0.5〜2.5）
            var poly = new[] { 0.5f, 0.5f, 2.5f, 0.5f, 2.5f, 2.5f, 0.5f, 2.5f };
            const int W2 = 24, D2 = 24;
            const float C2 = 0.125f;      // 24 * 0.125 = 3.0 m 四方の格子

            int n = PolygonMask.Build(poly, W2, D2, C2, 0f, 0f, -0.8f,
                out var scanned, out var floorY);

            // 面積が合うこと（2.0 x 2.0 = 4.0 m²、セル 0.015625 m²）
            Assert.AreEqual(4.0f, n * C2 * C2, 0.05f,
                $"走査済みの面積が {n * C2 * C2:F2} m²。ポリゴンは 4.00 m²");

            // **中央は走査済み**
            int mid = (12) * W2 + 12;     // (1.5625, 1.5625) m
            Assert.IsTrue(scanned[mid], "部屋の中央が走査外になっている");
            Assert.AreEqual(-0.8f, floorY[mid], 1e-6f, "床の高さが入っていない");

            // **壁の外側は走査外**（ここが2回目の失敗で抜けていた）
            Assert.IsFalse(scanned[2 * W2 + 2], "壁の外側（手前）が走査済みになっている");
            Assert.IsFalse(scanned[22 * W2 + 22], "壁の外側（奥）が走査済みになっている");
            Assert.IsFalse(scanned[12 * W2 + 22], "壁の外側（横）が走査済みになっている");
        }

        /// <summary>L 字の部屋でも凹んだ側が走査外になること（偶奇規則の確認）。</summary>
        [Test]
        public void AnLShapedRoomExcludesTheNotch()
        {
            // L 字: (0,0)-(2,0)-(2,1)-(1,1)-(1,2)-(0,2)
            var poly = new[] { 0f, 0f, 2f, 0f, 2f, 1f, 1f, 1f, 1f, 2f, 0f, 2f };

            Assert.IsTrue(PolygonMask.Contains(poly, 0.5f, 0.5f), "L 字の内側が外と判定された");
            Assert.IsTrue(PolygonMask.Contains(poly, 1.5f, 0.5f), "L 字の内側が外と判定された");
            Assert.IsTrue(PolygonMask.Contains(poly, 0.5f, 1.5f), "L 字の内側が外と判定された");

            // 切り欠き（右上）は外側
            Assert.IsFalse(PolygonMask.Contains(poly, 1.5f, 1.5f),
                "L 字の切り欠きが内側と判定された");
            Assert.IsFalse(PolygonMask.Contains(poly, 2.5f, 0.5f), "ポリゴンの外が内側と判定された");
        }

        /// <summary>ポリゴンが無ければ走査済みは 0（**近似で埋めない**）。</summary>
        [Test]
        public void WithoutAPolygonNothingIsScanned()
        {
            int n = PolygonMask.Build(null, 10, 10, 0.1f, 0f, 0f, 0f, out var scanned, out _);
            Assert.AreEqual(0, n, "ポリゴンが無いのに走査済みセルがある");
            foreach (bool b in scanned) Assert.IsFalse(b);
        }

        // ================= 格子の固定 =================

        /// <summary>
        /// **同じ GUID で 2回初期化して、格子の寸法と原点が一致すること。**
        ///
        /// 部屋の焼き込みを毎回やり直すと**同じ部屋でも起動ごとに格子が変わる**
        /// （実測 34×43 → 34×42）。場はセルに溜まるので、
        /// **格子が変わった瞬間に場の対応が崩れる。**
        /// </summary>
        [Test]
        public void TheGridIsStableAcrossRestartsForTheSameAnchor()
        {
            string dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "blockfield_grid_" + System.Guid.NewGuid());
            const string guid = "eb09cbb8-969d-1c50-0c9a-9ebb44b8c1e6";
            try
            {
                // 1回目: 保存値が無いので新規作成して保存
                Assert.IsFalse(RoomGridStore.TryLoad(dir, guid, out _),
                    "まだ保存していないのに読めた");
                var first = new RoomGridSpec(-1.36f, -1.72f, 34, 43, 0.08f);
                RoomGridStore.Save(dir, guid, first);

                // 2回目: 焼き込みが 34x42 を返しても、保存値を使う
                Assert.IsTrue(RoomGridStore.TryLoad(dir, guid, out var second),
                    "保存した格子を読めない");
                Assert.AreEqual(first.Width, second.Width, "幅が変わった");
                Assert.AreEqual(first.Depth, second.Depth, "奥行きが変わった");
                Assert.AreEqual(first.OriginX, second.OriginX, 1e-4f, "原点 X が変わった");
                Assert.AreEqual(first.OriginZ, second.OriginZ, 1e-4f, "原点 Z が変わった");
                Assert.AreEqual(first.CellSize, second.CellSize, 1e-6f, "セルサイズが変わった");

                // **別の GUID は別の格子。** 黙って引き継がない
                Assert.IsFalse(RoomGridStore.TryLoad(dir, "00000000-0000-0000-0000-000000000000", out _),
                    "別のアンカーの格子を読んでしまった");
            }
            finally
            {
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
        }

        // ================= L0a / L0b / L0c =================

        /// <summary>
        /// **L0b の確からしさが閾値を割ったら、L0c はカバレッジを空集合にする。**
        /// 古い変換に静かに落とさない。機体ではこれが「止まる」という挙動になる。
        /// </summary>
        [Test]
        public void LowConfidenceEmptiesTheCoverage()
        {
            var trusted = L0Localization.Identity(1, 1f);
            var lost = L0Localization.Identity(1, 0f);

            Assert.IsTrue(trusted.IsTrustworthy, "確からしさ 1 が信用されていない");
            Assert.IsFalse(lost.IsTrustworthy, "確からしさ 0 が信用されている");

            var f = Room();
            // 信用できるとき: 領域がカバレッジになる
            f.Ingest(new L0Sample(1, 1, 0.3f, 1.6f, 0.3f, 1f,
                L0Coverage.ScannedRoom, L0Label.Measured, trusted.Confidence));
            Assert.Greater(f.CoveredCells, 0);

            // 信用できないとき: 空集合（**位置は残っていても使わない**）
            f.Ingest(new L0Sample(1, 2, 0f, 0f, 0f, 0f,
                L0Coverage.None, L0Label.TrackingLost, lost.Confidence));
            Assert.AreEqual(0, f.CoveredCells, "確からしさが割れてもカバレッジが残っている");
        }

        /// <summary>恒等変換は生値をそのまま返す（段1 の頭位置プロデューサ）。</summary>
        [Test]
        public void TheIdentityTransformPassesThroughAndHasAStableHash()
        {
            var t = L0Transform.Identity;
            t.Apply(1.5f, -0.25f, 3f, out float x, out float y, out float z);
            Assert.AreEqual(1.5f, x, 1e-6f);
            Assert.AreEqual(-0.25f, y, 1e-6f);
            Assert.AreEqual(3f, z, 1e-6f);

            // **どの校正を使ったかをログに残す**ための識別子
            Assert.AreEqual(t.Hash(), L0Transform.Identity.Hash(), "同じ変換のハッシュが違う");
            var moved = new L0Transform(1f, 0f, 0f, 0.5f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f);
            Assert.AreNotEqual(t.Hash(), moved.Hash(), "違う変換のハッシュが同じ");
        }

        /// <summary>**確からしさがログを往復すること**（当時の解釈をやり直すため）。</summary>
        [Test]
        public void TheConfidenceSurvivesTheLogRoundTrip()
        {
            var s = new L0Sample(1, 42, 1f, 2f, 3f, 1f,
                L0Coverage.ScannedRoom, L0Label.Measured, 0.75f);
            Assert.IsTrue(L0LogFormat.TryParse(L0LogFormat.Format(s), out var back));
            Assert.AreEqual(0.75f, back.Confidence, 1e-3f, "確からしさが失われている");

            // 旧形式（conf 無し）も読める。確からしさ 1 とみなす
            Assert.IsTrue(L0LogFormat.TryParse(
                "[L0] t=1 p=1 pos=0.0,0.0,0.0 v=1.0 cov=1 label=0", out var old));
            Assert.AreEqual(1f, old.Confidence, 1e-6f, "旧形式の既定値が 1 でない");
        }

        /// <summary>**L1 が領域からラスタライズする**（L0 は領域で出す）。</summary>
        [Test]
        public void TheFieldRasterizesTheRegionItself()
        {
            var region = new L0Region(
                new[] { 0.5f, 0.5f, 2.5f, 0.5f, 2.5f, 2.5f, 0.5f, 2.5f }, -0.8f);
            var grid = new RoomGridSpec(0f, 0f, 24, 24, 0.125f);

            var f = new PresenceField(grid, region);

            Assert.AreEqual(24 * 24, f.CellCount);
            Assert.AreEqual(4.0f, f.ScannedCells * 0.125f * 0.125f, 0.05f,
                "ラスタライズした面積がポリゴンと合わない");
            Assert.IsTrue(f.IsScanned(f.Index(12, 12)), "部屋の中央が走査外");
            Assert.IsFalse(f.IsScanned(f.Index(2, 2)), "壁の外側が走査済み");
            Assert.AreEqual(-0.8f, f.FloorY(f.Index(12, 12)), 1e-6f, "床の高さが入っていない");
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
