// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Behaviour tests for v1.8: per-set wet/dry Mix.
using System;
using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;

class MxM : IMachine { public int TrackCount { get; set; } = 1; }
class MxH : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; }
  public IMachine Machine { get; } = new MxM(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=48000, SamplesPerTick=6000, TicksPerBeat=4 }; }
class MxP : IParameter { public MxP(string n) { Name = n; } public string Name { get; } }

static class MixTest {
  const int SR = 48000, SPT = 6000;
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }

  static float[] Run(Func<long,float> inp, int ticks, bool levelMatch, Action<Params> setup, Action<int, Params> perTick = null) {
    var e = new BlockFxEngine(SR) { Smooth = true, KeepDC = true, LevelMatch = levelMatch };
    var chg = new Params(); var outb = new float[(long)ticks * SPT];
    var io = new[]{ new float[256], new float[256] }; long pos = 0;
    for (int t = 0; t < ticks; t++) {
      if (t == 0) { chg.SetDefaults(); chg.TvN = 4; setup(chg); } else { chg.SetNoVal(); chg.TvN = 4; }
      perTick?.Invoke(t, chg);
      e.Tick(chg, SPT);
      for (int k = 0; k < SPT; ) { int n = Math.Min(256, SPT - k);
        for (int i = 0; i < n; i++) io[0][i] = inp(pos + i);
        e.Work(io, n, 1, true, SPT);
        Array.Copy(io[0], 0, outb, pos, n); pos += n; k += n; }
    }
    return outb;
  }
  static int Hz(string s) => EditModel.ParseFreq(s).Value;
  static int Semis(double st) => EditModel.FracToValue((float)((st / P.PitchRange + 1) / 2));
  static double Gz(float[] x, long from, long n, double hz) {
    double w = 2*Math.PI*hz/SR, c = 2*Math.Cos(w), s1=0, s2=0;
    for (long i = from; i < from+n; i++) { double win = 0.5-0.5*Math.Cos(2*Math.PI*(i-from)/n); double s0 = x[i]*win + c*s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(Math.Max(0, s1*s1 + s2*s2 - c*s1*s2)) * 4 / n; }
  static double Db(double a, double b) => 20*Math.Log10(Math.Max(a,1e-12)/Math.Max(b,1e-12));
  static bool Finite(float[] x) { foreach (var v in x) if (float.IsNaN(v) || float.IsInfinity(v)) return false; return true; }

  static int Main() {
    Func<long,float> a440 = t => (float)(8000*Math.Sin(2*Math.PI*440*t/SR));
    const long FROM = 20 * SPT, LEN = 16384;
    double in440 = Gz(Run(a440, 30, false, p => {}), FROM, LEN, 440);

    Console.WriteLine("PitchShift +12 on a 440 Hz sine (Overlap 75%, LevelMatch off)");
    Func<int, float[]> pitch = mix => Run(a440, 30, false, p => { p.G[P.Overlap] = 75;
      p.T[0,P.Effect]=P.PitchShift; p.T[0,P.FreqA]=Hz("30"); p.T[0,P.FreqB]=Hz("20k"); p.T[0,P.FxVal]=Semis(12); p.T[0,P.Mix]=mix; });
    var w100 = pitch(100); var w50 = pitch(50); var w0 = pitch(0);
    double s100 = Gz(w100, FROM, LEN, 880);
    Check("Mix 100: shifted only (440 gone)", Db(Gz(w100, FROM, LEN, 440), in440) < -60, $"440 {Db(Gz(w100, FROM, LEN, 440), in440):0.0} dB");
    double d440 = Db(Gz(w50, FROM, LEN, 440), in440), d880 = Db(Gz(w50, FROM, LEN, 880), s100);
    Check("Mix 50: original at -6 dB and shifted at -6 dB (a harmony)", Math.Abs(d440 + 6.02) < 0.3 && Math.Abs(d880 + 6.02) < 0.5,
          $"440 {d440:0.00} dB, 880 {d880:0.00} dB against full");
    Check("Mix 0: dry (440 at 0 dB, nothing at 880)", Math.Abs(Db(Gz(w0, FROM, LEN, 440), in440)) < 0.1 && Db(Gz(w0, FROM, LEN, 880), in440) < -60,
          $"440 {Db(Gz(w0, FROM, LEN, 440), in440):0.00} dB, 880 {Db(Gz(w0, FROM, LEN, 880), in440):0.0} dB");

    Console.WriteLine("Per-bin effect: Amp -inf over 1k..4k, 2 kHz sine (LevelMatch off)");
    Func<long,float> a2k = t => (float)(8000*Math.Sin(2*Math.PI*2000*t/SR));
    double in2k = Gz(Run(a2k, 30, false, p => {}), FROM, LEN, 2000);
    Func<int, int, float[]> cut = (mix, soft) => Run(a2k, 30, false, p => {
      p.T[0,P.Effect]=0; p.T[0,P.FreqA]=Hz("1k"); p.T[0,P.FreqB]=Hz("4k"); p.T[0,P.FxVal]=EditModel.FracToValue(0.5f); p.T[0,P.Amp]=0; p.T[0,P.Mix]=mix; p.T[0,P.Soft]=soft; });
    double c100 = Db(Gz(cut(100,0), FROM, LEN, 2000), in2k), c50 = Db(Gz(cut(50,0), FROM, LEN, 2000), in2k),
           c25 = Db(Gz(cut(25,0), FROM, LEN, 2000), in2k), c50s = Db(Gz(cut(50,12), FROM, LEN, 2000), in2k);
    Check("Mix 100 removes it; 50 leaves -6 dB; 25 leaves -2.5 dB", c100 < -60 && Math.Abs(c50 + 6.02) < 0.3 && Math.Abs(c25 + 2.50) < 0.3,
          $"{c100:0.0} / {c50:0.00} / {c25:0.00} dB");
    Check("Mix with Soft: the centre is still -6 dB at Mix 50", Math.Abs(c50s + 6.02) < 0.5, $"{c50s:0.00} dB");

    Console.WriteLine("Effects without Mix, automation, labels");
    {
      Func<int, float[]> sh = mix => Run(a440, 30, false, p => {
        p.T[0,P.Effect]=4; p.T[0,P.FreqA]=Hz("100"); p.T[0,P.FreqB]=Hz("5k"); p.T[0,P.FxVal]=EditModel.FracToValue(0.6f); p.T[0,P.Mix]=mix; });
      var a = sh(100); var b = sh(30); bool same = true;
      for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { same = false; break; }
      Check("ShiftAdd ignores Mix (HasMix false), like Soft", same && !P.HasMix(4) && P.HasMix(P.PitchShift) && P.HasMix(1), "");
      var g = Run(a440, 40, false, p => { p.G[P.Overlap] = 75;
        p.T[0,P.Effect]=P.PitchShift; p.T[0,P.FreqA]=Hz("30"); p.T[0,P.FreqB]=Hz("20k"); p.T[0,P.FxVal]=Semis(7); p.T[0,P.Mix]=100; },
        (t, c) => { if (t == 10) c.T[0,P.Mix] = 0; if (t == 25) c.T[0,P.Mix] = 100; });
      Check("Mix automation 100 -> 0 -> 100 stays finite and lands on dry, then wet", Finite(g)
            && Math.Abs(Db(Gz(g, 18*SPT, 8192, 440), in440)) < 0.2 && Db(Gz(g, 36*SPT, 8192, 440), in440) < -50,
            $"dry {Db(Gz(g, 18*SPT, 8192, 440), in440):0.00} dB, wet 440 {Db(Gz(g, 36*SPT, 8192, 440), in440):0.0} dB");
      var m = new PedalBlockFXMachine(new MxH());
      Check("labels", m.DescribeValue(new MxP("Mix"), 100) == "100% wet" && m.DescribeValue(new MxP("Mix"), 50) == "50% wet"
            && m.DescribeValue(new MxP("Mix"), 0) == "dry", m.DescribeValue(new MxP("Mix"), 50));
    }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails;
  }
}
