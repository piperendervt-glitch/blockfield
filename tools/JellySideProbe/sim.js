"use strict";
// jelly_side.html の物理部分をそのまま切り出した無描画ハーネス (jelly_2 K1)。
// 【方針】物理のコードは1文字も変えない。変えるのは W/H の与え方だけ。
// 書き換えると「プロトタイプの内部量」ではなくなるので、切り出しに徹する。
const W = Number(process.env.VW || 390), H = Number(process.env.VH || 844);
/* ---------- fluid grid (Stam stable fluids, collocated) ---------- */
const CS=6;
const GW=Math.floor(W/CS), GH=Math.floor(H/CS), NC=GW*GH;
let u=new Float32Array(NC), v=new Float32Array(NC),
    u2=new Float32Array(NC), v2=new Float32Array(NC),
    p=new Float32Array(NC), dv=new Float32Array(NC),
    dye=new Float32Array(NC), dye2=new Float32Array(NC);
const ix=(x,y)=>y*GW+x;
function sampleF(f,x,y){                    // bilinear, grid coords
  x=Math.max(0.5,Math.min(GW-1.5,x)); y=Math.max(0.5,Math.min(GH-1.5,y));
  const x0=x|0,y0=y|0,fx=x-x0,fy=y-y0;
  return f[ix(x0,y0)]*(1-fx)*(1-fy)+f[ix(x0+1,y0)]*fx*(1-fy)
       + f[ix(x0,y0+1)]*(1-fx)*fy+f[ix(x0+1,y0+1)]*fx*fy;
}
function addF(f,x,y,val){                    // bilinear spread
  x=Math.max(0.5,Math.min(GW-1.5,x)); y=Math.max(0.5,Math.min(GH-1.5,y));
  const x0=x|0,y0=y|0,fx=x-x0,fy=y-y0;
  f[ix(x0,y0)]+=val*(1-fx)*(1-fy); f[ix(x0+1,y0)]+=val*fx*(1-fy);
  f[ix(x0,y0+1)]+=val*(1-fx)*fy;   f[ix(x0+1,y0+1)]+=val*fx*fy;
}
function advect(dst,src,uu,vv,fade){
  for(let y=1;y<GH-1;y++)for(let x=1;x<GW-1;x++){
    const i=ix(x,y);
    dst[i]=sampleF(src, x-uu[i], y-vv[i])*fade;
  }
}
function project(){
  for(let y=1;y<GH-1;y++)for(let x=1;x<GW-1;x++){
    const i=ix(x,y);
    dv[i]=0.5*(u[ix(x+1,y)]-u[ix(x-1,y)]+v[ix(x,y+1)]-v[ix(x,y-1)]);
    p[i]=0;
  }
  for(let k=0;k<22;k++)
    for(let y=1;y<GH-1;y++)for(let x=1;x<GW-1;x++){
      const i=ix(x,y);
      p[i]=(p[ix(x-1,y)]+p[ix(x+1,y)]+p[ix(x,y-1)]+p[ix(x,y+1)]-dv[i])*0.25;
    }
  for(let y=1;y<GH-1;y++)for(let x=1;x<GW-1;x++){
    const i=ix(x,y);
    u[i]-=0.5*(p[ix(x+1,y)]-p[ix(x-1,y)]);
    v[i]-=0.5*(p[ix(x,y+1)]-p[ix(x,y-1)]);
  }
}
function walls(){
  for(let x=0;x<GW;x++){u[ix(x,0)]=v[ix(x,0)]=0;u[ix(x,GH-1)]=v[ix(x,GH-1)]=0;}
  for(let y=0;y<GH;y++){u[ix(0,y)]=v[ix(0,y)]=0;u[ix(GW-1,y)]=v[ix(GW-1,y)]=0;}
}

