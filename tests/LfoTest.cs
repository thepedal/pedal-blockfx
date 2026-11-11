// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Behaviour tests for v1.3: band LFO (song-position locked, through the tick FIFO).
using System; using System.Collections.Generic;
using Buzz.MachineInterface; using BuzzGUI.Interfaces; using BuzzGUI.Common; using PedalBlockFX;

class LSong : ISong { public event Action<IMachine> MachineAdded { add {} remove {} } public event Action<IMachine> MachineRemoved { add {} remove {} } public int PlayPosition { get; set; } }
class LBuzz : IBuzz { public LSong S = new LSong(); public ISong Song => S; public bool Playing { get; set; } = true; }
class LM : IMachine { public int TrackCount { get; set; } = 2; }
class LH : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; }
  public IMachine Machine { get; } = new LM(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=48000, SamplesPerTick=6000, TicksPerBeat=4 }; }

static class LfoTest {
  const int SR = 48000, SPT = 6000, DELAY = 4;     // TickDelay default = 4 ticks
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }

  // engine run; songPos(t) and playing(t) per tick (NaN = derive from audio)
  static float[] Run(Func<long,float> inp, int ticks, Action<Params> setup, Func<int,double> songPos, Func<int,bool> playing = null,
                     int blkLen = 5, bool smooth = true) {
    var e = new BlockFxEngine(SR) { Smooth = smooth, KeepDC = true, LevelMatch = false };
    var chg = new Params(); var outb = new float[(long)ticks * SPT];
    var io = new[]{ new float[250], new float[250] }; long pos = 0;
    for (int t = 0; t < ticks; t++) {
      if (t == 0) { chg.SetDefaults(); chg.TvN = 4; chg.G[P.BlkLen] = blkLen; setup(chg); } else { chg.SetNoVal(); chg.TvN = 4; }
      e.Tick(chg, SPT, songPos(t), playing == null || playing(t));
      for (int k = 0; k < SPT; ) { int n = Math.Min(250, SPT - k);
        for (int i = 0; i < n; i++) io[0][i] = inp(pos + i);
        e.Work(io, n, 1, true, SPT);
        Array.Copy(io[0], 0, outb, pos, n); pos += n; k += n; }
    }
    return outb;
  }
  static int Hz(string s) => EditModel.ParseFreq(s).Value;
  static void Set(Params p, int s, int fx, string a, string b, int val, int amp = 0xA0) {
    p.T[s,P.Effect]=fx; p.T[s,P.FreqA]=Hz(a); p.T[s,P.FreqB]=Hz(b); p.T[s,P.FxVal]=val; p.T[s,P.Amp]=amp; }
  static void Lfo(Params p, int s, int target, int shape, int rate, double depthPct, int phase = 0) {
    p.T[s,P.LfoTarget]=target; p.T[s,P.LfoShape]=shape; p.T[s,P.LfoRate]=rate; p.T[s,P.LfoDepth]=(int)Math.Round(depthPct*2); p.T[s,P.LfoPhase]=phase; }
  static int Rate(float ticks) => Array.IndexOf(P.LfoTicks, ticks);
  static double Rms(float[] x, long from, long n) { double e=0; for (long i=from;i<from+n;i++) e+=x[i]*(double)x[i]; return Math.Sqrt(e/n); }
  // level of the middle half of output tick t
  static double TickRms(float[] x, int t) => Rms(x, (long)t*SPT + SPT/4, SPT/2);
  static double Gz(float[] x, long from, long n, double hz) {
    double w = 2*Math.PI*hz/SR, c = 2*Math.Cos(w), s1=0, s2=0;
    for (long i = from; i < from+n; i++) { double win = 0.5-0.5*Math.Cos(2*Math.PI*(i-from)/n); double s0 = x[i]*win + c*s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(Math.Max(0, s1*s1 + s2*s2 - c*s1*s2)) * 4 / n; }
  static double Peak(float[] x, long from, long n, double lo, double hi) {
    double best = 0, bf = lo; for (double f = lo; f <= hi; f += 0.5) { double a = Gz(x, from, n, f); if (a > best) { best = a; bf = f; } } return bf; }
  static double Db(double a, double b) => 20*Math.Log10(Math.Max(a,1e-9)/Math.Max(b,1e-9));

  // Gate with Value LFO, square: +1 half (song phase 0..0.5) threshold 0 dBFS = tone gated; -1 half: open.
  static void GateSquare(Params p, int phase = 0) {
    Set(p, 0, P.Gate, "30", "20k", EditModel.FracToValue(0.5f));      // base -50 dBFS
    Lfo(p, 0, P.LfoValue, 4, Rate(8), 100, phase);                       // +-50 dB, 8-tick period
  }
  static Func<long,float> Tone(double f, double a = 8000) => t => (float)(a*Math.Sin(2*Math.PI*f*t/SR));

  // expected gate state for output tick t, given the song position of the input tick that produced it
  static string Pattern(float[] o, int from, int to) { var sb = new System.Text.StringBuilder(); double hi = 0;
    for (int t = from; t < to; t++) hi = Math.Max(hi, TickRms(o, t));
    for (int t = from; t < to; t++) sb.Append(TickRms(o, t) > hi * 0.3 ? 'o' : '.'); return sb.ToString(); }
  static string Expected(Func<int,double> song, int from, int to, double phaseOff = 0) { var sb = new System.Text.StringBuilder();
    for (int t = from; t < to; t++) { double s = song(t - DELAY) + 0.5; double ph = s / 8 + phaseOff; ph -= Math.Floor(ph); sb.Append(ph < 0.5 ? '.' : 'o'); }
    return sb.ToString(); }

  static int Main() {
    Console.WriteLine("Band LFO timing (Gate threshold square-modulated, 8-tick period, TickDelay 4)");
    {
      Func<int,double> song = t => t;
      var o = Run(Tone(1000), 40, p => GateSquare(p), song);
      string got = Pattern(o, 8, 40), exp = Expected(song, 8, 40);
      Check("pattern follows the song position, 4 ticks late", got == exp, $"got {got} / expected {exp}");
      double open = TickRms(o, 8), shut = TickRms(o, 12);
      Check("gated half is well down", Db(shut, open) < -15, $"{Db(shut, open):0.0} dB");
    }
    {
      Func<int,double> song = t => t + 100;                    // starting mid-song: same musical phase as tick 100
      var o = Run(Tone(1000), 40, p => GateSquare(p), song);
      Check("starting at song tick 100 lands on the song's phase", Pattern(o, 8, 40) == Expected(song, 8, 40), Pattern(o, 8, 40));
    }
    {
      Func<int,double> song = t => t;
      var o = Run(Tone(1000), 40, p => GateSquare(p, 128), song);
      Check("phase 180 degrees inverts the pattern", Pattern(o, 8, 40) == Expected(song, 8, 40, 0.5), Pattern(o, 8, 40));
    }
    {
      Func<int,double> song = t => t < 0 ? t : 4 + (t % 12);   // song loop: 4..15, back to 4
      var o = Run(Tone(1000), 52, p => GateSquare(p), song);
      string got = Pattern(o, 8, 52), exp = Expected(song, 8, 52);
      Check("song loop: the LFO jumps with the song position", got == exp, $"got {got} / expected {exp}");
    }
    {
      Func<int,double> song = t => t < 10 ? t : 10;            // transport stops at tick 10 (position 10 = phase 0.25: gated)
      var o = Run(Tone(1000), 40, p => GateSquare(p), song, t => t < 10);
      string got = Pattern(o, 8, 40);
      Check("stopped transport holds the LFO", got.Substring(8 + DELAY) == new string('.', got.Length - 8 - DELAY), got);
    }

    Console.WriteLine("Band LFO targets");
    {
      // band-pass 400-480 Hz (outside removed); Band square +-12 st: up half passes 880, down half passes 220
      Func<long,float> two = t => Tone(880, 4000)(t) + Tone(220, 4000)(t);
      var o = Run(two, 40, p => { Set(p, 0, 0, "480", "400", 0, 0); Lfo(p, 0, P.LfoBand, 4, Rate(8), 50); }, t => t, null, 7);
      // song phase 0..0.5 (square +1, band up at 880): output ticks 4..7 (+8k)
      double up880 = Gz(o, 13*SPT, 2*SPT, 880), up220 = Gz(o, 13*SPT, 2*SPT, 220);
      double dn880 = Gz(o, 17*SPT, 2*SPT, 880), dn220 = Gz(o, 17*SPT, 2*SPT, 220);
      Check("Band: + moves the band up an octave, - down", Db(up880, up220) > 30 && Db(dn220, dn880) > 30,
        $"up: 880 {Db(up880,4000):0} dB, 220 {Db(up220,4000):0} dB; down: 220 {Db(dn220,4000):0} dB, 880 {Db(dn880,4000):0} dB");
    }
    {
      // Width square +-12 st on a band-pass 400-480 (outside removed): wide half keeps 200-960 (880 passes);
      // narrow half: edges cross, so nothing is kept (the set stays an outside set, it doesn't flip)
      Func<long,float> one = t => Tone(880, 4000)(t);
      var o = Run(one, 40, p => { Set(p, 0, 0, "480", "400", 0, 0); Lfo(p, 0, P.LfoWidth, 4, Rate(8), 50); }, t => t, null, 7);
      double w880 = Gz(o, 13*SPT, 2*SPT, 880), n880 = Gz(o, 17*SPT, 2*SPT, 880);
      Check("Width: wide half passes 880 Hz, crossed edges keep nothing", Db(w880, 4000) > -3 && Db(n880, 4000) < -60,
        $"880 Hz: {Db(w880,4000):0} dB vs {Db(n880,4000):0} dB");
      // the same on an inside notch 400-480 (Amp -inf): crossed edges remove nothing, wide half removes 880
      var o2 = Run(one, 40, p => { Set(p, 0, 0, "400", "480", 0, 0); Lfo(p, 0, P.LfoWidth, 4, Rate(8), 50); }, t => t, null, 7);
      double w2 = Gz(o2, 13*SPT, 2*SPT, 880), n2 = Gz(o2, 17*SPT, 2*SPT, 880);
      Check("Width on a notch: wide half removes 880 Hz, crossed edges remove nothing", Db(w2, 4000) < -60 && Db(n2, 4000) > -1,
        $"880 Hz: {Db(w2,4000):0} dB vs {Db(n2,4000):0} dB");
    }
    {
      // FreqA square on a notch 400-480 (inside, Amp -inf): pushing A past B must not flip the set to outside mode
      var o = Run(Tone(1000), 40, p => { Set(p, 0, 0, "400", "480", 0, 0); Lfo(p, 0, P.LfoFreqA, 4, Rate(8), 100); }, t => t, null, 7);
      double a = TickRms(o, 13), b = TickRms(o, 17);
      Check("FreqA past FreqB doesn't flip to outside mode (1 kHz never removed)", Db(a, 5657) > -1 && Db(b, 5657) > -1, $"{Db(a,5657):0.0} / {Db(b,5657):0.0} dB");
    }
    {
      // PitchShift with a Value LFO, square 25%: +-6 semitones around 0
      var o = Run(Tone(440), 40, p => { p.G[P.Overlap] = 75; Set(p, 0, P.PitchShift, "30", "20k", 0x5000); Lfo(p, 0, P.LfoValue, 4, Rate(8), 25); }, t => t, null, 7);
      double up = Peak(o, 13*SPT, 2*SPT, 560, 680), dn = Peak(o, 17*SPT, 2*SPT, 280, 340);
      Check("Value LFO on PitchShift: +6 st / -6 st", Math.Abs(up - 622.25) < 2 && Math.Abs(dn - 311.13) < 2, $"{up:0.0} Hz / {dn:0.0} Hz");
    }
    {
      // S/H: deterministic (same output twice), different levels from cycle to cycle
      Func<Func<int,double>, float[]> sh = song => Run(Tone(1000), 30, p => { Set(p, 0, P.Gate, "30", "20k", EditModel.FracToValue(0.5f)); Lfo(p, 0, P.LfoValue, 5, Rate(2), 100); }, song);
      var a = sh(t => t); var b = sh(t => t);
      bool same = true; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { same = false; break; }
      var lv = new HashSet<float>(); bool inRange = true, steady = true;
      for (int c = 0; c < 64; c++) { float v = P.LfoShapeAt(5, c + 0.1, 0); lv.Add(v); if (v < -1 || v > 1) inRange = false; if (P.LfoShapeAt(5, c + 0.9, 0) != v) steady = false; }
      bool perSet = P.LfoShapeAt(5, 3.5, 0) != P.LfoShapeAt(5, 3.5, 1);
      Check("S/H: repeats exactly, holds within a cycle, varies per cycle and per set", same && lv.Count == 64 && inRange && steady && perSet,
        $"identical {same}, {lv.Count}/64 distinct, in range {inRange}, held {steady}, per set {perSet}");
    }
    {
      // depth 0 or target off: identical to no LFO
      Func<long,float> mus = t => Tone(220, 5000)(t) + Tone(1330, 3000)(t);
      var r0 = Run(mus, 16, p => Set(p, 0, 1, "100", "5k", 0x5000), t => t);
      var r1 = Run(mus, 16, p => { Set(p, 0, 1, "100", "5k", 0x5000); Lfo(p, 0, P.LfoBand, 0, 0, 0); }, t => t);
      var r2 = Run(mus, 16, p => { Set(p, 0, 1, "100", "5k", 0x5000); Lfo(p, 0, P.LfoOff, 0, 0, 100); }, t => t);
      bool s1 = true, s2 = true; for (int i = 0; i < r0.Length; i++) { if (r0[i] != r1[i]) s1 = false; if (r0[i] != r2[i]) s2 = false; }
      Check("depth 0 / target Off are bit-identical to no LFO", s1 && s2, $"{s1} / {s2}");
    }
    {
      // every target x shape x extreme depth, both modes: finite
      bool fin = true; string bad = "";
      for (int tg = 1; tg <= 5; tg++) for (int sh = 0; sh <= 5; sh++) foreach (bool sm in new[]{ false, true }) {
        var o = Run(Tone(700), 10, p => { Set(p, 0, P.PitchShift, "50", "10k", 0x7000); Set(p, 1, 3, "10k", "50", 0x6000); Lfo(p, 0, tg, sh, 0, 100); Lfo(p, 1, tg, sh, 3, 100, 64); }, t => t, null, 5, sm);
        foreach (var v in o) if (float.IsNaN(v) || float.IsInfinity(v)) { fin = false; bad = $"target {tg} shape {sh} smooth {sm}"; break; }
      }
      Check("all targets x shapes x modes, full depth: finite", fin, bad);
    }

    Console.WriteLine("Machine glue (song position from ISong.PlayPosition)");
    {
      var buzz = new LBuzz(); Global.Buzz = buzz; PedalBlockFXMachine.UiInvoker = a => a();
      var h = new LH(); var m = new PedalBlockFXMachine(h);
      m.Window = 1; m.BlockLen = 5; m.LevelMatch = false;
      m.SetFreqA(Hz("30"), 0); m.SetFreqB(Hz("20k"), 0); m.SetEffect(P.Gate, 0); m.SetValue(EditModel.FracToValue(0.5f), 0);
      m.SetLfoTarget(P.LfoValue, 0); m.SetLfoShape(4, 0); m.SetLfoRate(Rate(8), 0); m.SetLfoDepth(200, 0);
      var inp = new Sample[250]; var outp = new Sample[250]; var o = new float[40*SPT]; long pos = 0; var tone = Tone(1000);
      buzz.S.PlayPosition = 200;
      for (int t = 0; t < 40; t++) {
        for (int k = 0; k < SPT; k += 250) {
          if (t == 20 && k == 3000) m.SetLfoPhase(0, 0);     // a mid-tick setter (dirty commit) must not disturb the phase
          for (int i = 0; i < 250; i++) { float v = tone(pos + i); inp[i].L = v; inp[i].R = v; }
          bool r = m.Work(new[]{ outp }, new Sample[][]{ inp, null }, 250, WorkModes.WM_WRITE);
          for (int i = 0; i < 250; i++) o[pos + i] = r ? outp[i].L : 0;
          pos += 250; h.MasterInfo.PosInTick = (h.MasterInfo.PosInTick + 250) % SPT;
          if (h.MasterInfo.PosInTick == 0) buzz.S.PlayPosition++;
        }
      }
      Func<int,double> song = t => 200 + t;
      string got = Pattern(o, 8, 40), exp = Expected(song, 8, 40);
      Check("machine reads PlayPosition; mid-tick commit keeps the phase", got == exp, $"got {got} / expected {exp}");
      Check("labels", m.DescribeValue(new NamedParam("LfoRate"), Rate(16)) == "16 ticks (4 beats)"
                   && m.DescribeValue(new NamedParam("LfoDepth"), 25) == "12.5%"
                   && m.DescribeValue(new NamedParam("LfoPhase"), 64) == "90°"
                   && m.DescribeValue(new NamedParam("LfoTarget"), 4) == "Width"
                   && EditModel.LfoSwingText(P.LfoBand, 100) == "±12 st",
            $"{m.DescribeValue(new NamedParam("LfoRate"), Rate(16))} / {m.DescribeValue(new NamedParam("LfoDepth"), 25)} / {m.DescribeValue(new NamedParam("LfoPhase"), 64)} / {EditModel.LfoSwingText(P.LfoBand, 100)}");
      Global.Buzz = null;
    }

    Console.WriteLine("v1.7: free-running Hz rates, block-averaged LFO");
    {
      int hz1 = P.LfoTicks.Length + Array.IndexOf(P.LfoHz, 1f);          // 1 Hz = 48000 samples = 8 ticks
      Func<int,double> stopped = t => 10;
      var o = Run(Tone(1000), 40, p => { GateSquare(p); p.T[0, P.LfoRate] = hz1; }, stopped, t => false);
      string pat = Pattern(o, 8, 40); int opens = 0, flips = 0;
      for (int i = 0; i < pat.Length; i++) { if (pat[i] == 'o') opens++; if (i > 0 && pat[i] != pat[i-1]) flips++; }
      Check("1 Hz runs with the transport stopped (tempo rates hold)", flips >= 6 && opens >= 12 && opens <= 20, pat);
      var o2 = Run(Tone(1000), 40, p => { GateSquare(p); p.T[0, P.LfoRate] = hz1; }, t => (t % 12) + 4);   // song looping
      Check("Hz rate ignores song jumps (same as stopped)", Pattern(o2, 8, 40) == pat, Pattern(o2, 8, 40));
    }
    {
      var e = new BlockFxEngine(SR) { Smooth = true };
      int slow = P.LfoTicks.Length;                                       // 0.05 Hz
      e._lfoBlkStart = 0; e._lfoBlkLen = 4096;
      float avg = e.LfoAt(0, 2, slow, 0f);                                // saw up
      double centre = 2 * (2048 * 0.05 / SR) - 1, start = -1;
      Check("slow LFO = its value at the block centre, not the start (no half-block lag)",
            Math.Abs(avg - centre) < 1e-4 && avg - start > 0.004, $"{avg:0.00000} vs centre {centre:0.00000}, start {start}");
      int fast = P.LfoRateCount - 1;                                      // 12 Hz, ~16 cycles in a 65536 block
      double worstAvg = 0, worstPoint = 0;
      for (long st = 0; st < 200000; st += 7919) {
        e._lfoBlkStart = st; e._lfoBlkLen = 65536;
        worstAvg = Math.Max(worstAvg, Math.Abs(e.LfoAt(0, 0, fast, 0f)));
        worstPoint = Math.Max(worstPoint, Math.Abs(Math.Sin(2 * Math.PI * st * 12.0 / SR)));
      }
      Check("12 Hz in a 65536 block fades instead of aliasing", worstAvg < 0.02, $"averaged max {worstAvg:0.0000}, point samples up to {worstPoint:0.00}");
      e.Smooth = false; e._lfoBlkStart = 0; e._lfoBlkLen = 4096;
      Check("Blocky: flat average, still centred", Math.Abs(e.LfoAt(0, 2, slow, 0f) - centre) < 1e-4, $"{e.LfoAt(0, 2, slow, 0f):0.00000}");
    }
    {
      Check("rate labels", EditModel.LfoRateText(P.LfoTicks.Length, 4) == "0.05 Hz (free)" && EditModel.LfoRateText(P.LfoRateCount - 1, 4) == "12 Hz (free)"
            && EditModel.LfoRateText(10, 4) == "16 ticks (4 beats)" && P.LfoRateCount == 35,
            EditModel.LfoRateText(P.LfoTicks.Length, 4) + " / " + EditModel.LfoRateText(P.LfoRateCount - 1, 4));
      int r12 = P.LfoRateCount - 1;
      string a = EditModel.LfoSpeedHint(r12, SPT, SR, 2048), b = EditModel.LfoSpeedHint(r12, SPT, SR, 4096),
             c = EditModel.LfoSpeedHint(10, SPT, SR, 2048), d = EditModel.LfoSpeedHint(0, SPT, SR, 512), z = EditModel.LfoSpeedHint(r12, SPT, SR, 0);
      Check("speed hint: steps / fades / fine / quarter tick / unknown", a.StartsWith("steps") && b.StartsWith("too fast") && c == "" && d.StartsWith("steps") && z == "",
            $"'{a}' '{b}' '{c}' '{d}' '{z}'");
    }
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
class NamedParam : IParameter { public NamedParam(string n) { Name = n; } public string Name { get; } }
