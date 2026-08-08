# blockfield 開発規約

## アーキテクチャ
- Assets/Scripts/SimCore は UnityEngine 非依存（asmdefで強制済み）。
  シミュレーションロジックは必ずここに書く
- シーン/プレハブの変更は Editor スクリプト経由でコード化する。
  GUI手作業の結果だけをコミットしない
- RNGは SimCore/Rng の決定論実装のみ使用。System.Random / UnityEngine.Random 禁止
- 生態系から地形を書き換えるコードは VoxelGrid.TrySetBlockEcology を必ず使う
  （Player 出所ブロックは生態系から不変 — 固定レイヤー原則）
- 設計の終着点は docs/design/stigmergy_vision.md、
  全体計画は docs/design/roadmap.md。個別Demoの実装判断は
  これらと矛盾しないこと（特に: 場のデータ構造の統一、
  決定論 f(シード, イベントログ)、SimCore の Quest/PC 共用）

## テスト
- 実行: scripts/run_tests.ps1（EditMode、batchmode）
- Unity Editorが開いているとCLIテストは失敗する。Temp/UnityLockfile を確認すること
- pre-pushゲートあり。全テストパスなしにpushしない

## 実機テスト運用
- テスト手順は事前に固定し、プレイ前にすべて提示する
- ユーザーのHMD装着中はメッセージでの指示・質問・依頼をしない
  （ユーザーは画面を見られない）。ログ監視と記録に徹し、
  報告はユーザーがHMDを外して発言してから行う
- 装着中はMonitorの生存報告も出力しない（「記録継続中」等の
  状態報告の繰り返しは不要）。サイレントに記録し、
  ユーザーの報告後にまとめて出力する
- 1セッションの装着時間は5分以内を目安に手順を設計する

## ビルド
- scripts/build_quest.ps1 → scripts/deploy.ps1
- ProjectSettings/ の差分は必ずコミット（XR preloadedAssets消失対策）

## SBCE
- 各Demoは docs/prereg/ に事前登録メトリクスを書いてから実装
- Demo完了時に git tag demo-N.M でフリーズ
