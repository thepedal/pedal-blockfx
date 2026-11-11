// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// v1.5: Value as text (ParseValue inverts ValueDesc), LFO edge travel, live band positions.
using System; using PedalBlockFX;
static class ValueTextTest {
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }
  static string D(int fx, int v) => EditModel.ValueDesc(fx, v, f => f, 44100);
  static string Typed(int fx, string text) { var v = EditModel.ParseValue(fx, text, 44100); return v.HasValue ? D(fx, v.Value) : "(null)"; }

  static int Main() {
    Console.WriteLine("Round trip: every label parses back to the same label (all 16 effects, 81 values each)");
    for (int fx = 0; fx <= P.LastEffect; fx++) {
      int bad = 0; string ex = "";
      for (int v = 0; v <= 0xA000; v += 0x200) {
        string d = D(fx, v); var p = EditModel.ParseValue(fx, d, 44100);
        string back = p.HasValue ? D(fx, p.Value) : "(null)";
        if (back != d) { bad++; if (ex == "") ex = $"'{d}' -> '{back}'"; }
      }
      Check($"{P.EffectNames[fx],-12}", bad == 0, bad == 0 ? "" : $"{bad} mismatches, e.g. {ex}");
    }

    Console.WriteLine("Typed forms");
    var cases = new (int fx, string text, string want)[] {
      (P.PitchShift, "+7", "+7.00 st"), (P.PitchShift, "-12 st", "-12.00 st"), (P.PitchShift, "0.15", "+0.15 st"), (P.PitchShift, "99", "+24.00 st"),
      (P.Gate, "-40", "-40 dBFS"), (P.Gate, "-70dbfs", "-70 dBFS"),
      (P.Blur, "1.5 s", "1.5 s"), (P.Blur, "250ms", "250 ms"), (P.Blur, "off", "off"),
      (P.Tilt, "+4", "+4.0 dB/oct"), (P.Tilt, "-5 dB/oct", "-5.0 dB/oct"),
      (6, "odd 50", "odd 50%"), (7, "both 30%", "both 30%"), (8, "even 100", "even 100%"),
      (P.RoboWhisper, "robot 100", "robot 100%"), (P.RoboWhisper, "whisper 50%", "whisper 50%"), (P.RoboWhisper, "-100", "robot 100%"), (P.RoboWhisper, "off", "off"),
      (P.Freeze, "live", "live"), (P.Freeze, "hold 55%", "hold 55%"), (P.Freeze, "100", "hold 100%"),
      (0, "+40", "+40%"), (3, "-60%", "-60%"), (1, "55", "55%"), (P.Cross, "50", "50% sidechain"),
      (4, "+150", null), (5, "-1.2k", null),
      (P.PitchShift, "0x5000", "0.00 st"), (P.PitchShift, "#40960", "+24.00 st"),
    };
    foreach (var c in cases) {
      string got = Typed(c.fx, c.text);
      bool ok = c.want != null ? got == c.want : got != "(null)";
      Check($"{P.EffectNames[c.fx],-12} '{c.text}'", ok, got);
    }
    { // shift: typed Hz lands within one parameter step of the request
      var v = EditModel.ParseValue(4, "+150 Hz", 44100).Value; float hz = P.FreqShift(P.T[P.FxVal].Frac(v), 22050);
      Check("ShiftAdd '+150 Hz' lands near 150 Hz", Math.Abs(hz - 150) < 3, $"{hz:0.0} Hz");
    }
    Check("garbage is rejected", EditModel.ParseValue(P.Gate, "loud", 44100) == null && EditModel.ParseValue(6, "50", 44100) == null && EditModel.ParseValue(P.PitchShift, "", 44100) == null, "");

    Console.WriteLine("LFO edge travel (Edit window overlay)");
    {
      int a = 0x3000, b = 0x3600;
      float aLo, aHi, bLo, bHi;
      bool t1 = EditModel.LfoEdgeTravel(P.LfoBand, 100, a, b, out aLo, out aHi, out bLo, out bHi);   // 50% = +-12 st = +-3072
      Check("Band 50%: both edges travel +-12 st", t1 && aLo == a - 3072 && aHi == a + 3072 && bLo == b - 3072 && bHi == b + 3072, $"{aLo}..{aHi} / {bLo}..{bHi}");
      bool t2 = EditModel.LfoEdgeTravel(P.LfoFreqB, 100, a, b, out aLo, out aHi, out bLo, out bHi);
      Check("FreqB: only B travels", t2 && aLo == a && aHi == a && bHi - bLo == 6144, $"{aLo}..{aHi} / {bLo}..{bHi}");
      Check("Value target / Off / depth 0: no edge travel",
        !EditModel.LfoEdgeTravel(P.LfoValue, 100, a, b, out aLo, out aHi, out bLo, out bHi) &&
        !EditModel.LfoEdgeTravel(P.LfoOff, 100, a, b, out aLo, out aHi, out bLo, out bHi) &&
        !EditModel.LfoEdgeTravel(P.LfoBand, 0, a, b, out aLo, out aHi, out bLo, out bHi), "");
      EditModel.LfoEdgeTravel(P.LfoBand, 200, 0x100, 0x200, out aLo, out aHi, out bLo, out bHi);
      Check("travel is clamped to the parameter range", aLo >= 1 && bLo >= 1, $"{aLo} / {bLo}");
    }

    Console.WriteLine("Live band positions from the engine");
    {
      const int SR = 48000, SPT = 6000;
      var e = new BlockFxEngine(SR) { Smooth = true };
      var chg = new Params(); var io = new[]{ new float[250], new float[250] };
      int a = EditModel.ParseFreq("400").Value, b = EditModel.ParseFreq("480").Value;
      Check("NaN before any block", float.IsNaN(e.LiveA[0]), "");
      float upA = 0, dnA = 0;
      for (int t = 0; t < 24; t++) {
        if (t == 0) { chg.SetDefaults(); chg.TvN = 2; chg.G[P.BlkLen] = 5;
          chg.T[0,P.FreqA]=a; chg.T[0,P.FreqB]=b; chg.T[0,P.Amp]=0;
          chg.T[0,P.LfoTarget]=P.LfoBand; chg.T[0,P.LfoShape]=4; chg.T[0,P.LfoRate]=Array.IndexOf(P.LfoTicks, 8f); chg.T[0,P.LfoDepth]=100; }
        else { chg.SetNoVal(); chg.TvN = 2; }
        e.Tick(chg, SPT);
        for (int k = 0; k < SPT; k += 250) { for (int i = 0; i < 250; i++) io[0][i] = (float)Math.Sin(i); e.Work(io, 250, 1, true, SPT);
          if (t == 13 && k == 3000) dnA = e.LiveA[0]; if (t == 17 && k == 3000) upA = e.LiveA[0]; }   // song phase 1.69 (-1), 2.19 (+1)
      }
      Check("square LFO: live FreqA at base -12 st, then +12 st", Math.Abs(upA - (a + 3072)) < 1 && Math.Abs(dnA - (a - 3072)) < 1, $"{upA - a:+0;-0} / {dnA - a:+0;-0} units (want +-3072)");
    }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
