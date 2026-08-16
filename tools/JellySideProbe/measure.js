"use strict";
// jelly_2 K1: 抗力係数の逆算。手順は prereg 追記1 A1.2 で計測前に登録済み。
//   M-K1a 並進 τ: ペースメーカー停止・流体静定後に v0 を与え、|v| が 0.8v0→0.2v0 の区間で ln|v| を直線当てはめ
//   M-K1b 回転 τ: 同じ手順を om で
//   M-K1c 推力  : ペースメーカーON、5拍動(1150tick)を捨てて5拍動の平均速度
// 不合格条件: R² < 0.98 なら「1次減衰」が合っていないので K1 を保留する。

const sim = require('./sim.js');

const TICKS_PER_PULSE = 230;        // t%230===10 で拍動（プロトタイプが与える）
const SEC_PER_TICK = 1 / TICKS_PER_PULSE;   // 1拍動 ≡ 1.0 秒
const BELL_M = 0.15;                // 傘の直径 (m)。C# の BellDiameter
const M_PER_PX = BELL_M / (2 * sim.Rb);

/** ln y = a + b x の最小二乗と R²。*/
function fitLog(xs, ys) {
  const n = xs.length;
  const ly = ys.map(Math.log);
  const mx = xs.reduce((a, b) => a + b, 0) / n;
  const my = ly.reduce((a, b) => a + b, 0) / n;
  let sxy = 0, sxx = 0;
  for (let i = 0; i < n; i++) { sxy += (xs[i] - mx) * (ly[i] - my); sxx += (xs[i] - mx) ** 2; }
  const b = sxy / sxx, a = my - b * mx;
  let ssRes = 0, ssTot = 0;
  for (let i = 0; i < n; i++) {
    const pred = a + b * xs[i];
    ssRes += (ly[i] - pred) ** 2; ssTot += (ly[i] - my) ** 2;
  }
  return { slope: b, r2: 1 - ssRes / ssTot, n };
}

/** 流体を静定させる（ペースメーカー停止で放置）。 */
function settle(ticks) {
  sim.reset();
  sim.world.pace = false;
  sim.world.ink = false;
  for (let t = 0; t < ticks; t++) sim.step();
}

/** 惰行の減衰を測る。pick は速度スカラーを取り出す関数。 */
function coastDown(setV, pick, v0, label) {
  settle(400);
  setV(v0);
  const ts = [], vs = [];
  for (let t = 0; t < 4000; t++) {
    sim.step();
    const s = Math.abs(pick());
    ts.push(t); vs.push(s);
    if (s < 0.05 * v0) break;
  }
  // 登録した当てはめ区間: 0.8·v0 → 0.2·v0
  const hi = vs.findIndex(s => s <= 0.8 * v0);
  const lo = vs.findIndex(s => s <= 0.2 * v0);
  if (hi < 0 || lo < 0 || lo - hi < 5) {
    console.log(`  ${label}: **登録した当てはめ区間で標本が足りない**（hi=${hi} lo=${lo}）`);
    console.log(`    v/v0 の推移: ${vs.slice(0, 24).map((s, i) => `${i}:${(s / v0).toFixed(3)}`).join(' ')}`);
    return null;
  }
  // 【当てはめの前に形を見る】1次減衰かどうかは R² だけでなく曲線の形で判断する
  const shape = vs.slice(0, 24).map((s, i) => `${i}:${(s / v0).toFixed(3)}`).join(' ');
  const fit = fitLog(ts.slice(hi, lo + 1), vs.slice(hi, lo + 1));
  fit.shape = shape;
  const kPerTick = Math.exp(fit.slope);
  const tauTicks = -1 / fit.slope;
  return { ...fit, kPerTick, tauTicks, tauSec: tauTicks * SEC_PER_TICK, span: [hi, lo] };
}

console.log(`=== ビューポート W=${process.env.VW || 390} H=${process.env.VH || 844} ===`);
console.log(`格子 ${sim.GW}x${sim.GH}  Rb=${sim.Rb.toFixed(1)}px  ` +
  `1px = ${(M_PER_PX * 1000).toFixed(2)}mm  1tick = ${(SEC_PER_TICK * 1000).toFixed(2)}ms`);
console.log();

// --- M-K1a 並進 ---
const tr = coastDown(v => { sim.bell.vx = v; sim.bell.vy = 0; },
  () => Math.hypot(sim.bell.vx, sim.bell.vy), 1.0, 'M-K1a');
console.log('M-K1a 並進の抗力');
if (tr) {
  console.log(`  v/v0 の推移: ${tr.shape}`);
  console.log(`  当てはめ区間 tick ${tr.span[0]}..${tr.span[1]} (n=${tr.n})`);
  console.log(`  減衰 k = ${tr.kPerTick.toFixed(6)} /tick`);
  console.log(`  τ_trans = ${tr.tauTicks.toFixed(2)} tick = ${tr.tauSec.toFixed(4)} 秒`);
  console.log(`  R² = ${tr.r2.toFixed(5)}  → ${tr.r2 >= 0.98 ? '合格' : '**不合格（K1 を保留）**'}`);
}
console.log();

// --- M-K1b 回転 ---
const rot = coastDown(v => { sim.bell.om = v; }, () => sim.bell.om, 0.02, 'M-K1b');
console.log('M-K1b 回転の抗力');
if (rot) {
  console.log(`  ω/ω0 の推移: ${rot.shape}`);
  console.log(`  当てはめ区間 tick ${rot.span[0]}..${rot.span[1]} (n=${rot.n})`);
  console.log(`  減衰 k = ${rot.kPerTick.toFixed(6)} /tick`);
  console.log(`  τ_rot = ${rot.tauTicks.toFixed(2)} tick = ${rot.tauSec.toFixed(4)} 秒`);
  console.log(`  R² = ${rot.r2.toFixed(5)}  → ${rot.r2 >= 0.98 ? '合格' : '**不合格（K1 を保留）**'}`);
}
console.log();

// --- 無次元比（主たる成果物）---
if (tr && rot) {
  console.log(`**τ_rot / τ_trans = ${(rot.tauSec / tr.tauSec).toFixed(4)}**  ` +
    `(回転の抗力は並進の ${(tr.tauSec / rot.tauSec).toFixed(2)} 倍)`);
}
console.log();

// --- M-K1c 推力（定常遊泳） ---
sim.reset();
sim.world.ink = false;
for (let t = 0; t < 5 * TICKS_PER_PULSE; t++) sim.step();   // 5拍動を捨てる
const x0 = sim.bell.bx, y0 = sim.bell.by;
for (let t = 0; t < 5 * TICKS_PER_PULSE; t++) sim.step();   // 5拍動を平均
const dist = Math.hypot(sim.bell.bx - x0, sim.bell.by - y0);
const secs = 5 * TICKS_PER_PULSE * SEC_PER_TICK;
console.log('M-K1c 定常遊泳（推力と抗力の釣り合い）');
console.log(`  変位 ${dist.toFixed(2)}px / ${secs.toFixed(1)}秒`);
console.log(`  = ${(dist / (5 * TICKS_PER_PULSE)).toFixed(5)} px/tick`);
console.log(`  = **${(dist * M_PER_PX / secs).toFixed(4)} m/s**  （C# の現行目標 0.04 m/s）`);
console.log(`  傘の直径で規格化: ${(dist * M_PER_PX / secs / BELL_M).toFixed(4)} 体長/秒`);
