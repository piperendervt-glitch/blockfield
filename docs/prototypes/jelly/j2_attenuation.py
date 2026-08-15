"""J2 refinement: add amplitude attenuation with propagation distance
(real nerve nets attenuate). Impulse at cell i scales by g^hops(i).
Prediction: stimulated side contracts harder -> escape AWAY from stimulus."""
import math, sys
sys.path.insert(0, '/home/claude/jelly')
from excitable_ring import step

def swim(n=16, stim_cell=0, r0=4, steps=80, impulse=1.0, drag=0.1, g=0.85):
    E=[0.0]*n; R=[0]*n
    E[stim_cell]=1.0; R[stim_cell]=r0
    vx=vy=x=y=0.0
    for t in range(steps):
        E,R,fired = step(E,R,r0=r0)
        for i in fired:
            hops = min((i-stim_cell)%n, (stim_cell-i)%n)
            amp = impulse*(g**hops)
            a = 2*math.pi*i/n
            vx -= amp*math.cos(a); vy -= amp*math.sin(a)
        vx*=(1-drag); vy*=(1-drag); x+=vx; y+=vy
    return math.degrees(math.atan2(y,x))%360, math.hypot(x,y)

print("J2 with attenuation g=0.85 (impulse decays per hop):")
print("  stim_angle  escape_heading  expected(opposite)  err")
ok=True
for c in (0,2,4,6,8,11,14):
    sa=math.degrees(2*math.pi*c/16)%360
    h,d=swim(stim_cell=c)
    exp=(sa+180)%360
    err=min(abs(h-exp),360-abs(h-exp)); ok &= err<5
    print(f"    {sa:6.1f}      {h:6.1f}          {exp:6.1f}       {err:.2f}  (dist {d:.1f})")
print(f"  -> escape AWAY from stimulus: {'PASS' if ok else 'FAIL'}")

print("\n  sign flip boundary: sweep g (attenuation per hop)")
for g in (1.0, 0.95, 0.92, 0.90, 0.88, 0.85, 0.7):
    h,d = swim(stim_cell=0, g=g)
    side = 'TOWARD' if min(h,360-h)<90 else 'AWAY'
    print(f"    g={g:.2f}: heading={h:6.1f} dist={d:5.2f} -> {side}")
