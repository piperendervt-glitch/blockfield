using System.Collections.Generic;
using System.Text;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 個体数時系列ログ (Demo 3 E5)。毎ティックの個体数を蓄積し CSV 文字列を出力する。
    /// ファイルIOは Runtime/Editor 側の責務（SimCore は文字列まで）。
    /// </summary>
    public sealed class PopulationLog
    {
        readonly struct Row
        {
            public readonly long tick;
            public readonly int plants;
            public readonly int sheep;
            public readonly int pigs;
            public readonly int wolves;

            public Row(long tick, int plants, int sheep, int pigs, int wolves)
            {
                this.tick = tick;
                this.plants = plants;
                this.sheep = sheep;
                this.pigs = pigs;
                this.wolves = wolves;
            }
        }

        readonly List<Row> m_Rows = new List<Row>();

        public int Count => m_Rows.Count;

        /// <summary>読み出し用の1点（表示・集計用。ログ自体は書き換えない）。</summary>
        public readonly struct Sample
        {
            public readonly long tick;
            public readonly int plants;
            public readonly int herbivores;
            public readonly int wolves;

            public Sample(long tick, int plants, int herbivores, int wolves)
            {
                this.tick = tick;
                this.plants = plants;
                this.herbivores = herbivores;
                this.wolves = wolves;
            }

            /// <summary>草食獣＋狼。</summary>
            public int Animals => herbivores + wolves;
        }

        /// <summary>
        /// i 番目の記録を読み出す (Demo 5a の診断表示)。
        /// Sheep と Pig は表示上まとめて「草食獣」にする。
        /// </summary>
        public Sample GetSample(int index)
        {
            var r = m_Rows[index];
            return new Sample(r.tick, r.plants, r.sheep + r.pigs, r.wolves);
        }

        public void Record(World world)
        {
            m_Rows.Add(new Row(world.TickCount, world.PlantCount, world.SheepCount, world.PigCount, world.WolfCount));
        }

        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("tick,plants,sheep,pigs,wolves");
            foreach (var row in m_Rows)
            {
                sb.Append(row.tick).Append(',')
                  .Append(row.plants).Append(',')
                  .Append(row.sheep).Append(',')
                  .Append(row.pigs).Append(',')
                  .Append(row.wolves).AppendLine();
            }
            return sb.ToString();
        }
    }
}
