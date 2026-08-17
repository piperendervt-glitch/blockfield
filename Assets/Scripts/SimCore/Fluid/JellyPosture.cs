using System;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 傘の姿勢（jelly_2 K2）。**軸は積分されるだけで、代入されない。**
    ///
    /// 【M-J2b との線引き】M-J2b が禁じているのは「動物が行き先を決めるのに
    /// 方向を計算すること」で、禁止されている形は「刺激を感じる → 目標方向を
    /// 計算 → そこへ操舵する」という**設定値を持つ制御器**である。
    ///
    /// 姿勢はそれに当たらない。位置 X,Y,Z はすでに状態だが、これが M-J2b を
    /// 破っているとは言わない。速度は各セルの局所的な寄与の総和として
    /// 出てきたもので、方向を計算した結果ではない。**姿勢は位置の1階微分上の
    /// 同じ構成**である（prereg jelly_2 追記7 A7.1）。
    ///
    /// 【なぜ軸だけでなく基準ベクトルも持つか】リングは軸に垂直な平面にある。
    /// セルの解剖学的な位置を部屋座標へ写すには、その平面内の基準が要る。
    /// 軸だけだとリングが軸まわりに滑ってしまい、「同じセル」が毎ステップ
    /// 違う向きを指すことになる。姿勢は**回転行列に相当する情報**が必要で、
    /// ここでは正規直交な2本（軸と基準）で持つ。3本目は外積で出る。
    ///
    /// 【正規化は代入ではない】数値誤差で長さが 1 からずれるのを直すだけで、
    /// 向きを外から決めていない。
    /// </summary>
    public struct JellyPosture
    {
        /// <summary>傘の軸（頂点の向き。局所 +Y に相当）。</summary>
        public float AxisX, AxisY, AxisZ;

        /// <summary>リング平面内の基準（解剖学的な 0° の向き。局所 +X に相当）。</summary>
        public float RefX, RefY, RefZ;

        /// <summary>軸が真上を向き、基準が +X を向いた初期姿勢。</summary>
        public static JellyPosture Upright => new JellyPosture
        {
            AxisX = 0f, AxisY = 1f, AxisZ = 0f,
            RefX = 1f, RefY = 0f, RefZ = 0f,
        };

        /// <summary>3本目の基底（軸 × 基準）。リング平面内で基準に直交する。</summary>
        public void Third(out float x, out float y, out float z)
        {
            x = AxisY * RefZ - AxisZ * RefY;
            y = AxisZ * RefX - AxisX * RefZ;
            z = AxisX * RefY - AxisY * RefX;
        }

        /// <summary>
        /// 解剖学的な角度 a のセルが、部屋座標でどの半径方向を向いているか。
        /// リング平面内の単位ベクトル。
        /// </summary>
        public void RadialAt(float cos, float sin, out float x, out float y, out float z)
        {
            Third(out float tx, out float ty, out float tz);
            x = RefX * cos + tx * sin;
            y = RefY * cos + ty * sin;
            z = RefZ * cos + tz * sin;
        }

        /// <summary>
        /// 角速度で1ステップ回す。**これが軸の唯一の変更経路**である。
        /// 1次のオイラー積分（v += (ω × v) dt）のあと正規直交化する。
        /// </summary>
        public void Integrate(float omX, float omY, float omZ, float dt)
        {
            float ax = AxisX + (omY * AxisZ - omZ * AxisY) * dt;
            float ay = AxisY + (omZ * AxisX - omX * AxisZ) * dt;
            float az = AxisZ + (omX * AxisY - omY * AxisX) * dt;

            float rx = RefX + (omY * RefZ - omZ * RefY) * dt;
            float ry = RefY + (omZ * RefX - omX * RefZ) * dt;
            float rz = RefZ + (omX * RefY - omY * RefX) * dt;

            // 軸を正規化
            float an = (float)Math.Sqrt(ax * ax + ay * ay + az * az);
            if (an < 1e-9f) return;
            AxisX = ax / an; AxisY = ay / an; AxisZ = az / an;

            // 基準から軸成分を抜いて正規化（グラム・シュミット）
            float d = rx * AxisX + ry * AxisY + rz * AxisZ;
            rx -= d * AxisX; ry -= d * AxisY; rz -= d * AxisZ;
            float rn = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rn < 1e-9f) return;
            RefX = rx / rn; RefY = ry / rn; RefZ = rz / rn;
        }

        /// <summary>
        /// 姿勢を上向きへ戻すトルク（重力と浮力の分離による復元モーメント）。
        ///
        /// **これは受動的な物理であって、行き先の計算ではない。** 軸が真上なら 0、
        /// 傾いているほど上へ戻す向きに働く。実物のミズクラゲが姿勢を
        /// 立て直すのと同じ。gain = 0 でアブレーション。
        /// </summary>
        public void RightingTorque(float gain, out float tx, out float ty, out float tz)
        {
            // 軸 × 上（上 = (0,1,0)）。この軸まわりに回すと軸が上へ寄る。
            //   cross(axis, up) = (Ay*0 - Az*1, Az*0 - Ax*0, Ax*1 - Ay*0) = (-Az, 0, Ax)
            // 確認: これを n として n × axis = up - axis(axis·up) となり、
            // 上の「軸に垂直な成分」＝軸を上へ寄せる向きになる（追記7 A7.2）
            tx = -gain * AxisZ;
            ty = 0f;
            tz = gain * AxisX;
        }

        /// <summary>軸が真上からどれだけ傾いているか（度）。判定とログ用。</summary>
        public float TiltDegrees()
        {
            float c = AxisY;
            if (c > 1f) c = 1f; else if (c < -1f) c = -1f;
            return (float)(Math.Acos(c) * 180.0 / Math.PI);
        }
    }
}
