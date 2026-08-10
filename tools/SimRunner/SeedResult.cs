using System.Collections.Generic;
using BlockField.SimCore.Ecology;

namespace SimRunner
{
    /// <summary>1シード1条件を走らせた結果。集計と report.html の材料になる。</summary>
    public sealed class SeedResult
    {
        public string Condition = "";
        public uint Seed;
        public int Ticks;
        public int SuitableCells;

        // 最終状態
        public int Plants, Sheep, Pigs, Wolves;

        // 時間を通した最小値（warmup 以降）。M5 の判定はこれで行う
        public int MinPlants, MinHerbivores, MinWolves;

        // 累計
        public int Starvation, Predation, Births, TrampleCrush;

        // 場の平均・最大（名前昇順）
        public Dictionary<string, double> FieldMean = new();
        public Dictionary<string, double> FieldMax = new();

        // Demo 8 第2段 M2: 墓場セルとそれ以外の「植物本数 / セル数」。
        // 比は合算してから取るので、割った値ではなく素の数を持つ
        public int GraveCells, GravePlants, NonGraveCells, NonGravePlants;

        // Demo 8 第2段 M3: 迂回行動
        public int MovesAwayFromFear, MovesTowardFear;

        // Demo 8 第3段 M2: 踏み荒らし場の上位25%/下位25%
        public int HighTrampleCells, HighTramplePlants, LowTrampleCells, LowTramplePlants;
        public double TrampleQuartileHigh, TrampleQuartileLow;

        // M4: 決定論の確認用
        public ulong ContentHash;

        /// <summary>個体数の時系列（CSV 用。間引いて持つ）。</summary>
        public List<(long tick, int plants, int herbivores, int wolves)> Series = new();

        public bool GuildExtinct => MinHerbivores == 0;
        public bool WolvesExtinct => MinWolves == 0;
        public bool PlantsExtinct => MinPlants == 0;
    }
}
