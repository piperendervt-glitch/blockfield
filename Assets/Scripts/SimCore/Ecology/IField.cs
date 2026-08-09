using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 場の共通インターフェース (Demo 4.5 作業1)。
    /// World.Fields はこの型の辞書で場を一元管理し、ContentHash 計算と更新ループが
    /// 場の種類を知らずに回るようにする。
    ///
    /// 【意味論: 表面場】
    /// Demo 4.5 時点の場はすべて「表面場」— 各 (x,z) の**最上面**に付随する 2D 場である。
    /// 机の下の床面は場を持たない（机がある (x,z) の場は机上を指す）。
    /// 空間の高さ方向の層は「フロア」と呼ぶ
    /// （stigmergy_vision.md §7 の時間軸の「層」＝τの速さの層とは別概念）。
    ///
    /// 【将来の拡張（roadmap Demo 6 拡張点）】
    /// - (b) フロア構造: 面をフロアに分け、各フロアが独立した 2D 場を持つ。
    ///   表面場からの移行は意味論の読み替え（「最上面の場」→「フロア0の場」）で可能
    /// - (c) 3Dベクトル場: 移行時は**スパースチャンク方式**（VoxelGrid 同様の 16³ 疎チャンク、
    ///   地形表面±数セルのみ生成）を前提とし、実効セル数を 10^4 台に抑える
    ///   （10^5 セルは roadmap Demo 6 の PCバッチ再検討トリガに抵触するため）
    ///
    /// 座標 API は上記 (c) を見越して Int3 に統一してある。現行実装は y を無視するが、
    /// デバッグビルドでは y が表層高さと一致するかを検証する（表面場の前提が破れたら検出）。
    /// </summary>
    public interface IField
    {
        /// <summary>場の識別名。ContentHash はこの名前の昇順で畳み込む（決定論のため）。</summary>
        string Name { get; }

        int Width { get; }

        int Depth { get; }

        /// <summary>表面セルの読み出し（3D対応API。現行は y を無視）。</summary>
        float Get(Int3 cell);

        /// <summary>表面セルへの書き込み（3D対応API。現行は y を無視）。</summary>
        void Deposit(Int3 cell, float amount);

        /// <summary>毎ティックの更新（拡散・減衰など）。静的な場は何もしない。</summary>
        void Update(SimParams p);

        /// <summary>ContentHash への畳み込み（決定論の対象）。</summary>
        ulong AccumulateHash(ulong hash, ulong prime);
    }
}
