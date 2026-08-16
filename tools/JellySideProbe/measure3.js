"use strict";
// jelly_2 K1（定義変更後）。prereg 追記5 A5.2〜A5.5 で計測前に登録済み。
//   取るもの: 体長あたりの回頭角 [度/体長]
//   片側: ペースメーカーを止め t%230===10 で fireNerve(0,t) のみ
//   両側: 既定のペースメーカー（対照・直進）
//   区間: 5拍動を捨てて5拍動。前進は経路長（旋回で曲がるため正味変位ではない）
//
// 不合格条件（A5.5、先に登録済み）:
//   1. 片側で |回頭角| >= 1.0 度/拍動
//   2. 拍動ごとの回頭角の変動係数 < 0.30
//   3. 両側の回頭が片側の 20% 未満
//   4. 2つのビューポートで比が 25% 以内で一致（呼び出し側で比較）

const sim = require('./sim.js');

const T = 230, DISCARD = 5, MEASURE = 5;
const BELL_PX = 2 * sim.Rb;
const DEG = 180 / Math.PI;
const mean = a => a.reduce((x, y) => x + y, 0) / a.length;
const sd = a => Math.sqrt(mean(a.map(x => (x - mean(a)) ** 2)));

/**
 * @param {boolean} oneSided 片側発火なら true
 * 位相は両条件で同一（t%230===10）。変えるのは片側にするという1点だけ。
 */
function run(oneSided) {
  sim.reset();
  sim.world.ink = false;
  if (oneSided) sim.world.pace = false;

  let tick = 0;
  const stepOnce = () => {
    // 片側条件では、ペースメーカーと同じ位相で端の1セグメントだけ叩く
    if (oneSided && tick % T === 10) sim.fireNerve(0, sim.world.t);
    sim.step();
    tick++;
  };

  for (let i = 0; i < DISCARD * T; i++) stepOnce();

  const turns = [], paths = [];
  for (let p = 0; p < MEASURE; p++) {
    const tilt0 = sim.bell.tilt;
    let px = sim.bell.bx, py = sim.bell.by, path = 0;
    for (let i = 0; i < T; i++) {
      stepOnce();
      path += Math.hypot(sim.bell.bx - px, sim.bell.by - py);
      px = sim.bell.bx; py = sim.bell.by;
    }
    turns.push((sim.bell.tilt - tilt0) * DEG);
    paths.push(path);
  }
  return { turns, paths };
}

const VW = process.env.VW || 390, VH = process.env.VH || 844;
console.log(`=== ビューポート W=${VW} H=${VH} ===`);
console.log(`Rb=${sim.Rb.toFixed(1)}px  傘の直径=${BELL_PX.toFixed(1)}px`);
console.log();

const one = run(true);
const both = run(false);

console.log('片側発火（旋回）');
one.turns.forEach((t, i) => console.log(
  `  拍動 ${i + 1}: 回頭 ${t.toFixed(3)}度  経路長 ${one.paths[i].toFixed(2)}px ` +
  `(${(one.paths[i] / BELL_PX).toFixed(4)} 体長)`));
console.log('両側発火（対照・直進）');
both.turns.forEach((t, i) => console.log(
  `  拍動 ${i + 1}: 回頭 ${t.toFixed(3)}度  経路長 ${both.paths[i].toFixed(2)}px`));
console.log();

const turnOne = mean(one.turns.map(Math.abs));
const turnBoth = mean(both.turns.map(Math.abs));
const bodyLenOne = mean(one.paths) / BELL_PX;
const ratio = turnOne / bodyLenOne;
const cv = sd(one.turns.map(Math.abs)) / turnOne;

console.log('--- 判定（不合格条件は追記5 A5.5 で登録済み）---');
const c1 = turnOne >= 1.0;
const c2 = cv < 0.30;
const c3 = turnBoth < 0.20 * turnOne;
console.log(`1. 片側 |回頭| >= 1.0 度/拍動 : ${turnOne.toFixed(3)} 度  → ${c1 ? '合格' : '**不合格**'}`);
console.log(`2. 変動係数 < 0.30            : ${cv.toFixed(4)}  → ${c2 ? '合格' : '**不合格**'}`);
console.log(`3. 両側 < 片側の 20%          : ${turnBoth.toFixed(3)} 対 ` +
  `${(0.20 * turnOne).toFixed(3)} 度  → ${c3 ? '合格' : '**不合格**'}`);
console.log();
console.log(`**体長あたりの回頭角 = ${ratio.toFixed(3)} 度/体長**  ` +
  `(回頭 ${turnOne.toFixed(3)}度 ÷ ${bodyLenOne.toFixed(4)}体長)`);
console.log(`RESULT ${VW} ${ratio.toFixed(6)} ${c1 && c2 && c3 ? 'PASS' : 'FAIL'}`);
