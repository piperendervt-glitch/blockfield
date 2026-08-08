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
