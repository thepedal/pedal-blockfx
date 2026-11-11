// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
using System; using System.IO; using PedalBlockFX;
static class Dbg { static void Main(string[] a) {
  int fx = int.Parse(a[0]); bool sc = a.Length > 1;
  var rnd=new Random(11); var e=new BlockFxEngine(48000){Smooth=true,KeepDC=true}; var chg=new Params();
  var io=new[]{new float[256],new float[256]}; var s=new[]{new float[256],new float[256]}; var o=new float[6000*20]; long pos=0;
  for(int t=0;t<20;t++){ if(t==0){chg.SetDefaults(); chg.TvN=1; chg.T[0,P.Effect]=fx; chg.T[0,P.FreqA]=EditModel.ParseFreq("100").Value; chg.T[0,P.FreqB]=EditModel.ParseFreq("10k").Value; chg.T[0,P.FxVal]=(fx==0?0:0xA000);} else {chg.SetNoVal(); chg.TvN=1;}
    e.Tick(chg,6000);
    for(int k=0;k<6000;k+=200){ for(int i=0;i<200;i++){ io[0][i]=(float)(rnd.NextDouble()*2-1)*8000; s[0][i]=(float)(10000*Math.Sin(2*Math.PI*500*(pos+i)/48000.0)); }
      e.Work(io,200,1,true,6000, sc?s:null); Array.Copy(io[0],0,o,pos,200); pos+=200; } }
  var b=new byte[o.Length*4]; Buffer.BlockCopy(o,0,b,0,b.Length); File.WriteAllBytes("/tmp/dbg.f32",b); } }
