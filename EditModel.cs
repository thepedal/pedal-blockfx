// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
//
// Pedal BlockFX - Edit window logic that doesn't need WPF (unit-tested headless).

using System;
using System.Globalization;

namespace PedalBlockFX
{
    internal static class EditModel
    {
        // ---- per-effect Value descriptions (ParamDesc.cpp tables) -------------------
        public static readonly string[] HarmTypes = { "both", "odd", "even" };

        /// <param name="roundHz">rounds a frequency to the current bin centre (roundFreqDisp)</param>
        public static string ValueDesc(int effect, int value, Func<float, float> roundHz, int sampleRate)
        {
            float v = P.T[P.FxVal].Frac(value);
            switch (P.T[P.Effect].Lim(effect))
            {
                case 0: // Contrast: percent2
                case 3: // Weed: percent2
                    return Signed((v * 2.0f - 1.0f) * 100.0f) + "%";
                case 1: // Smear: percent
                case 2: // Clip: percent
                    return (v * 100.0f).ToString("0", CultureInfo.InvariantCulture) + "%";
                case 4:
                case 5: // shift in Hz, rounded to the bin spacing
                {
                    float hz = P.FreqShift(v, sampleRate * 0.5f);
                    float r = Math.Sign(hz) * roundHz(Math.Abs(hz));
                    return Signed(r) + " Hz";
                }
                case P.Freeze:
                    return v < 0.01f ? "live" : "hold " + Pct(v);
                case P.Gate:
                    return (-100f + 100f * v).ToString("0", CultureInfo.InvariantCulture) + " dBFS";
                case P.Blur:
                {
                    if (v < 0.01f) return "off";
                    float ms = 5f * MathF.Pow(400f, v);
                    return ms < 1000f ? ms.ToString("0", CultureInfo.InvariantCulture) + " ms"
                                      : (ms / 1000f).ToString("0.0#", CultureInfo.InvariantCulture) + " s";
                }
                case P.Tilt:
                {
                    float slope = (v * 2f - 1f) * 12f;
                    string t = slope.ToString("0.0", CultureInfo.InvariantCulture);
                    return (slope > 0.05f ? "+" : "") + (t == "-0.0" ? "0.0" : t) + " dB/oct";
                }
                case P.RoboWhisper:
                    if (v < 0.5f) return "robot " + Pct((0.5f - v) * 2f);
                    if (v > 0.5f) return "whisper " + Pct((v - 0.5f) * 2f);
                    return "off";
                case P.Cross:
                    return Pct(v) + " sidechain";
                case P.PitchShift:
                {
                    float st = P.PitchSemis(v);
                    if (Math.Abs(st) < 0.005f) return "0.00 st";
                    return (st > 0 ? "+" : "") + st.ToString("0.00", CultureInfo.InvariantCulture) + " st";
                }
                default: // harmonic family: type + width
                {
                    SplitHarm(v, out int type, out float width);
                    return HarmTypes[type] + " " + (width * 100.0f).ToString("0", CultureInfo.InvariantCulture) + "%";
                }
            }
        }

