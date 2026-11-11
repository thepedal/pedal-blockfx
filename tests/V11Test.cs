// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// v1.1 behaviour: track auto-grow, latency reporting, LevelMatch, Capture trigger.
using System; using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;
class M7 : IMachine { public int TrackCount { get; set; } = 1; public int Latency { get; set; } = -1; }
class H7 : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; }
  public M7 M = new M7(); public IMachine Machine => M; public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=44100, SamplesPerTick=5512 }; }
static class V11Test {
  static int fails = 0;
  static void Check(string w, bool ok, string d) { if (!ok) fails++; Console.WriteLine((ok?"  ok   ":"  FAIL ")+w+"  "+d); }
  static double Run(H7 h, PedalBlockFXMachine m, Func<long,float> src, int ticks, long measureFrom, Action<int> perTick, out float[] outL) {
    var inp = new Sample[212]; var o = new Sample[212]; long pos = 0; double e = 0; long c = 0; outL = new float[ticks*5512];
    for (int t = 0; t < ticks; t++) { perTick?.Invoke(t);
      for (int k = 0; k < 5512; k += 212) { int n = Math.Min(212, 5512-k);
        for (int i=0;i<n;i++){ float v = src(pos+i); inp[i].L=v; inp[i].R=v; }
        bool r = m.Work(new[]{o}, new Sample[][]{inp,null}, n, WorkModes.WM_WRITE);
        for (int i=0;i<n;i++){ float y = r ? o[i].L : 0; outL[pos+i]=y; if (pos+i >= measureFrom) { e += y*(double)y; c++; } }
        pos += n; h.MasterInfo.PosInTick = (h.MasterInfo.PosInTick + n) % 5512; } }
    return Math.Sqrt(e / Math.Max(1,c)); }
  static double Gz(float[] x, long from, long n, double hz) { double w=2*Math.PI*hz/44100, cc=2*Math.Cos(w), s1=0, s2=0; for (long i=from;i<from+n;i++){ double s0=x[i]+cc*s1-s2; s2=s1; s1=s0; } return Math.Sqrt(s1*s1+s2*s2-cc*s1*s2)*2/n; }
  static int V(string f) => EditModel.ParseFreq(f).Value;

  static int Main() {
    PedalBlockFXMachine.UiInvoker = a => a();      // run UI-thread work inline
    Func<long,float> tone = t => (float)(8000*Math.Sin(2*Math.PI*440*t/44100.0));

    Console.WriteLine("1. Track auto-grow");
    { var h = new H7(); var m = new PedalBlockFXMachine(h);
      m.SetFreqA(0, 2); m.SetFreqB(0, 2);                       // defaults on a hidden track: no grow
      Check("default values on track 2 don't grow", h.M.TrackCount == 1, $"TrackCount {h.M.TrackCount}");
      m.SetFreqA(V("200"), 3); m.SetFreqB(V("2k"), 3);         // preset turning set 3 on
      Check("set 3 switched on grows to 4 tracks", h.M.TrackCount == 4, $"TrackCount {h.M.TrackCount}");
      h.M.TrackCount = 2; m.SetAmp(0x90, 3);                    // user shrank it; a non-frequency write doesn't regrow
      Check("non-frequency write doesn't regrow", h.M.TrackCount == 2, $"TrackCount {h.M.TrackCount}"); }

    Console.WriteLine("2. Latency reporting");
    { var h = new H7(); var m = new PedalBlockFXMachine(h);
      Check("GetLatency = TickDelay (4 ticks)", m.GetLatency() == 4*5512, $"{m.GetLatency()} samples");
      float[] o; Run(h, m, tone, 6, 0, null, out o);
      Check("pushed to the machine after it settled", h.M.Latency == 4*5512, $"Latency {h.M.Latency}");
      Run(h, m, tone, 4, 0, t => { if (t == 0) m.TickDelay = 0x200; }, out o);
      Check("TickDelay 2 ticks re-pushed", h.M.Latency == 2*5512, $"Latency {h.M.Latency}");
      // the engine's real delay equals the reported one
      var h2 = new H7(); var m2 = new PedalBlockFXMachine(h2); m2.Window = 1; m2.KeepDC = true;
      Func<long,float> click = t => t == 30000 ? 20000f : 0f;
      float[] y; Run(h2, m2, t => tone(t)*0.001f + click(t), 12, 0, null, out y);
      int pk = 0; for (int i = 1; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[pk])) pk = i;
      Check("measured delay = reported latency", pk - 30000 == m2.GetLatency(), $"click out at +{pk-30000}, reported {m2.GetLatency()}"); }

    Console.WriteLine("3. LevelMatch");
    { Func<bool,double> R = lm => { var h = new H7(); var m = new PedalBlockFXMachine(h); m.LevelMatch = lm;
        m.SetEffect(0,0); m.SetFreqA(V("20"),0); m.SetFreqB(V("22k"),0); m.SetAmp(0xA0 - 12, 0);   // -6 dB over everything
        float[] o; return Run(h, m, tone, 12, 8*5512, null, out o); };
      double on = R(true), off = R(false), refl = 8000/Math.Sqrt(2);
      Check("On: whole-band Amp -6 dB is matched away", Math.Abs(20*Math.Log10(on/refl)) < 0.5, $"{20*Math.Log10(on/refl):0.00} dB");
      Check("Off: whole-band Amp -6 dB is heard", Math.Abs(20*Math.Log10(off/refl) + 6) < 0.5, $"{20*Math.Log10(off/refl):0.00} dB"); }

    Console.WriteLine("4. Capture trigger (Freeze)");
    { var h = new H7(); var m = new PedalBlockFXMachine(h);
      m.SetEffect(P.Freeze,0); m.SetFreqA(V("50"),0); m.SetFreqB(V("18k"),0); m.SetValue(0xA000,0);
      Func<long,float> sw = t => (float)(8000*Math.Sin(2*Math.PI*(t < 10*5512 ? 1000 : 3000)*t/44100.0));
      float[] o; Run(h, m, sw, 30, 0, t => { if (t == 16) m.SetCapture(true, 0); }, out o);
      long before = 14*5512, after = 24*5512, n = 4*5512;
      Check("before Capture: still frozen on 1 kHz", Gz(o,before,n,1000) > 5*Gz(o,before,n,3000), $"1k {Gz(o,before,n,1000):0} 3k {Gz(o,before,n,3000):0}");
      Check("after Capture: frozen on 3 kHz", Gz(o,after,n,3000) > 5*Gz(o,after,n,1000), $"1k {Gz(o,after,n,1000):0} 3k {Gz(o,after,n,3000):0}");
      // Capture fires once: input moves on again, frozen sound must not follow
      var h3 = new H7(); var m3 = new PedalBlockFXMachine(h3);
      m3.SetEffect(P.Freeze,0); m3.SetFreqA(V("50"),0); m3.SetFreqB(V("18k"),0); m3.SetValue(0xA000,0);
      Func<long,float> sw3 = t => (float)(8000*Math.Sin(2*Math.PI*(t < 10*5512 ? 1000 : t < 20*5512 ? 3000 : 2000)*t/44100.0));
      Run(h3, m3, sw3, 32, 0, t => { if (t == 16) m3.SetCapture(true, 0); }, out o);
      long late = 27*5512;
      Check("Capture is one-shot (doesn't follow later input)", Gz(o,late,n,3000) > 5*Gz(o,late,n,2000), $"3k {Gz(o,late,n,3000):0} 2k {Gz(o,late,n,2000):0}"); }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILURES"); return fails; } }
