// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// v1.6: Edit window preferences (parse/format, fitting to screens, file) and the level legend scale.
using System; using System.IO; using PedalBlockFX;
static class PrefsTest {
  static int fails = 0;
  static void Check(string what, bool ok, string detail) { if (!ok) fails++; Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); }

  static int Main() {
    Console.WriteLine("Window prefs: parse / format");
    {
      var p = new WindowPrefs { Left = 120, Top = -40, Width = 1400, Height = 900, Maximized = true, FloorIndex = 2 };
      var q = WindowPrefs.Parse(p.Format());
      Check("round trip", q.Left == 120 && q.Top == -40 && q.Width == 1400 && q.Height == 900 && q.Maximized && q.FloorIndex == 2, p.Format().Replace("\n", " | "));
      var d = WindowPrefs.Parse("");
      Check("empty -> defaults", !d.HasBounds && !d.Maximized && d.FloorIndex == -1, "");
      var j = WindowPrefs.Parse("garbage\nLeft=abc\nWidth=-5\nHeight=NaN\nFloor=-75\nTop=10\nMaximized=yes\n=3\n");
      Check("junk is ignored, never throws", !j.HasBounds && j.Top == 10 && !j.Maximized && j.FloorIndex == -1, $"top {j.Top}, floor {j.FloorIndex}");
      var c = WindowPrefs.Parse("left = 5\r\nTOP=6\r\nwidth=700\r\nheight=500\r\nfloor=-60\r\nmaximized=true\r\n");
      Check("keys case-insensitive, CRLF, spaces", c.HasBounds && c.Left == 5 && c.FloorIndex == 0 && c.Maximized, "");
    }

    Console.WriteLine("Fitting saved bounds to the current screens");
    {
      Func<double,double,double,double,WindowPrefs> P = (l,t,w,h) => new WindowPrefs { Left = l, Top = t, Width = w, Height = h };
      var a = P(3000, 200, 1180, 740); a.FitTo(0, 0, 1920, 1080, 640, 420);      // second monitor unplugged
      Check("window on a missing monitor comes back on screen", a.Left == 1920 - 1180 && a.Top == 200, $"{a.Left},{a.Top}");
      var b = P(100, 100, 2600, 1600); b.FitTo(0, 0, 1920, 1080, 640, 420);
      Check("larger than the screen is shrunk", b.Width == 1920 && b.Height == 1080 && b.Left == 0 && b.Top == 0, $"{b.Width}x{b.Height} at {b.Left},{b.Top}");
      var c = P(-1500, 50, 1180, 740); c.FitTo(-1920, 0, 3840, 1080, 640, 420);   // monitor to the left
      Check("a valid spot on a left-hand monitor is kept", c.Left == -1500 && c.Top == 50, $"{c.Left},{c.Top}");
      var d = P(200, -300, 1180, 740); d.FitTo(0, 0, 1920, 1080, 640, 420);
      Check("title bar above the screen is pulled down", d.Top == 0, $"{d.Top}");
      var e = P(10, 10, 300, 200); e.FitTo(0, 0, 1920, 1080, 640, 420);
      Check("smaller than the minimum is grown", e.Width == 640 && e.Height == 420, $"{e.Width}x{e.Height}");
    }

    Console.WriteLine("File");
    {
      string dir = Path.Combine(Path.GetTempPath(), "pbfx_prefs_" + Guid.NewGuid().ToString("N"));
      WindowPrefs.PathOverride = Path.Combine(dir, "sub", "EditWindow.txt");
      var none = WindowPrefs.Load();
      Check("missing file -> defaults", !none.HasBounds && none.FloorIndex == -1, "");
      new WindowPrefs { Left = 11, Top = 22, Width = 900, Height = 600, FloorIndex = 0 }.Save();
      var back = WindowPrefs.Load();
      Check("save creates the folder; load reads it back", back.Left == 11 && back.Width == 900 && back.FloorIndex == 0, WindowPrefs.PathOverride);
      File.WriteAllBytes(WindowPrefs.PathOverride, new byte[] { 0xFF, 0x00, 0xFE, 0x41 });
      var bin = WindowPrefs.Load();
      Check("binary junk -> defaults, no exception", !bin.HasBounds, "");
      try { Directory.Delete(dir, true); } catch { }
      WindowPrefs.PathOverride = null;
    }

    Console.WriteLine("Level legend scale");
    {
      Check("ticks -60: 0,-20,-40,-60", string.Join(",", EditModel.LegendTicks(-60)) == "0,-20,-40,-60", string.Join(",", EditModel.LegendTicks(-60)));
      Check("ticks -90: 0,-30,-60,-90", string.Join(",", EditModel.LegendTicks(-90)) == "0,-30,-60,-90", string.Join(",", EditModel.LegendTicks(-90)));
      Check("tick 0 is +0 (no -0 label)", !float.IsNegative(EditModel.LegendTicks(-90)[0]) && BitConverter.SingleToInt32Bits(EditModel.LegendTicks(-90)[0]) == 0, "");
      Check("label of -0f is 0", EditModel.LegendLabel(-0f) == "0" && EditModel.LegendLabel(-30f) == "-30", EditModel.LegendLabel(-0f));
      Check("ticks -120: 0..-120 by 30", string.Join(",", EditModel.LegendTicks(-120)) == "0,-30,-60,-90,-120", string.Join(",", EditModel.LegendTicks(-120)));
      Check("positions: floor 0, 0 dBFS 1, middle 0.5",
        EditModel.LegendFrac(-90, -90) == 0 && EditModel.LegendFrac(0, -90) == 1 && Math.Abs(EditModel.LegendFrac(-45, -90) - 0.5f) < 1e-6, "");
      // the legend's colours are the spectrogram's: same pixel for the same level
      bool same = true; string bad = "";
      foreach (float floor in WindowPrefs.FloorChoices)
        for (float db = floor; db <= 0; db += 1f) {
          int a = EditModel.PowerToPixel(EditModel.DbfsToPower(db), floor);
          float back = EditModel.PowerToDbfs(EditModel.DbfsToPower(db));
          if (Math.Abs(back - db) > 1e-3) { same = false; bad = $"dB {db} -> {back}"; }
          int b = EditModel.PowerToPixel(EditModel.DbfsToPower(back), floor);
          if (a != b) { same = false; bad = $"pixel differs at {db}"; }
        }
      Check("DbfsToPower inverts PowerToDbfs; legend pixels match the rows", same, bad);
      int lo = EditModel.PowerToPixel(EditModel.DbfsToPower(-90), -90), hi = EditModel.PowerToPixel(EditModel.DbfsToPower(0), -90);
      Check("left end black, right end red", (lo & 0xFFFFFF) == 0 && (hi & 0xFFFFFF) == 0xFF0000, $"{lo & 0xFFFFFF:X6} / {hi & 0xFFFFFF:X6}");
    }

    Console.WriteLine("v2.0 view options and layout");
    {
      var p = new WindowPrefs { SpecView = WindowPrefs.SpecOutOnly, GlobalsCollapsed = true, Compact = true };
      var q = WindowPrefs.Parse(p.Format());
      Check("view options round trip", q.SpecView == 1 && q.GlobalsCollapsed && q.Compact, p.Format().Replace("\n", " | "));
      var d = WindowPrefs.Parse("Width=800\nHeight=600");
      Check("v1.6 file (no view keys) -> defaults", d.SpecView == 0 && !d.GlobalsCollapsed && !d.Compact, "");
      var j = WindowPrefs.Parse("Spec=7\nSpec=1.5\nCompact=maybe\nGlobalsCollapsed=TRUE");
      Check("bad Spec values ignored, bools tolerant", j.SpecView == 0 && !j.Compact && j.GlobalsCollapsed, $"spec {j.SpecView}");
      var k = WindowPrefs.Parse("Spec=2");
      Check("Spec=2 is In only", k.SpecView == WindowPrefs.SpecInOnly && WindowPrefs.SpecViewNames.Length == 3, "");

      double T = EditModel.FullRowsWidth, H = EditModel.LayoutHysteresis;
      Check("wide -> full rows, narrow -> list + detail", EditModel.UseFullRows(1600, false, false) && !EditModel.UseFullRows(900, false, true), "");
      Check("compact always list + detail", !EditModel.UseFullRows(2500, true, true), "");
      Check("hysteresis: just above the threshold stays as it was",
            EditModel.UseFullRows(T + H - 1, false, true) && !EditModel.UseFullRows(T + H - 1, false, false)
            && !EditModel.UseFullRows(T - H - 1, false, true) && EditModel.UseFullRows(T + H, false, false), "");
      Check("unmeasured width keeps the current layout", EditModel.UseFullRows(0, false, true) && !EditModel.UseFullRows(double.NaN, false, false), "");
      Check("full rows threshold covers the fixed row and the v2.2 second line", T >= 1320 + 30, $"{T}");
      Check("legend: shown wide, hidden narrow or compact",
            EditModel.ShowLegend(1200, false) && !EditModel.ShowLegend(900, false) && !EditModel.ShowLegend(1600, true) && EditModel.ShowLegend(0, false), "");

      int a70 = EditModel.ParseFreq("70").Value, b445 = EditModel.ParseFreq("445").Value, k2 = EditModel.ParseFreq("2k").Value, k16 = EditModel.ParseFreq("16.7k").Value;
      string inside = EditModel.BandText(a70, b445, null), outside = EditModel.BandText(k2, b445, null), off = EditModel.BandText(0, 0, null),
             hi = EditModel.BandText(k2, k16, null);
      Check("band text", inside.StartsWith("70 Hz - 44") && inside.EndsWith(" Hz") && outside.StartsWith("outside 44") && outside.EndsWith("2 kHz")
            && off == "off" && hi.EndsWith("16.7 kHz"), $"{inside} | {outside} | {off} | {hi}");
      Check("short Hz", EditModel.ShortHz(70f) == "70 Hz" && EditModel.ShortHz(1234f) == "1.23 kHz" && EditModel.ShortHz(16700f) == "16.7 kHz",
            EditModel.ShortHz(1234f));
      Check("band text uses the display rounding", EditModel.BandText(a70, b445, f => 100f) == "100 Hz - 100 Hz", EditModel.BandText(a70, b445, f => 100f));
    }

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
