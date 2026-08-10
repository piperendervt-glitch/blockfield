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
        public int MinHerbivores, MinWolves;

        /// <summary>
        /// 草の総量の最小値 (Demo 8.5)。植物が Entity でなくなり「最小本数」が
        /// 存在しないため、安定条件の「植物≧1」はこれに読み替わる。
        /// </summary>
        public float MinVegetation;

        // ---- 時間平均（warmup 以降の毎ティック平均）----
        // 最終時点の1点は揺れが大きく、実装の前後を比べる基準値にならない。
        // Demo 8.5（植物の場化）の M1/M2 はこちらを使う
        public double MeanPlantsPerTick, MeanHerbivoresPerTick, MeanWolvesPerTick;
        public double MeanEntitiesPerTick;

        /// <summary>
        /// 個体あたりの餓死・捕食（1個体1000ティックあたり）。
        /// 絶対数はスケールと個体数に比例して増えるため、実装の前後を
        /// 比べるには個体あたりに正規化する必要がある。
        /// </summary>
        public double StarvationPerAnimalPerKiloTick, PredationPerAnimalPerKiloTick;

        /// <summary>
        /// 適性セルあたりの植生場の平均（最終時点）。
        /// 植物を場にしたあとは「本数」が存在しなくなるため、
        /// 移行の前後で比較できる指標はこれになる。
        /// </summary>
        public double VegetationPerSuitableCell;

        /// <summary>
        /// ティックループだけの所要時間（ミリ秒）。ワールド生成・集計・
        /// 画像生成・ファイル出力は含まない。M1 の基準値。
        /// </summary>
        public double SimMilliseconds;

        // 累計
        public int Starvation, Predation, Births, TrampleCrush;

        // 場の平均・最大（名前昇順）
        public Dictionary<string, double> FieldMean = new();
        public Dictionary<string, double> FieldMax = new();

        // Demo 8 第2段 M2: 墓場セルとそれ以外の「植物本数 / セル数」。
        // 比は合算してから取るので、割った値ではなく素の数を持つ
        // Demo 8.5 で「本数」から「草の量（植生場の合計）」に変わった
        public int GraveCells, NonGraveCells;
        public double GraveGrass, NonGraveGrass;

        // Demo 8 第2段 M3: 迂回行動
        public int MovesAwayFromFear, MovesTowardFear;

        // Demo 8 第3段 M2: 踏み荒らし場の上位25%/下位25%
        public int HighTrampleCells, LowTrampleCells;
        public double HighTrampleGrass, LowTrampleGrass;
        public double TrampleQuartileHigh, TrampleQuartileLow;

        // M4: 決定論の確認用
        public ulong ContentHash;

        /// <summary>個体数の時系列（CSV 用。間引いて持つ）。</summary>
        public List<(long tick, int plants, int herbivores, int wolves)> Series = new();

        public bool GuildExtinct => MinHerbivores == 0;
        public bool WolvesExtinct => MinWolves == 0;
        /// <summary>草が消え去ったか。Demo 8.5 で「植物が0本」から「草の総量が0」に変わった。</summary>
        public bool PlantsExtinct => MinVegetation <= 0f;

        /// <summary>安定条件（ギルド・狼・植物のいずれか）に違反したシードか。</summary>
        public bool StabilityViolated => GuildExtinct || WolvesExtinct || PlantsExtinct;
    }
}
