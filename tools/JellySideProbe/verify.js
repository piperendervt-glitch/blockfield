"use strict";
// ハーネスがプロトタイプと同じ振る舞いを出すかの確認（本計測の前、jelly_2 追記1 A1.8）。
// 見るのは2点だけ:
//   - 拍動周期が 230 ティックであること
//   - ペースメーカーONでの定常速度が得られること（発散も停止もしない）

const sim = require('./sim.js');

console.log(`ビューポート W=${process.env.VW || 390} H=${process.env.VH || 844}`);
console.log(`格子 ${sim.GW}x${sim.GH} (CS=${sim.CS})  傘 Rb=${sim.Rb.toFixed(1)}px ` +
  `Hb=${sim.Hb.toFixed(1)}px  セグメント M=${sim.M}`);
console.log(`傘は格子 ${(2 * sim.Rb / sim.CS).toFixed(1)} 個ぶんの幅`);
console.log();

// --- 1. 拍動周期 ---
sim.reset();
const fireTicks = [];
let prevR = 0;
for (let t = 0; t < 1200; t++) {
  sim.step();
  // ペースメーカーは端のセグメント(0, M-1)を叩く。不応期が満タンに戻った瞬間を拍動とみなす
  // fireNerve が R=46 を置いた直後、同じ step 内で 1 減るので発火ティックでは 45
  const r = sim.bell.R[0];
  if (prevR === 0 && r >= 45) fireTicks.push(t);
  prevR = r;
}
const periods = fireTicks.slice(1).map((v, i) => v - fireTicks[i]);
console.log(`拍動の検出: ${fireTicks.length} 回  周期 = ${periods.join(', ')}`);
// 【空振りで OK を出さない】検出が 0 件のとき every() は true を返す。
// 最初に書いたときこれで「OK」と表示され、周期を確かめられていなかった
if (periods.length < 3) {
  console.log(`  → NG: 拍動を ${fireTicks.length} 回しか検出できていない（検出器の誤り）`);
} else {
  console.log(`  → 期待 230 ティック: ${periods.every(p => p === 230) ? 'OK' : 'NG'}`);
}
console.log();

// --- 2. 定常速度 ---
sim.reset();
const speedAt = [];
for (let t = 0; t < 230 * 12; t++) {
  sim.step();
  if ((t + 1) % 230 === 0) {
    const b = sim.bell;
    speedAt.push(Math.hypot(b.vx, b.vy));
  }
}
console.log('拍動ごとの速さ (px/tick):');
speedAt.forEach((s, i) => console.log(`  拍動 ${String(i + 1).padStart(2)}: ${s.toFixed(4)}`));

const late = speedAt.slice(-5);
const mean = late.reduce((a, b) => a + b, 0) / late.length;
const spread = Math.max(...late) - Math.min(...late);
console.log(`  後半5拍動の平均 ${mean.toFixed(4)} / 振れ幅 ${spread.toFixed(4)}`);
console.log(`  → 発散していない: ${Number.isFinite(mean) && mean < 10 ? 'OK' : 'NG'}`);
console.log(`  → 停止していない: ${mean > 1e-4 ? 'OK' : 'NG'}`);

const b = sim.bell;
console.log(`  最終位置 (${b.bx.toFixed(1)}, ${b.by.toFixed(1)}) 傾き ${b.tilt.toFixed(4)}`);
