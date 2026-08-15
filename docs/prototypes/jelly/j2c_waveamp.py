"""M-J2c re-run on the WAVE-AMPLITUDE model (supersedes the geometric version)."""
import math, sys
sys.path.insert(0,'/home/claude/jelly')
from j2_wave_amplitude import swim

def rng_stream(seed):
    s=seed & 0xffffffff
    while True:
        s=(s*1664525+1013904223)&0xffffffff
        yield s/2**32

errs=[]
for seed in range(1000,1048):
    r=rng_stream(seed)
    cell=int(next(r)*16); g=0.75+next(r)*0.17; drag=0.05+next(r)*0.15
    h,d,_=swim(stims=((cell,0),), g=g, drag=drag, steps=120)
    exp=(math.degrees(2*math.pi*cell/16)+180)%360
    errs.append(min(abs(h-exp),360-abs(h-exp)))
print(f"M-J2c (wave-amplitude): mean {sum(errs)/48:.3f} deg / max {max(errs):.3f} deg over 48 seeds")
print(f"  -> {'PASS' if max(errs)<5 else 'FAIL'}")

print("\n多重刺激の比較（新旧モデル）")
for label,kw in [("同時 0&90", dict(stims=((0,0),(4,0)))),
                 ("0@t0, 90@t6", dict(stims=((0,0),(4,6)))),
                 ("0,90,180 同時", dict(stims=((0,0),(4,0),(8,0))))]:
    h,d,_=swim(**kw)
    print(f"  {label:14s} heading {h:6.1f}  dist {d:5.1f}")

print("\n単一ペースメーカーの持続遊泳（J3a）")
for cell in (0,4,8,12):
    h,d,_=swim(pace=cell, steps=400)
    exp=(math.degrees(2*math.pi*cell/16)+180)%360
    print(f"  pace=cell{cell:2d} ({math.degrees(2*math.pi*cell/16):5.1f}deg) -> heading {h:6.1f} (expect {exp:5.1f}) dist {d:6.1f}")
