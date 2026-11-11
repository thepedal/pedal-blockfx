// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Behaviour tests for v0.3 additions: Smooth mode, Soft edges, effects 9-14, sidechain.
using System; using System.Collections.Generic; using PedalBlockFX;
static class NewFxTest {
  const int SR = 48000, SPT = 6000;
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }

  // Run the engine: input(t) and optional sidechain(t) generators; set params on tick 0 (+ optional per-tick changes)
  static float[] Run(Func<long,float> inp, Func<long,float> sc, int ticks, bool smooth, bool keepDC,
                     Action<Params> setup, Action<int, Params> perTick = null) {
    var e = new BlockFxEngine(SR) { Smooth = smooth, KeepDC = keepDC };
    var chg = new Params(); var outb = new float[(long)ticks * SPT];
    var io = new[]{ new float[256], new float[256] }; var scb = new[]{ new float[256], new float[256] };
    long pos = 0;
    for (int t = 0; t < ticks; t++) {
      if (t == 0) { chg.SetDefaults(); chg.TvN = 4; setup(chg); } else { chg.SetNoVal(); chg.TvN = 4; }
      perTick?.Invoke(t, chg);
      e.Tick(chg, SPT);
      for (int k = 0; k < SPT; ) { int n = Math.Min(256, SPT - k);
        for (int i = 0; i < n; i++) { io[0][i] = inp(pos + i); if (sc != null) scb[0][i] = sc(pos + i); }
        e.Work(io, n, 1, true, SPT, sc != null ? scb : null);
        Array.Copy(io[0], 0, outb, pos, n); pos += n; k += n; }
    }
    return outb;
  }
  static void Set(Params p, int s, int fx, int a, int b, int val, int amp = 0xA0, int soft = 0) {
    p.T[s,P.Effect]=fx; p.T[s,P.FreqA]=a; p.T[s,P.FreqB]=b; p.T[s,P.FxVal]=val; p.T[s,P.Amp]=amp; p.T[s,P.Soft]=soft; }
  static double Gz(float[] x, long from, long n, double hz) {      // Goertzel amplitude
    double w = 2*Math.PI*hz/SR, c = 2*Math.Cos(w), s1=0, s2=0;
    for (long i = from; i < from+n; i++) { double s0 = x[i] + c*s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(s1*s1 + s2*s2 - c*s1*s2) * 2 / n; }
  static double Rms(float[] x, long from, long n) { double e=0; for (long i=from;i<from+n;i++) e+=x[i]*x[i]; return Math.Sqrt(e/n); }
  static double BandShare(float[] x, long from, double lo, double hi) {   // energy share in [lo,hi) Hz
    int N = 32768; var f = new RealFft(N); var buf = new float[N]; var hc = new float[N];
    for (int i = 0; i < N; i++) buf[i] = x[from+i] * (float)(0.5 - 0.5*Math.Cos(2*Math.PI*i/N));
    f.Forward(buf, 0, hc); double tot = 0, band = 0;
    for (int k = 1; k < N/2; k++) { double p = hc[k]*(double)hc[k] + hc[N-k]*(double)hc[N-k]; tot += p; double hz = k*(double)SR/N; if (hz >= lo && hz < hi) band += p; }
    return band / tot; }
  static bool Finite(float[] x) { foreach (var v in x) if (float.IsNaN(v) || float.IsInfinity(v)) return false; return true; }
  static double Db(double a, double b) => 20*Math.Log10(a/b);
  static int Val(string note) => EditModelHz(note);
  static int EditModelHz(string s) => EditModel.ParseFreq(s).Value;

  static int Main() {
    var rnd = new Random(11); var noise = new float[SR*60]; for (int i=0;i<noise.Length;i++) noise[i]=(float)(rnd.NextDouble()*2-1)*8000;
    Func<long,float> music = t => (float)(6000*Math.Sin(2*Math.PI*220*t/SR) + 3000*Math.Sin(2*Math.PI*1330*t/SR+1) + 0.4*noise[t % noise.Length]);
    int delay = 4*SPT;

    Console.WriteLine("Smooth mode reconstruction (no FX, KeepDC on): output vs input delayed by TickDelay");
    foreach (var cfg in new[]{ (7,50,0), (7,5,0), (3,50,0), (11,80,0), (9,50,0x50) }) {
      var o = Run(music, null, 30, true, true, p => { p.G[P.BlkLen]=cfg.Item1; p.G[P.Overlap]=cfg.Item2; p.G[P.MixBack]=cfg.Item3; });
      double err=0, sig=0; for (long i = delay + 4*SPT; i < o.Length; i++) { double d = o[i]-music(i-delay); err+=d*d; sig+=music(i-delay)*music(i-delay); }
      Check($"BlkLen {cfg.Item1} Overlap {cfg.Item2}% MixBack {cfg.Item3:X}", 10*Math.Log10(sig/err) > 60, $"SNR {10*Math.Log10(sig/err):0.0} dB");
    }
    { // block length changing every few ticks
      var o = Run(music, null, 40, true, true, p => {}, (t,p) => { if (t%5==2) p.G[P.BlkLen] = 5 + (t/5)%5; });
      double err=0, sig=0; for (long i = delay + 4*SPT; i < o.Length; i++) { double d = o[i]-music(i-delay); err+=d*d; sig+=music(i-delay)*music(i-delay); }
      Check("BlkLen automated 5..9", 10*Math.Log10(sig/err) > 50, $"SNR {10*Math.Log10(sig/err):0.0} dB");
    }
    { // Smooth vs Blocky graininess: Smear 50% on a pure tone -> energy away from the tone
      Func<long,float> tone = t => (float)(10000*Math.Sin(2*Math.PI*1000*t/SR));
      Func<bool,double> Spur = sm => { var o = Run(tone, null, 20, sm, true, p => Set(p,0,1,Val("100"),Val("10k"),0x5000)); long a=10*SPT, n=8*SPT;
        return Rms(o,a,n)/ (Gz(o,a,n,1000)/Math.Sqrt(2)); };
      double sb = Spur(false), ss = Spur(true);
      Check("Smear artefacts lower in Smooth", ss < sb, $"total/tone ratio blocky {sb:0.000} smooth {ss:0.000}");
    }

    Console.WriteLine("Soft edges: Amp -inf over 1k..4k on noise; level at 1.1 kHz (just inside) and 2 kHz (centre)");
    {
      Func<int,double,double> L = (soft, hz) => { var o = Run(t => noise[t % noise.Length], null, 20, true, true, p => Set(p,0,0,Val("1k"),Val("4k"),0,0,soft)); return Gz(o,10*SPT,8*SPT,hz); };
      double ref11 = Gz(Run(t => noise[t % noise.Length], null, 20, true, true, p => {}),10*SPT,8*SPT,1100);
      double h0 = L(0,1100), h12 = L(12,1100), c12 = L(12,2000);
      Check("soft 0 removes 1.1 kHz", Db(h0,ref11) < -20, $"{Db(h0,ref11):0.0} dB");
      Check("soft 12 keeps most of 1.1 kHz", Db(h12,ref11) > -6, $"{Db(h12,ref11):0.0} dB");
      Check("soft 12 still removes the centre", Db(c12,ref11) < -15, $"{Db(c12,ref11):0.0} dB");
    }

    Console.WriteLine("New effects (Smooth mode)");
    Func<long,float> nz = t => noise[t % noise.Length];
    { var o = Run(nz, null, 20, true, true, p => Set(p,0,P.Gate,Val("10"),Val("24k"),0xA000));
      var r = Run(nz, null, 20, true, true, p => Set(p,0,P.Gate,Val("10"),Val("24k"),0));
      Check("Gate at 0 dBFS removes low-level noise", Db(Rms(o,10*SPT,8*SPT), Rms(r,10*SPT,8*SPT)) < -12 && Finite(o), $"{Db(Rms(o,10*SPT,8*SPT), Rms(r,10*SPT,8*SPT)):0.0} dB vs Gate off"); }
    { // Freeze: 1 kHz for 8 ticks, then 3 kHz; Value up at tick 6 -> 1 kHz should persist
      Func<long,float> sw = t => (float)(10000*Math.Sin(2*Math.PI*(t < 8*SPT ? 1000 : 3000)*t/SR));
      var o = Run(sw, null, 24, true, true, p => Set(p,0,P.Freeze,Val("20"),Val("20k"),0), (t,p) => { if (t==6) p.T[0,P.FxVal]=0xA000; });
      long a = 18*SPT, n = 5*SPT;
      Check("Freeze holds 1 kHz after input moved to 3 kHz", Gz(o,a,n,1000) > 4*Gz(o,a,n,3000) && Finite(o), $"1k {Gz(o,a,n,1000):0} vs 3k {Gz(o,a,n,3000):0}"); }
    { // Blur: 1 kHz bursts; long blur keeps energy in the gaps
      Func<long,float> bursts = t => (t / 3000) % 4 == 0 ? (float)(10000*Math.Sin(2*Math.PI*1000*t/SR)) : 0f;
      Func<int,double> Gap = v => { var o = Run(bursts, null, 20, true, true, p => Set(p,0,P.Blur,Val("20"),Val("20k"),v)); double g=0; int c=0;
        for (long blk = 10*SPT/3000; blk < (20*SPT-delay)/3000 - 1; blk++) if (blk % 4 == 2) { g += Rms(o, blk*3000 + delay, 3000); c++; } return g/c; };
      double g0 = Gap(0x800), g1 = Gap(0x7000);
      Check("Blur fills the gaps between bursts", g1 > 3*g0, $"gap rms short {g0:0} long {g1:0}"); }
    { var up = Run(t => nz(t) * 0.005f, null, 20, true, true, p => Set(p,0,P.Tilt,Val("100"),Val("10k"),0xA000));   // quiet: +36 dB must not clip
      double lo = Gz(up,10*SPT,8*SPT,200), hi = Gz(up,10*SPT,8*SPT,5000);
      Check("Tilt +12 dB/oct boosts highs over lows", Db(hi,lo) > 30, $"5k vs 200 Hz {Db(hi,lo):0.0} dB"); }
    { var rb = Run(music, null, 20, false, true, p => { Set(p,0,P.RoboWhisper,Val("20"),Val("20k"),0); p.G[P.BlkLen]=8; });
      var wh = Run(music, null, 20, true, true, p => Set(p,0,P.RoboWhisper,Val("20"),Val("20k"),0xA000));
      Check("RoboWhisper robot/whisper finite and non-silent", Finite(rb) && Finite(wh) && Rms(rb,10*SPT,4*SPT) > 100 && Rms(wh,10*SPT,4*SPT) > 100, $"rms {Rms(rb,10*SPT,4*SPT):0} / {Rms(wh,10*SPT,4*SPT):0}"); }

    Console.WriteLine("Cross with sidechain: main = noise, sidechain = 500 Hz tone");
    { Func<long,float> sct = t => (float)(10000*Math.Sin(2*Math.PI*500*t/SR));
      var o = Run(nz, sct, 20, true, true, p => Set(p,0,P.Cross,Val("10"),Val("24k"),0xA000));
      var r = Run(nz, null, 20, true, true, p => Set(p,0,P.Cross,Val("20"),Val("20k"),0xA000));
      double share = BandShare(o, 10*SPT, 400, 600);
      Check("Cross v=1 imposes the sidechain spectrum", share > 0.9 && Finite(o), $"400-600 Hz energy share {share:0.000}");
      Check("Cross without sidechain passes main through", Math.Abs(Db(Rms(r,10*SPT,8*SPT), Rms(Run(nz,null,20,true,true,p=>{}),10*SPT,8*SPT))) < 0.5, ""); }

    Console.WriteLine("All new effects x both modes x soft: no NaN/Inf");
    bool allFinite = true;
    for (int fx = 0; fx <= P.LastEffect; fx++) foreach (bool sm in new[]{false,true}) foreach (int soft in new[]{0,24}) {
      var o = Run(music, t => noise[(t*7) % noise.Length], 10, sm, true, p => { Set(p,0,fx,Val("200"),Val("5k"),0x3000,0xB0,soft); Set(p,1,fx,Val("6k"),Val("300"),0x8000,0x90,soft); },
                  (t,p) => { if (t==5) { p.T[0,P.FxVal]=0x9000; p.G[P.BlkLen]=5; } });
      if (!Finite(o)) { allFinite = false; Console.WriteLine($"    non-finite: fx {fx} smooth {sm} soft {soft}"); } }
    Check("finite everywhere", allFinite, "");
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILURES");
    return fails;
  }
}
