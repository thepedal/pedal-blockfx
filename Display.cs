// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Based on DT Block Effects, Copyright (C) 2003-2004 Darrell Tam.
//
// Pedal BlockFX - value text helpers (FreqToNote.cpp / _FreqParam::desc).

using System;
using System.Globalization;

namespace PedalBlockFX
{
    internal static class Display
    {
        static readonly string[] NoteNames = { "c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b" };
        static readonly float Log2 = MathF.Log(2);
        static readonly float LogC0Hz = MathF.Log(P.A4Freq) - (4.0f + 9.0f / 12.0f) * MathF.Log(2);

        /// <summary>e.g. "0Hz,-", "440Hz,a4:0", "1.05kHz,c6:-2"</summary>
        public static string Freq(float freq)
        {
            var ci = CultureInfo.InvariantCulture;
            string s;
            if (freq < 1000.0f) s = freq.ToString("0", ci) + "Hz,";
            else if (freq < 10000.0f) s = (freq * 1e-3).ToString("0.00", ci) + "kHz,";
            else s = (freq * 1e-3f).ToString("0.0", ci) + "kHz,";
            return s + HzToNote(freq);
        }

        public static string HzToNote(float freq)
        {
            float logFreq = MathF.Log(freq);
            if (!(logFreq >= LogC0Hz)) return "-";          // below c0 (or 0 Hz)
            float note = 12.0f * (logFreq - LogC0Hz) / Log2;
            int close = (int)(note + 0.5f);
            int cents = (int)((note - close) * 100.0f);
            int octave = close / 12;
            int note12 = close - 12 * octave;
            return NoteNames[note12] + octave + ":" + cents;
        }
    }
}
