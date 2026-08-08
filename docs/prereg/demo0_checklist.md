# Demo 0 実機検証チェックリスト（v3）

対象: docs/prereg/demo0_metrics.md の M1〜M3。
前提: HMD連続稼働は最小限にする。**装着中のPC操作はなし**（PC操作はすべて非装着時）。
HMD内のデバッグパネル（カメラ前下方の黒パネル）で状態を確認できる:
`USE_SCENE / Planes / RayHit / Origin / AnchorSaved / Voxels / FPS / Last`

※M1の判定方式は「テープ」から「机の角」に変更済み（demo0_metrics.md の変更記録参照）。

## 実施済み（2026-08-08 セッション、304f7cd ビルド）
- [x] 権限フロー: OK（USE_SCENE ダイアログ→許可→パネル `USE_SCENE: OK`）
- [x] 平面検出: **Planes 11枚**
- [x] レティクル表示: OK（緑リング、`RayHit: Y`）
- [x] 原点確定: OK（`Origin: Placed` → `AnchorSaved: Y`）
- [x] M2 計測完了: **全水準でFPS低下なし**
  - 1万個: 72 FPS / 2万個: 72 FPS / 4万個: 72 FPS / **8万個: 72 FPS**
- [ ] M3: **未実施**（手順の説明不足のため次セッションで実施）
- [ ] M1: 未実施（テープ方式を廃止し「机の角」方式に変更したため、セッションCでやり直し）

## セッションC（残作業）
- [ ] 1. PC: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\reset_anchor.ps1`
      → `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart_app.ps1`
      ※pm clear のため**権限ダイアログが再度出る**
- [ ] 2. 装着: 権限を許可 → **机の「角」**にレティクルを合わせ、
      橋（+Z）が**机の縁の外へ突き出す向き**でトリガー確定
      （赤箱＝角にぴったり重なる。パネル `Origin: Placed` → `AnchorSaved: Y`）
- [ ] 3. M3判定: しゃがんで**頭を天板より低く**し、橋の空中部分が
      机に隠れて見えなくなるか確認（エッジのちらつきは許容）
      → 結果: 合格 / 不合格（メモ:　　　　　　　）
      → 実在感メモ（BlockVerse比）:（　　　　　　　）
- [ ] 4. 外す → PC: restart_app.ps1
- [ ] 5. 装着: パネル `Origin: Restored` を待ち、赤箱が**机の角に重なったまま**か
      （ズレ4cm以内）→ M1-1回目: 合格 / 不合格（メモ:　　　　　　　）
- [ ] 6. 外す → PC: restart_app.ps1
- [ ] 7. 装着: 同判定 → M1-2回目: 合格 / 不合格（メモ:　　　　　　　）→ 外して終了

## 結果まとめ
- M1（2回とも合格で合格）: 合格 / 不合格
- M2 FPS: 1万（72）/ 2万（72）/ 4万（72）/ 8万（72）、カクつきを感じた水準:（なし）
- M3: 合格 / 不合格
- Go/No-Go（M1合格 かつ M3合格でGo）: **Go / No-Go**
- No-Go の場合の原因1行メモ:（　　　　　　　　　　　　）
- 完了時: `git tag demo-0.1` でフリーズ（demo0_metrics.md 参照）

## トラブル時
- アプリがすぐ閉じる/起動しない: HMDがスリープしている。装着してから restart_app.ps1
- パネルの `Last:` 行に直近イベント（origin placed / anchor saved / 失敗など）が出る
- ログ確認（PC）: `adb logcat -s Unity` で `[DioramaOrigin]` 等のタグを追う
