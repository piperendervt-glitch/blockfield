import sys, math
sys.path.insert(0, '/home/claude/flow')
from flow_colony import run
SEEDS=list(range(1,9)); T=8000
def d_paired(ds):
    n=len(ds); m=sum(ds)/n
    sd=math.sqrt(sum((x-m)**2 for x in ds)/(n-1))
    return m/sd if sd>1e-12 else 0.0
print("M-F4: does rigid alignment (stiff) rescue coloniality under flow?\n")
print("  seed  | flexible U=.35 | rigid U=.35 | rigid U=0 | diff(rigid-flex, flow)")
a,b,c,dd=[],[],[],[]
for s in SEEDS:
    r_f,_ = run(s,0.35,ticks=T,rigid=False)
    r_r,_ = run(s,0.35,ticks=T,rigid=True)
    r_r0,_= run(s,0.0, ticks=T,rigid=True)
    a.append(r_f[0]); b.append(r_r[0]); c.append(r_r0[0]); dd.append(r_r[0]-r_f[0])
    print(f"  {s:>4}  |     {r_f[0]:.3f} ({r_f[2]*100:4.1f}%) |   {r_r[0]:.3f} ({r_r[2]*100:4.1f}%) |   {r_r0[0]:.3f}   | {r_r[0]-r_f[0]:+.3f}")
print(f"\n  mean link: flexible+flow={sum(a)/8:.3f}  rigid+flow={sum(b)/8:.3f}  rigid+still={sum(c)/8:.3f}")
print(f"  sign consistency (rigid>flexible under flow): {sum(1 for x in dd if x>0)}/8")
print(f"  paired Cohen's d = {d_paired(dd):+.3f}")
print(f"\n  M-F4 (rigidity rescues link under flow): {'PASS' if d_paired(dd)>0.8 and sum(1 for x in dd if x>0)>=7 else 'FAIL'}")
