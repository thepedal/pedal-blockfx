// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Behaviour tests for v2.2: note-following bands (Note column, Follow, MIDI).
using System; using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;
class NtM : IMachine { public int TrackCount { get; set; } = 2; }
class NtH : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; }
  public IMachine Machine { get; } = new NtM(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=48000, SamplesPerTick=6000, TicksPerBeat=4 }; }
class NtP : IParameter { public NtP(string n) { Name = n; } public string Name { get; } }
static class NoteTest {
  const int SR = 48000, SPT = 6000;
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }

  static BlockFxEngine last;
  static float[] Run(Func<long,float> inp, int ticks, Action<Params> setup, Action<int, Params> perTick = null, bool levelMatch = false) {
    var e = new BlockFxEngine(SR) { Smooth = true, KeepDC = true, LevelMatch = levelMatch }; last = e;
    var chg = new Params(); var outb = new float[(long)ticks * SPT];
    var io = new[]{ new float[256], new float[256] };
    long pos = 0;
    for (int t = 0; t < ticks; t++) {
      if (t == 0) { chg.SetDefaults(); chg.TvN = 2; setup(chg); } else { chg.SetNoVal(); chg.TvN = 2; }
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
  static int N(string name) {   // "C-4", "E-4", "C#5"
    string[] nn = { "C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-" };
    return P.NoteFromIndex(Array.IndexOf(nn, name.Substring(0, 2)) + 12 * (name[2] - '0')); }
  static int Semis(double st) => EditModel.FracToValue((float)((st / P.PitchRange + 1) / 2));
  static double Gz(float[] x, long from, long n, double hz) {
    double w = 2*Math.PI*hz/SR, c = 2*Math.Cos(w), s1=0, s2=0;
    for (long i = from; i < from+n; i++) { double win = 0.5-0.5*Math.Cos(2*Math.PI*(i-from)/n); double s0 = x[i]*win + c*s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(Math.Max(0, s1*s1 + s2*s2 - c*s1*s2)) * 4 / n; }
  static double Db(double a, double b) => 20*Math.Log10(Math.Max(a, 1e-30)/b);
  static bool Finite(float[] x) { foreach (var v in x) if (float.IsNaN(v) || float.IsInfinity(v)) return false; return true; }
  static Func<long,float> Sines(params double[] f) => t => { double s = 0; foreach (var x in f) s += 3000*Math.Sin(2*Math.PI*x*t/SR); return (float)s; };
  // band-pass G#3..E4 as set for C-4: an outside set with Amp -inf removes everything else
  static void BandPass(Params p, int follow) {
    p.T[0,P.Effect]=0; p.T[0,P.FreqA]=Hz("330"); p.T[0,P.FreqB]=Hz("208"); p.T[0,P.Amp]=0; p.T[0,P.Follow]=follow;
    p.G[P.BlkLen]=7; p.G[P.Overlap]=75; }

  static int Main() {
    Console.WriteLine("Note-following (Smooth, 4096, Overlap 75%, TickDelay 4)");
    const double C4 = 261.63, E4 = 329.63, C5 = 523.25, G3 = 196.0;
    var tones = Sines(C4, C5, G3);
    long seg = 4*SPT;

    { // Band: the band-pass jumps an octave with C-5 at tick 10 (heard from 14), back with off at 20 (from 24)
      var o = Run(tones, 30, p => BandPass(p, P.FollowBand), (t, p) => { if (t == 10) p.T[0,P.Note] = N("C-5"); if (t == 20) p.T[0,P.Note] = P.NoteOff; });
      double a1 = Db(Gz(o, 8*SPT, seg, C4), 3000), b1 = Db(Gz(o, 8*SPT, seg, C5), 3000);
      double a2 = Db(Gz(o, 15*SPT, seg, C4), 3000), b2 = Db(Gz(o, 15*SPT, seg, C5), 3000);
      double a3 = Db(Gz(o, 25*SPT, seg, C4), 3000), b3 = Db(Gz(o, 25*SPT, seg, C5), 3000);
      double g = Db(Gz(o, 15*SPT, seg, G3), 3000);
      Check("Band: C-4 passes 262 Hz; C-5 moves the band an octave; off moves it back",
        a1 > -3 && b1 < -40 && a2 < -40 && b2 > -3 && a3 > -3 && b3 < -40 && g < -40,
        $"before {a1:0.0}/{b1:0.0} dB, C-5 {a2:0.0}/{b2:0.0} dB, off {a3:0.0}/{b3:0.0} dB (262/523 Hz); 196 Hz {g:0.0} dB");
      // timing: per-tick level at 523 Hz around tick 14
      string pat = ""; for (int t = 11; t < 18; t++) pat += Db(Gz(o, t*SPT, SPT, C5), 3000) > -10 ? "o" : ".";
      Check("Band: the note lands TickDelay (4 ticks) later", pat.StartsWith("..") && pat.EndsWith("ooo"), "ticks 11-17 at 523 Hz: " + pat);
    }

    { // a note holds through later ticks (NoValue) and a new note replaces it
      var o = Run(tones, 30, p => BandPass(p, P.FollowBand), (t, p) => { if (t == 4) p.T[0,P.Note] = N("C-5"); if (t == 18) p.T[0,P.Note] = N("G-3"); });
      double h = Db(Gz(o, 12*SPT, seg, C5), 3000), gg = Db(Gz(o, 24*SPT, seg, G3), 3000), cc = Db(Gz(o, 24*SPT, seg, C5), 3000);
      Check("a note holds until the next one", h > -3 && gg > -3 && cc < -40, $"C-5 held {h:0.0} dB; after G-3: 196 Hz {gg:0.0} dB, 523 Hz {cc:0.0} dB");
    }

    { // Follow off: notes are ignored (bit-identical to no notes)
      var a = Run(tones, 16, p => BandPass(p, P.FollowOff), (t, p) => { if (t == 3) p.T[0,P.Note] = N("C-5"); });
      var b = Run(tones, 16, p => BandPass(p, P.FollowOff));
      bool same = true; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { same = false; break; }
      Check("Follow off ignores notes (bit-identical)", same, "");
      var c = Run(tones, 16, p => BandPass(p, P.FollowBand), (t, p) => { if (t == 3) p.T[0,P.Note] = N("C-4"); });
      same = true; for (int i = 0; i < c.Length; i++) if (b[i] != c[i]) { same = false; break; }
      Check("C-4 is the reference: Band with C-4 is bit-identical to no note", same, "");
    }

    { // FreqA: only FreqA moves (live edges)
      Run(tones, 12, p => { p.T[0,P.Effect]=6; p.T[0,P.FreqA]=0x3000; p.T[0,P.FreqB]=Hz("12k"); p.T[0,P.FxVal]=0x5000; p.T[0,P.Follow]=P.FollowFreqA; },
          (t, p) => { if (t == 2) p.T[0,P.Note] = N("E-4"); });
      float la = last.LiveA[0], lb = last.LiveB[0];
      Check("FreqA: E-4 moves FreqA up 4 st, FreqB stays", Math.Abs(la - (0x3000 + 4*256)) < 0.5f && Math.Abs(lb - Hz("12k")) < 0.5f && last.LiveNote[0] == N("E-4"),
        $"FreqA {la:0} (want {0x3000 + 4*256}), FreqB {lb:0} (want {Hz("12k")}), note {P.NoteName(last.LiveNote[0])}");
    }

    { // FreqA pushed past FreqB never flips the set: an inside notch with crossed edges removes nothing
      var o = Run(Sines(450), 20, p => { p.T[0,P.Effect]=0; p.T[0,P.FreqA]=Hz("400"); p.T[0,P.FreqB]=Hz("500"); p.T[0,P.Amp]=0; p.T[0,P.Follow]=P.FollowFreqA;
        p.G[P.BlkLen]=7; p.G[P.Overlap]=75; }, (t, p) => { if (t == 2) p.T[0,P.Note] = N("C-7"); });
      double l = Db(Gz(o, 12*SPT, seg, 450), 3000);
      var o2 = Run(Sines(450), 20, p => { p.T[0,P.Effect]=0; p.T[0,P.FreqA]=Hz("400"); p.T[0,P.FreqB]=Hz("500"); p.T[0,P.Amp]=0; p.G[P.BlkLen]=7; p.G[P.Overlap]=75; });
      double l2 = Db(Gz(o2, 12*SPT, seg, 450), 3000);
      Check("FreqA past FreqB: no flip (notch empties instead of removing everything else)", l > -1 && l2 < -40, $"450 Hz with C-7 {l:0.0} dB (without note {l2:0.0} dB)");
    }

    { // Pitch: PitchShift's interval follows the note (C-4 = unison)
      var o = Run(Sines(440), 30, p => { p.T[0,P.Effect]=P.PitchShift; p.T[0,P.FreqA]=Hz("30"); p.T[0,P.FreqB]=Hz("20k"); p.T[0,P.FxVal]=Semis(0);
        p.T[0,P.Follow]=P.FollowPitch; p.G[P.BlkLen]=7; p.G[P.Overlap]=75; },
        (t, p) => { if (t == 2) p.T[0,P.Note] = N("E-4"); if (t == 16) p.T[0,P.Note] = N("G-3"); });
      double up = Db(Gz(o, 9*SPT, seg, 554.37), 3000), dn = Db(Gz(o, 23*SPT, seg, 440 * Math.Pow(2, -5 / 12.0)), 3000), orig = Db(Gz(o, 9*SPT, seg, 440), 3000);
      Check("Pitch: E-4 shifts +4 st, G-3 shifts -5 st", up > -3 && dn > -3 && orig < -40, $"554 Hz {up:0.0} dB, 330 Hz {dn:0.0} dB, 440 left {orig:0.0} dB");
      var o2 = Run(Sines(440), 20, p => { p.T[0,P.Effect]=P.PitchShift; p.T[0,P.FreqA]=Hz("30"); p.T[0,P.FreqB]=Hz("20k"); p.T[0,P.FxVal]=Semis(7);
        p.T[0,P.Follow]=P.FollowPitch; p.G[P.BlkLen]=7; p.G[P.Overlap]=75; }, (t, p) => { if (t == 2) p.T[0,P.Note] = N("C-6"); });
      double clamp = Db(Gz(o2, 10*SPT, seg, 440 * 4), 3000);
      Check("Pitch: Value + note is clamped to +-24 st", clamp > -3 && Finite(o2), $"+7 st + 24 st -> 1760 Hz {clamp:0.0} dB");
    }

    { // with the band LFO: the LFO moves around the followed position
      Run(tones, 14, p => { BandPass(p, P.FollowBand); p.T[0,P.LfoTarget]=P.LfoBand; p.T[0,P.LfoDepth]=10; p.T[0,P.LfoRate]=4; },
          (t, p) => { if (t == 1) p.T[0,P.Note] = N("C-6"); });
      float la = last.LiveA[0];
      float home = Hz("330") + 24 * 256;
      Check("LFO + Follow: the LFO swings around the followed band", Math.Abs(la - home) <= 0.05f * 24 * 256 + 1, $"FreqA {la:0}, followed home {home:0}, swing +-{0.05f*24*256:0}");
    }

    { // robustness: extreme notes, every follow mode, both window modes
      bool ok = true;
      foreach (var f in new[]{ 1, 2, 3 }) foreach (var nt in new[]{ N("C-0"), N("B-9"), 156, 200, 255 }) {
        var o = Run(tones, 8, p => { BandPass(p, f); if (f == 3) { p.T[0,P.Effect]=P.PitchShift; p.T[0,P.FxVal]=Semis(0); } }, (t, p) => { if (t == 1) p.T[0,P.Note] = nt; });
        ok &= Finite(o); }
      Check("finite: C-0, B-9, invalid and off notes, every mode", ok, "");
    }

    { // machine: Note setter, Follow, MIDI with last-note priority, labels
      var h = new NtH(); var m = new PedalBlockFXMachine(h);
      m.SetFollow(P.FollowBand, 0); m.SetFollow(P.FollowOff, 1);
      m.MidiNote(0, 60, 100);                       // MIDI 60 -> Buzz C-5 (ReBuzz's Note.FromMIDINote)
      int n0 = m.GetT(0, P.Note), n1 = m.GetT(1, P.Note);
      m.MidiNote(0, 64, 100); int n2 = m.GetT(0, P.Note);
      m.MidiNote(0, 60, 0); int n3 = m.GetT(0, P.Note);      // releasing an older key: no change
      m.MidiNote(0, 64, 0); int n4 = m.GetT(0, P.Note);      // releasing the latest: off
      Check("MIDI: steers following sets only, last-note priority, release sends off",
        n0 == N("C-5") && n1 == 0 && n2 == N("E-5") && n3 == N("E-5") && n4 == P.NoteOff,
        $"{P.NoteName(n0)} / set 1 {n1} / {P.NoteName(n2)} / {P.NoteName(n3)} / {P.NoteName(n4)}");
      m.SetNote(new Note(N("A-3")), 1);
      Check("Note setter", m.GetT(1, P.Note) == N("A-3"), P.NoteName(m.GetT(1, P.Note)));
      bool lab = m.DescribeValue(new NtP("Follow"), 1) == "Band" && m.DescribeValue(new NtP("Follow"), 3) == "Pitch"
              && m.DescribeValue(new NtP("Note"), N("C#4")) == "C#4" && m.DescribeValue(new NtP("Note"), 255) == "off"
              && P.NoteIndex(N("C-4")) == P.NoteRef && P.NoteIndex(0) == -1 && P.NoteIndex(0x4D) == -1;
      Check("labels and note maths", lab, $"{m.DescribeValue(new NtP("Follow"), 2)}, {m.DescribeValue(new NtP("Note"), N("C#4"))}, C-4 = {P.NoteIndex(N("C-4"))}");
      // through Work: a MIDI note lands in the engine
      var inp = new Sample[256]; var o = new Sample[256];
      for (int b = 0; b < 200; b++) { for (int i = 0; i < 256; i++) { float v = (float)(3000*Math.Sin(2*Math.PI*523.25*(b*256+i)/SR)); inp[i].L = v; inp[i].R = v; }
        m.Work(new[]{o}, new Sample[][]{inp, null}, 256, WorkModes.WM_WRITE); h.MasterInfo.PosInTick = (h.MasterInfo.PosInTick + 256) % SPT; }
      m.SetFreqA(Hz("330"), 0); m.SetFreqB(Hz("208"), 0); m.SetAmp(0, 0);
      m.MidiNote(0, 60, 100);
      for (int b = 0; b < 400; b++) { for (int i = 0; i < 256; i++) { float v = (float)(3000*Math.Sin(2*Math.PI*523.25*(b*256+i)/SR)); inp[i].L = v; inp[i].R = v; }
        m.Work(new[]{o}, new Sample[][]{inp, null}, 256, WorkModes.WM_WRITE); h.MasterInfo.PosInTick = (h.MasterInfo.PosInTick + 256) % SPT; }
      Check("machine: a MIDI note reaches the engine", m.LiveNote(0) == N("C-5"), P.NoteName(m.LiveNote(0)));
    }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
