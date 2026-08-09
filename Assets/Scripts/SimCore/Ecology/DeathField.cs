namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 死の場 (Demo 8 第2段 I1)。個体が死んだ場所に残る痕跡。
    ///
    /// 【何を場に移したか】これまでの場は「生き物が今どこにいるか」の痕跡だったが、
    /// これは**もう存在しない個体の痕跡**である。個体が消えても場所は覚えている、
    /// という形で生態系が空間的な記憶を持つ。
    ///
    /// 【用途は養分（prereg の選択記録）】草食獣が避ける危険地帯の記憶にもできるが、
    /// それは恐怖場と機能が重複する。ここでは**死骸が養分になって植生を高める**方に使う。
    /// 死が生を生むという新しい因果が入り、墓場に草が茂るという創発が期待できる。
    ///
    /// 【τ（減衰率）の設計意図】0.003 は植生場0.02・恐怖場0.03・獲物場0.05 より
    /// 桁違いに遅い。土に還った養分は長く残るという意味であり、
    /// これが「長期記憶の層」になる（層別τ設計の最も遅い層）。
    ///
    /// 拡散は 0.02×1パスと最小限。死骸は動かないので痕跡は局所的であるべきで、
    /// にじませると「どこで死んだか」の情報が失われる。
    /// 第1段で確立した「到達距離は拡散のパス数で作る」原則に従い、
    /// 広げたいときはパス数を増やす（拡散率を上げない）。
    /// </summary>
    public sealed class DeathField : DiffusingField
    {
        /// <summary>World.Fields のキー。ContentHash の畳み込み順は名前昇順。</summary>
        public const string FieldName = "death";

        public DeathField(int width, int depth)
            : base(FieldName, width, depth)
        {
        }

        public override void Update(SimParams p)
        {
            int passes = p.deathDiffusePasses < 1 ? 1 : p.deathDiffusePasses;
            for (int i = 0; i < passes - 1; i++)
            {
                UpdateDiffusion(p.deathDiffuse, 0f);
            }
            UpdateDiffusion(p.deathDiffuse, p.deathDecay);
        }
    }
}
