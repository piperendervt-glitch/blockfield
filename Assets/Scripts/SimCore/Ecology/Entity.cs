using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 植物・動物共用のエンティティ (Demo 2 D2)。
    /// cell は「立っている表層の上のセル」。facing は動物のみ使用（0..3 = +X,+Z,-X,-Z）。
    /// </summary>
    public struct Entity
    {
        public int id;
        public EntityKind kind;
        public Int3 cell;
        public int facing;

        /// <summary>空腹度 0..1（動物のみ使用。1.0 で餓死）。ContentHash 対象。</summary>
        public float hunger;

        /// <summary>繁殖クールダウン残りティック（動物のみ使用）。ContentHash 対象。</summary>
        public int breedCooldown;

        /// <summary>
        /// 空腹・捕食モードでの場の重み (Demo 8 第3段 J2)。ContentHash 対象。
        /// 進化の基盤であり、本段では全個体が同じ初期値を持ち、繁殖でそのままコピーされる。
        /// </summary>
        public EntityWeights forageWeights;

        /// <summary>
        /// 満腹時の徘徊での場の重み (Demo 8 第3段 J2)。ContentHash 対象。
        /// 探索と回避で優先順位が変わりうるので、採餌時とは別に持つ。
        /// </summary>
        public EntityWeights wanderWeights;

        public bool IsAnimal => kind == EntityKind.Sheep || kind == EntityKind.Pig || kind == EntityKind.Wolf;

        public bool IsHerbivore => kind == EntityKind.Sheep || kind == EntityKind.Pig;

        public bool IsPlant => kind == EntityKind.GrassTuft || kind == EntityKind.Flower;
    }
}
