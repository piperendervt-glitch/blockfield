using System;

namespace BlockField.SimCore.Rng
{
    /// <summary>
    /// 決定論的な擬似乱数生成器 (mulberry32)。
    /// 同一シードからは常に同一の系列を生成する。UnityEngine 非依存。
    /// リファレンス: https://gist.github.com/tommyettinger/46a874533244883189143505d203312c
    /// 検証値: seed=1 → 2693262067, 11749833, 2265367787
    /// </summary>
    public sealed class Mulberry32
    {
        private uint _state;

        public Mulberry32(uint seed)
        {
            _state = seed;
        }

        /// <summary>次の32bit乱数を返す。</summary>
        public uint NextUInt()
        {
            unchecked
            {
                _state += 0x6D2B79F5u;
                uint t = _state;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + (t ^ (t >> 7)) * (t | 61u);
                return t ^ (t >> 14);
            }
        }

        /// <summary>[0, 1) の float を返す（上位24bitを使用、1.0f には決して到達しない）。</summary>
        public float NextFloat01()
        {
            return (NextUInt() >> 8) * (1f / 16777216f);
        }

        /// <summary>[minInclusive, maxExclusive) の int を返す。min は含み、max は含まない。</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (minInclusive >= maxExclusive)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxExclusive),
                    $"maxExclusive ({maxExclusive}) must be greater than minInclusive ({minInclusive}).");
            }

            uint span = (uint)((long)maxExclusive - minInclusive);
            return (int)(minInclusive + (long)(NextUInt() % span));
        }
    }
}
