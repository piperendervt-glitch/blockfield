using BlockField.SimCore.Rng;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    public class Mulberry32Tests
    {
        [Test]
        public void SameSeed_ProducesIdenticalSequence()
        {
            var a = new Mulberry32(12345u);
            var b = new Mulberry32(12345u);

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(a.NextUInt(), b.NextUInt(), $"index {i} で系列が一致しない");
            }

            // 既知値照合（seed=1、BigInteger による正確なリファレンス計算で算出した値）
            var known = new Mulberry32(1u);
            Assert.AreEqual(2693262067u, known.NextUInt());
            Assert.AreEqual(11749833u, known.NextUInt());
            Assert.AreEqual(2265367787u, known.NextUInt());
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentSequences()
        {
            var a = new Mulberry32(1u);
            var b = new Mulberry32(2u);

            bool anyDifferent = false;
            for (int i = 0; i < 100; i++)
            {
                if (a.NextUInt() != b.NextUInt())
                {
                    anyDifferent = true;
                    break;
                }
            }

            Assert.IsTrue(anyDifferent, "異なるシードで100個すべて一致するのは異常");
        }

        [Test]
        public void Range_IncludesMin_ExcludesMax()
        {
            var rng = new Mulberry32(999u);

            bool sawMin = false;
            for (int i = 0; i < 1000; i++)
            {
                int v = rng.Range(0, 3);
                Assert.GreaterOrEqual(v, 0, "min を下回った");
                Assert.Less(v, 3, "max (含まない) に到達した");
                if (v == 0) sawMin = true;
            }
            Assert.IsTrue(sawMin, "1000回で min (0) が一度も出ないのは異常");

            // 幅1なら常に min
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(5, rng.Range(5, 6));
            }

            // 負の範囲もmin含む/max含まない
            for (int i = 0; i < 100; i++)
            {
                int v = rng.Range(-3, -1);
                Assert.GreaterOrEqual(v, -3);
                Assert.Less(v, -1);
            }
        }
    }
}
