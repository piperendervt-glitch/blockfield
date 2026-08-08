# Demo 2 実機検証チェックリスト

対象: docs/prereg/demo2_metrics.md の M1/M2/M4/M5（M3 は EditModeテストで判定済み）。
前提: 装着1回・5分以内。**装着中のPC操作なし**。
パネル表示: `USE_SCENE / Planes / RayHit / Origin / AnchorSaved / Blocks /
Field / FPS / Seed / Gen / Tick / Plants / Animals / Last`

## 事前（PC、非装着）
- [ ] 1. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\deploy.ps1`
      （アンカーは保存済みのまま。復元→地形→シム開始まで自動）

## セッション（装着1回、5分以内）
- [ ] 2. 装着 → アプリ起動 → 原点復元 → 地形表示を確認
      （パネル `Origin: Restored`、`Tick` が1秒ごとに増える）
- [ ] 3. **M1（スポーン）**: そのまま地形を観察し、植物（小さい緑/黄の立方体）と
      動物（白=Sheep / ピンク=Pig の胴＋頭）が現れるのを待つ
      → 判定: **開始2分以内に両方視認** — 合格 / 不合格（メモ:　　　　）
- [ ] 4. **M2（徘徊）**: 動物を1匹見つけて追い、起伏（高低差1）を
      昇降する場面を確認（0.3秒補間でスライド移動する）
      → 判定: **1回でも確認できれば合格** — 合格 / 不合格（メモ:　　　　）
- [ ] 5. **M4（視認性・記録のみ）**: 面明度差での起伏の見やすさ
      （Demo 1 の「のっぺり」比の主観メモ:　　　　　　　　　）
- [ ] 6. **M5（性能・記録のみ）**: パネルのFPS値を読む
      （Plants/Animals が上限近く: 植物200/動物20 時点が望ましい）
      → FPS:（　　　）/ その時の Plants:（　　）Animals:（　　）
- [ ] 7. 外して報告

## 結果まとめ
- M1: 合格 / 不合格
- M2: 合格 / 不合格
- M3: **合格**（EditModeテスト M3_SameSeedAndTicks_ProduceIdenticalWorldContentHash パス）
- M4 メモ: （　　　　　　　　　）
- M5 FPS: （　　　）
- Go/No-Go（M1 かつ M2 かつ M3 でGo）: **Go / No-Go**
- No-Go の場合の原因1行メモ: （　　　　　　　　　　　　）
- 完了時: `git tag demo-2.1` でフリーズ

## トラブル時
- アプリがすぐ閉じる: HMDスリープ。装着してから `scripts\restart_app.ps1`
- 動物が湧かない: パネル `Animals` が0のままなら suitability 1.0 セル不足の
  可能性。Aボタンでシード切替して別地形で再確認
- ログ確認（PC）: `adb logcat -s Unity` で `[TerrainField]` タグを追う
