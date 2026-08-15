"""M-J1b: reentry boundary map.
Unidirectional initiation: stimulate cell 0 with cell n-1 refractory,
so only the +direction wave survives. If ring transit (n steps) exceeds
recovery, the wave re-enters its own wake and circulates forever."""
import sys
sys.path.insert(0, '/home/claude/jelly')
from excitable_ring import step

def run_uni(n, r0, steps=400, k=0.6, delta=0.5):
    E = [0.0]*n; R = [0]*n
    E[0] = 1.0; R[0] = r0
    R[n-1] = r0 + 1          # block backward wave -> unidirectional
    total_fired = 0
    for t in range(1, steps+1):
        E, R, fired = step(E, R, r0=r0, k=k, delta=delta)
        total_fired += len(fired)
        if not fired and all(e < 0.01 for e in E) and all(r == 0 for r in R):
            return 'died', t
    return 'REENTRY', steps

print("M-J1b: reentry map — rows: ring size N, cols: refractory R_0")
r0_vals = list(range(2, 22, 2))
print("  N\\R0 " + " ".join(f"{r:>3d}" for r in r0_vals))
for n in (8, 12, 16, 24, 32):
    row = []
    for r0 in r0_vals:
        fate, t = run_uni(n, r0)
        row.append(' R ' if fate == 'REENTRY' else ' . ')
    print(f"  {n:>3d}  " + " ".join(row))
print("\n  R = perpetual rotation (reentry), . = wave dies")
print("  Theory: reentry iff transit time (N steps) > R_0 + recovery(~1)")

# Verify the boundary precisely for N=16
print("\n  N=16 boundary detail:")
for r0 in range(12, 18):
    fate, t = run_uni(16, r0)
    print(f"    R_0={r0}: {fate} (t={t})")
