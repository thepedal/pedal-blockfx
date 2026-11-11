// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
using System; using System.Collections.Generic; using PedalBlockFX;
static class EditModelTest {
  static int fails = 0;
  static void Eq(string what, object got, object want) { bool ok = Equals(got, want); if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + " = " + got + (ok ? "" : "  (want " + want + ")")); }
  static int Main() {
    Console.WriteLine("ParseFreq:");
    Eq("c4", EditModel.ParseFreq("c4"), (int?)0x3000);
    Eq("A4", EditModel.ParseFreq("A4"), (int?)0x3900);
    Eq("440", EditModel.ParseFreq("440"), (int?)0x3900);
    Eq("440Hz", EditModel.ParseFreq("440Hz"), (int?)0x3900);
    Eq("c#4", EditModel.ParseFreq("c#4"), (int?)0x3100);
    Eq("bb3", EditModel.ParseFreq("bb3"), (int?)(3*12*256+10*256));
    Eq("c4:-50", EditModel.ParseFreq("c4:-50"), (int?)(0x3000-128));
    Eq("0x2400", EditModel.ParseFreq("0x2400"), (int?)0x2400);
    Eq("off", EditModel.ParseFreq("off"), (int?)0);
    Eq("0", EditModel.ParseFreq("0"), (int?)0);
    Eq("1k ~ b5", Math.Abs(EditModel.ParseFreq("1k").Value - 18231) <= 2, true);
    Eq("junk", EditModel.ParseFreq("zz"), (int?)null);
    // round trip through the original's label format
    float hz = P.FreqHz(0x3000); string lbl = Display.Freq(hz);
    Eq("label(c4)", lbl, "262Hz,c4:0");
    Console.WriteLine("ValueDesc:");
    Func<float,float> rf = f => f;
    Eq("contrast 0.5", EditModel.ValueDesc(0, 0x5000, rf, 44100), "0%");
    Eq("contrast 1.0", EditModel.ValueDesc(0, 0xA000, rf, 44100), "+100%");
    Eq("weed 0", EditModel.ValueDesc(3, 0, rf, 44100), "-100%");
    Eq("smear 0.25", EditModel.ValueDesc(1, 0x2800, rf, 44100), "25%");
    Eq("shift 0.5", EditModel.ValueDesc(4, 0x5000, rf, 44100), "0 Hz");
    Eq("shift 1.0", EditModel.ValueDesc(5, 0xA000, rf, 44100), "+21969 Hz");
    Eq("harm 0", EditModel.ValueDesc(6, 0, rf, 44100), "both 100%");
    Eq("harm 0.5", EditModel.ValueDesc(6, 0x5000, rf, 44100), "odd 50%");
    EditModel.SplitHarm(EditModel.JoinHarm(2, 0.3f), out int ty, out float wd);
    Eq("harm join/split", ty + "/" + Math.Round(wd, 3), "2/0.3");
    Console.WriteLine("Axis:");
    int mv = SpectrumTap.HzToVal(22050);
    Eq("val->x->val", EditModel.XToVal(EditModel.ValToX(0x3000, mv, 801), mv, 801) / 64, 0x3000 / 64);
    Console.WriteLine("SpectrumTap through the engine (1 kHz sine at -6 dBFS):");
    var e = new BlockFxEngine(44100); var tap = new SpectrumTap(); tap.Active = true; e.Tap = tap;
    var chg = new Params(); chg.SetDefaults(); chg.TvN = 1;
    var io = new[]{ new float[256], new float[256] }; long pos = 0;
    for (int tick = 0; tick < 40; tick++) {
      if (tick > 0) chg.SetNoVal(); chg.TvN = 1;
      e.Tick(chg, 5512);
      for (int t = 0; t < 5512; ) { int n = Math.Min(256, 5512 - t);
        for (int i = 0; i < n; i++) io[0][i] = (float)(16384 * Math.Sin(2 * Math.PI * 1000 * (pos + i) / 44100.0));
        e.Work(io, n, 1, true, 5512); pos += n; t += n; }
    }
    var row = tap.Get(tap.Written - 1); int pk = 0; for (int b = 1; b < SpectrumTap.Bands; b++) if (row.In[b] > row.In[pk]) pk = b;
    double pkHz = P.FreqHz((float)pk * tap.MaxVal / (SpectrumTap.Bands - 1));
    Console.WriteLine($"  rows/s {tap.Written / (pos / 44100.0):0.0}  peak band {pk} = {pkHz:0} Hz  in {EditModel.PowerToDbfs(row.In[pk]):0.0} dBFS  out {EditModel.PowerToDbfs(row.Out[pk]):0.0} dBFS");
    Eq("peak near 1 kHz", Math.Abs(pkHz - 1000) < 30, true);
    Eq("level -6..-10.5 dBFS (rect window scalloping)", EditModel.PowerToDbfs(row.In[pk]) < -5.5 && EditModel.PowerToDbfs(row.In[pk]) > -10.5, true);
    Eq("out == in with no FX", Math.Abs(EditModel.PowerToDbfs(row.Out[pk]) - EditModel.PowerToDbfs(row.In[pk])) < 0.1, true);
    long aud = tap.AudiblePos; Eq("playhead = output pos - delay", aud, pos - 4 * 5512);
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILURES");
    return fails;
  }
}
