// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
using System; using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;
class M5 : IMachine { public int TrackCount { get; set; } = 1; }
class H5 : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; } public IMachine Machine => new M5(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=48000, SamplesPerTick=6000 }; }
static class FreezeTailTest { static int Main() {
  var h=new H5(); var m=new PedalBlockFXMachine(h);
  m.SetEffect(P.Freeze,0); m.SetFreqA(EditModel.ParseFreq("50").Value,0); m.SetFreqB(EditModel.ParseFreq("20k").Value,0); m.SetValue(0,0);
  var inp=new Sample[200]; var o=new Sample[200]; long pos=0; double late=0; int sleeps=0;
  for(int b=0;b<30*30;b++){ int tick=b/30; if (b%30==0 && tick==3) m.SetValue(0xA000,0);
    bool live = tick < 8;
    for(int i=0;i<200;i++){ float v= live ? (float)(8000*Math.Sin(2*Math.PI*440*(pos+i)/48000.0)) : 0f; inp[i].L=v; inp[i].R=v; }
    bool r = m.Work(new[]{o}, new Sample[][]{inp,null}, 200, WorkModes.WM_WRITE); if(!r) sleeps++;
    if (tick >= 20 && r) for(int i=0;i<200;i++) late += o[i].L*(double)o[i].L;
    pos+=200; h.MasterInfo.PosInTick=(h.MasterInfo.PosInTick+200)%6000; }
  double rms = Math.Sqrt(late/(10*6000));
  bool ok = rms > 500 && sleeps == 0;
  Console.WriteLine((ok?"  ok   ":"  FAIL ")+$"Freeze keeps sounding 12+ ticks after the input stopped: rms {rms:0}, sleeping blocks {sleeps}");
  return ok?0:1; } }
