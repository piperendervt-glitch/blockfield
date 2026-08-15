"""J1 prototype: ExcitableField on a ring (16 cells).
Prereg: jelly_1_metrics.md — M-J1a (propagation/annihilation),
M-J1b (reentry boundary map), M-J1c (determinism).
Update: synchronous double-buffer (see note on prereg amendment).
"""
import hashlib

def step(E, R, theta=0.5, e_max=1.0, delta=0.5, k=0.6, r0=4):
    """One synchronous update. Returns (E', R', fired_indices)."""
    n = len(E)
    newE = [0.0]*n; newR = [0]*n; fired = []
    for i in range(n):
        if R[i] > 0:                      # refractory: cannot excite
            newR[i] = R[i]-1; newE[i] = 0.0
            continue
        inp = 0.0
        for j in (i-1, (i+1) % n):        # ring neighbors
            if E[j] >= e_max:             # neighbor fired last step
                inp += k
        e = E[i]*delta + inp              # decay + input
        if e >= theta:
            newE[i] = e_max; newR[i] = r0; fired.append(i)
        else:
            newE[i] = e; newR[i] = 0
    return newE, newR, fired

def run(n=16, stim=(0,), steps=60, **kw):
    E = [0.0]*n; R = [0]*n; log = []
    for s in stim: E[s] = 1.0; R[s] = kw.get('r0', 4)
    log.append((0, list(stim)))
    for t in range(1, steps+1):
        E, R, fired = step(E, R, **kw)
        log.append((t, fired))
        if not fired and all(e < 0.01 for e in E) and all(r == 0 for r in R):
            return log, t, 'died'
    return log, steps, 'alive'

def state_hash(log):
    return hashlib.sha256(str(log).encode()).hexdigest()[:16]

# ---- M-J1a: stimulate cell 0, expect bidirectional waves annihilating at antipode (cell 8)
log, t_end, fate = run(n=16, stim=(0,), steps=60)
print("M-J1a: stimulate cell 0 on 16-ring")
for t, fired in log[:14]:
    print(f"  t={t:2d} fired={fired}")
print(f"  -> fate={fate} at t={t_end}")
antipode_fires = [t for t, f in log if 8 in f]
print(f"  cell 8 (antipode) fired at t={antipode_fires}")

# ---- M-J1c: determinism
h1 = state_hash(run(n=16, stim=(3,), steps=60)[0])
h2 = state_hash(run(n=16, stim=(3,), steps=60)[0])
print(f"\nM-J1c: determinism hash1={h1} hash2={h2} match={h1==h2}")
