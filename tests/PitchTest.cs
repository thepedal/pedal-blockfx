// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Behaviour tests for v1.2: effect 15 PitchShift (phase vocoder, bin remapping).
using System; using PedalBlockFX;
static class PitchTest {
  const int SR = 48000, SPT = 6000;
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }

  static float[] Run(Func<long,float> inp, int ticks, bool smooth, Action<Params> setup, Action<int, Params> perTick = null) {
    var e = new BlockFxEngine(SR) { Smooth = smooth, KeepDC = true };
    var chg = new Params(); var outb = new float[(long)ticks * SPT];
    var io = new[]{ new float[256], new float[256] };
    long pos = 0;
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
  static void Pitch(Params p, int s, string a, string b, double st, int soft = 0, int amp = 0xA0) {
    p.T[s,P.Effect]=P.PitchShift; p.T[s,P.FreqA]=Hz(a); p.T[s,P.FreqB]=Hz(b); p.T[s,P.FxVal]=Semis(st); p.T[s,P.Soft]=soft; p.T[s,P.Amp]=amp; }
  static double Gz(float[] x, long from, long n, double hz) {
    double w = 2*Math.PI*hz/SR, c = 2*Math.Cos(w), s1=0, s2=0;
    for (long i = from; i < from+n; i++) { double win = 0.5-0.5*Math.Cos(2*Math.PI*(i-from)/n); double s0 = x[i]*win + c*s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(Math.Max(0, s1*s1 + s2*s2 - c*s1*s2)) * 4 / n; }
  // strongest frequency in [lo,hi], 0.25 Hz steps
  static double Peak(float[] x, long from, long n, double lo, double hi) {
    double best = 0, bf = lo; for (double f = lo; f <= hi; f += 0.25) { double a = Gz(x, from, n, f); if (a > best) { best = a; bf = f; } } return bf; }
  static double Rms(float[] x, long from, long n) { double e=0; for (long i=from;i<from+n;i++) e+=x[i]*x[i]; return Math.Sqrt(e/n); }
  static double Db(double a, double b) => 20*Math.Log10(a/b);
  static bool Finite(float[] x) { foreach (var v in x) if (float.IsNaN(v) || float.IsInfinity(v)) return false; return true; }

  static int Main() {
    Func<double,Func<long,float>> sine = f => t => (float)(8000*Math.Sin(2*Math.PI*f*t/SR));
    long a0 = 10*SPT, n = 8*SPT;
    Action<Params> std = p => { p.G[P.BlkLen]=7; p.G[P.Overlap]=75; };

    Console.WriteLine("PitchShift (Smooth, 4096, Overlap 75%), 440 Hz sine at 8000");
    foreach (var st in new[]{ 12.0, -12.0, 7.0, -5.0, 0.5, 24.0 }) {
      double target = 440*Math.Pow(2, st/12);
      var o = Run(sine(440), 20, true, p => { std(p); Pitch(p,0,"50","20k",st); });
      double pk = Peak(o, a0, n, target-15, target+15);
      double cents = 1200*Math.Log(pk/target, 2);
      double lvT = Gz(o,a0,n,pk), lv440 = Gz(o,a0,n,440);
      bool ok = Math.Abs(cents) < 5 && (Math.Abs(st) < 1 || Db(lv440, lvT) < -30) && Math.Abs(Db(lvT, 8000)) < 3;
      Check($"{st:+0.0;-0.0} st -> {target:0.00} Hz", ok,
        $"peak {pk:0.00} Hz ({cents:+0.0;-0.0} cents), level {Db(lvT,8000):+0.0;-0.0} dB, 440 left {Db(lv440,lvT):0.0} dB");
    }

    { // a chord keeps its intervals
      Func<long,float> chord = t => sine(220)(t)*0.4f + sine(277.18)(t)*0.4f + sine(329.63)(t)*0.4f;
      var o = Run(chord, 20, true, p => { std(p); Pitch(p,0,"50","20k",12); });
      double e1 = Peak(o,a0,n,430,450), e2 = Peak(o,a0,n,544,564), e3 = Peak(o,a0,n,649,669);
      Check("A-major chord +12 keeps its intervals", Math.Abs(e1-440)<1.5 && Math.Abs(e2-554.37)<1.5 && Math.Abs(e3-659.26)<1.5, $"{e1:0.0} / {e2:0.0} / {e3:0.0} Hz");
    }

    { // unity is transparent
      int delay = 4*SPT; Func<long,float> mus = t => sine(220)(t)*0.6f + sine(1330)(t)*0.3f;
      var o = Run(mus, 20, true, p => { std(p); Pitch(p,0,"50","20k",0); });
      double err=0, sig=0; for (long i = delay + 4*SPT; i < o.Length; i++) { double d = o[i]-mus(i-delay); err+=d*d; sig+=mus(i-delay)*mus(i-delay); }
      Check("0 st is transparent", 10*Math.Log10(sig/err) > 100, $"SNR {10*Math.Log10(sig/err):0.0} dB");
    }

    { // only the band moves; content outside it is untouched
      Func<long,float> two = t => sine(440)(t)*0.5f + sine(3000)(t)*0.5f;
      var o = Run(two, 20, true, p => { std(p); Pitch(p,0,"300","700",12); });
      double l880 = Gz(o,a0,n,880), l440 = Gz(o,a0,n,440), l3k = Gz(o,a0,n,3000);
      Check("band 300-700 Hz: 440 -> 880, 3 kHz untouched", Db(l440,l880) < -30 && Math.Abs(Db(l3k,4000)) < 0.5 && Math.Abs(Db(l880,4000)) < 3,
        $"880 {Db(l880,4000):+0.0;-0.0} dB, 440 left {Db(l440,l880):0.0} dB, 3k {Db(l3k,4000):+0.00;-0.00} dB");
    }

    { // inverted range (FreqA > FreqB): everything outside 300-700 moves
      Func<long,float> two = t => sine(440)(t)*0.5f + sine(3000)(t)*0.5f;
      var o = Run(two, 20, true, p => { std(p); Pitch(p,0,"700","300",-12); });
      double l1500 = Gz(o,a0,n,1500), l440 = Gz(o,a0,n,440), l3k = Gz(o,a0,n,3000);
      Check("outside 300-700 Hz: 3 kHz -> 1.5 kHz, 440 untouched", Db(l3k,l1500) < -30 && Math.Abs(Db(l440,4000)) < 0.5,
        $"1.5k {Db(l1500,4000):+0.0;-0.0} dB, 3k left {Db(l3k,l1500):0.0} dB, 440 {Db(l440,4000):+0.00;-0.00} dB");
    }

    { // Amp scales the shifted part relative to the rest (LevelMatch on: Amp rebalances bands, as in the original)
      Func<long,float> two = t => sine(440)(t)*0.5f + sine(3000)(t)*0.5f;
      var o0 = Run(two, 20, true, p => { std(p); Pitch(p,0,"300","700",12); });
      var o6 = Run(two, 20, true, p => { std(p); Pitch(p,0,"300","700",12,0,0xA0-12); });
      double r0 = Db(Gz(o0,a0,n,880), Gz(o0,a0,n,3000)), r6 = Db(Gz(o6,a0,n,880), Gz(o6,a0,n,3000));
      Check("Amp -6 dB lowers the shifted part against the rest", Math.Abs((r6 - r0) + 6) < 1.0, $"880 vs 3k: {r0:+0.0;-0.0} dB -> {r6:+0.0;-0.0} dB");
    }

    { // Blocky mode still shifts (rougher)
      var o = Run(sine(440), 20, false, p => { std(p); Pitch(p,0,"50","20k",12); });
      double pk = Peak(o,a0,n,865,895);
      Check("Blocky mode +12 still lands on 880 Hz", Math.Abs(pk-880) < 2 && Db(Gz(o,a0,n,440), Gz(o,a0,n,pk)) < -20, $"peak {pk:0.00} Hz");
    }

    { // Soft: near the edges only part of a bin moves
      Func<long,float> two = t => sine(440)(t)*0.5f;
      var hard = Run(two, 20, true, p => { std(p); Pitch(p,0,"400","2k",12,0); });
      var soft = Run(two, 20, true, p => { std(p); Pitch(p,0,"400","2k",12,24); });
      double h = Gz(hard,a0,n,440), s = Gz(soft,a0,n,440);
      Check("Soft 24 keeps most of 440 Hz just inside the 400 Hz edge", Db(s,4000) > -3 && Db(h,4000) < -25, $"hard {Db(h,4000):0.0} dB, soft {Db(s,4000):0.0} dB");
    }

    { // glide 0 -> +12 over 8 ticks, then back; no NaN, no jumps far above the input level
      var o = Run(sine(440), 40, true, p => { std(p); Pitch(p,0,"50","20k",0); },
        (t,p) => { if (t == 6) p.T[0,P.FxVal] = Semis(12); if (t == 14) p.T[0,P.FxVal] = Semis(12); if (t == 22) p.T[0,P.FxVal] = Semis(0); });
      float mx = 0; for (long i = 8*SPT; i < o.Length; i++) mx = Math.Max(mx, Math.Abs(o[i]));
      double end = Peak(o, 34*SPT, 6*SPT, 425, 455);
      Check("glide up and back: finite, bounded, ends at 440", Finite(o) && mx < 8000*1.6 && Math.Abs(end-440) < 1, $"peak abs {mx:0}, final {end:0.00} Hz");
    }

    { // other block sizes, overlaps and the extremes stay finite and on pitch
      bool allOk = true; string worst = "";
      foreach (int bl in new[]{ 3, 5, 9, 11 }) foreach (int ov in new[]{ 5, 50, 80 }) foreach (bool sm in new[]{ false, true }) {
        var o = Run(sine(1000), 16, sm, p => { p.G[P.BlkLen]=bl; p.G[P.Overlap]=ov; Pitch(p,0,"20","22k",-24,12); });
        if (!Finite(o)) { allOk = false; worst = $"NaN at bl {bl} ov {ov} smooth {sm}"; }
      }
      Check("finite at block 3..11, overlap 5..80, both modes, -24 st, soft 12", allOk, worst);
    }

    { // stereo through the machine: Effect 15 accepted and labelled
      Check("Effect 15 is named PitchShift", P.EffectNames[P.PitchShift] == "PitchShift" && P.LastEffect == 15, "");
      Check("Value text", EditModel.ValueDesc(P.PitchShift, Semis(7), f=>f, 44100) == "+7.00 st"
                        && EditModel.ValueDesc(P.PitchShift, Semis(-12), f=>f, 44100) == "-12.00 st"
                        && EditModel.ValueDesc(P.PitchShift, 0x5000, f=>f, 44100) == "0.00 st",
            $"{EditModel.ValueDesc(P.PitchShift, Semis(7), f=>f, 44100)} / {EditModel.ValueDesc(P.PitchShift, Semis(-12), f=>f, 44100)} / {EditModel.ValueDesc(P.PitchShift, 0x5000, f=>f, 44100)}");
      Check("Soft enabled for PitchShift, generic blend not used", P.HasSoft(P.PitchShift) && !P.IsPerBin(P.PitchShift), "");
    }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