/* ---------- bell (cross-section arc) + nerve chain ---------- */
const M=27, Rb=Math.min(W*0.30,150), Hb=Rb*0.74;
const world={t:0,paused:false,pace:true,ink:true};
let bell;
function birth(){
  bell={bx:W/2, by:H*0.62, vx:0, vy:0, tilt:0, om:0,
    a:new Float32Array(M),                 // muscle activation
    R:new Int16Array(M),                   // nerve refractory
    fireAt:new Int32Array(M).fill(-1),     // scheduled firing tick
    px:new Float32Array(M), py:new Float32Array(M),  // previous world pos (for boundary velocity)
    A0:0, initA:false, init:false};
  u.fill(0);v.fill(0);dye.fill(0);
}
birth();
function restShape(i){                     // body frame: dome opening downward
  const s=i/(M-1), phi=Math.PI*s;
  return [-Math.cos(phi)*Rb, -Math.sin(phi)*Hb];
}
function fireNerve(i,t){
  if(bell.R[i]>0)return;
  bell.R[i]=46; bell.a[i]=1;
  for(const j of [i-1,i+1])
    if(j>=0&&j<M&&bell.R[j]===0&&bell.fireAt[j]<t) bell.fireAt[j]=t+2;   // conduction: 2 ticks/segment
}
function step(){
  const t=world.t;
  if(world.pace && t%230===10){ fireNerve(0,t); fireNerve(M-1,t); }  // bilateral -> straight pulse
  for(let i=0;i<M;i++){
    if(bell.fireAt[i]===t) fireNerve(i,t);
    if(bell.R[i]>0)bell.R[i]--;
    bell.a[i]*=0.90;
  }
  // target boundary (world frame)
  const c=Math.cos(bell.tilt), s=Math.sin(bell.tilt);
  let Fx=0,Fy=0,Tq=0;
  for(let i=0;i<M;i++){
    const [rx0,ry0]=restShape(i);
    const rx=rx0*(1-0.38*bell.a[i]);       // circular muscle: pull toward axis
    const ry=ry0;
    const wx=bell.bx+rx*c-ry*s, wy=bell.by+rx*s+ry*c;
    const gx=wx/CS, gy=wy/CS;
    // boundary velocity (target motion + body drift)
    const bvx=bell.init?(wx-bell.px[i]):0, bvy=bell.init?(wy-bell.py[i]):0;
    bell.px[i]=wx; bell.py[i]=wy;
    // direct forcing: push fluid toward boundary velocity
    const fu=0.55*((bvx/CS)-sampleF(u,gx,gy));
    const fv=0.55*((bvy/CS)-sampleF(v,gx,gy));
    addF(u,gx,gy,fu); addF(v,gx,gy,fv);
    Fx-=fu; Fy-=fv;                        // reaction on the body
    Tq+=(rx*c-ry*s)*(-fv)-(rx*s+ry*c)*(-fu);
    if(world.ink && bell.a[i]>0.5) addF(dye,gx,gy+1.2,0.5);   // ink shed at contracting margin
  }
  bell.init=true;
  // enclosed-volume jet (Daniel 1983): contraction ejects water through the aperture
  let A=0;
  for(let i=0;i<M;i++){
    const j2=(i+1)%M;
    A+=bell.px[i]*bell.py[j2]-bell.px[j2]*bell.py[i];
  }
  A=Math.abs(A)*0.5;
  const dA=bell.initA?(bell.A0-A):0;
  bell.A0=A; bell.initA=true;
  if(dA>0.5){
    const ap=Math.max(30,Math.hypot(bell.px[M-1]-bell.px[0],bell.py[M-1]-bell.py[0]));
    const jv=dA/ap;                                     // jet speed (px/tick)
    const dux=Math.sin(bell.tilt), duy=-Math.cos(bell.tilt);   // apex = swim direction
    const J=Math.min(1.0, 0.0035*dA*jv);
    bell.vx+=dux*J; bell.vy+=duy*J;
    const mx=(bell.px[0]+bell.px[M-1])/2, my=(bell.py[0]+bell.py[M-1])/2;
    for(let k=-1;k<=1;k++){                             // matching momentum into the water
      addF(u,(mx+k*ap*0.25)/CS,my/CS,-dux*(jv/CS)*2.0);
      addF(v,(mx+k*ap*0.25)/CS,my/CS,-duy*(jv/CS)*2.0);
    }
  }
  bell.vx+=Fx*0.9*CS/M; bell.vy+=Fy*0.9*CS/M + 0.0025;  // reaction + slight negative buoyancy
  bell.om+=Tq*0.0018/M;
  bell.vx*=0.985; bell.vy*=0.985; bell.om*=0.95;
  bell.bx+=bell.vx; bell.by+=bell.vy; bell.tilt+=bell.om;
  bell.bx=Math.max(Rb*0.7,Math.min(W-Rb*0.7,bell.bx));
  bell.by=Math.max(Hb*1.2,Math.min(H-Hb*0.6,bell.by));
  // fluid step
  advect(u2,u,u,v,0.999); advect(v2,v,u,v,0.999);
  [u,u2]=[u2,u]; [v,v2]=[v2,v];
  project(); walls();
  advect(dye2,dye,u,v,0.992); [dye,dye2]=[dye2,dye];
  world.t++;
}

module.exports = {
  step, birth, get bell(){return bell;}, world, M, Rb, Hb, CS, GW, GH,
  // 【world も戻す】birth() は bell と流体しか初期化しない。world.pace を
  // 戻し忘れると、前の計測でペースメーカーを切った状態が次へ漏れる
  // （最初に書いたとき M-K1c がペースメーカーOFFのまま測られていた）
  reset(){ birth(); world.t = 0; world.pace = true; world.ink = true; world.paused = false; },
};
