using System;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 流れ関数 ψ から流れを構成する場 (系列2 Phase B)。
    ///
    /// <code>u = ∇×ψ</code>
    ///
    /// 【Navier-Stokes を解かない】速度場を時間発展させず、毎ティック ψ から構成する。
    /// 回転を取るだけなので**非圧縮性が構造的に保証され**、圧力ポアソン方程式が要らない。
    /// コストは格子1枚分の差分のみ。`jelly_side.html` のヤコビ反復は
    /// 注視モード（jelly_2 段2 の軸対称流体）専用に温存する。
    ///
    /// 【ψ を毎フレーム作り直さない】実測で、ψ の構築（カールノイズ3オクターブ）が
    /// 全体の 98% を占め、∇×ψ は 48³ でも 0.28 ms（72FPS 予算の 1.9%）だった。
    /// つまり重いのは解像度ではなく「毎フレーム作り直す」ことである。
    /// ψ のノイズ項は <see cref="NoiseStripes"/> 本の縞に分けて更新する。
    ///
    /// 【フレームでなくティックで進める】縞の更新をフレーム駆動にすると
    /// フレームレート依存になり決定論が壊れる。**固定ティックで進め、View が補間する**
    /// （既存の「表示と真実の分離」と同形）。
    ///
    /// 【境界】ψ に「境界までの距離のランプ」を掛ける。壁で ψ → 0 になるので
    /// u は壁に沿う（法線成分が消える）。家具のポリゴンがそのまま水中の岩になり、
    /// その周りを流れが回る。
    /// </summary>
    public sealed class FlowField
    {
        /// <summary>ノイズ項を何ティックに分けて更新するか。</summary>
        public const int NoiseStripes = 8;

        readonly FlowGrid m_Grid;
        readonly float[] m_Psi;        // 3成分/セル
        readonly float[] m_Velocity;   // 3成分/セル
        readonly FlowParams m_Params;

        long m_Tick;

        public FlowGrid Grid => m_Grid;
        public long TickCount => m_Tick;

        public FlowField(FlowGrid grid, FlowParams p)
        {
            m_Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            m_Params = p;
            m_Psi = new float[grid.CellCount * 3];
            m_Velocity = new float[grid.CellCount * 3];
        }

        /// <summary>
        /// 全セルの ψ を作り直す（初期化用）。以降は <see cref="Tick"/> が縞で更新する。
        /// </summary>
        public void RebuildAll()
        {
            for (int s = 0; s < NoiseStripes; s++)
            {
                BuildPsiStripe(s, 0);
            }
            ComputeCurl();
        }

        /// <summary>
        /// 1ティック進める。ノイズ項の1縞を更新してから ∇×ψ を取り直す。
        /// **フレームではなくティック**で呼ぶこと。
        /// </summary>
        public void Tick()
        {
            BuildPsiStripe((int)(m_Tick % NoiseStripes), m_Tick);
            ComputeCurl();
            m_Tick++;
        }

        /// <summary>
        /// ψ の1縞を作る。縞は z 方向で切る（連続したメモリを触るので局所性が良い）。
        ///
        /// ψ = 浮力項（Phase D まで係数0、枠だけ）
        ///   + カールノイズ3オクターブ（日周で振幅変調）
        ///   × 境界のランプ
        /// 擾乱項と航跡場は Phase C 以降。
        /// </summary>
        void BuildPsiStripe(int stripe, long tick)
        {
            var g = m_Grid;
            float t = tick * m_Params.NoiseTimeStep;
            float scale = m_Params.NoiseScale;
            float ramp = Math.Max(1e-6f, m_Params.BoundaryRampCells);

            for (int z = stripe; z < g.Depth; z += NoiseStripes)
            {
                for (int y = 0; y < g.Height; y++)
                {
                    for (int x = 0; x < g.Width; x++)
                    {
                        int cell = g.Index(x, y, z);
                        int i = cell * 3;

                        if (g.IsSolidAt(cell))
                        {
                            m_Psi[i] = 0f; m_Psi[i + 1] = 0f; m_Psi[i + 2] = 0f;
                            continue;
                        }

                        float fx = x * scale, fy = y * scale, fz = z * scale;

                        // 3成分に別々のシードを与える。同じ場を3回読むと ψ が
                        // 対角線方向に潰れて回転が出ない
                        float px = CurlNoise3.Fbm(fx, fy, fz + t, m_Params.Seed, m_Params.Octaves);
                        float py = CurlNoise3.Fbm(fx + 31.416f, fy, fz + t, m_Params.Seed + 1u, m_Params.Octaves);
                        float pz = CurlNoise3.Fbm(fx, fy + 17.705f, fz + t, m_Params.Seed + 2u, m_Params.Octaves);

                        // 浮力項。Phase D で温度センサが入るまで係数0（枠だけ用意する）
                        py += m_Params.BuoyancyWeight * 0f;

                        // 境界のランプ。壁で 0 になるので u が壁に沿う
                        float d = g.DistanceInCells(cell);
                        float k = d >= ramp ? 1f : Smooth(d / ramp);

                        m_Psi[i] = px * k;
                        m_Psi[i + 1] = py * k;
                        m_Psi[i + 2] = pz * k;
                    }
                }
            }
        }

        /// <summary>u = ∇×ψ を中心差分で取る。格子端は 0（外周は固体なので流れない）。</summary>
        void ComputeCurl()
        {
            var g = m_Grid;
            float inv = 1f / (2f * g.CellSize);

            for (int z = 1; z < g.Depth - 1; z++)
            {
                for (int y = 1; y < g.Height - 1; y++)
                {
                    for (int x = 1; x < g.Width - 1; x++)
                    {
                        int cell = g.Index(x, y, z);
                        int i = cell * 3;

                        if (g.IsSolidAt(cell))
                        {
                            m_Velocity[i] = 0f; m_Velocity[i + 1] = 0f; m_Velocity[i + 2] = 0f;
                            continue;
                        }

                        int xp = g.Index(x + 1, y, z) * 3, xm = g.Index(x - 1, y, z) * 3;
                        int yp = g.Index(x, y + 1, z) * 3, ym = g.Index(x, y - 1, z) * 3;
                        int zp = g.Index(x, y, z + 1) * 3, zm = g.Index(x, y, z - 1) * 3;

                        // (∇×ψ)_x = ∂ψz/∂y − ∂ψy/∂z
                        m_Velocity[i] = ((m_Psi[yp + 2] - m_Psi[ym + 2])
                                       - (m_Psi[zp + 1] - m_Psi[zm + 1])) * inv;
                        // (∇×ψ)_y = ∂ψx/∂z − ∂ψz/∂x
                        m_Velocity[i + 1] = ((m_Psi[zp] - m_Psi[zm])
                                           - (m_Psi[xp + 2] - m_Psi[xm + 2])) * inv;
                        // (∇×ψ)_z = ∂ψy/∂x − ∂ψx/∂y
                        m_Velocity[i + 2] = ((m_Psi[xp + 1] - m_Psi[xm + 1])
                                           - (m_Psi[yp] - m_Psi[ym])) * inv;
                    }
                }
            }
        }

        /// <summary>セルの流速（アンカーローカル、m/tick）。</summary>
        public void VelocityAt(int x, int y, int z, out float vx, out float vy, out float vz)
        {
            int i = m_Grid.Index(x, y, z) * 3;
            vx = m_Velocity[i]; vy = m_Velocity[i + 1]; vz = m_Velocity[i + 2];
        }

        /// <summary>
        /// 任意の位置（アンカーローカル、m）の流速を三線形補間で読む。
        /// 粒子の移流に使う（View 専用だが、場の読み出し自体は真実側）。
        /// </summary>
        public void SampleVelocity(float wx, float wy, float wz,
                                   out float vx, out float vy, out float vz)
        {
            var g = m_Grid;
            float gx = (wx - g.OriginX) / g.CellSize - 0.5f;
            float gy = (wy - g.OriginY) / g.CellSize - 0.5f;
            float gz = (wz - g.OriginZ) / g.CellSize - 0.5f;

            int x0 = FloorToInt(gx), y0 = FloorToInt(gy), z0 = FloorToInt(gz);
            float tx = gx - x0, ty = gy - y0, tz = gz - z0;

            vx = vy = vz = 0f;
            for (int dz = 0; dz <= 1; dz++)
            {
                float wz2 = dz == 0 ? 1f - tz : tz;
                for (int dy = 0; dy <= 1; dy++)
                {
                    float wy2 = dy == 0 ? 1f - ty : ty;
                    for (int dx = 0; dx <= 1; dx++)
                    {
                        float wx2 = dx == 0 ? 1f - tx : tx;
                        int cx = x0 + dx, cy = y0 + dy, cz = z0 + dz;
                        if (!g.InRange(cx, cy, cz)) continue;

                        float w = wx2 * wy2 * wz2;
                        int i = g.Index(cx, cy, cz) * 3;
                        vx += m_Velocity[i] * w;
                        vy += m_Velocity[i + 1] * w;
                        vz += m_Velocity[i + 2] * w;
                    }
                }
            }
        }

        /// <summary>
        /// 非圧縮性の確認用。セルの発散 ∂u/∂x + ∂v/∂y + ∂w/∂z。
        /// u = ∇×ψ なので**構造的に 0 のはず**（丸め誤差の範囲で）。
        /// </summary>
        public float DivergenceAt(int x, int y, int z)
        {
            var g = m_Grid;
            if (x <= 0 || y <= 0 || z <= 0
                || x >= g.Width - 1 || y >= g.Height - 1 || z >= g.Depth - 1)
            {
                return 0f;
            }
            float inv = 1f / (2f * g.CellSize);
            int xp = g.Index(x + 1, y, z) * 3, xm = g.Index(x - 1, y, z) * 3;
            int yp = g.Index(x, y + 1, z) * 3, ym = g.Index(x, y - 1, z) * 3;
            int zp = g.Index(x, y, z + 1) * 3, zm = g.Index(x, y, z - 1) * 3;
            return ((m_Velocity[xp] - m_Velocity[xm])
                  + (m_Velocity[yp + 1] - m_Velocity[ym + 1])
                  + (m_Velocity[zp + 2] - m_Velocity[zm + 2])) * inv;
        }

        /// <summary>決定論の検証用ハッシュ。格子・ψ・速度をまとめて畳み込む。</summary>
        public ulong ComputeContentHash()
        {
            const ulong prime = 1099511628211UL;
            ulong hash = m_Grid.ComputeContentHash();
            unchecked
            {
                hash = (hash ^ (ulong)m_Tick) * prime;
                for (int i = 0; i < m_Psi.Length; i++)
                {
                    uint bits = (uint)BitConverter.SingleToInt32Bits(m_Psi[i]);
                    hash = (hash ^ (bits & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 8) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 16) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 24) & 0xFF)) * prime;
                }
            }
            return hash;
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return v < i ? i - 1 : i;
        }
    }
}
