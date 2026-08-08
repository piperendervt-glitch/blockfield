# blockfield 開発規約

## アーキテクチャ
- Assets/Scripts/SimCore は UnityEngine 非依存（asmdefで強制済み）。
  シミュレーションロジックは必ずここに書く
- シーン/プレハブの変更は Editor スクリプト経由でコード化する。
  GUI手作業の結果だけをコミットしない
- RNGは SimCore/Rng の決定論実装のみ使用。System.Random / UnityEngine.Random 禁止

## テスト
- 実行: scripts/run_tests.ps1（EditMode、batchmode）
- Unity Editorが開いているとCLIテストは失敗する。Temp/UnityLockfile を確認すること
- pre-pushゲートあり。全テストパスなしにpushしない

## 実機テスト運用
- テスト手順は事前に固定し、プレイ前にすべて提示する
- ユーザーのHMD装着中はメッセージでの指示・質問・依頼をしない
  （ユーザーは画面を見られない）。ログ監視と記録に徹し、
  報告はユーザーがHMDを外して発言してから行う
- 1セッションの装着時間は5分以内を目安に手順を設計する

## ビルド
- scripts/build_quest.ps1 → scripts/deploy.ps1
- ProjectSettings/ の差分は必ずコミット（XR preloadedAssets消失対策）

## SBCE
- 各Demoは docs/prereg/ に事前登録メトリクスを書いてから実装
- Demo完了時に git tag demo-N.M でフリーズ
