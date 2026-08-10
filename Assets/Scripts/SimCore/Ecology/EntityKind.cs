namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// エンティティ種別 (Demo 2 D2)。
    ///
    /// 【Demo 8.5 で植物が消えた】GrassTuft と Flower を削除した。
    /// 草は Entity ではなく**植生場の値そのもの**になったため、
    /// 「種別」を持つ対象ではなくなっている。
    /// 値も詰め直した（Sheep が 2 → 0）。過去のイベントログに記録された
    /// PlayerBreakPlant の payload（破壊した植物の種別）は解釈できなくなるが、
    /// その操作自体が廃止されているので実害はない。
    /// </summary>
    public enum EntityKind : byte
    {
        Sheep = 0,
        Pig = 1,
        Wolf = 2,
    }
}
