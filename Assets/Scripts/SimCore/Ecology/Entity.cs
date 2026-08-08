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

        public bool IsAnimal => kind == EntityKind.Sheep || kind == EntityKind.Pig;

        public bool IsPlant => kind == EntityKind.GrassTuft || kind == EntityKind.Flower;
    }
}
