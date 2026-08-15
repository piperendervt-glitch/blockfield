"""M-J2c: 48-seed quantitative relation (stimulus angle vs escape heading).
Seeds generate: stimulus cell, plus parameter jitter (g, drag) to test
robustness. Judgment: monotonic 1:1 mapping, offset 180 deg, across all seeds.
Bonus: two-stimulus superposition (emergent vector averaging?)."""
import math, sys
sys.path.insert(0, '/home/claude/jelly')
from excitable_ring import step

def swim(n=16, stims=((0,0),), r0=14, steps=120, impulse=1.0, drag=0.1, g=0.85):
    """stims: list of (cell, time). Returns escape heading (deg) and dist."""
    E=[0.0]*n; R=[0]*n
    pend = sorted(stims, key=lambda s: s[1])
    origin = pend[0][0]  # attenuation reference = first stimulus (see note)
    vx=vy=x=y=0.0
    for t in range(steps):
        for c,ts in pend:
            if ts==t and R[c]==0: E[c]=1.0; R[c]=r0
        E,R,fired = step(E,R,r0=r0)
        for i in fired:
            hops = min((i-origin)%n,(origin-i)%n)
            amp = impulse*(g**hops)
            a = 2*math.pi*i/n
            vx -= amp*math.cos(a); vy -= amp*math.sin(a)
        vx*=(1-drag); vy*=(1-drag); x+=vx; y+=vy
    return math.degrees(math.atan2(y,x))%360, math.hypot(x,y)

# --- M-J2c: 48 seeds, mulberry32-style LCG for reproducibility
def rng_stream(seed):
    s = seed & 0xffffffff
    while True:
        s = (s*1664525+1013904223) & 0xffffffff
        yield s / 2**32

print("M-J2c: 48 seeds — stim cell + jitter(g in [0.75,0.92], drag in [0.05,0.2])")
errs = []
for seed in range(1000, 1048):
    r = rng_stream(seed)
    cell = int(next(r)*16)
    g = 0.75 + next(r)*0.17
    drag = 0.05 + next(r)*0.15
    h, d = swim(stims=((cell,0),), g=g, drag=drag)
    exp = (math.degrees(2*math.pi*cell/16)+180) % 360
    err = min(abs(h-exp), 360-abs(h-exp))
    errs.append(err)
mx, avg = max(errs), sum(errs)/len(errs)
print(f"  escape vs expected-opposite: mean err {avg:.2f} deg, max err {mx:.2f} deg over 48 seeds")
print(f"  M-J2c: {'PASS (1:1 monotonic, offset 180deg, robust to parameter jitter)' if mx < 5 else 'FAIL'}")

# --- bonus: two simultaneous stimuli at 90 deg separation -> emergent averaging?
print("\n  two-stimulus superposition (cells 0 and 4 = 0deg & 90deg, same time):")
h, d = swim(stims=((0,0),(4,0)))
print(f"    escape heading = {h:.1f} deg (vector-average prediction: 225.0), dist {d:.1f}")
