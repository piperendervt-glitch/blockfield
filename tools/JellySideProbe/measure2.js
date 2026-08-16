"use strict";
// jelly_2 K1（手順変更後）。prereg 追記3 A3.2 で再計測の前に登録済み。
//   M-K1a′ 実効抗力 k : 拍動 +60〜+200 ティックの惰行区間で ln|v| を直線当てはめ
//   M-K1c′ 推力積分 I : 同じ拍動で J の総和
//   M-K1d  往復の確認 : I と k から定常速度を予測し実測と比べる
//
// 不合格条件（追記3 A3.4、先に登録済み）:
//   1. 惰行区間の当てはめが R² >= 0.98 かつ単調減少
//   2. 拍動ごとの k のばらつきが変動係数 < 0.10
//   3. 往復の一致が 20% 以内
// いずれか外れたら K1 を保留する。値をひねり出さない。

const sim = require('./sim.js');

const T = 230;                       // 1拍動のティック数（プロトタイプが与える）
const SEC_PER_TICK = 1 / T;
const BELL_M = 0.15;
const M_PER_PX = BELL_M / (2 * sim.Rb);
const COAST_FROM = 60, COAST_TO = 200;   // 登録した惰行区間
const DISCARD_PULSES = 5, MEASURE_PULSES = 5;

function fitLog(xs, ys) {
  const n = xs.length, ly = ys.map(Math.log);
  const mx = xs.reduce((a, b) => a + b, 0) / n, my = ly.reduce((a, b) => a + b, 0) / n;
  let sxy = 0, sxx = 0;
  for (let i = 0; i < n; i++) { sxy += (xs[i] - mx) * (ly[i] - my); sxx += (xs[i] - mx) ** 2; }
  const b = sxy / sxx, a = my - b * mx;
  let ssRes = 0, ssTot = 0;
  for (let i = 0; i < n; i++) {
    ssRes += (ly[i] - (a + b * xs[i])) ** 2; ssTot += (ly[i] - my) ** 2;
  }
  return { slope: b, r2: ssTot > 0 ? 1 - ssRes / ssTot : 0 };
}
const mean = a => a.reduce((x, y) => x + y, 0) / a.length;
const sd = a => Math.sqrt(mean(a.map(x => (x - mean(a)) ** 2)));

console.log(`=== ビューポート W=${process.env.VW || 390} H=${process.env.VH || 844} ===`);
console.log(`Rb=${sim.Rb.toFixed(1)}px  傘=格子${(2 * sim.Rb / sim.CS).toFixed(0)}個ぶん  ` +
  `1px=${(M_PER_PX * 1000).toFixed(2)}mm  1tick=${(SEC_PER_TICK * 1000).toFixed(2)}ms`);
console.log(`惰行区間: 拍動 +${COAST_FROM}〜+${COAST_TO} ティック（登録済み）`);
console.log();

sim.reset();
sim.world.ink = false;
for (let t = 0; t < DISCARD_PULSES * T; t++) sim.step();   // 定常まで捨てる

const ks = [], r2s = [], impulses = [], monotone = [];
const x0 = sim.bell.bx, y0 = sim.bell.by;

for (let pulse = 0; pulse < MEASURE_PULSES; pulse++) {
  const vs = [], ts = [];
  let I = 0;
  for (let i = 0; i < T; i++) {
    sim.step();
    I += sim.world.lastJ;
    if (i >= COAST_FROM && i <= COAST_TO) {
      ts.push(i); vs.push(Math.hypot(sim.bell.vx, sim.bell.vy));
    }
  }
  const fit = fitLog(ts, vs);
  // 単調減少の確認（当てはめの良さとは別に見る。追記2 で単調ですらなかった）
  let drops = 0;
  for (let i = 1; i < vs.length; i++) if (vs[i] <= vs[i - 1]) drops++;
  const monoFrac = drops / (vs.length - 1);
  ks.push(Math.exp(fit.slope)); r2s.push(fit.r2); impulses.push(I); monotone.push(monoFrac);
  console.log(`  拍動 ${pulse + 1}: k=${Math.exp(fit.slope).toFixed(6)} /tick  ` +
    `R²=${fit.r2.toFixed(4)}  単調率=${(monoFrac * 100).toFixed(0)}%  ` +
    `推力積分 I=${I.toFixed(5)} px/tick  ` +
    `|v| ${vs[0].toFixed(4)}→${vs[vs.length - 1].toFixed(4)}`);
}

const dist = Math.hypot(sim.bell.bx - x0, sim.bell.by - y0);
const measuredMean = dist / (MEASURE_PULSES * T);       // px/tick

const kMean = mean(ks), kCV = sd(ks) / kMean;
const iMean = mean(impulses);
const predicted = iMean / (T * (1 - kMean));            // px/tick
const err = Math.abs(predicted - measuredMean) / measuredMean;

console.log();
console.log('--- 判定（不合格条件は追記3 A3.4 で登録済み）---');
const c1 = r2s.every(r => r >= 0.98) && monotone.every(m => m >= 0.98);
const c2 = kCV < 0.10;
const c3 = err <= 0.20;
console.log(`1. R² >= 0.98 かつ単調減少 : R² 最小 ${Math.min(...r2s).toFixed(4)} / ` +
  `単調率 最小 ${(Math.min(...monotone) * 100).toFixed(0)}%  → ${c1 ? '合格' : '**不合格**'}`);
console.log(`2. k の変動係数 < 0.10     : CV = ${kCV.toFixed(4)}  → ${c2 ? '合格' : '**不合格**'}`);
console.log(`3. 往復の一致 20% 以内     : 予測 ${predicted.toFixed(5)} 対 実測 ` +
  `${measuredMean.toFixed(5)} px/tick、ずれ ${(err * 100).toFixed(1)}%  → ${c3 ? '合格' : '**不合格**'}`);
console.log();

if (c1 && c2 && c3) {
  const tauTicks = -1 / Math.log(kMean);
  console.log('=== K1 の成果物 ===');
  console.log(`  実効抗力 k        = ${kMean.toFixed(6)} /tick`);
  console.log(`  τ_trans           = ${tauTicks.toFixed(2)} tick = ${(tauTicks * SEC_PER_TICK).toFixed(4)} 秒`);
  console.log(`  1拍動の推力積分 I = ${iMean.toFixed(5)} px/tick = ` +
    `${(iMean * M_PER_PX / SEC_PER_TICK).toFixed(4)} m/s`);
  console.log(`  定常遊泳          = ${(measuredMean * M_PER_PX / SEC_PER_TICK).toFixed(4)} m/s ` +
    `= ${(measuredMean * M_PER_PX / SEC_PER_TICK / BELL_M).toFixed(3)} 体長/秒`);
} else {
  console.log('=== K1 を保留する。値を出さない（追記3 A3.4 のとおり）===');
}
