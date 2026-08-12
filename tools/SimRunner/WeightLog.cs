using System;
using System.Globalization;
using System.IO;
using System.Text;
using BlockField.SimCore.Ecology;

namespace SimRunner
{
    /// <summary>
    /// 行動重みの時系列 (Demo 8 第4.5段 K2)。進化が重みを動かしたかを追う。
    ///
    /// 【なぜ縦持ちか】観察したい系列は
    /// 種(3) × モード(採餌/徘徊) × 場(9) × 統計(平均/標準偏差) = 108 ある。
    /// checkpoints.csv に横へ並べると列が100を超えて人間には読めなくなり、
    /// 場が増えるたびに列の並びも変わる。1行1系列の縦持ちにすれば
    /// 列は固定で、集計側（pandas / Import-Csv）でも扱いやすい。
    ///
    /// 【なぜ徘徊側も出すか】群れ重み (colonySelfWeight) は徘徊時の重みにしか
    /// 入らない（第4段 K4）。E1 が動かすのはまさにその重みなので、
    /// 採餌側だけ記録していると中心の実験が観察できない。
    /// </summary>
    public sealed class WeightLog : IDisposable
    {
        readonly object m_Gate = new object();
        readonly StreamWriter m_Writer;

        public WeightLog(string path)
        {
            m_Writer = new StreamWriter(path, append: false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            m_Writer.WriteLine("condition,seed,tick,species,mode,field,count,mean,sd");
        }

        public void Write(string condition, World world)
        {
            var sb = new StringBuilder();

            foreach (var (kind, species) in Runner.FlockSpecies)
            {
                foreach (bool wandering in new[] { false, true })
                {
                    var (mean, variance, count) = EcologyStats.SpeciesWeightStats(world, kind, wandering);
                    if (count == 0)
                    {
                        continue;   // その種が居ないティックは行を作らない
                    }
                    string mode = wandering ? "wander" : "forage";
                    for (int i = 0; i < EntityWeights.FieldCount; i++)
                    {
                        sb.Append(condition).Append(',')
                          .Append(world.Params.seed).Append(',')
                          .Append(world.TickCount).Append(',')
                          .Append(species).Append(',')
                          .Append(mode).Append(',')
                          .Append(EntityWeights.FieldNames[i]).Append(',')
                          .Append(count).Append(',')
                          .Append(F(mean[i])).Append(',')
                          .Append(F(Math.Sqrt(variance[i])))
                          .Append('\n');
                    }
                }
            }

            lock (m_Gate)
            {
                m_Writer.Write(sb.ToString());
            }
        }

        static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        public void Dispose() => m_Writer.Dispose();
    }
}
