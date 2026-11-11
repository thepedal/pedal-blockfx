// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
using System; using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;
class M2 : IMachine { public int TrackCount { get; set; } = 1; }
class H2 : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; } public IMachine Machine => new M2(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=44100, SamplesPerTick=5512 }; }
static class StereoTest { static int Main() {
  foreach (int mode in new[]{1,2}) {
    var h=new H2(); var m=new PedalBlockFXMachine(h); m.Stereo=1;
    m.SetFreqA(0x2400,0); m.SetFreqB(0x5400,0); m.SetEffect(1,0); m.SetValue(0x9000,0);
    var inp=new Sample[256]; var o=new Sample[256]; var r=new Random(5); double eL=0,eR=0,c=0;
    for(int b=0;b<1500;b++){ int n=Math.Min(256,5512-h.MasterInfo.PosInTick);
      for(int i=0;i<n;i++){ float v=(float)(r.NextDouble()*16000-8000); inp[i].L=v; inp[i].R=v; }
      if (b==300) m.Stereo=mode;            // switch without touching the effect
      m.Work(new[]{o}, new Sample[][]{inp, null}, n, WorkModes.WM_WRITE);
      if (b>700) for(int i=0;i<n;i++){ eL+=o[i].L*o[i].L; eR+=o[i].R*o[i].R; c+=o[i].L*o[i].R; }
      h.MasterInfo.PosInTick=(h.MasterInfo.PosInTick+n)%5512; }
    Console.WriteLine((mode==1?"Stereo":"Wide  ")+" L-R correlation after switch: "+(c/Math.Sqrt(eL*eR)).ToString("F4"));
  } return 0; } }