        // ---- Value as text (v1.5) -------------------------------------------------
        // ParseValue inverts ValueDesc for every effect: what the label shows can be typed back.
        // Also accepts raw values ("0x5000", "#20480"). Returns null if not understood.
        public static int? ParseValue(int effect, string text, int sampleRate)
        {
            if (text == null) return null;
            string t = text.Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            var ci = CultureInfo.InvariantCulture;
            var d = P.T[P.FxVal];
            if (t.StartsWith("0x"))
                return int.TryParse(t.Substring(2), NumberStyles.HexNumber, ci, out int h) ? d.Lim(h) : (int?)null;
            if (t.StartsWith("#"))
                return int.TryParse(t.Substring(1), NumberStyles.Integer, ci, out int r) ? d.Lim(r) : (int?)null;

            effect = P.T[P.Effect].Lim(effect);
            // words first (they carry no number, or decide the sign/type)
            switch (effect)
            {
                case P.Freeze: if (t == "live" || t == "off") return 0; t = Strip(t, "hold"); break;
                case P.Blur: if (t == "off") return 0; break;
                case P.RoboWhisper:
                    if (t == "off") return FracToValue(0.5f);
                    if (t.StartsWith("robot") || t.StartsWith("r "))
                    { float? p = Num(Strip(Strip(t, "robot"), "r"), "%"); return p.HasValue ? FracToValue(0.5f - Clamp01(p.Value / 100f) / 2f) : (int?)null; }
                    if (t.StartsWith("whisper") || t.StartsWith("w "))
                    { float? p = Num(Strip(Strip(t, "whisper"), "w"), "%"); return p.HasValue ? FracToValue(0.5f + Clamp01(p.Value / 100f) / 2f) : (int?)null; }
                    break;
                case 6: case 7: case 8:
                {
                    int type = -1;
                    for (int i = 0; i < HarmTypes.Length; i++) if (t.StartsWith(HarmTypes[i])) { type = i; t = t.Substring(HarmTypes[i].Length); break; }
                    if (type < 0) return null;
                    float? w = Num(t, "%");
                    if (!w.HasValue) return null;
                    return FracToValue(JoinHarm(type, Clamp01(w.Value / 100f)));
                }
            }

            float? v;
            switch (effect)
            {
                case 0: case 3:                                         // Contrast, Weed: -100..+100 %
                    v = Num(t, "%"); return v.HasValue ? FracToValue((Clamp(v.Value, -100f, 100f) / 100f + 1f) / 2f) : (int?)null;
                case 1: case 2: case P.Cross: case P.Freeze:          // 0..100 %
                    v = Num(t.Replace("sidechain", ""), "%"); return v.HasValue ? FracToValue(Clamp01(v.Value / 100f)) : (int?)null;
                case 4: case 5:                                         // shift in Hz
                {
                    float mul = 1; string u = t.Replace(" ", "");
                    if (u.EndsWith("hz")) u = u.Substring(0, u.Length - 2);
                    if (u.EndsWith("k")) { mul = 1000; u = u.Substring(0, u.Length - 1); }
                    if (!float.TryParse(u, NumberStyles.Float, ci, out float hz)) return null;
                    return FracToValue(InvertShift(hz * mul, sampleRate * 0.5f));
                }
                case P.Gate:                                            // -100..0 dBFS
                    v = Num(t, "dbfs", "db"); return v.HasValue ? FracToValue((Clamp(v.Value, -100f, 0f) + 100f) / 100f) : (int?)null;
                case P.Blur:                                            // 5 ms .. 2 s
                {
                    string u = t.Replace(" ", ""); float mul = 1;
                    if (u.EndsWith("ms")) u = u.Substring(0, u.Length - 2);
                    else if (u.EndsWith("s")) { mul = 1000; u = u.Substring(0, u.Length - 1); }
                    if (!float.TryParse(u, NumberStyles.Float, ci, out float ms)) return null;
                    ms = Clamp(ms * mul, 5f, 2000f);
                    // a typed time always means on: stay above the 1% "off" threshold
                    return FracToValue(Math.Max(0.0101f, MathF.Log(ms / 5f) / MathF.Log(400f)));
                }
                case P.Tilt:                                            // -12..+12 dB/oct
                    v = Num(t, "db/oct", "db", "/oct"); return v.HasValue ? FracToValue((Clamp(v.Value, -12f, 12f) / 12f + 1f) / 2f) : (int?)null;
                case P.RoboWhisper:                                     // plain number: -100 robot .. +100 whisper
                    v = Num(t, "%"); return v.HasValue ? FracToValue(0.5f + Clamp(v.Value, -100f, 100f) / 200f) : (int?)null;
                case P.PitchShift:                                      // -24..+24 semitones
                    v = Num(t, "st", "semitones", "semi"); return v.HasValue ? FracToValue((Clamp(v.Value, -P.PitchRange, P.PitchRange) / P.PitchRange + 1f) / 2f) : (int?)null;
            }
            return null;
        }

        /// <summary>Examples for the Value box's tooltip, per effect.</summary>
        public static string ValueHint(int effect)
        {
            switch (P.T[P.Effect].Lim(effect))
            {
                case 0: case 3: return "-100% .. +100%  (e.g. +40, -60%)";
                case 1: case 2: return "0 .. 100%  (e.g. 55)";
                case 4: case 5: return "Hz, signed  (e.g. +150, -1.2k)";
                case 6: case 7: case 8: return "both / odd / even + width %  (e.g. odd 50)";
                case P.Freeze: return "live, or hold %  (e.g. 100, hold 55%)";
                case P.Gate: return "-100 .. 0 dBFS  (e.g. -40)";
                case P.Blur: return "off, or 5 ms .. 2 s  (e.g. 250 ms, 1.5 s)";
                case P.Tilt: return "-12 .. +12 dB/oct  (e.g. +4, -5)";
                case P.RoboWhisper: return "robot %, whisper %, off, or -100 (robot) .. +100 (whisper)";
                case P.Cross: return "0 .. 100% sidechain  (e.g. 50)";
                default: return "-24 .. +24 semitones  (e.g. +7, -12, 0.15)";
            }
        }

