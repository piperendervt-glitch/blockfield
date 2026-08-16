# JellySideProbe — jelly_side.html の内部量を測る（jelly_2 K1）

`docs/prototypes/jelly/jelly_side.html` の物理部分（240行中 50〜186行）を
そのまま node へ切り出した無描画ハーネス。

- `sim.js` — **物理のコードは1文字も変えていない**。変えたのは `W`/`H` の
  与え方（環境変数 `VW`/`VH`）と、`reset()` が `world` も戻すことだけ
- `verify.js` — 本計測の前の確認（拍動周期 230 ティック、定常遊泳が
  発散も停止もしないこと）
- `measure.js` — prereg 追記1 A1.2 で登録した手順の実行

```
cd tools/JellySideProbe
VW=390  VH=844 node verify.js
VW=390  VH=844 node measure.js     # 登録ビューポート
VW=1200 VH=800 node measure.js     # 頑健性チェック
```

結果は `docs/prereg/jelly_2_metrics.md` の追記2 を参照。
**M-K1a / M-K1b は不合格（K1 保留）。**
