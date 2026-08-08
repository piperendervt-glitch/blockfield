namespace BlockField.SimCore.Terrain
{
    /// <summary>地形生成パラメータ。</summary>
    public struct TerrainParams
    {
        /// <summary>生成シード。同一パラメータからは常に同一の地形が生成される。</summary>
        public uint seed;

        /// <summary>X方向のセル数。</summary>
        public int width;

        /// <summary>Z方向のセル数。</summary>
        public int depth;

        /// <summary>柱の最大高さ（層数）。高さは 1..maxHeight に収まる。</summary>
        public int maxHeight;

        /// <summary>起伏の空間スケール（セル数）。大きいほどなだらか。</summary>
        public float reliefScale;

        /// <summary>平地バイオームの起伏振幅 (0..1)。</summary>
        public float plainsAmplitude;

        /// <summary>山バイオームの起伏振幅 (0..1)。</summary>
        public float mountainAmplitude;

        public static TerrainParams Default => new TerrainParams
        {
            seed = 1u,
            width = 100,
            depth = 100,
            maxHeight = 16,
            reliefScale = 24f,
            plainsAmplitude = 0.25f,
            mountainAmplitude = 1f,
        };
    }
}
