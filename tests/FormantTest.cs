// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Behaviour tests for v2.1: PitchShift formant control (spectral envelope correction).
using System; using PedalBlockFX;
static class FormantTest {
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
  static int Fmt(double st) => P.FormantKeep + (int)Math.Round(st * 2);
  static void Pitch(Params p, int s, double st, int formant, int mix = 100) {
    p.T[s,P.Effect]=P.PitchShift; p.T[s,P.FreqA]=Hz("30"); p.T[s,P.FreqB]=Hz("20k"); p.T[s,P.FxVal]=Semis(st);
    p.T[s,P.Formant]=formant; p.T[s,P.Mix]=mix; p.G[P.BlkLen]=7; p.G[P.Overlap]=75; p.G[P.OutAmp]=0xA0; }
  static double Gz(float[] x, long from, long n, double hz) {
    double w = 2*Math.PI*hz/SR, c = 2*Math.Cos(w), s1=0, s2=0;
    for (long i = from; i < from+n; i++) { double win = 0.5-0.5*Math.Cos(2*Math.PI*(i-from)/n); double s0 = x[i]*win + c*s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(Math.Max(0, s1*s1 + s2*s2 - c*s1*s2)) * 4 / n; }
  static double Rms(float[] x, long from, long n) { double e=0; for (long i=from;i<from+n;i++) e+=x[i]*x[i]; return Math.Sqrt(e/n); }
  static double Db(double a) => 20*Math.Log10(Math.Max(a, 1e-30));
  static bool Finite(float[] x) { foreach (var v in x) if (float.IsNaN(v) || float.IsInfinity(v)) return false; return true; }

  // vowel /a/-like envelope: three two-pole resonances (F1 700, F2 1220, F3 2600 Hz)
  static readonly double[] Fc = { 700, 1220, 2600 }, Bw = { 110, 140, 200 }, Ga = { 1.0, 0.6, 0.25 };
  static double Env(double f) {
    double a = 0;
    foreach (var i in new[]{0,1,2}) {
      double d1 = (f - Fc[i]) / (Bw[i] / 2), d2 = (f + Fc[i]) / (Bw[i] / 2);
      a += Ga[i] / Math.Sqrt(1 + d1 * d1) / Math.Sqrt(1 + d2 * d2) * Fc[i] / Bw[i] * 0.1;
    }
    return a + 0.0005;
  }
  // harmonic source with the vowel envelope, slightly random phases (fixed seed)
  static Func<long,float> Vowel(double f0) {
    int nh = (int)(5000 / f0); var amp = new double[nh + 1]; var ph = new double[nh + 1]; var r = new Random(7);
    double norm = 0; for (int h = 1; h <= nh; h++) { amp[h] = Env(h * f0); ph[h] = r.NextDouble() * 6.283; norm += amp[h] * amp[h]; }
    double g = 6000 / Math.Sqrt(norm / 2);
    return t => { double s = 0; for (int h = 1; h <= nh; h++) s += amp[h] * Math.Sin(2 * Math.PI * h * f0 * t / SR + ph[h]); return (float)(s * g); };
  }

  // dB error between the output's harmonics (spacing fout, up to 4 kHz) and the envelope
  // they should follow (target(f)), after removing the mean level difference
  static double EnvErr(float[] o, long a0, long n, double fout, Func<double,double> target, double fmax = 4000) {
    var d = new System.Collections.Generic.List<double>();
    for (double f = fout; f <= fmax; f += fout) d.Add(Db(Gz(o, a0, n, f)) - Db(target(f)));
    double m = 0; foreach (var v in d) m += v; m /= d.Count;
    double e = 0; foreach (var v in d) e += (v - m) * (v - m); return Math.Sqrt(e / d.Count);
  }

  static int Main(string[] args) {
    if (args.Length > 0) { // debug: print harmonic levels
      double f0 = double.Parse(args[0]), st = double.Parse(args[1]); int fm = int.Parse(args[2]); double r = Math.Pow(2, st/12);
      var o = Run(Vowel(f0), 20, true, p => Pitch(p, 0, st, fm)); long b0 = 10*SPT, nn = 8*SPT;
      for (double f = f0*r; f <= 3000; f += f0*r) Console.WriteLine($"{f,8:0.0} out {Db(Gz(o,b0,nn,f)),7:0.0}  env {Db(Env(f)),7:0.0}  envMoved {Db(Env(f/r)),7:0.0}  mid {Db(Gz(o,b0,nn,f+f0*r/2)),7:0.0}");
      return 0; }
    long a0 = 10*SPT, n = 8*SPT;
    Console.WriteLine("Formant control (PitchShift, Smooth, 4096, Overlap 75%), vowel F1 700 / F2 1220 / F3 2600 Hz");

    foreach (var f0 in new[]{ 110.0, 150.0, 220.0 })
    foreach (var st in new[]{ 12.0, -12.0, 7.0 }) {
      double r = Math.Pow(2, st / 12), fout = f0 * r;
      var src = Vowel(f0);
      var off  = Run(src, 20, true, p => Pitch(p, 0, st, P.FormantOff));
      var keep = Run(src, 20, true, p => Pitch(p, 0, st, P.FormantKeep));
      double fmax = Math.Min(4000, 5000 * Math.Min(1, r) * 0.9);   // the source stops at 5 kHz
      double eOff = EnvErr(off, a0, n, fout, Env, fmax), eKeep = EnvErr(keep, a0, n, fout, Env, fmax);
      double eOffMoved = EnvErr(off, a0, n, fout, f => Env(f / r), fmax);
      double lv = Db(Rms(keep, a0, n)) - Db(Rms(off, a0, n));
      // Above ~200 Hz the harmonics sample a 110 Hz-wide formant too sparsely for any
      // envelope estimate to see its true peak, so only "no worse than Off" is required.
      bool ok = f0 < 200 ? eKeep < 3.5 && eKeep < eOff - 3 : eKeep < eOff && eKeep < 5.5;
      Check($"f0 {f0} Hz {st:+0;-0} st: Keep follows the original envelope", ok && Math.Abs(lv) < 4,
        $"error Keep {eKeep:0.0} dB, Off {eOff:0.0} dB (Off vs moved envelope {eOffMoved:0.0} dB), level Keep-Off {lv:+0.0;-0.0} dB");
    }

    { // formant shift with no pitch change: harmonics stay, envelope moves
      double f0 = 150, fs = 4, fr = Math.Pow(2, fs / 12);
      var src = Vowel(f0);
      var o = Run(src, 20, true, p => Pitch(p, 0, 0, Fmt(fs)));
      double eMoved = EnvErr(o, a0, n, f0, f => Env(f / fr)), eOrig = EnvErr(o, a0, n, f0, Env);
      double pk = Db(Gz(o, a0, n, 150)) - Db(Gz(o, a0, n, 151.5 + 0.0)), off = Db(Gz(o, a0, n, 175));
      Check("0 st, Formant +4 st: pitch unchanged, envelope up 4 st", eMoved < 3.5 && eMoved < eOrig - 2 && Db(Gz(o,a0,n,150)) - off > 30,
        $"error vs moved {eMoved:0.0} dB, vs original {eOrig:0.0} dB, 150 Hz vs 175 Hz {Db(Gz(o,a0,n,150)) - off:0.0} dB");
    }

    { // pitch up with formant down: envelope moves to f / fr while pitch goes up
      double f0 = 150, st = 7, fs = -3, r = Math.Pow(2, st / 12), fr = Math.Pow(2, fs / 12);
      var o = Run(Vowel(f0), 20, true, p => Pitch(p, 0, st, Fmt(fs)));
      double e = EnvErr(o, a0, n, f0 * r, f => Env(f / fr));
      Check("+7 st, Formant -3 st: envelope lands 3 st down", e < 3.5, $"error {e:0.0} dB");
    }

    { // Keep at 0 st is bit-identical to Off (nothing to do), and Formant on another effect does nothing
      var src = Vowel(150);
      var a = Run(src, 12, true, p => Pitch(p, 0, 0, P.FormantOff));
      var b = Run(src, 12, true, p => Pitch(p, 0, 0, P.FormantKeep));
      bool same = true; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { same = false; break; }
      Check("0 st with Keep is bit-identical to Off", same, "");
      Action<Params,int> smear = (p, f) => { p.T[0,P.Effect]=1; p.T[0,P.FreqA]=Hz("300"); p.T[0,P.FreqB]=Hz("3k"); p.T[0,P.FxVal]=0x6000; p.T[0,P.Formant]=f; };
      var c = Run(src, 12, true, p => smear(p, P.FormantOff));
      var d = Run(src, 12, true, p => smear(p, Fmt(5)));
      same = true; for (int i = 0; i < c.Length; i++) if (c[i] != d[i]) { same = false; break; }
      Check("Formant on Smear is ignored (bit-identical)", same, "");
    }

    { // Mix 50 with Keep: dry stays, shifted part follows the envelope
      double f0 = 150; var o = Run(Vowel(f0), 20, true, p => Pitch(p, 0, 12, P.FormantKeep, 50));
      double l150 = Db(Gz(o, a0, n, 150)), l300 = Db(Gz(o, a0, n, 300));
      var dry = Run(Vowel(f0), 20, true, p => Pitch(p, 0, 12, P.FormantKeep, 0));
      double d150 = Db(Gz(dry, a0, n, 150));
      Check("Mix 50 + Keep: dry 150 Hz at -6 dB, shifted part present", Math.Abs(l150 - d150 + 6.02) < 0.5 && l300 > l150 - 20,
        $"150 Hz {l150 - d150:+0.00;-0.00} dB vs dry, 300 Hz {l300 - l150:+0.0;-0.0} dB vs 150");
    }

    { // robustness: every block size and both modes, extreme settings, automation
      bool ok = true; string bad = "";
      foreach (var smooth in new[]{ true, false })
      foreach (var bl in new[]{ 3, 5, 7, 9, 11 })
      foreach (var fv in new[]{ 1, P.FormantKeep, 49 }) {
        var o = Run(Vowel(220), 10, smooth, p => { Pitch(p, 0, -24, fv); p.G[P.BlkLen] = bl; p.G[P.TickDelay] = 0x1000; p.T[0,P.Soft] = 12; });
        if (!Finite(o)) { ok = false; bad += $" bl{bl}/{(smooth?"S":"B")}/f{fv}"; }
      }
      var auto = Run(Vowel(150), 24, true, p => Pitch(p, 0, 12, P.FormantOff),
        (t, p) => { if (t >= 4) p.T[0,P.Formant] = (t * 7) % 50; });
      ok &= Finite(auto);
      double peak = 0; foreach (var v in auto) peak = Math.Max(peak, Math.Abs(v));
      Check("finite: block sizes 3-11, both modes, -24 st, formant 1/25/49, automation", ok && peak < 32768, $"peak {peak:0}{bad}");
    }

    { // silence in, silence out (envelope floor)
      var o = Run(t => 0f, 10, true, p => Pitch(p, 0, 12, P.FormantKeep));
      double mx = 0; foreach (var v in o) mx = Math.Max(mx, Math.Abs(v));
      Check("silent input stays silent", Finite(o) && mx < 1e-3, $"max {mx:0.0e0}");
    }

    { // labels
      bool ok = EditModel.FormantText(0) == "off" && EditModel.FormantText(P.FormantKeep) == "keep"
             && EditModel.FormantText(Fmt(4)) == "+4.0 st" && EditModel.FormantText(1) == "-12.0 st" && EditModel.FormantText(49) == "+12.0 st"
             && EditModel.FormantText(Fmt(-0.5)) == "-0.5 st";
      Check("labels", ok, $"{EditModel.FormantText(0)}, {EditModel.FormantText(25)}, {EditModel.FormantText(Fmt(4))}, {EditModel.FormantText(1)}, {EditModel.FormantText(Fmt(-0.5))}");
    }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
