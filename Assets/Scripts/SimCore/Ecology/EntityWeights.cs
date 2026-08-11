using System;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 個体が持つ場ごとの重み (Demo 8 第3段 J2 — 進化の基盤)。
    ///
    /// 【なぜ個体に持たせるか】これまで行動の重みは <see cref="SimParams"/> にあり、
    /// 全個体が同じ値を使っていた。将来の進化的アルゴリズムでは
    /// 「どの場をどれだけ重視するか」が個体ごとに違い、繁殖で継承され変異する。
    /// 本段では**構造だけを移す**。全個体が同じ初期値を使い、
    /// 繁殖時は親の重みをそのままコピーする（変異なし）。
    ///
    /// 【なぜ配列でなく構造体か】<see cref="Entity"/> は構造体でリストに値で入る。
    /// 重みを float[] にすると、構造体をコピーしても配列は共有され、
    /// 繁殖で「親の重みをコピー」したつもりが**同じ配列を指す**ことになる。
    /// 変異を入れた瞬間に親子で値が連動する。構造体にすれば値でコピーされる。
    ///
    /// 【メンバの順序】場の名前**昇順**で並べる。<see cref="World.Fields"/> の
    /// ハッシュ畳み込み順と一致させ、場が増えたときに重みの並びが変わって
    /// 決定論が壊れることを防ぐ。順序の一致は EditMode テストで検証する。
    ///
    /// 【なぜ辞書を引かないか】<see cref="World.Fields"/> を毎回引く形にすると、
    /// 1個体1ティックあたり4方向 × 場の数だけ辞書アクセスが発生する。
    /// Quest で72FPSを保つ必要があるため、ここは固定メンバで展開する。
    /// 場を追加するときはこの構造体にメンバを1つ足すだけで、
    /// 行動コード側（<see cref="Simulation"/>）は触らずに済む。
    /// </summary>
    public struct EntityWeights : IEquatable<EntityWeights>
    {
        /// <summary>場の数。<see cref="World.Fields"/> の数と一致すること（テストで検証）。</summary>
        public const int FieldCount = 9;

        // --- 場の名前昇順 ---

        // コロニー場の3枚 (Demo 8 第4段 K1)。**器だけ作って初期値0で寝かせている。**
        // 自種への重みは 4c (K4) で群れ行動として配線し、他種への重み（盗聴）は
        // 第4.5段の進化が発見するかという問いとして温存する（prereg 判断2）
        public float colonyPig;
        public float colonySheep;
        public float colonyWolf;

        public float death;
        public float fear;
        public float prey;
        public float suitability;
        public float trample;
        public float vegetation;

        /// <summary>
        /// 4近傍の1セルを評価する。「各場について 重み × 場の値 を合計」という
        /// 一般化された形。場が増えてもここにメンバを足すだけで、
        /// 呼び出し側の行動コードは変わらない。
        ///
        /// 【加算順序と決定論】名前昇順に固定する。重み0の項は 0×値 = 0 で、
        /// 0 の加算は IEEE754 で厳密なので、重みを設定していない場が
        /// 結果を変えることはない。
        /// </summary>
        public float Score(World world, int x, int z)
        {
            return colonyPig * world.ColonyPig.GetAtColumn(x, z)
                + colonySheep * world.ColonySheep.GetAtColumn(x, z)
                + colonyWolf * world.ColonyWolf.GetAtColumn(x, z)
                + death * world.Death.GetAtColumn(x, z)
                + fear * world.Fear.GetAtColumn(x, z)
                + prey * world.Prey.GetAtColumn(x, z)
                + suitability * world.Suitability.GetAtColumn(x, z)
                + trample * world.Trample.GetAtColumn(x, z)
                + vegetation * world.Vegetation.GetAtColumn(x, z);
        }

        /// <summary>名前昇順の i 番目の重み（統計・ハッシュ用）。</summary>
        public float this[int index] => index switch
        {
            0 => colonyPig,
            1 => colonySheep,
            2 => colonyWolf,
            3 => death,
            4 => fear,
            5 => prey,
            6 => suitability,
            7 => trample,
            8 => vegetation,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        /// <summary>名前昇順の場の名前（重みの並びと対応することをテストで検証する）。</summary>
        public static readonly string[] FieldNames =
        {
            // 名前の定義は ColonyField 側が一次情報。並びは名前昇順
            // （colony-pig → colony-sheep → colony-wolf）で、テストで検証する
            ColonyField.NameFor(EntityKind.Pig),
            ColonyField.NameFor(EntityKind.Sheep),
            ColonyField.NameFor(EntityKind.Wolf),
            DeathField.FieldName,
            FearField.FieldName,
            PreyField.FieldName,
            SuitabilityField.FieldName,
            TrampleField.FieldName,
            VegetationField.FieldName,
        };

        /// <summary>
        /// 「食べ物・獲物を探しているとき」の初期重み。
        ///
        /// 草食獣: 植生に引かれ、恐怖に押される。w_fear を w_veg より大きくして
        /// あるので、迷ったら安全側に倒れる。
        /// 狼: 獲物場だけを追う。
        /// </summary>
        public static EntityWeights ForagingFor(EntityKind kind, SimParams p) => kind switch
        {
            EntityKind.Wolf => new EntityWeights { prey = p.wolfPreyWeight },
            EntityKind.Sheep or EntityKind.Pig => new EntityWeights
            {
                vegetation = p.herbivoreVegetationWeight,
                fear = -p.herbivoreFearWeight,
            },
            _ => default, // 植物は行動しない
        };

        /// <summary>
        /// 「満腹で徘徊しているとき」の初期重み。
        /// 餌は探さないが**危険だけは避ける**（Demo 8 第2段 I3）。
        /// 狼は避ける相手がいないので全て0＝純粋なランダム徘徊。
        /// </summary>
        public static EntityWeights WanderingFor(EntityKind kind, SimParams p) => kind switch
        {
            EntityKind.Sheep or EntityKind.Pig => new EntityWeights { fear = -p.herbivoreFearWeight },
            _ => default,
        };

        public bool Equals(EntityWeights other) =>
            colonyPig.Equals(other.colonyPig) && colonySheep.Equals(other.colonySheep)
            && colonyWolf.Equals(other.colonyWolf)
            && death.Equals(other.death) && fear.Equals(other.fear) && prey.Equals(other.prey)
            && suitability.Equals(other.suitability) && trample.Equals(other.trample)
            && vegetation.Equals(other.vegetation);

        public override bool Equals(object obj) => obj is EntityWeights other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = colonyPig.GetHashCode();
                h = h * 397 ^ colonySheep.GetHashCode();
                h = h * 397 ^ colonyWolf.GetHashCode();
                h = h * 397 ^ death.GetHashCode();
                h = h * 397 ^ fear.GetHashCode();
                h = h * 397 ^ prey.GetHashCode();
                h = h * 397 ^ suitability.GetHashCode();
                h = h * 397 ^ trample.GetHashCode();
                h = h * 397 ^ vegetation.GetHashCode();
                return h;
            }
        }
    }
}
