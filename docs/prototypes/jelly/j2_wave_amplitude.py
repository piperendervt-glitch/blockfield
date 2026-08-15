"""Fix: amplitude is WAVE STATE, not geometry. Each firing cell carries
amplitude A = max(neighbor firing amplitudes) * g; stimulus injects A=1.
Multi-stimulus superposition now physical. Then J3 preview: pacemaker
locomotion + stimulus interruption."""
import math

def step_amp(E, R, A, theta=0.5, e_max=1.0, delta=0.5, k=0.6, r0=14, g=0.85):
    n=len(E); nE=[0.0]*n; nR=[0]*n; nA=[0.0]*n; fired=[]
    for i in range(n):
        if R[i]>0: nR[i]=R[i]-1; continue
        inp=0.0; src_amp=0.0
        for j in (i-1,(i+1)%n):
            if E[j]>=e_max:
                inp+=k; src_amp=max(src_amp, A[j])
        e=E[i]*delta+inp
        if e>=theta:
            nE[i]=e_max; nR[i]=r0; nA[i]=src_amp*g; fired.append((i,src_amp*g))
        else:
            nE[i]=e
    return nE,nR,nA,fired

def swim(n=16, stims=((0,0),), pace=None, t_pace=40, steps=200, drag=0.1, g=0.85, r0=14, record=False):
    E=[0.0]*n; R=[0]*n; A=[0.0]*n
    vx=vy=x=y=0.0; traj=[]
    for t in range(steps):
        for c,ts in stims:
            if ts==t and R[c]==0: E[c]=1.0; A[c]=1.0; R[c]=r0
        if pace is not None and t % t_pace == 0 and R[pace]==0:
            E[pace]=1.0; A[pace]=1.0; R[pace]=r0
        E,R,A,fired = step_amp(E,R,A,r0=r0,g=g)
        for i,amp in fired:
            a=2*math.pi*i/n
            vx-=amp*math.cos(a); vy-=amp*math.sin(a)
        vx*=(1-drag); vy*=(1-drag); x+=vx; y+=vy
        if record: traj.append((t,x,y))
    return math.degrees(math.atan2(y,x))%360, math.hypot(x,y), traj

# 1) single-stimulus regression check (must still be exact)
errs=[]
for c in range(16):
    h,d,_=swim(stims=((c,0),))
    exp=(math.degrees(2*math.pi*c/16)+180)%360
    errs.append(min(abs(h-exp),360-abs(h-exp)))
print(f"wave-amplitude model, single stimulus: max err {max(errs):.2f} deg (16 positions)")

# 2) two-stimulus superposition, now physical
h,d,_=swim(stims=((0,0),(4,0)))
print(f"two stimuli 0&90deg simultaneous: heading {h:.1f} (vector-avg predicts 225.0), dist {d:.1f}")
h,d,_=swim(stims=((0,0),(4,6)))
print(f"two stimuli 0deg@t0, 90deg@t6:    heading {h:.1f} (later/nearer should pull harder)")

# 3) J3 preview: pacemaker at cell 8 -> sustained swim AWAY from 180deg (=toward 0deg)
h,d,traj=swim(pace=8, steps=400, record=True)
print(f"\nJ3a preview: pacemaker at cell 8 (180deg): heading {h:.1f} (expect 0.0), dist {d:.1f}")
print(f"  distance at t=100/200/400: {math.hypot(*[c for _,*c in [traj[99]]][0]):.1f} / "
      f"{math.hypot(traj[199][1],traj[199][2]):.1f} / {math.hypot(traj[399][1],traj[399][2]):.1f}  (linear growth = sustained locomotion)")

# 4) J3b preview: swimming + lateral stimulus at t=100 -> deflect then resume
h1,d1,tr1=swim(pace=8, steps=400, record=True)
h2,d2,tr2=swim(pace=8, stims=((4,100),), steps=400, record=True)
def head_at(tr,a,b):
    (t0,xa,ya),(t1,xb,yb)=tr[a],tr[b]
    return math.degrees(math.atan2(yb-ya,xb-xa))%360
print(f"\nJ3b preview: lateral poke (cell4=90deg) at t=100 during pacemaker swim:")
print(f"  heading t80-100 (before): {head_at(tr2,79,99):6.1f}   (undisturbed: {head_at(tr1,79,99):.1f})")
print(f"  heading t100-140 (during): {head_at(tr2,99,139):6.1f}  <- deflected away from 90deg")
print(f"  heading t300-400 (after):  {head_at(tr2,299,399):6.1f}  <- resumed pacemaker course")
