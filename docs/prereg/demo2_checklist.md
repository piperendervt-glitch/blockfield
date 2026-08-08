# Demo 2 実機検証チェックリスト

対象: docs/prereg/demo2_metrics.md の M1/M2/M4/M5（M3 は EditModeテストで判定済み）。
前提: 装着1回・5分以内。**装着中のPC操作なし**。
パネル表示: `USE_SCENE / Planes / RayHit / Origin / AnchorSaved / Blocks /
Field / FPS / Seed / Gen / Tick / Plants / Animals / Last`

## 実施結果（2026-08-08、e08ff1d ビルド、装着1回）
- [x] 1. デプロイ → 2. 装着・復元・地形表示・Tick進行を確認
- [x] 3. **M1（スポーン）: 合格** — 開始2分以内に植物（緑/黄）と動物（白/ピンク）の両方を視認
- [x] 4. **M2（徘徊）: 合格** — 段差1ブロックの昇降場面を確認（補間移動も正常）
- [x] 5. M4 記録（下記）
- [x] 6. M5 記録（下記）

## 結果まとめ（確定）
- M1: **合格**
- M2: **合格**
- M3: **合格**（EditModeテスト M3_SameSeedAndTicks_ProduceIdenticalWorldContentHash パス。
  エディタでの Reset→Simulate 100 ticks ×2回の配置一致も目視確認済み）
- M4 メモ: 面明度差により Demo 1 比で起伏が明確に見やすくなった
  （エディタのオン/オフ比較でも確認）。
  ユーザー要望: 将来的にもう少しリアルな陰影を（パフォーマンス影響のない範囲で）
- M5: **72FPS維持**、問題なし
- Go/No-Go（M1 かつ M2 かつ M3 でGo）: **Go** → `git tag demo-2.1` でフリーズ

## 次への課題（Demo 3 の視認性改善タスク）
- (a) エンティティ（植物・動物）に面明度差が未適用（地形メッシャーのみ）
- (b) 頂点AO（凹角の暗さの焼き込み）導入

## トラブル時
- アプリがすぐ閉じる: HMDスリープ。装着してから `scripts\restart_app.ps1`
- 動物が湧かない: パネル `Animals` が0のままなら suitability 1.0 セル不足の
  可能性。Aボタンでシード切替して別地形で再確認
- ログ確認（PC）: `adb logcat -s Unity` で `[TerrainField]` タグを追う
