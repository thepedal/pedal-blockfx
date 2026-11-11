// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Behaviour tests for v1.4: per-set Mute and Solo.
using System;
using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;

class SM : IMachine { public int TrackCount { get; set; } = 3; }
class SH : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; }
  public IMachine Machine { get; } = new SM(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=48000, SamplesPerTick=6000, TicksPerBeat=4 }; }

static class SoloMuteTest {
  const int SR = 48000, SPT = 6000;
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }

  static float[] Run(Func<long,float> inp, int ticks, Action<Params> setup, Action<int, Params> perTick = null) {
    var e = new BlockFxEngine(SR) { Smooth = true, KeepDC = true, LevelMatch = false };
    var chg = new Params(); var outb = new float[(long)ticks * SPT];
    var io = new[]{ new float[250], new float[250] }; long pos = 0;
    for (int t = 0; t < ticks; t++) {
      if (t == 0) { chg.SetDefaults(); chg.TvN = 4; chg.G[P.BlkLen] = 7; setup(chg); } else { chg.SetNoVal(); chg.TvN = 4; }
      perTick?.Invoke(t, chg);
      e.Tick(chg, SPT);
      for (int k = 0; k < SPT; ) { int n = Math.Min(250, SPT - k);
        for (int i = 0; i < n; i++) io[0][i] = inp(pos + i);
        e.Work(io, n, 1, true, SPT);
        Array.Copy(io[0], 0, outb, pos, n); pos += n; k += n; }
    }
    return outb;
  }
  static int Hz(string s) => EditModel.ParseFreq(s).Value;
  // notch: Contrast at Value 0 = plain Amp, Amp -inf removes the band
  static void Notch(Params p, int s, string a, string b) { p.T[s,P.Effect]=0; p.T[s,P.FreqA]=Hz(a); p.T[s,P.FreqB]=Hz(b); p.T[s,P.FxVal]=0; p.T[s,P.Amp]=0; }
  static double Gz(float[] x, long from, long n, double hz) {
    double w = 2*Math.PI*hz/SR, c = 2*Math.Cos(w), s1=0, s2=0;
    for (long i = from; i < from+n; i++) { double win = 0.5-0.5*Math.Cos(2*Math.PI*(i-from)/n); double s0 = x[i]*win + c*s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(Math.Max(0, s1*s1 + s2*s2 - c*s1*s2)) * 4 / n; }
  static double Db(double a) => 20*Math.Log10(Math.Max(a,1e-9)/4000);
  static Func<long,float> Tones = t => (float)(4000*Math.Sin(2*Math.PI*440*t/SR) + 4000*Math.Sin(2*Math.PI*1000*t/SR) + 4000*Math.Sin(2*Math.PI*3000*t/SR));

  // levels of 440 / 1000 / 3000 Hz in output ticks [t0, t0+2)
  static string Hear(float[] o, int t0) {
    var sb = new System.Text.StringBuilder();
    foreach (double f in new[]{ 440.0, 1000.0, 3000.0 }) sb.Append(Db(Gz(o, (long)t0*SPT, 2*SPT, f)) > -3 ? 'o' : '.');
    return sb.ToString(); }

  static int Main() {
    // set 0 removes 440, set 1 removes 1000, set 2 removes 3000
    Action<Params> three = p => { Notch(p, 0, "400", "480"); Notch(p, 1, "900", "1.1k"); Notch(p, 2, "2.7k", "3.3k"); };
    Console.WriteLine("Mute and Solo (sets: 0 removes 440 Hz, 1 removes 1 kHz, 2 removes 3 kHz; o = heard)");
    Check("no switches: all three removed", Hear(Run(Tones, 16, three), 10) == "...", Hear(Run(Tones, 16, three), 10));
    { var o = Run(Tones, 16, p => { three(p); p.T[0,P.Mute] = 1; });
      Check("Mute set 0: 440 comes back", Hear(o, 10) == "o..", Hear(o, 10)); }
    { var o = Run(Tones, 16, p => { three(p); p.T[1,P.Solo] = 1; });
      Check("Solo set 1: only set 1 runs", Hear(o, 10) == "o.o", Hear(o, 10)); }
    { var o = Run(Tones, 16, p => { three(p); p.T[1,P.Solo] = 1; p.T[2,P.Solo] = 1; });
      Check("Solo sets 1 and 2", Hear(o, 10) == "o..", Hear(o, 10)); }
    { var o = Run(Tones, 16, p => { three(p); p.T[1,P.Solo] = 1; p.T[1,P.Mute] = 1; });
      Check("Mute wins over Solo (a muted solo doesn't silence the others)", Hear(o, 10) == ".o.", Hear(o, 10)); }
    { var o = Run(Tones, 16, p => { three(p); p.T[3,P.Solo] = 1; });   // set 3 is off (no frequencies)
      Check("Solo on an off set is ignored", Hear(o, 10) == "...", Hear(o, 10)); }

    Console.WriteLine("Timing (lands TickDelay = 4 ticks later)");
    { var o = Run(Tones, 30, three, (t, p) => { if (t == 10) p.T[0,P.Mute] = 1; if (t == 20) p.T[0,P.Mute] = 0; });
      string before = Hear(o, 11), during = Hear(o, 16), after = Hear(o, 26);
      Check("mute at tick 10, unmute at 20: heard from ~14 to ~24", before == "..." && during == "o.." && after == "...", $"{before} {during} {after}"); }

    Console.WriteLine("State survives a mute");
    {
      // Freeze on set 0, captured on 440; input then moves to 1 kHz. Muting and unmuting keeps the held 440.
      Func<long,float> moving = t => (float)(t < 12*SPT ? 6000*Math.Sin(2*Math.PI*440*t/SR) : 6000*Math.Sin(2*Math.PI*1000*t/SR));
      var o = Run(moving, 40, p => { p.T[0,P.Effect]=P.Freeze; p.T[0,P.FreqA]=Hz("50"); p.T[0,P.FreqB]=Hz("20k"); p.T[0,P.FxVal]=0xA000; },
                  (t, p) => { if (t == 20) p.T[0,P.Mute] = 1; if (t == 26) p.T[0,P.Mute] = 0; });
      double muted440 = Gz(o, 27*SPT, 2*SPT, 440), muted1k = Gz(o, 27*SPT, 2*SPT, 1000);
      double back440 = Gz(o, 34*SPT, 4*SPT, 440), back1k = Gz(o, 34*SPT, 4*SPT, 1000);
      Check("muted Freeze lets the live input through", muted1k > 5 * muted440, $"1k {muted1k:0} vs 440 {muted440:0}");
      Check("unmuted Freeze still holds the original 440", back440 > 5 * back1k, $"440 {back440:0} vs 1k {back1k:0}");
    }

    Console.WriteLine("Machine glue");
    {
      PedalBlockFXMachine.UiInvoker = a => a();
      var h = new SH(); var m = new PedalBlockFXMachine(h);
      Check("labels", m.DescribeValue(new NP("Mute"), 1) == "Muted" && m.DescribeValue(new NP("Solo"), 1) == "Solo" && m.DescribeValue(new NP("Mute"), 0) == "",
            m.DescribeValue(new NP("Mute"), 1) + " / " + m.DescribeValue(new NP("Solo"), 1));
      m.SetFreqA(Hz("400"), 0); m.SetFreqB(Hz("480"), 0); m.SetAmp(0, 0);
      m.SetMute(true, 0);
      var inp = new Sample[250]; var outp = new Sample[250]; var o = new float[16*SPT]; long pos = 0;
      for (int t = 0; t < 16; t++) for (int k = 0; k < SPT; k += 250) {
        for (int i = 0; i < 250; i++) { float v = Tones(pos + i); inp[i].L = v; inp[i].R = v; }
        bool r = m.Work(new[]{ outp }, new Sample[][]{ inp, null }, 250, WorkModes.WM_WRITE);
        for (int i = 0; i < 250; i++) o[pos + i] = r ? outp[i].L : 0;
        pos += 250; h.MasterInfo.PosInTick = (h.MasterInfo.PosInTick + 250) % SPT; }
      Check("SetMute(true) through the machine bypasses the set", Hear(o, 10) == "ooo", Hear(o, 10));
    }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
class NP : IParameter { public NP(string n) { Name = n; } public string Name { get; } }