        // a number with optional (case-insensitive) unit suffixes, e.g. "+7 st", "-40dbfs", "55%"
        static float? Num(string t, params string[] units)
        {
            string u = t.Trim();
            foreach (var unit in units) if (u.EndsWith(unit)) { u = u.Substring(0, u.Length - unit.Length).Trim(); break; }
            u = u.Replace(" ", "");
            return float.TryParse(u, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : (float?)null;
        }
        static string Strip(string t, string word) => t.StartsWith(word) ? t.Substring(word.Length).Trim() : t;
        static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        // FreqShift is monotonic in v: bisect
        static float InvertShift(float hz, float nyquist)
        {
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 40; i++) { float m = 0.5f * (lo + hi); if (P.FreqShift(m, nyquist) > hz) hi = m; else lo = m; }
            return 0.5f * (lo + hi);
        }

        // ---- band LFO travel (v1.5, for the Edit window overlay) ---------------------
        /// <summary>
        /// Where each edge can travel under the LFO, in FreqA/FreqB units (unclamped edges
        /// that don't move return their base value twice). False when the LFO doesn't move edges.
        /// </summary>
        public static bool LfoEdgeTravel(int target, int depth, int a, int b,
                                         out float aLo, out float aHi, out float bLo, out float bHi)
        {
            aLo = aHi = a; bLo = bHi = b;
            target = P.T[P.LfoTarget].Lim(target);
            if (target == P.LfoOff || target == P.LfoValue) return false;
            float d = P.T[P.LfoDepth].Frac(depth) * P.LfoFreqRange * 256f;
            if (d <= 0f) return false;
            float max = P.T[P.FreqA].Max;
            bool moveA = target == P.LfoBand || target == P.LfoFreqA || target == P.LfoWidth;
            bool moveB = target == P.LfoBand || target == P.LfoFreqB || target == P.LfoWidth;
            if (moveA) { aLo = Math.Max(1f, a - d); aHi = Math.Min(max, a + d); }
            if (moveB) { bLo = Math.Max(1f, b - d); bHi = Math.Min(max, b + d); }
            return true;
        }

        // ---- band LFO text (v1.3) -------------------------------------------------
        public static string LfoRateText(int rate, int ticksPerBeat)
        {
            rate = P.T[P.LfoRate].Lim(rate);
            var ci = CultureInfo.InvariantCulture;
            if (P.LfoIsHz(rate)) return P.LfoHz[rate - P.LfoTicks.Length].ToString("0.###", ci) + " Hz (free)";
            float t = P.LfoTicks[rate];
            string s = t == 0.25f ? "1/4 tick" : t == 0.5f ? "1/2 tick" : t == 1f ? "1 tick" : t.ToString("0.#", ci) + " ticks";
            if (ticksPerBeat > 0)
            {
                float beats = t / ticksPerBeat;
                if (beats >= 1f) s += " (" + beats.ToString("0.##", ci) + (beats == 1f ? " beat)" : " beats)");
            }
            return s;
        }

        /// <summary>
        /// v1.7: Edit-window warning when the LFO is too fast for the block size. Each block takes
        /// one (averaged) LFO value, so below about four blocks per cycle the modulation steps and
        /// then fades. Empty when fine or unknown.
        /// </summary>
        public static string LfoSpeedHint(int rate, double sampsPerTick, int sampleRate, long hop)
        {
            if (hop <= 0 || sampleRate <= 0 || sampsPerTick <= 0) return "";
            double blocks = P.LfoPeriodSamples(P.T[P.LfoRate].Lim(rate), sampsPerTick, sampleRate) / hop;
            if (blocks >= 4.0) return "";
            return blocks < 1.5 ? "too fast for BlockLen: fades out" : "steps: shorter BlockLen or more Overlap";
        }

