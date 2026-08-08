using System;

namespace BlockField.SimCore.Voxel
{
    /// <summary>
    /// 整数3次元座標。UnityEngine.Vector3Int は SimCore (noEngineReferences) では使えないため自作。
    /// </summary>
    public readonly struct Int3 : IEquatable<Int3>
    {
        public readonly int x;
        public readonly int y;
        public readonly int z;

        public Int3(int x, int y, int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Int3 operator +(Int3 a, Int3 b) => new Int3(a.x + b.x, a.y + b.y, a.z + b.z);

        public static bool operator ==(Int3 a, Int3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

        public static bool operator !=(Int3 a, Int3 b) => !(a == b);

        public bool Equals(Int3 other) => this == other;

        public override bool Equals(object obj) => obj is Int3 other && this == other;

        public override int GetHashCode()
        {
            unchecked
            {
                int h = x;
                h = h * 486187739 + y;
                h = h * 486187739 + z;
                return h;
            }
        }

        public override string ToString() => $"({x}, {y}, {z})";
    }
}
