// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
using System; using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;
class M : IMachine { public int TrackCount { get; set; } = 2; }
class H : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; } public IMachine Machine => new M(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=48000, SamplesPerTick=6000 }; }
class Prm : IParameter { public string Name { get; set; } }
static class GlueTest {
  static int Main() {
    var h = new H(); var m = new PedalBlockFXMachine(h);
    // host pushes defaults (SetParameterDefaults), then a smear on track 0, wide stereo
    m.MixBack=0; m.OutAmp=0xA0; m.TickDelay=0x400; m.BlockLen=7; m.Overlap=50; m.Stereo=2;
    for (int t=0;t<2;t++){ m.SetFreqA(0,t); m.SetFreqB(0,t); m.SetAmp(0xA0,t); m.SetEffect(0,t); m.SetValue(0,t); }
    m.SetEffect(1,0); m.SetFreqA(0x2400,0); m.SetFreqB(0x5400,0); m.SetValue(0x9000,0);
    var rnd = new Random(3); var inp = new Sample[256]; var outp = new Sample[256];
    double eL=0,eR=0,corr=0; int falseAt=-1; long pos=0;
    for (int blk=0; blk<2000; blk++) {
      int n = Math.Min(256, 6000 - h.MasterInfo.PosInTick);
      bool live = blk < 1200;
      for (int i=0;i<n;i++){ float v = live ? (float)(Math.Sin(pos*0.03)*8000 + rnd.NextDouble()*2000) : 0f; inp[i].L=v; inp[i].R=v; pos++; }
      bool r = m.Work(new[]{outp}, new Sample[][]{inp, null}, n, WorkModes.WM_WRITE);
      if (!r && falseAt<0 && !live) falseAt = blk;
      if (r && blk>200 && live) for (int i=0;i<n;i++){ eL+=outp[i].L*outp[i].L; eR+=outp[i].R*outp[i].R; corr+=outp[i].L*outp[i].R; }
      h.MasterInfo.PosInTick = (h.MasterInfo.PosInTick + n) % 6000;
      if (blk==600) m.Stereo = 0;   // switch to mono mid-stream
      if (blk==800) m.Stereo = 1;   // and back to stereo
    }
    Console.WriteLine($"L/R energy {eL:E2}/{eR:E2}, L-R correlation {corr/Math.Sqrt(eL*eR):F3}, tail ended (Work=false) at block {falseAt}");
    var L = new object[]{"FreqA",0x2400,"FreqA",0,"FreqB",0x5400,"Amp",0,"Amp",0xA0,"OutAmp",0xE0,"TickDelay",0x400,"BlockLen",7,"BlockLen",11,"Overlap",50,"MixBack",0x50,"Effect",8,"Value",0xA000,"InterpMode",2,"Stereo",2};
      for (int k=0;k<L.Length;k+=2){ string n=(string)L[k]; int v=(int)L[k+1]; Console.WriteLine($"  {n,-10} {v,6:X} -> {m.DescribeValue(new Prm{Name=n}, v)}"); }
    return 0;
  }
}
