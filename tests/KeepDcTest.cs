// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
using System; using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;
class M3 : IMachine { public int TrackCount { get; set; } = 1; }
class H3 : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; } public IMachine Machine => new M3(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=48000, SamplesPerTick=5714 }; }
static class KeepDcTest { static int Main() {
  foreach (bool keep in new[]{false,true}) {
    var h=new H3(); var m=new PedalBlockFXMachine(h); m.TickDelay=0x0A; m.KeepDC=keep;
    var inp=new Sample[256]; var o=new Sample[256]; double ei=0,eo=0, dc=0; long pos=0; int cnt=0;
    for(int b=0;b<3000;b++){ int n=Math.Min(256,5714-h.MasterInfo.PosInTick);
      for(int i=0;i<n;i++){ float v=(float)(10000*Math.Sin(2*Math.PI*84*(pos+i)/48000.0)+3000); inp[i].L=v; inp[i].R=v; }
      m.Work(new[]{o}, new Sample[][]{inp, null}, n, WorkModes.WM_WRITE); pos+=n;
      if(b>1000) for(int i=0;i<n;i++){ ei+=(inp[i].L-3000)*(inp[i].L-3000); eo+=o[i].L*o[i].L; dc+=o[i].L; cnt++; }
      h.MasterInfo.PosInTick=(h.MasterInfo.PosInTick+n)%5714; }
    Console.WriteLine($"KeepDC={keep}: 84 Hz level out/in {10*Math.Log10(eo/ei):0.0} dB, output DC {dc/cnt:0.0} (input DC 3000)");
  } return 0; } }