        // ---- v2.0: Edit window layout -------------------------------------------
        /// <summary>
        /// Window width (DIPs) from which every set gets its full one-line row. The fixed row is
        /// about 1240 px; since v2.2 the second line (LFO, Depth, Phase, Mix, Formant, Follow) is the
        /// wider, about 1320 px from the window's left edge. Margins and the scrollbar take the rest.
        /// </summary>
        public const double FullRowsWidth = 1360;
        /// <summary>Hysteresis either side of FullRowsWidth, so a scrollbar appearing can't flip it back and forth.</summary>
        public const double LayoutHysteresis = 12;
        /// <summary>Below this width (or in compact mode) the level legend makes way for the controls.</summary>
        public const double LegendMinWidth = 1000;

        /// <summary>Full rows, or the list plus a detail panel for the selected set.</summary>
        public static bool UseFullRows(double width, bool compact, bool currentlyFull)
        {
            if (compact) return false;
            if (!(width > 0)) return currentlyFull;   // not measured yet
            return width >= FullRowsWidth + (currentlyFull ? -LayoutHysteresis : LayoutHysteresis);
        }

        public static bool ShowLegend(double width, bool compact) => !compact && !(width > 0 && width < LegendMinWidth);

        /// <summary>A frequency for the compact set list: "70 Hz", "1.2 kHz", "16.7 kHz".</summary>
        public static string ShortHz(float hz)
        {
            var ci = CultureInfo.InvariantCulture;
            if (hz < 1000f) return hz.ToString("0", ci) + " Hz";
            if (hz < 10000f) return (hz * 1e-3f).ToString("0.##", ci) + " kHz";
            return (hz * 1e-3f).ToString("0.#", ci) + " kHz";
        }

        /// <summary>
        /// The band of a set for the compact list: "off", "70 Hz - 445 Hz", or
        /// "outside 445 Hz - 2 kHz" when FreqA > FreqB (the set processes outside the range).
        /// </summary>
        public static string BandText(int a, int b, Func<float, float> round)
        {
            if (a == 0 && b == 0) return "off";
            if (round == null) round = f => f;
            bool outside = a > b;
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            string s = ShortHz(round(P.FreqHz(lo))) + " - " + ShortHz(round(P.FreqHz(hi)));
            return outside ? "outside " + s : s;
        }

        /// <summary>v1.8: per-set Mix label.</summary>
        public static string MixText(int mix)
        {
            int m = P.T[P.Mix].Lim(mix);
            return m >= 100 ? "100% wet" : m <= 0 ? "dry" : m.ToString(CultureInfo.InvariantCulture) + "% wet";
        }

        /// <summary>v2.1: PitchShift formant label: off, keep, or the formant shift.</summary>
        public static string FormantText(int v)
        {
            int f = P.T[P.Formant].Lim(v);
            if (f == P.FormantOff) return "off";
            if (f == P.FormantKeep) return "keep";
            return P.FormantSemis(f).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " st";
        }

        public static string LfoDepthText(int depth)
            => (P.T[P.LfoDepth].Lim(depth) * 0.5f).ToString("0.#", CultureInfo.InvariantCulture) + "%";

        public static string LfoPhaseText(int phase)
            => (P.T[P.LfoPhase].Lim(phase) * 360f / 256f).ToString("0", CultureInfo.InvariantCulture) + "\u00B0";

        /// <summary>Depth in the target's own units, for the Edit window.</summary>
        public static string LfoSwingText(int target, int depth)
        {
            float f = P.T[P.LfoDepth].Frac(depth);
            var ci = CultureInfo.InvariantCulture;
            target = P.T[P.LfoTarget].Lim(target);
            if (target == P.LfoOff) return "off";
            if (target == P.LfoValue) return "\u00B1" + (f * P.LfoValueRange * 100f).ToString("0.#", ci) + "% of Value";
            return "\u00B1" + (f * P.LfoFreqRange).ToString("0.##", ci) + " st";
        }

