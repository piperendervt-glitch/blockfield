"""M-F prototype: does an ambient current create selection pressure for coloniality?

Prereg (declared before running):
  M-F1  control (U=0) -> link gene stays low  (reproduces viewer's negative result)
  M-F2  U>0 -> final mean link significantly above control (paired, 8 seeds)
  M-F3  determinism: identical seed -> identical hash

World laws (designed):
  - current U in +x, food source band localized upstream, torus in x
  - colony of n: thrust ~ n, drag coeff ~ n^(2/3)  => cruise speed ~ n^(1/6)
  - tug-of-war: members push along own heading; net thrust = |vector mean| * n
  - metabolic discount and nutrient sharing for colonies (as in viewer)
Organism genes (evolved only, never hand-set): link, period
"""
import math, hashlib, random

W, H = 200.0, 100.0
FOOD_BAND = 0.25          # source occupies x in [0, 0.25W]
DT = 1.0

class Ind:
    __slots__ = ('x','y','vx','vy','th','e','g','phase','colony')
    def __init__(self, x, y, g, rng):
        self.x, self.y = x, y
        self.vx = self.vy = 0.0
        self.th = rng.random()*6.2832
        self.e = 1.0
        self.g = g
        self.phase = rng.random()
        self.colony = None

class Colony:
    __slots__ = ('members',)
    def __init__(self, members): self.members = members

def mutate(g, rng, s=0.04):
    return {'link': min(1.0, max(0.0, g['link'] + rng.gauss(0, s))),
            'period': min(60.0, max(6.0, g['period'] + rng.gauss(0, 1.5)))}

def run(seed, U, ticks=8000, cap=500, record=False, rigid=False):
    rng = random.Random(seed)
    food = [1.0]*40                       # coarse 1D food field along x (40 bins)
    pop = []
    for _ in range(40):
        pop.append(Ind(rng.random()*W, rng.random()*H,
                       {'link': 0.25, 'period': 20.0}, rng))
    colonies = []
    log = []

    for t in range(ticks):
        # --- food field: fixed upstream source, decay everywhere, advection by current
        for b in range(40):
            xb = (b+0.5)/40
            src = 0.045 if xb < FOOD_BAND else 0.0
            food[b] += src - 0.020*food[b]
            if food[b] < 0.0: food[b] = 0.0
        if U > 0:                          # advect food downstream (fractional mix)
            a = min(0.9, U*DT/(W/40))
            food = [(1-a)*food[b] + a*food[b-1] for b in range(40)]

        # --- group individuals by colony
        groups = {}
        for ind in pop:
            key = id(ind.colony) if ind.colony else id(ind)
            groups.setdefault(key, []).append(ind)

        newborn = []
        for members in groups.values():
            n = len(members)
            # net thrust direction: vector mean of member headings (tug-of-war)
            if rigid and n > 1:
                # rigid attachment: members share a common body axis
                # (bending stiffness -> aligned nectophores, as in siphonophores)
                bx = sum(math.cos(m.th) for m in members)
                by = sum(math.sin(m.th) for m in members)
                ang = math.atan2(by, bx)
                for m in members: m.th = ang
            sx = sum(math.cos(m.th) for m in members)
            sy = sum(math.sin(m.th) for m in members)
            mag = math.hypot(sx, sy)
            if mag > 1e-9:
                ux, uy = sx/mag, sy/mag
            else:
                ux, uy = 0.0, 0.0
            align = mag/n                       # 1.0 if perfectly aligned
            # pulsing: each member contributes when its phase wraps
            pulse = 0.0
            for m in members:
                m.phase += 1.0/max(6.0, m.g['period'])
                if m.phase >= 1.0:
                    m.phase -= 1.0
                    pulse += 1.0
            thrust = 0.35*pulse*align           # ~n when aligned, cancels when not
            drag_c = 0.06*(n**(2.0/3.0))
            # colony state = centroid
            cx = sum(m.x for m in members)/n
            cy = sum(m.y for m in members)/n
            vx = sum(m.vx for m in members)/n
            vy = sum(m.vy for m in members)/n
            vx += thrust*ux/n; vy += thrust*uy/n
            rel_x = vx - U                       # velocity relative to water
            rel_y = vy
            sp = math.hypot(rel_x, rel_y)
            vx -= drag_c*rel_x*sp/n
            vy -= drag_c*rel_y*sp/n
            cx += vx*DT; cy += vy*DT
            cx %= W
            if cy < 0: cy = 0.0; vy = 0.0
            if cy > H: cy = H; vy = 0.0
            for m in members:
                m.x, m.y, m.vx, m.vy = cx, cy, vx, vy

            # --- feeding: intake from local food bin, split among members
            b = int(cx/W*40) % 40
            take = min(food[b], 0.020*n)
            food[b] -= take
            share = take/n                      # nutrient sharing (equal split)
            discount = 1.0 - 0.15*(1.0 - 1.0/n) # metabolic discount for colonies
            for m in members:
                cost = (0.0032 + 0.00025*(1.0/max(6.0, m.g['period']))*60)*discount
                m.e += share*3.0 - cost
            # swimming cost charged to colony
            for m in members:
                m.e -= 0.0009*thrust/n

            # --- klinokinesis: turn rate rises in poor conditions.
            # No gradient is computed and no direction is chosen anywhere;
            # only the MAGNITUDE of random turning depends on local food.
            sigma = 0.03 + 0.55*(1.0 - min(1.0, food[b]/0.8))
            for m in members:
                m.th += rng.gauss(0, sigma)

            # --- reproduction / death
            for m in list(members):
                if m.e > 2.0 and len(pop)+len(newborn) < cap:
                    m.e *= 0.5
                    child = Ind(m.x, m.y, mutate(m.g, rng), rng)
                    child.e = m.e
                    if rng.random() < m.g['link'] and n < 8:
                        if m.colony is None:
                            c = Colony([m]); m.colony = c
                        m.colony.members.append(child)
                        child.colony = m.colony
                    newborn.append(child)

        pop.extend(newborn)
        dead = [m for m in pop if m.e <= 0.0]
        for m in dead:
            if m.colony:
                try: m.colony.members.remove(m)
                except ValueError: pass
                if len(m.colony.members) <= 1:
                    for r in m.colony.members: r.colony = None
        pop = [m for m in pop if m.e > 0.0]
        if not pop:
            return None, log

        if record and t % 500 == 0:
            ml = sum(m.g['link'] for m in pop)/len(pop)
            log.append((t, len(pop), round(ml, 3)))

    ml = sum(m.g['link'] for m in pop)/len(pop)
    mn = sum(1 for m in pop if m.colony)/len(pop)
    return (ml, len(pop), mn), log

if __name__ == '__main__':
    print("smoke test: seed 1, U=0")
    r, log = run(1, 0.0, ticks=3000, record=True)
    print("  ", r)
    for row in log[:8]: print("   ", row)
    print("smoke test: seed 1, U=0.35")
    r, log = run(1, 0.35, ticks=3000, record=True)
    print("  ", r)
    for row in log[:8]: print("   ", row)
