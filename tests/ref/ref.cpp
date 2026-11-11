// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Reference harness: drives the ORIGINAL DT BlockFx DSP (shimmed) from a scenario file.
#include "stdafx.h"
#define protected public
#include "MIBlkFx.h"
#undef protected
#include "BlkFxProcess.h"
#include <cstdio>
ParamDesc *param_desc_hex=0,*param_desc_percent=0,*param_desc_percent2=0,*param_desc_freq_shift=0,*param_desc_harmonic_width=0,*param_desc_harmonic_type=0;

struct Ch{long tick;int kind,a,b,v;};
static void setG(Params& p,int i,int v){switch(i){case 0:p.blk_sync.data=v;break;case 1:p.interp_mode.data=v;break;case 2:p.mix_back.data=v;break;case 3:p.out_amp.data=v;break;case 4:p.tick_delay.data=v;break;case 5:p.blk_len.data=v;break;case 6:p.overlap.data=v;break;}}
static void setT(Params::TVals& t,int j,int v){switch(j){case 0:t.freq_a.data=v;break;case 1:t.freq_b.data=v;break;case 2:t.amp.data=v;break;case 3:t.effect.data=v;break;case 4:t.fxval.data=v;break;}}
static void noVal(Params& p,int nt){p.blk_sync.setNoVal();p.interp_mode.setNoVal();p.mix_back.setNoVal();p.out_amp.setNoVal();p.tick_delay.setNoVal();p.blk_len.setNoVal();p.overlap.setNoVal();
 for(int i=0;i<nt;i++){p.tv[i].freq_a.setNoVal();p.tv[i].freq_b.setNoVal();p.tv[i].amp.setNoVal();p.tv[i].effect.setNoVal();p.tv[i].fxval.setNoVal();}}
static void defs(Params& p,int nt){Params d; d.tv_n=p.tv_n; p.copy(d);}

int main(int argc,char**argv){
  // ref in.f32 scen.txt out.f32 sr spt ntracks inputLen totalLen
  FILE* fi=fopen(argv[1],"rb"); int sr=atoi(argv[4]), spt=atoi(argv[5]), nt=atoi(argv[6]); long inLen=atol(argv[7]), total=atol(argv[8]);
  std::vector<float> in(total,0.f); fread(&in[0],4,inLen,fi); fclose(fi);
  std::vector<Ch> ch; FILE* fs=fopen(argv[2],"r"); Ch c;
  while(fscanf(fs,"%ld %d %d %d %d",&c.tick,&c.kind,&c.a,&c.b,&c.v)==5) ch.push_back(c); fclose(fs);
  CMasterInfo mi; mi.SamplesPerSec=sr; mi.SamplesPerTick=spt; mi.PosInTick=0; mi.BeatsPerMin=120; mi.TicksPerBeat=4; mi.TicksPerSec=(float)sr/spt;
  MIBlkFx* m=new MIBlkFx(); m->pMasterInfo=&mi; m->Init(0); m->SetNumTracks(nt);
  std::vector<float> out(total,0.f); float buf[256];
  for(long pos=0,tick=0; pos<total; tick++){
    if(tick==0) defs(m->params,nt); else noVal(m->params,nt);
    for(size_t k=0;k<ch.size();k++) if(ch[k].tick==tick){ if(ch[k].kind==0) setG(m->params,ch[k].a,ch[k].v); else setT(m->params.tv[ch[k].a],ch[k].b,ch[k].v);} 
    m->Tick();
    for(long t=0;t<spt && pos<total;){ int n=(int)std::min<long>(256,std::min<long>(spt-t,total-pos));
      int mode = pos<inLen?3:2; for(int i=0;i<n;i++) buf[i]=(mode==3)?in[pos+i]:0.f;
      bool r=m->Work(buf,n,mode); for(int i=0;i<n;i++) out[pos+i]=r?buf[i]:0.f; pos+=n; t+=n; }
  }
  FILE* fo=fopen(argv[3],"wb"); fwrite(&out[0],4,total,fo); fclose(fo); return 0; }
void MIBlkFx::Command(int const){}
char const* MIBlkFx::DescribeValue(int const, int const){return 0;}
