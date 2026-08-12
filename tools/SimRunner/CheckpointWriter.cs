using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BlockField.SimCore.Ecology;

namespace SimRunner
{
    /// <summary>
    /// 長時間実験の途中経過を追記していく (Demo 6 の前哨実験用)。
    ///
    /// 【なぜ途中で書くか】10万ティックのような長い実験では、終わるまで
    /// 何も見えないと「まだ動いているのか、固まっているのか」も分からない。
    /// 場の飽和（死の場は τ≈333 なので数万ティックで頭打ちになるはず）が
    /// いつ起きたかも、終端の1点では読めない。
    ///
    /// 【並列との両立】シードごとに別スレッドから呼ばれるので、
    /// 書き込みは lock で直列化する。チェックポイントは数千ティックに1回しか
    /// 起きないため、この lock がシミュレーション速度に影響することはない。
    /// AutoFlush を有効にして、実行中に別プロセスから覗けるようにしてある。
    /// </summary>
    public sealed class CheckpointWriter : IDisposable
    {
        readonly object m_Gate = new object();
        readonly StreamWriter m_Writer;
        readonly List<string> m_FieldNames;

        public CheckpointWriter(string path, IEnumerable<string> fieldNames)
        {
            m_FieldNames = fieldNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
            m_Writer = new StreamWriter(path, append: false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            var header = new StringBuilder(
                "condition,seed,tick,plants,herbivores,wolves,starvation,predation,births,trampleCrush");
            foreach (string n in m_FieldNames)
            {
                header.Append(",").Append(n).Append("_mean,").Append(n).Append("_max");
            }
            // 進化を入れたあとに「集団の重みが動いたか」を追えるようにしておく。
            // 今は全個体が同じ初期値なので分散は0のはず
            foreach (string n in EntityWeights.FieldNames)
            {
                header.Append(",w_").Append(n).Append("_mean,w_").Append(n).Append("_sd");
            }

            // 群れの長期動態 (Demo 8 第4段 K4 以降)。**その時点の瞬間値**である
            // （summary.json の群れ指標は時間平均なので別物）。
            // 拠点が永続するのか、移動・分裂・消滅するのかは時系列でしか見えないので、
            // ここに時刻つきで残す。個体が居ない種は空欄にする
            // （0 を書くと「居なかった」と「群れていなかった」が混ざる）
            foreach (var (_, species) in Runner.FlockSpecies)
            {
                header.Append(",flock_").Append(species).Append("_count")
                      .Append(",flock_").Append(species).Append("_neighbor")
                      .Append(",flock_").Append(species).Append("_pairdist")
                      .Append(",flock_").Append(species).Append("_concentration");
            }

            // 認知範囲 (Demo 8 第4.5段 K2)。進化が「何を覚える系」を選ぶかの見出し指標。
            // 重み成分ごとの平均・分散は列が多すぎるので weights.csv（縦持ち）に分けた
            foreach (var (_, species) in Runner.FlockSpecies)
            {
                header.Append(",cog_").Append(species).Append("_time")
                      .Append(",cog_").Append(species).Append("_space");
            }
            m_Writer.WriteLine(header.ToString());
        }

        public void Write(string condition, World world, SimParams p)
        {
            var sb = new StringBuilder();
            sb.Append(condition).Append(',').Append(world.Params.seed).Append(',').Append(world.TickCount)
              .Append(',').Append(world.VegetationTotal.ToString("0.##", CultureInfo.InvariantCulture))
              .Append(',').Append(world.SheepCount + world.PigCount)
              .Append(',').Append(world.WolfCount)
              .Append(',').Append(world.StarvationCount)
              .Append(',').Append(world.PredationCount)
              .Append(',').Append(world.BirthCount)
              .Append(',').Append(world.TrampleCrushCount);

            foreach (string n in m_FieldNames)
            {
                var (mean, max) = EcologyStats.FieldStats((ScalarField)world.Fields[n]);
                sb.Append(',').Append(F(mean)).Append(',').Append(F(max));
            }

            var (wMean, wVar, _) = EcologyStats.AnimalForageWeightStats(world);
            for (int i = 0; i < EntityWeights.FieldCount; i++)
            {
                sb.Append(',').Append(F(wMean[i])).Append(',').Append(F(Math.Sqrt(wVar[i])));
            }

            foreach (var (kind, _) in Runner.FlockSpecies)
            {
                int count = 0;
                foreach (var e in world.Entities)
                {
                    if (e.kind == kind) count++;
                }
                sb.Append(',').Append(count);

                sb.Append(',');
                if (EcologyStats.TrySameSpeciesNeighborMean(world, kind, out float nm))
                {
                    sb.Append(F(nm));
                }
                sb.Append(',');
                if (EcologyStats.TrySameSpeciesPairDistanceMedian(world, kind, out float pd))
                {
                    sb.Append(F(pd));
                }
                sb.Append(',').Append(F(EcologyStats.FieldTop10Concentration(world.Colony(kind))));
            }

            foreach (var (kind, _) in Runner.FlockSpecies)
            {
                var (t, s, n) = EcologyStats.SpeciesCognitiveRange(world, kind, p);
                sb.Append(',');
                if (n > 0) sb.Append(F(t));
                sb.Append(',');
                if (n > 0) sb.Append(F(s));
            }

            lock (m_Gate)
            {
                m_Writer.WriteLine(sb.ToString());
            }

            m_Weights?.Write(condition, world);
        }

        /// <summary>
        /// 重み成分ごとの統計を出す先 (Demo 8 第4.5段 K2)。
        /// 種 × モード（採餌/徘徊）× 9成分 = 54系列あるので、
        /// checkpoints.csv に横持ちすると列が100を超えて読めなくなる。
        /// **縦持ちの別ファイル**にしてある。
        /// </summary>
        public WeightLog? m_Weights;

        static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        public void Dispose() => m_Writer.Dispose();
    }
}
