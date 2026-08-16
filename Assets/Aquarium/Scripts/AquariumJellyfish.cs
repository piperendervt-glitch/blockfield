using BlockField.SimCore.Fluid;
using UnityEngine;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 水槽のクラゲ1体 (系列2 Phase C-7/8)。
    ///
    /// 【真実側】粒子と違い、クラゲは位置が力学の状態である。
    /// 決定論の対象に入れ、**固定ティック（神経 40Hz）**で進める。
    /// 流れ場は 20Hz なので、流れ1ティックにつき神経2ステップの整数比になる。
    ///
    /// 【流れとの結合は一方向】流れはクラゲを運ぶが、クラゲは流れに書き戻さない。
    /// 航跡場（生物が流れに書き込む場）は後の段。
    /// </summary>
    public sealed class AquariumJellyfish : MonoBehaviour
    {
        /// <summary>神経を進める固定周波数 (Hz)。1拍動 = 40 ステップ = 1.0 秒。</summary>
        const float k_NeuralHz = 40f;

        /// <summary>
        /// 実機で切り替える傘径 (m)。ミズクラゲの実物は 10〜25cm。
        /// 実部屋に浮かべる以上、見え方に直結するので振れるようにする。
        /// </summary>
        public static readonly float[] BellDiameterChoices = { 0.10f, 0.15f, 0.25f };

        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] int m_BellIndex = 1;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }

        public int BellIndex => m_BellIndex;
        public float BellDiameter => BellDiameterChoices[m_BellIndex];

        /// <summary>クラゲ本体。焼き込みが済むまで null。</summary>
        public Jellyfish Body { get; private set; }

        /// <summary>直近の流速 (m/s)。パネルとログ用。</summary>
        public Vector3 FlowAt { get; private set; }

        float m_Accumulator;

        void Update()
        {
            var field = m_Flow != null ? m_Flow.Field : null;
            if (field == null)
            {
                Body = null;
                return;
            }
            if (Body == null)
            {
                Spawn(field);
                return;
            }

            m_Accumulator += Time.deltaTime;
            float step = 1f / k_NeuralHz;
            int stepped = 0;
            while (m_Accumulator >= step && stepped < 8)
            {
                field.SampleVelocity(Body.X, Body.Y, Body.Z,
                    out float vx, out float vy, out float vz);
                FlowAt = new Vector3(vx, vy, vz);
                Body.Step(step, vx, vy, vz);
                KeepInsideTank(field);
                m_Accumulator -= step;
                stepped++;
            }
        }

        /// <summary>傘径を切り替える。位置は引き継がず、湧かせ直す。</summary>
        public void CycleBellDiameter()
        {
            m_BellIndex = (m_BellIndex + 1) % BellDiameterChoices.Length;
            Body = null;
            Debug.Log($"[Aquarium] 傘径を {BellDiameter * 100f:F0}cm に切り替え");
        }

        void Spawn(FlowField field)
        {
            var g = field.Grid;
            // 部屋の真ん中あたりの水セルへ置く
            float cx = g.OriginX + g.Width * g.CellSize * 0.5f;
            float cy = g.OriginY + g.Height * g.CellSize * 0.55f;
            float cz = g.OriginZ + g.Depth * g.CellSize * 0.5f;

            var p = JellyParams.Default;
            p.BellDiameter = BellDiameter;
            Body = new Jellyfish(p, cx, cy, cz);

            Debug.Log($"[Aquarium] クラゲを投入: 傘 {BellDiameter * 100f:F0}cm / " +
                $"目標遊泳 {p.SwimSpeed:F3}m/s / 拍動 {p.PulsePeriodTicks / k_NeuralHz:F2}秒 / " +
                $"換算係数 {Body.SpeedScale:F4} / 位置({cx:F2}, {cy:F2}, {cz:F2})");
        }

        /// <summary>
        /// 固体セルや格子の外へ出たら、水のある側へ押し戻す。
        ///
        /// 【なぜ要るか】クラゲは流れに運ばれるが、境界のランプは流れを
        /// 壁に沿わせるだけで**貫通を厳密に禁止してはいない**
        /// （|u·n|/|u| = 0.14 で 0 ではない）。積分の刻みによっては壁に入りうる。
        /// 壁にめり込んだクラゲは「生きて見えるか」の判定を壊すので、
        /// 表示上の破綻を防ぐ最小限の処理を入れる。
        /// </summary>
        void KeepInsideTank(FlowField field)
        {
            var g = field.Grid;
            float margin = BellDiameter * 0.5f;
            float minX = g.OriginX + margin, maxX = g.OriginX + g.Width * g.CellSize - margin;
            float minY = g.OriginY + margin, maxY = g.OriginY + g.Height * g.CellSize - margin;
            float minZ = g.OriginZ + margin, maxZ = g.OriginZ + g.Depth * g.CellSize - margin;

            float x = Mathf.Clamp(Body.X, minX, maxX);
            float y = Mathf.Clamp(Body.Y, minY, maxY);
            float z = Mathf.Clamp(Body.Z, minZ, maxZ);

            // 固体セルに入っていたら、近くの水セルへ寄せる
            int gx = Mathf.FloorToInt((x - g.OriginX) / g.CellSize);
            int gy = Mathf.FloorToInt((y - g.OriginY) / g.CellSize);
            int gz = Mathf.FloorToInt((z - g.OriginZ) / g.CellSize);
            if (g.InRange(gx, gy, gz) && g.IsSolid(gx, gy, gz))
            {
                y = Mathf.Min(maxY, y + g.CellSize);   // まず上へ逃がす（床が最も多い）
            }

            Body.Teleport(x, y, z);
        }
    }
}
