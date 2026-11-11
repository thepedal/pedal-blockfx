// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
//
// Pedal BlockFX - Edit window preferences (v1.6): size, position, maximised, Floor.
// v2.0 adds the view options: which spectrograms show, globals collapsed, compact mode.
// Per user, not per song: %APPDATA%\Pedal BlockFX\EditWindow.txt, plain key=value lines.
// Nothing here is WPF, so it is tested headless. Every failure is silent: a missing or
// unreadable file just means defaults.

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PedalBlockFX
{
    internal sealed class WindowPrefs
    {
        public double Left = double.NaN, Top = double.NaN, Width = double.NaN, Height = double.NaN;
        public bool Maximized;
        public int FloorIndex = -1;          // index into FloorChoices; -1 = default
        // v2.0 view options
        public int SpecView;                 // SpecBoth, SpecOutOnly or SpecInOnly
        public bool GlobalsCollapsed;
        public bool Compact;

        public const int SpecBoth = 0, SpecOutOnly = 1, SpecInOnly = 2;
        public static readonly string[] SpecViewNames = { "In + Out", "Out only", "In only" };

        public static readonly float[] FloorChoices = { -60f, -90f, -120f };
        public const int DefaultFloorIndex = 1;

        public bool HasBounds => !double.IsNaN(Left) && !double.IsNaN(Top) && Width > 0 && Height > 0;

        public static WindowPrefs Parse(string text)
        {
            var p = new WindowPrefs();
            if (string.IsNullOrEmpty(text)) return p;
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                double d;
                bool num = double.TryParse(v, NumberStyles.Float, ci, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
                switch (k)
                {
                    case "left": if (num) p.Left = d; break;
                    case "top": if (num) p.Top = d; break;
                    case "width": if (num && d > 0) p.Width = d; break;
                    case "height": if (num && d > 0) p.Height = d; break;
                    case "maximized": p.Maximized = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "spec":
                        if (num && d >= SpecBoth && d <= SpecInOnly && d == Math.Floor(d)) p.SpecView = (int)d;
                        break;
                    case "globalscollapsed": p.GlobalsCollapsed = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "compact": p.Compact = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "floor":
                        if (num) { int i = Array.IndexOf(FloorChoices, (float)d); if (i >= 0) p.FloorIndex = i; }
                        break;
                }
            }
            return p;
        }

        public string Format()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# Pedal BlockFX Edit window (written when the window closes)\n");
            if (HasBounds)
            {
                sb.Append("Left=").Append(Left.ToString("0", ci)).Append('\n');
                sb.Append("Top=").Append(Top.ToString("0", ci)).Append('\n');
                sb.Append("Width=").Append(Width.ToString("0", ci)).Append('\n');
                sb.Append("Height=").Append(Height.ToString("0", ci)).Append('\n');
            }
            sb.Append("Maximized=").Append(Maximized ? "1" : "0").Append('\n');
            if (FloorIndex >= 0 && FloorIndex < FloorChoices.Length)
                sb.Append("Floor=").Append(FloorChoices[FloorIndex].ToString("0", ci)).Append('\n');
            sb.Append("Spec=").Append(SpecView.ToString(ci)).Append('\n');
            sb.Append("GlobalsCollapsed=").Append(GlobalsCollapsed ? "1" : "0").Append('\n');
            sb.Append("Compact=").Append(Compact ? "1" : "0").Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Make saved bounds usable on the current screens: never larger than the virtual
        /// screen, never smaller than the minimum, and moved so the whole window is inside it
        /// (a monitor that was unplugged since must not leave the window off-screen).
        /// </summary>
        public void FitTo(double vsLeft, double vsTop, double vsWidth, double vsHeight, double minW, double minH)
        {
            if (!HasBounds || vsWidth <= 0 || vsHeight <= 0) return;
            Width = Math.Max(minW, Math.Min(Width, vsWidth));
            Height = Math.Max(minH, Math.Min(Height, vsHeight));
            if (Left + Width > vsLeft + vsWidth) Left = vsLeft + vsWidth - Width;
            if (Top + Height > vsTop + vsHeight) Top = vsTop + vsHeight - Height;
            if (Left < vsLeft) Left = vsLeft;
            if (Top < vsTop) Top = vsTop;
        }

        // ---- file ------------------------------------------------------------------
        /// <summary>Overridable for tests.</summary>
#pragma warning disable 0649   // assigned by the headless tests only
        internal static string PathOverride;
#pragma warning restore 0649

        static string FilePath()
        {
            if (PathOverride != null) return PathOverride;
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(dir, "Pedal BlockFX", "EditWindow.txt");
        }

        public static WindowPrefs Load()
        {
            try
            {
                string f = FilePath();
                return File.Exists(f) ? Parse(File.ReadAllText(f)) : new WindowPrefs();
            }
            catch { return new WindowPrefs(); }
        }

        public void Save()
        {
            try
            {
                string f = FilePath();
                string d = Path.GetDirectoryName(f);
                if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                File.WriteAllText(f, Format());
            }
            catch { }
        }
    }
}
