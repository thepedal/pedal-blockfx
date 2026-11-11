# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Pedal
import numpy as np, subprocess, sys, os
HERE=os.path.dirname(os.path.abspath(__file__)); D=os.path.join(HERE,'out'); os.makedirs(D,exist_ok=True); SR=44100; SPT=5512
rng=np.random.default_rng(7)
def make_input(n):
    t=np.arange(n)/SR; x=np.zeros(n)
    for f0,a in [(110,1.0),(220*1.5,0.5),(440*1.26,0.3)]:
        ph=(t*f0)%1.0; x+=a*(2*ph-1)                       # saws
    env=np.zeros(n)
    for s in range(0,n,SPT*4): env[s:s+SPT*2]+=np.exp(-np.arange(min(SPT*2,n-s))/3000.)
    x+=0.8*rng.standard_normal(n)*env                      # noise hits
    return (x/np.max(np.abs(x))*16000).astype(np.float32)
G=dict(BlkSync=0,InterpMode=1,MixBack=2,OutAmp=3,TickDelay=4,BlkLen=5,Overlap=6)
T=dict(FreqA=0,FreqB=1,Amp=2,Effect=3,Value=4)
def g(tick,k,v): return (tick,0,G[k],0,v)
def t(tick,s,k,v): return (tick,1,s,T[k],v)
def fxset(tick,s,fx,a,b,val,amp=0xA0): return [t(tick,s,'Effect',fx),t(tick,s,'FreqA',a),t(tick,s,'FreqB',b),t(tick,s,'Value',val),t(tick,s,'Amp',amp)]
C3,C5,C7,C2=0x2400,0x3c00,0x5400,0x1800
sc={}
sc['passthrough']=[]
names=['contrast','smear','clip','weed','shiftadd','shiftrep','harmonic','autoharm','copyharm']
for fx in range(9):
    for val in (0x2800,0x7800):
        sc[f'{names[fx]}_{val:x}']=fxset(0,0,fx,C3,C7,val,0xA8)
sc['contrast_inverted']=fxset(0,0,0,C7,C3,0x8000)
sc['smear_inverted']=fxset(0,0,1,C5,C2,0x6000)
sc['multi_mix']=fxset(0,0,0,C2,C5,0x9000)+fxset(0,1,1,C5,0x6000,0x5000,0x98)+fxset(0,2,4,C3,C5,0x3000,0x90)+fxset(0,3,3,0x2000,0x6000,0x6000)+[g(0,'MixBack',0x40),g(0,'OutAmp',0x98)]
for bl in (0,3,11):
    sc[f'blklen{bl}']=fxset(0,0,1,C3,C7,0x5000)+[g(0,'BlkLen',bl)]
for ov in (5,80):
    sc[f'overlap{ov}']=fxset(0,0,2,C3,C7,0x6000)+[g(0,'Overlap',ov)]
auto=fxset(0,0,0,C3,C7,0x1000)
for k in range(1,40): auto.append(t(k,0,'Value',0x1000+k*0x380)); auto.append(t(k,0,'FreqA',C3+k*100))
sc['automation']=auto
sync=fxset(0,0,1,C3,C7,0x5000)+[g(0,'BlkLen',9)]
for k in range(2,40,3): sync.append(g(k,'BlkSync',1))
sc['blksync']=sync
td=fxset(0,0,6,C3,C7,0x4000)
for k,v in [(5,0x100),(15,0x800),(25,0x200)]: td.append(g(k,'TickDelay',v))
sc['tickdelay_changes']=td
im=fxset(0,0,0,C3,C7,0x1000)+[g(0,'InterpMode',1)]
for k in range(1,40,2): im.append(t(k,0,'Value',(k*0x700)%0xA000))
sc['interp_off']=im
sc['mixback_full']=fxset(0,0,1,C3,C7,0x9000)+[g(0,'MixBack',0xA0)]
NT=4; inLen=SPT*44; total=SPT*56     # input stops at tick 44 -> tail test
x=make_input(inLen); x.tofile(f'{D}/in.f32')
rows=[]
for name,ev in sc.items():
    with open(f'{D}/{name}.txt','w') as f:
        for e in ev: f.write(' '.join(map(str,e))+'\n')
    args=[f'{D}/in.f32',f'{D}/{name}.txt',None,str(SR),str(SPT),str(NT),str(inLen),str(total)]
    a=list(args); a[2]=f'{D}/{name}.ref'; subprocess.run([os.path.join(HERE,'refbin')]+a,check=True)
    a=list(args); a[2]=f'{D}/{name}.cs'; subprocess.run(['mono',os.path.join(HERE,'harness.exe')]+a,check=True,stderr=subprocess.DEVNULL)
    r=np.fromfile(f'{D}/{name}.ref',np.float32).astype(np.float64); c=np.fromfile(f'{D}/{name}.cs',np.float32).astype(np.float64)
    d=r-c; snr=10*np.log10(np.sum(r**2)/max(np.sum(d**2),1e-30))
    tail_r=np.max(np.abs(r[inLen+SPT*8:])); tail_c=np.max(np.abs(c[inLen+SPT*8:]))
    rows.append((name,snr,np.max(np.abs(d)),np.sqrt(np.mean(r**2)),tail_r,tail_c))
print(f"{'scenario':22s} {'SNR dB':>8s} {'maxdiff':>9s} {'rms':>8s} {'tail r/c':>10s}")
for n,s,m,rms,tr,tc in rows: print(f"{n:22s} {s:8.1f} {m:9.3f} {rms:8.0f} {tr:5.0f}/{tc:<5.0f}")
# pass-through sanity: output == input delayed by 4 ticks
r=np.fromfile(f'{D}/passthrough.cs',np.float32); dl=4*SPT
e=r[dl+2*SPT:inLen].astype(float)-x[2*SPT:inLen-dl]
print('passthrough = input delayed by TickDelay; residual (original per-block DC removal): %.1f dB below signal' % (10*np.log10(np.sum(x.astype(float)**2)/len(x)/np.mean(e**2))))
