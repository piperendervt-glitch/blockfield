# blockfield 開発規約

## アーキテクチャ
- Assets/Scripts/SimCore は UnityEngine 非依存（asmdefで強制済み）。
  シミュレーションロジックは必ずここに書く
- シーン/プレハブの変更は Editor スクリプト経由でコード化する。
  GUI手作業の結果だけをコミットしない
- RNGは SimCore/Rng の決定論実装のみ使用。System.Random / UnityEngine.Random 禁止
- 生態系から地形を書き換えるコードは VoxelGrid.TrySetBlockEcology を必ず使う
  （Player 出所ブロックは生態系から不変 — 固定レイヤー原則）
- MR合成の制約: アルファ<1の描画はパススルー（現実映像）と
  合成される。半透明表現は原則使わず、スケール・ワイヤー
  フレーム・明度で代替する
- 設計の終着点は docs/design/stigmergy_vision.md、
  全体計画は docs/design/roadmap.md。個別Demoの実装判断は
  これらと矛盾しないこと（特に: 場のデータ構造の統一、
  決定論 f(シード, イベントログ)、SimCore の Quest/PC 共用）

## テスト
- 実行: scripts/run_tests.ps1（EditMode、batchmode）
- Unity Editorが開いているとCLIテストは失敗する。Temp/UnityLockfile を確認すること
- pre-pushゲートあり。全テストパスなしにpushしない

## 実機テスト運用
- **エディタ確認から実機セッションへ移る際は、必ず
  build_quest.ps1 → deploy.ps1 を実行してから
  capture_session.ps1 をアームする。** 実機には前回ビルドの APK が
  入ったままのため、コード修正後にデプロイを省くと古い挙動を
  判定してしまう（2回発生）
- テスト手順は事前に固定し、プレイ前にすべて提示する
- ユーザーのHMD装着中はメッセージでの指示・質問・依頼をしない
  （ユーザーは画面を見られない）。ログ監視と記録に徹し、
  報告はユーザーがHMDを外して発言してから行う
- 装着中はMonitorの生存報告も出力しない（「記録継続中」等の
  状態報告の繰り返しは不要）。サイレントに記録し、
  ユーザーの報告後にまとめて出力する
- ログ捕捉は scripts/capture_session.ps1 で行い、
  終了時に stop_capture.ps1 を実行する。Claude Code は
  セッション中に logcat を読み続けない（トークン浪費と
  出力ループの原因になる）。ユーザーの報告後に
  Logs/session_*.log を一度だけ読んでサマリーする
- 1セッションの装着時間は5分以内を目安に手順を設計する
- **パネルに表示する指標は必ずログにも出す。** 装着中のユーザーは
  数値を読み上げられず、セッション後に転記もできないため、
  パネルにしか無い数値は事実上取得できない。
  同じ漏れを2回起こしている（Demo 4.5 の M6 FPS、Demo 5a の密度指標）
- ログのタグを増やしたら capture_session.ps1 の捕捉対象を確認する
  （現在は Unity ログ全体を拾う設定なので通常は不要）

## ビルド
- scripts/build_quest.ps1 → scripts/deploy.ps1
- ProjectSettings/ の差分は必ずコミット（XR preloadedAssets消失対策）

## SBCE
- 各Demoは docs/prereg/ に事前登録メトリクスを書いてから実装
- Demo完了時に git tag demo-N.M でフリーズ
- **生態系の判定は最低48シードで行う。** 3〜5シードでは指標が
  数倍単位で振れ、効果とノイズを区別できない
  （Demo 8 第2段で墓場の植物密度比 1.166→0.523、
  Demo 5b で狼の全滅率 0/5→3/48 の訂正が発生している）
- 掃引（複数パラメータの格子探索）を行う際は条件数の掛け算に
  注意する。48シード×3000ティックは1条件34秒だが、
  3×3格子×2条件で10分を超える。掃引は必要最小限の格子に絞るか、
  並列化してから行うこと
