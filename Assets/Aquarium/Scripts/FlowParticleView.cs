using BlockField.SimCore.Fluid;
using UnityEngine;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 流れを見せる粒子 (系列2 Phase B、**View 専用**)。
    ///
    /// 【力学に不干渉】粒子は流れ場を**読むだけ**で、場へ書き戻さない。
    /// 乱数も View 専用ストリームで、SimCore の決定論に一切触れない
    /// （roadmap 系列2 Phase C-12 の規約を先に守っておく）。
    ///
    /// 【MR 合成の制約】アルファ&lt;1 の描画はパススルーと合成されるので使えない。
    /// 粒子は**不透明**で描き、密度・大きさ・明度で表現する
    /// （半透明の代わりにスケールと明度で見せるのは Demo 0 以来の方針）。
    ///
    /// 【実機で調整できる形にしてある】ψ から作った流れは滑らかで大域的なので、
    /// 粒子を流すと「霧が動いている」ように見える恐れがある。
    /// 見た目が物足りないときにまず疑うのは View 側なので、
    /// 密度・大きさ・明度を実機で切り替えられるようにした
    /// （<see cref="CyclePreset"/>）。力学は変わらない。
    /// </summary>
    public sealed class FlowParticleView : MonoBehaviour
    {
        /// <summary>見た目のプリセット。実機で回して比べる。</summary>
        public struct Preset
        {
            public string Name;
            public int Count;
            /// <summary>粒子の基準サイズ (m)。</summary>
            public float Size;
            /// <summary>サイズのばらつき（0 = 一様、1 = 4倍まで散る）。</summary>
            public float SizeSpread;
            /// <summary>明度の基準。速度で変調する幅は下の Contrast。</summary>
            public float Brightness;
            /// <summary>速度による明度の変調幅。大きいほど速い所だけ光る。</summary>
            public float Contrast;
            /// <summary>粒子の寿命の範囲（秒）。尽きたら別の水セルへ湧き直す。</summary>
            public float LifeMin, LifeMax;
        }

        // 【SpeedGain を削除した】流れ場が m/s で正規化される前は、
        // 生の ∇×ψ を見えるようにするために 6〜9 倍していた。
        // 正規化後にこれを残すと、目標流速 0.08 m/s を指定しても
        // 粒子は 0.48 m/s で動く——**目標値が意味を失う**。
        // 粒子は場の速度そのままで動かす。
        //
        // 【寿命を延ばした】以前は 1〜5 秒だった。流速を 1/180 にしたので
        // 粒子はほとんど動かずに湧き直すことになり、**湧き直しのチラつきが
        // 動きより目立つ**（3000粒子 × 1/3秒 で毎フレーム14個が消えて現れる）。
        // 0.08 m/s では格子の横断に約51秒かかるので、寿命もその桁に合わせる。
        public static readonly Preset[] Presets =
        {
            // 細かい粒を多数。水中の微粒子に見せる狙い
            new Preset { Name = "微粒子", Count = 3000, Size = 0.010f, SizeSpread = 0.5f,
                         Brightness = 0.55f, Contrast = 0.8f, LifeMin = 20f, LifeMax = 60f },
            // 大きめを少数。数が少ないぶん1粒の動きが追える
            new Preset { Name = "粗い粒", Count = 900, Size = 0.022f, SizeSpread = 0.8f,
                         Brightness = 0.75f, Contrast = 0.5f, LifeMin = 20f, LifeMax = 60f },
            // 速い所だけ明るくする。**線は描かない**。
            // 以前は「流線強調」という名前だったが、名前からライン描画を想像させ、
            // 実際には出ないので混乱を招いた（2026-08-16 のセッションで指摘）。
            // 名前は実態に合わせる（テストの改名と同じ趣旨）
            new Preset { Name = "速い所が明るい", Count = 2000, Size = 0.013f, SizeSpread = 0.3f,
                         Brightness = 0.25f, Contrast = 2.0f, LifeMin = 20f, LifeMax = 60f },
        };

        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] Material m_Material;
        [SerializeField] AnchorSpaceRenderer m_Space;
        [SerializeField] int m_PresetIndex;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }
        public Material material { get => m_Material; set => m_Material = value; }
        /// <summary>描画は AnchorSpaceRenderer に集約している（規約: 描画の入口は1つ）。</summary>
        public AnchorSpaceRenderer space { get => m_Space; set => m_Space = value; }

        public int PresetIndex => m_PresetIndex;
        public Preset Current => Presets[m_PresetIndex];
        public int DrawnParticles { get; private set; }

        // View 専用の乱数。SimCore の Rng には触れない
        System.Random m_ViewRng = new System.Random(12345);

        Vector3[] m_Position;
        float[] m_Scale;
        float[] m_Life;
        Mesh m_Mesh;
        Matrix4x4[] m_Batch;
        MaterialPropertyBlock m_Block;
        int m_BuiltFor = -1;

        void OnDestroy()
        {
            if (m_Mesh != null) Destroy(m_Mesh);
        }

        public void CyclePreset()
        {
            m_PresetIndex = (m_PresetIndex + 1) % Presets.Length;
            m_BuiltFor = -1;   // 次の Update で作り直す
            Debug.Log($"[Aquarium] 粒子プリセット: {Current.Name} " +
                $"(数={Current.Count} 大きさ={Current.Size * 100f:F1}cm 明度={Current.Brightness:F2})");
        }

        void Update()
        {
            var field = m_Flow != null ? m_Flow.Field : null;
            if (field == null || m_Material == null)
            {
                DrawnParticles = 0;
                return;
            }

            if (m_BuiltFor != m_PresetIndex)
            {
                Rebuild(field);
            }

            Advect(field, Time.deltaTime);
            Draw(field);
        }

        void Rebuild(FlowField field)
        {
            var preset = Current;
            m_Position = new Vector3[preset.Count];
            m_Scale = new float[preset.Count];
            m_Life = new float[preset.Count];
            m_ViewRng = new System.Random(12345);

            for (int i = 0; i < preset.Count; i++)
            {
                m_Position[i] = RandomFluidPoint(field);
                // サイズは対数的に散らす。均一だと粒が揃いすぎて人工的に見える
                float u = (float)m_ViewRng.NextDouble();
                m_Scale[i] = preset.Size * Mathf.Lerp(1f, 1f + 3f * u, preset.SizeSpread);
                m_Life[i] = RandomLife(preset);
            }

            if (m_Mesh == null) m_Mesh = BuildQuad();
            m_Batch = new Matrix4x4[1023];
            m_Block = new MaterialPropertyBlock();
            m_BuiltFor = m_PresetIndex;
        }

        Vector3 RandomFluidPoint(FlowField field)
        {
            var g = field.Grid;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                int x = m_ViewRng.Next(g.Width), y = m_ViewRng.Next(g.Height), z = m_ViewRng.Next(g.Depth);
                if (g.IsSolid(x, y, z)) continue;
                return new Vector3(
                    g.OriginX + (x + (float)m_ViewRng.NextDouble()) * g.CellSize,
                    g.OriginY + (y + (float)m_ViewRng.NextDouble()) * g.CellSize,
                    g.OriginZ + (z + (float)m_ViewRng.NextDouble()) * g.CellSize);
            }
            return new Vector3(g.OriginX, g.OriginY, g.OriginZ);
        }

        float RandomLife(Preset preset) =>
            preset.LifeMin + (float)m_ViewRng.NextDouble() * (preset.LifeMax - preset.LifeMin);

        void Advect(FlowField field, float dt)
        {
            var preset = Current;
            var g = field.Grid;
            for (int i = 0; i < m_Position.Length; i++)
            {
                var p = m_Position[i];
                field.SampleVelocity(p.x, p.y, p.z, out float vx, out float vy, out float vz);
                // 場の速度そのままで動かす（目標流速が m/s で正規化済み）
                p += new Vector3(vx, vy, vz) * dt;

                m_Life[i] -= dt;
                bool outside =
                    p.x < g.OriginX || p.y < g.OriginY || p.z < g.OriginZ
                    || p.x > g.OriginX + g.Width * g.CellSize
                    || p.y > g.OriginY + g.Height * g.CellSize
                    || p.z > g.OriginZ + g.Depth * g.CellSize;

                // 寿命が尽きたら別の場所へ。**淀みに全部溜まるのを防ぐ**ためで、
                // 流れが遅い所ほど滞在が長くなる性質（走化性で既知）への対処でもある
                if (outside || m_Life[i] <= 0f)
                {
                    p = RandomFluidPoint(field);
                    m_Life[i] = RandomLife(preset);
                }
                m_Position[i] = p;
            }
        }

        void Draw(FlowField field)
        {
            var preset = Current;
            DrawnParticles = 0;
            if (m_Space == null) return;

            // 【カメラに直接触らない】ビルボードの回転はワールドの量なので、
            // 部屋座標の行列にそのまま入れるとアンカーの姿勢だけ余計に回る。
            // 部屋座標へ直した回転を AnchorSpaceRenderer からもらう
            if (!m_Space.TryGetBillboardRotation(out var faceCamera)) return;

            float reference = Mathf.Max(1e-5f, (float)m_Flow.MaxSpeed);
            int batched = 0;

            for (int i = 0; i < m_Position.Length; i++)
            {
                var p = m_Position[i];
                field.SampleVelocity(p.x, p.y, p.z, out float vx, out float vy, out float vz);
                float speed = Mathf.Sqrt(vx * vx + vy * vy + vz * vz) / reference;

                // 明度で速さを見せる。**アルファは使わない**（パススルーと合成されるため）
                float brightness = Mathf.Clamp01(preset.Brightness + (speed - 0.5f) * preset.Contrast);
                float scale = m_Scale[i] * (0.7f + 0.6f * Mathf.Clamp01(speed));

                // 部屋座標のまま渡す。ワールドへの変換は AnchorSpaceRenderer の仕事
                m_Batch[batched++] = Matrix4x4.TRS(p, faceCamera, Vector3.one * scale);

                // 明度はインスタンスごとに変えたいが、DrawMeshInstanced では
                // バッチ内で共通になる。速度帯でバッチを割る手もあるが、
                // まず「動いて見えるか」を確かめる段なので、バッチ全体の平均で近似する
                if (batched == m_Batch.Length)
                {
                    Flush(batched, brightness);
                    batched = 0;
                }
            }
            if (batched > 0)
            {
                Flush(batched, preset.Brightness);
            }
        }

        void Flush(int count, float brightness)
        {
            m_Block.SetColor("_BaseColor",
                new Color(brightness * 0.65f, brightness * 0.9f, brightness, 1f));
            // 部屋座標のまま渡す。ワールドへの変換は AnchorSpaceRenderer が持つ
            DrawnParticles += m_Space.DrawInstancedRaw(m_Mesh, m_Material, m_Batch, count, m_Block);
        }

        static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "FlowParticleQuad" };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.SetNormals(new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward });
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