        static string Pct(float f) => (f * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

        static string Signed(float x)
        {
            string s = x.ToString("0", CultureInfo.InvariantCulture);
            if (s == "-0") s = "0";
            return (x > 0.5f ? "+" : "") + s;
        }

        public static string EffectHint(int effect)
        {
            switch (P.T[P.Effect].Lim(effect))
            {
                case 0: return "Contrast: <0 flattens, >0 sharpens the spectrum";
                case 1: return "Smear: amount of random phase";
                case 2: return "Clip: limits bin magnitudes (higher = harder)";
                case 3: return "Weed: >0 removes weak bins, <0 removes strong bins";
                case 4: return "ShiftAdd: shifted copy added to the original";
                case 5: return "ShiftReplace: range replaced by its shifted copy";
                case 6: return "Harmonic: Amp applied to harmonics of FreqA (type, width)";
                case 7: return "AutoHarm: as Harmonic, fundamental = loudest bin in range";
                case 8: return "CopyHarm: FreqA harmonic copied up the spectrum (type, width)";
                case P.Freeze: return "Freeze: raising Value from 0 captures the spectrum; Value = how much of the held sound replaces the live one";
                case P.Gate: return "Gate: bins quieter than the threshold are pushed down (spectral noise gate)";
                case P.Blur: return "Blur: each bin's level is smoothed over time (5 ms .. 2 s)";
                case P.Tilt: return "Tilt: -12..+12 dB per octave around the band's centre";
                case P.RoboWhisper: return "RoboWhisper: below 50% flattens phase (robot), above 50% randomises it (whisper)";
                case P.Cross: return "Cross: band takes the Sidechain input's spectrum shape (connect a machine to input 2)";
                default: return "PitchShift: band moved by -24..+24 semitones (50% = none). Best in Smooth mode with Overlap 75%";
            }
        }

        // SplitHarmonicParam and its inverse (operator float)
        public static void SplitHarm(float param, out int type, out float width)
        {
            param *= 3.0f;
            type = (int)param; if (type < 0) type = 0; if (type > 2) type = 2;
            width = param - type;
            if ((type & 1) == 0) width = 1 - width;
        }

        public static float JoinHarm(int type, float width)
        {
            float r = type;
            r += (type & 1) != 0 ? width : 1.0f - width;
            return r / 3.0f;
        }

        public static int FracToValue(float frac)
        {
            var d = P.T[P.FxVal];
            int v = (int)(frac * (d.Max - d.Min) + d.Min + 0.5f);
            return d.Lim(v);
        }

        // ---- frequency text ---------------------------------------------------------
        static readonly string[] Notes = { "c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b" };

        /// <summary>
        /// Parse a FreqA/FreqB entry. Accepts: off / 0, Hz ("440", "440hz", "1.2k",
        /// "1.2khz"), notes ("c4", "C#4", "db3", "a4:-20" cents), hex ("0x3000").
        /// Returns null if not understood.
        /// </summary>
        public static int? ParseFreq(string text)
        {
            if (text == null) return null;
            string t = text.Trim().ToLowerInvariant().Replace(" ", "");
            if (t.Length == 0) return null;
            if (t == "off" || t == "-") return 0;
            var ci = CultureInfo.InvariantCulture;

            if (t.StartsWith("0x"))
                return int.TryParse(t.Substring(2), NumberStyles.HexNumber, ci, out int h) ? P.T[P.FreqA].Lim(h) : (int?)null;

            // note name
            if (t[0] >= 'a' && t[0] <= 'g')
            {
                int i = 1;
                int semis = "c d ef g a b".IndexOf(t[0]);
                if (i < t.Length && t[i] == '#') { semis++; i++; }
                else if (i < t.Length && t[i] == 'b' && i + 1 < t.Length && (char.IsDigit(t[i + 1]) || t[i + 1] == '-')) { semis--; i++; }
                int colon = t.IndexOf(':', i);
                string octS = colon < 0 ? t.Substring(i) : t.Substring(i, colon - i);
                if (!int.TryParse(octS, NumberStyles.Integer, ci, out int oct)) return null;
                float cents = 0;
                if (colon >= 0 && !float.TryParse(t.Substring(colon + 1), NumberStyles.Float, ci, out cents)) return null;
                // value units: 1/256 semitone, c0 = 0
                float val = (oct * 12 + semis) * 256 + cents * 2.56f;
                return P.T[P.FreqA].Lim((int)MathF.Round(val));
            }

            // Hz
            float mul = 1;
            if (t.EndsWith("hz")) t = t.Substring(0, t.Length - 2);
            if (t.EndsWith("k")) { mul = 1000; t = t.Substring(0, t.Length - 1); }
            if (!float.TryParse(t, NumberStyles.Float, ci, out float hz)) return null;
            hz *= mul;
            if (hz <= 0) return 0;
            return SpectrumTap.HzToVal(hz);
        }

        // ---- display axis (linear in parameter value, as the original PixelFreqMap) ---
        public static double ValToX(int value, int maxVal, double width)
            => maxVal <= 0 ? 0 : (double)value / maxVal * (width - 1);

        public static int XToVal(double x, int maxVal, double width)
        {
            if (width <= 1) return 0;
            int v = (int)Math.Round(x / (width - 1) * maxVal);
            return Math.Max(0, Math.Min(maxVal, v));
        }

        public static int SnapSemitone(int v) => (int)Math.Round(v / 256.0) * 256;

        // ---- colours ------------------------------------------------------------------
        // The original's colour map: black, blue, cyan, green, yellow, red
        static readonly float[,] Stops =
        {
            { 0.0f, 0f, 0f, 0f }, { 0.2f, 0f, 0f, 1f }, { 0.4f, 0f, 1f, 1f },
            { 0.6f, 0f, 0.8f, 0f }, { 0.8f, 1f, 1f, 0f }, { 1.0f, 1f, 0f, 0f },
        };

        static readonly int[] _lut = BuildLut();
        static int[] BuildLut()
        {
            var lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                float x = i / 255f;
                int s = 0;
                while (s < 4 && x > Stops[s + 1, 0]) s++;
                float f = (x - Stops[s, 0]) / (Stops[s + 1, 0] - Stops[s, 0]);
                int r = (int)(255 * (Stops[s, 1] + f * (Stops[s + 1, 1] - Stops[s, 1])) + 0.5f);
                int g = (int)(255 * (Stops[s, 2] + f * (Stops[s + 1, 2] - Stops[s, 2])) + 0.5f);
                int b = (int)(255 * (Stops[s, 3] + f * (Stops[s + 1, 3] - Stops[s, 3])) + 0.5f);
                lut[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
            return lut;
        }

        /// <summary>BGRA32 pixel for a power value, floorDb..0 dBFS mapped across the colour map.</summary>
        public static int PowerToPixel(float power, float floorDb)
        {
            if (!(power > 0)) return _lut[0];
            float db = 10f * MathF.Log10(power / SpectrumTap.FullScalePower);
            float f = 1f - db / floorDb;            // floorDb is negative
            int i = (int)(f * 255f);
            if (i < 0) i = 0; else if (i > 255) i = 255;
            return _lut[i];
        }

        // ---- level legend (v1.6) -----------------------------------------------------
        /// <summary>Power at a dBFS level, the inverse of PowerToDbfs (for drawing the legend).</summary>
        public static float DbfsToPower(float db) => SpectrumTap.FullScalePower * MathF.Pow(10f, db / 10f);

        /// <summary>Legend position 0..1 (Floor .. 0 dBFS) of a level, matching PowerToPixel's scale.</summary>
        public static float LegendFrac(float db, float floorDb)
            => floorDb >= 0 ? 1f : Math.Max(0f, Math.Min(1f, 1f - db / floorDb));

        /// <summary>Labelled legend levels for a Floor: 0 dBFS down to the Floor in steps of 20 or 30 dB.</summary>
        public static string LegendLabel(float db)
            => (db == 0f ? 0f : db).ToString("0", CultureInfo.InvariantCulture);   // == folds -0 into +0

        public static float[] LegendTicks(float floorDb)
        {
            if (floorDb >= 0) return new[] { 0f };
            float step = floorDb > -70f ? 20f : 30f;
            int n = (int)Math.Floor(-floorDb / step + 1e-3) + 1;
            var t = new float[n];
            for (int i = 1; i < n; i++) t[i] = -step * i;   // t[0] stays +0: -step*0 is -0f, which .NET Core prints as "-0"
            return t;
        }

        public static float PowerToDbfs(float power)
            => power > 0 ? 10f * MathF.Log10(power / SpectrumTap.FullScalePower) : float.NegativeInfinity;

        // 16 distinguishable set colours (RGB)
        public static readonly int[] SetColours =
        {
            0xFF5555, 0x55AAFF, 0x66DD66, 0xFFBB33, 0xCC66FF, 0x33DDDD, 0xFF77BB, 0xBBBB44,
            0xFF8855, 0x7799FF, 0x99FF99, 0xFFDD88, 0xDD99FF, 0x88FFEE, 0xFFAADD, 0xDDDD99,
        };
    }
}
