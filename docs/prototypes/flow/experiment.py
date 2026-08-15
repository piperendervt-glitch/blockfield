"""M-F1/M-F2/M-F3 paired experiment."""
import sys, math, hashlib
sys.path.insert(0, '/home/claude/flow')
from flow_colony import run

SEEDS = list(range(1, 9))
TICKS = 8000

def cohen_d_paired(diffs):
    n = len(diffs); m = sum(diffs)/n
    sd = math.sqrt(sum((d-m)**2 for d in diffs)/(n-1)) if n > 1 else 0.0
    return m/sd if sd > 1e-12 else float('inf') if m else 0.0

print(f"M-F experiment: {len(SEEDS)} seeds x 2 conditions x {TICKS} ticks\n")
print("  seed    U=0 link  pop  col%   |  U=0.35 link  pop  col%   |  diff")
ctrl, flow, diffs = [], [], []
for s in SEEDS:
    r0, _ = run(s, 0.0,  ticks=TICKS)
    r1, _ = run(s, 0.35, ticks=TICKS)
    if r0 is None or r1 is None:
        print(f"  {s:>4}   EXTINCT"); continue
    l0, p0, c0 = r0; l1, p1, c1 = r1
    ctrl.append(l0); flow.append(l1); diffs.append(l1-l0)
    print(f"  {s:>4}      {l0:.3f}  {p0:>4} {c0*100:5.1f}%  |     {l1:.3f}  {p1:>4} {c1*100:5.1f}%  | {l1-l0:+.3f}")

m0 = sum(ctrl)/len(ctrl); m1 = sum(flow)/len(flow)
pos = sum(1 for d in diffs if d > 0)
print(f"\n  mean link  control={m0:.3f}  flow={m1:.3f}  delta={m1-m0:+.3f}")
print(f"  sign consistency: {pos}/{len(diffs)} seeds positive")
print(f"  paired Cohen's d = {cohen_d_paired(diffs):+.3f}")

print("\n  M-F1 (control keeps link low, i.e. no rise above founder 0.25):",
      "PASS" if m0 <= 0.25 else f"FAIL (control rose to {m0:.3f})")
print("  M-F2 (flow > control, d>0.8 and sign>=7/8):",
      "PASS" if (cohen_d_paired(diffs) > 0.8 and pos >= 7) else "FAIL")

h1 = hashlib.sha256(str(run(3, 0.35, ticks=1500)[0]).encode()).hexdigest()[:16]
h2 = hashlib.sha256(str(run(3, 0.35, ticks=1500)[0]).encode()).hexdigest()[:16]
print(f"  M-F3 (determinism): {h1} == {h2} -> {'PASS' if h1==h2 else 'FAIL'}")
