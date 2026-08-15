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
        /// 草食獣を種別に分けた時間平均。羊と豚は行動パラメータが同一なので
        /// 期待値は等しく、差が出るなら対称性が破れている（中立ドリフトの検定用）。
        /// MeanHerbivoresPerTick = MeanSheepPerTick + MeanPigPerTick。
        /// </summary>
        public double MeanSheepPerTick, MeanPigPerTick;

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

        /// <summary>
        /// コロニー場（3枚）と、その重みを除いた ContentHash (Demo 8 第4段 K1)。
        ///
        /// 場を1枚足せば全体のハッシュは必ず変わるので、それだけでは
        /// 「足した分だけ変わったのか、既存の状態まで変わってしまったのか」を
        /// 区別できない。除いた部分が**追加前のハッシュと完全一致**すれば、
        /// 出生時の書き込みが RNG を消費しておらず、既存の場にも個体にも
        /// 手が入っていないことを一度に言える（判定 M0b）。
        /// </summary>
        public ulong ContentHashExcludingColony;

        // ---- 群れ指標 (Demo 8 第4段 K5)。種名（sheep/pig/wolf）で引く ----
        //
        // 4c の M4「群れの創発」の主指標。近傍数とペア距離は warmup 以降の**時間平均**で、
        // その種の個体が居ないティックは標本から外してある
        // （0 を入れると個体数の指標が群れ指標に混ざる）。
        // 集中度は場の形の記録項目なので最終時点の1点。

        /// <summary>半径3セル内の同種個体数の平均（時間平均）。群れていれば大きい。</summary>
        public Dictionary<string, double> NeighborMean = new();

        /// <summary>同種ペア距離の中央値（時間平均）。群れていれば小さい。</summary>
        public Dictionary<string, double> PairDistanceMedian = new();

        /// <summary>コロニー場の上位10%セルが総量に占める割合（最終時点、記録項目）。</summary>
        public Dictionary<string, double> ColonyConcentration = new();

        /// <summary>群れ指標の標本になったティック数（種別）。0 なら指標は未定義。</summary>
        public Dictionary<string, long> FlockSamples = new();

        /// <summary>
        /// 同一シードで並走させた対照の結果 (Demo 8 第4段 K5、--control のときだけ)。
        ///
        /// 別の <see cref="BlockField.SimCore.Ecology.World"/>・別の乱数で走るので、
        /// 本条件の進行には一切影響しない。対にして持つのは、
        /// **シードごとの差**を取れるようにするため（地形と初期配置が相殺される）。
        /// 対照の対照は無いので、この中の Control は常に null。
        /// </summary>
        public SeedResult? Control;

        /// <summary>個体数の時系列（CSV 用。間引いて持つ）。</summary>
        public List<(long tick, int plants, int herbivores, int wolves)> Series = new();

        /// <summary>
        /// 条件が狼を**意図的に**排除しているか（<c>wolfCap = 0</c>）。
        ///
        /// E1（第4.5段）は狼を初期条件から外して走る。そのとき狼は必ず
        /// 「全滅」と記録されるが、それは設計どおりであって退行ではない。
        /// これを区別しないと M5 が常に不合格になり、**終了コード1が
        /// 意味を持たなくなる**（本物の退行＝ギルドや植物の全滅を隠す）。
        /// </summary>
        public bool WolvesExcluded;

        public bool GuildExtinct => MinHerbivores == 0;
        public bool WolvesExtinct => MinWolves == 0;
        /// <summary>草が消え去ったか。Demo 8.5 で「植物が0本」から「草の総量が0」に変わった。</summary>
        public bool PlantsExtinct => MinVegetation <= 0f;

        /// <summary>安定条件（ギルド・狼・植物のいずれか）に違反したシードか。</summary>
        public bool StabilityViolated =>
            GuildExtinct || (WolvesExtinct && !WolvesExcluded) || PlantsExtinct;
    }
}
