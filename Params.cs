// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Based on DT Block Effects, Copyright (C) 2003-2004 Darrell Tam.
//
// Pedal BlockFX - parameter model, ported from DT BlockFx params.h / params.cpp.
//
// The engine keeps the original's "Buzz param struct" semantics: every value
// slot holds either a real value or that parameter's NoValue, and a tick's
// parameter set only carries the values that actually changed on that tick.
// Interpolation between ticks depends on this (a NoValue on the next tick means
// "hold"), as do TickDelay and BlkSync, so the managed glue rebuilds the
// NoValue-per-tick picture from its setters.

using System;

namespace PedalBlockFX
{
    internal readonly struct ParamDef
    {
        public readonly int Min, Max, No, Def;
        public ParamDef(int min, int max, int no, int def) { Min = min; Max = max; No = no; Def = def; }
        public int Lim(int v) => v < Min ? Min : (v > Max ? Max : v);
        public float LimF(float v) => v < Min ? Min : (v > Max ? Max : v);
        public float Frac(int v) => (float)(Lim(v) - Min) / (Max - Min);
    }

    internal static class P
    {
        // Global parameter slots (declaration order of the original GLOBAL_PARAMS)
        public const int BlkSync = 0, InterpMode = 1, MixBack = 2, OutAmp = 3, TickDelay = 4, BlkLen = 5, Overlap = 6;
        public const int NG = 7;

        // Track (FX set) parameter slots (TRK_PARAMS)
        public const int FreqA = 0, FreqB = 1, Amp = 2, Effect = 3, FxVal = 4;
        public const int Soft = 5;          // appended (not in the original)
        public const int Capture = 6;       // appended v1.1: stateless trigger (Freeze recapture, Blur restart)
        // appended v1.3: band LFO
        public const int LfoTarget = 7, LfoShape = 8, LfoRate = 9, LfoDepth = 10, LfoPhase = 11;
        // appended v1.4: audition switches
        public const int Mute = 12, Solo = 13;
        // appended v1.8: per-set wet/dry
        public const int Mix = 14;
        // appended v2.1: PitchShift formant control
        public const int Formant = 15;
        // appended v2.2: note-following bands
        public const int Note = 16, Follow = 17;
        public const int NT = 18;

        public const int MaxSets = 16;     // N_FX_PARAM_SETS (track build)

        public static readonly ParamDef[] G =
        {
            new ParamDef(0, 1,      0xff,   0),      // BlkSync     (switch, stateless)
            new ParamDef(0, 2,      0xff,   0),      // InterpMode  (stateless)
            new ParamDef(0, 0xA0,   0xff,   0),      // MixBack
            new ParamDef(0, 0xE0,   0xff,   0xA0),   // OutAmp
            new ParamDef(0, 0x4000, 0xffff, 0x400),  // TickDelay
            new ParamDef(0, 11,     0xff,   7),      // BlockLen
            new ParamDef(5, 80,     0xff,   50),     // Overlap
        };

        public static readonly ParamDef[] T =
        {
            new ParamDef(0, 132 * 256, 0xffff, 0),   // FreqA
            new ParamDef(0, 132 * 256, 0xffff, 0),   // FreqB
            new ParamDef(0, 0xE0,      0xff,   0xA0),// Amp
            new ParamDef(0, LastEffect, 0xff,  0),   // Effect (original: 0..8)
            new ParamDef(0, 0xA000,    0xffff, 0),   // Value
            new ParamDef(0, 48,        0xff,   0),   // Soft: edge ramp in semitones
            new ParamDef(0, 1,         0xff,   0),   // Capture (switch, stateless)
            new ParamDef(0, 5,         0xff,   0),   // LfoTarget: off, band, FreqA, FreqB, width, Value
            new ParamDef(0, 5,         0xff,   0),   // LfoShape: sine, triangle, saw up, saw down, square, S&H
            new ParamDef(0, 34,        0xff,   10),  // LfoRate: 0..18 = LfoTicks (10 = 16 ticks), 19..34 = LfoHz (v1.7)
            new ParamDef(0, 200,       0xff,   0),   // LfoDepth: 0..100% in 0.5% steps
            new ParamDef(0, 255,       0xff,   0),   // LfoPhase: 0..255 = 0..360 degrees
            new ParamDef(0, 1,         0xff,   0),   // Mute (switch)
            new ParamDef(0, 1,         0xff,   0),   // Solo (switch)
            new ParamDef(0, 100,       0xff,   100), // Mix: wet % (v1.8), effects that take Soft
            new ParamDef(0, 49,        0xff,   0),   // Formant (v2.1): 0 off, 1..49 = -12..+12 st, 25 = keep
            new ParamDef(0, 255,       0,      0),   // Note (v2.2, stateless): Buzz note, 0 = none, 255 = off
            new ParamDef(0, 3,         0xff,   0),   // Follow (v2.2): off, band, FreqA, pitch
        };

        // Effects 0..8 are the original's; 9.. are appended.
        public const int Freeze = 9, Gate = 10, Blur = 11, Tilt = 12, RoboWhisper = 13, Cross = 14;
        public const int PitchShift = 15;   // appended v1.2
        public const int LastEffect = 15;

        public const float PitchRange = 24f;   // PitchShift: Value 0..1 = -24..+24 semitones
        public static float PitchSemis(float frac) => (frac * 2f - 1f) * PitchRange;

        // ---- block sizes ------------------------------------------------------
        public const int NFftBlkSz = 12;
        public const int BlkSz0 = 32;
        public const int MaxBlkSz = BlkSz0 << (NFftBlkSz - 1);   // 65536
        public static int BlkSz(int planN) => planN < 0 ? 0 : BlkSz0 << planN;

        // ---- value conversions (static helpers of the original Param classes) --
        public const float A4Freq = 440.000031f;
        const int ZeroDb = 0xA0, UnitsPerDb = 2;
        const int BuzzOctave = 256 * 12;
        public const int Tick = 0x100;

        public static float AmpMult(float v)
            => v > 0 ? MathF.Pow(10f, (G[OutAmp].LimF(v) - ZeroDb) / 20.0f / UnitsPerDb) : 0.0f;
        public static float AmpDb(int v) => (float)(G[OutAmp].Lim(v) - ZeroDb) / UnitsPerDb;

        public static float FreqHz(float v)
            => v > 0 ? A4Freq * MathF.Pow(2.0f, T[FreqA].LimF(v) / BuzzOctave - (4.0f + 9.0f / 12.0f)) : 0.0f;

        public static float TickDelaySamples(float v, long sampsPerTick, long maxDelay)
        {
            float s = v / Tick * sampsPerTick;
            if (s < BlkSz0) return BlkSz0;
            if (s > maxDelay) return maxDelay;
            return s;
        }

        public static float OverlapFrac(float v) => G[Overlap].LimF(v) / 100.0f;

        // freqShift() from ParamDesc.h: linear-into-exponential map of 0..1 onto
        // -nyquist..+nyquist. The original hard-coded 22050 Hz; it is scaled to the
        // actual Nyquist here, which is identical at 44.1 kHz.
        public static float FreqShift(float v, float nyquist)
        {
            const float lin = 2048.0f / 32768.0f;
            const int expRaise = 8;
            const float expScale = (1.0f - lin) / (1 << expRaise);
            v = (v - 0.5f) * 2;
            bool pos = true;
            if (v < 0) { v = -v; pos = false; }
            float r = (MathF.Pow(2, v * expRaise) - 1.0f) * expScale + v * lin;
            return (pos ? r : -r) * nyquist;
        }

        public static readonly string[] EffectNames =
            { "Contrast", "Smear", "Clip", "Weed", "ShiftAdd", "ShiftReplace", "Harmonic", "AutoHarm", "CopyHarm",
              "Freeze", "Gate", "Blur", "Tilt", "RoboWhisper", "Cross", "PitchShift" };

        /// <summary>Effects that only touch bins inside their range (generic soft-edge blend applies).</summary>
        public static bool IsPerBin(int fx) => fx <= 3 || (fx >= Freeze && fx <= Cross);

        // ---- band LFO (v1.3) --------------------------------------------------
        public const int LfoOff = 0, LfoBand = 1, LfoFreqA = 2, LfoFreqB = 3, LfoWidth = 4, LfoValue = 5;
        public static readonly string[] LfoTargetNames = { "Off", "Band", "FreqA", "FreqB", "Width", "Value" };
        public static readonly string[] LfoShapeNames = { "Sine", "Triangle", "Saw up", "Saw down", "Square", "S/H" };
        /// <summary>LFO period in ticks, by LfoRate.</summary>
        public static readonly float[] LfoTicks =
            { 0.25f, 0.5f, 1, 1.5f, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256 };
        /// <summary>v1.7: free-running LFO rates in Hz, at LfoRate = LfoTicks.Length + i (19..34).</summary>
        public static readonly float[] LfoHz =
            { 0.05f, 0.1f, 0.15f, 0.2f, 0.25f, 0.33f, 0.5f, 0.75f, 1, 1.5f, 2, 3, 4, 6, 8, 12 };
        public static int LfoRateCount => LfoTicks.Length + LfoHz.Length;
        public static bool LfoIsHz(int rate) => rate >= LfoTicks.Length;
        /// <summary>LFO period in samples (tempo rates need the tick length).</summary>
        public static double LfoPeriodSamples(int rate, double sampsPerTick, double sampleRate)
        {
            rate = rate < 0 ? 0 : (rate >= LfoRateCount ? LfoRateCount - 1 : rate);
            return LfoIsHz(rate) ? sampleRate / LfoHz[rate - LfoTicks.Length] : LfoTicks[rate] * sampsPerTick;
        }
        /// <summary>Full LfoDepth on a frequency target swings this many semitones each way.</summary>
        public const float LfoFreqRange = 24f;
        /// <summary>Full LfoDepth on Value swings this fraction of Value's range each way.</summary>
        public const float LfoValueRange = 0.5f;

        /// <summary>LFO output -1..1 at the given phase in cycles (S&H is a hash of cycle and set).</summary>
        public static float LfoShapeAt(int shape, double phase, int set)
        {
            double cyc = Math.Floor(phase);
            float f = (float)(phase - cyc);
            switch (shape)
            {
                case 1: return f < 0.25f ? 4f * f : (f < 0.75f ? 2f - 4f * f : 4f * f - 4f);   // triangle, sine-aligned
                case 2: return 2f * f - 1f;                                                  // saw up
                case 3: return 1f - 2f * f;                                                  // saw down
                case 4: return f < 0.5f ? 1f : -1f;                                          // square
                case 5:                                                                      // sample & hold
                {
                    uint h = unchecked((uint)(long)cyc * 0x9E3779B1u ^ (uint)(set + 1) * 0x85EBCA77u);
                    h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                    return (h & 0xFFFFFF) / 8388607.5f - 1f;
                }
                default: return MathF.Sin(f * 6.28318531f);                                  // sine
            }
        }

        /// <summary>Effects that use the Soft parameter (PitchShift does its own edge crossfade).</summary>
        public static bool HasSoft(int fx) => IsPerBin(fx) || fx == PitchShift;
        /// <summary>v1.8: effects that take the per-set Mix (the same ones as Soft: it uses the same blend).</summary>
        public static bool HasMix(int fx) => HasSoft(fx);

        // ---- v2.1: formant control (PitchShift only) ----
        /// <summary>Formant value: 0 = off (formants move with the pitch, as before v2.1).</summary>
        public const int FormantOff = 0;
        /// <summary>Formant value that keeps the formants where they are (0 st formant shift).</summary>
        public const int FormantKeep = 25;
        /// <summary>Formant shift in semitones for a value 1..49 (0.5 st steps, -12..+12).</summary>
        public static float FormantSemis(int v) => (T[Formant].Lim(v) - FormantKeep) * 0.5f;
        /// <summary>Effects that use the Formant parameter.</summary>
        public static bool HasFormant(int fx) => fx == PitchShift;

        // ---- v2.2: note-following bands ----
        public const int FollowOff = 0, FollowBand = 1, FollowFreqA = 2, FollowPitch = 3;
        public static readonly string[] FollowNames = { "Off", "Band", "FreqA", "Pitch" };
        /// <summary>The reference note: a set's band (and PitchShift's Value) is as set for C-4.</summary>
        public const int NoteRef = 48;
        public const int NoteOff = 255;
        /// <summary>Semitone index of a Buzz note byte (octave in the high nibble, 1..12 in the low
        /// one; C-0 = 0, C-4 = 48, the same scale as FreqA / 256), or -1 for none, off or invalid.</summary>
        public static int NoteIndex(int v)
        {
            if (v < 1 || v > 156) return -1;
            int n = (v & 15) - 1;
            if (n < 0 || n > 11) return -1;
            return (v >> 4) * 12 + n;
        }
        /// <summary>Buzz note byte for a semitone index 0..119 (MIDI-style, C-0 = 0).</summary>
        public static int NoteFromIndex(int i) => i < 0 || i > 119 ? 0 : ((i / 12) << 4) + (i % 12) + 1;
        static readonly string[] NoteNames = { "C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-" };
        public static string NoteName(int v)
        {
            if (v == NoteOff) return "off";
            int i = NoteIndex(v);
            return i < 0 ? "" : NoteNames[i % 12] + (i / 12).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One complete parameter set (Params in the original).</summary>
    internal class Params
    {
        public readonly int[] G = new int[P.NG];
        public readonly int[,] T = new int[P.MaxSets, P.NT];
        public int TvN;

        public Params() { SetDefaults(); }

        public void SetDefaults()
        {
            for (int i = 0; i < P.NG; i++) G[i] = P.G[i].Def;
            for (int s = 0; s < P.MaxSets; s++)
                for (int j = 0; j < P.NT; j++) T[s, j] = P.T[j].Def;
            TvN = 0;
        }

        public void SetNoVal()
        {
            for (int i = 0; i < P.NG; i++) G[i] = P.G[i].No;
            for (int s = 0; s < P.MaxSets; s++)
                for (int j = 0; j < P.NT; j++) T[s, j] = P.T[j].No;
        }

        /// <summary>Copy values from b that are not NoValue (Params::update).</summary>
        public void Update(Params b)
        {
            TvN = b.TvN;
            for (int i = 0; i < P.NG; i++) if (b.G[i] != P.G[i].No) G[i] = b.G[i];
            for (int s = 0; s < b.TvN; s++)
                for (int j = 0; j < P.NT; j++) if (b.T[s, j] != P.T[j].No) T[s, j] = b.T[s, j];
        }

        public void CopyFrom(Params b)
        {
            Array.Copy(b.G, G, P.NG);
            Array.Copy(b.T, T, b.T.Length);
            TvN = b.TvN;
        }
    }

    internal sealed class Node : Params
    {
        public long PosAbs;          // absolute sample position of these params
        public long SampsPerTick;    // -1 tags the initial (default) node
        public long Serial;          // identifies the tick this node holds (Capture fires once per node)
        public float TicksPerSamp;
        public double SongPos;       // v1.3: song position in ticks at PosAbs (band LFO phase)
        public bool Advancing;       // v1.3: transport playing when the node was committed

        public void CopyNode(Node b)
        {
            CopyFrom(b);
            PosAbs = b.PosAbs; SampsPerTick = b.SampsPerTick; TicksPerSamp = b.TicksPerSamp; Serial = b.Serial;
            SongPos = b.SongPos; Advancing = b.Advancing;
        }
    }

    /// <summary>FIFO of per-tick parameter sets (ParamsVec in the original).</summary>
    internal sealed class ParamsVec
    {
        readonly Node[] _v;
        int _in, _curr, _next;
        public readonly Params FilledCurr = new Params(), FilledPrev = new Params();
        public float TickFrac;

        public ParamsVec(int size = 256)   // original used 128
        {
            _v = new Node[size];
            for (int i = 0; i < size; i++) _v[i] = new Node();
            Reset();
        }

        public void Reset()
        {
            _in = 0;
            _v[0].SetDefaults();
            _v[0].PosAbs = 0;
            _v[0].TvN = 0;
            _v[0].SampsPerTick = -1;
            _curr = 0; _next = 0;
            FilledCurr.SetDefaults();
            FilledPrev.SetDefaults();
            TickFrac = 0;
        }

        public Node In => _v[_in];
        public Node Curr => _v[_curr];
        public Node Next => _v[_next];

        public bool ExistsNext => _curr != _in;
        public long AbsTickPosNext => _v[_next].PosAbs;
        public int NTrkVals => _v[_curr].TvN;
        public long SampsPerTick => _v[_curr].SampsPerTick;
        public bool InitialParam => _v[_in].SampsPerTick == -1;
        public bool Full => (_in + 1 == _v.Length) ? _curr == 0 : _in + 1 == _curr;

        long _serial;
        public void IncIn() { if (++_in == _v.Length) _in = 0; _v[_in].Serial = ++_serial; }

        public void IncOutPos()
        {
            if (++_curr == _v.Length) _curr = 0;
            _next = _curr + 1;
            if (_next == _v.Length) _next = 0;
            FilledPrev.CopyFrom(FilledCurr);
            FilledCurr.Update(_v[_curr]);
            TickFrac = 0.0f;
        }

        public void InUpdate()
        {
            if (_in == _curr)
            {
                FilledCurr.Update(_v[_curr]);
                FilledPrev.CopyFrom(FilledCurr);
            }
        }

        public void SetFrac(long posAbs)
        {
            float f = (posAbs - _v[_curr].PosAbs) * _v[_curr].TicksPerSamp;
            TickFrac = f < 0f ? 0f : (f > 1f ? 1f : f);
        }

        static float Lin(float frac, float a, float b) => (1.0f - frac) * a + frac * b;

        int Interp(in ParamDef d, int prev, int curr, int next)
        {
            float t;
            switch (FilledCurr.G[P.InterpMode])
            {
                case 1: t = curr; break;                                   // no interpolation
                case 2: t = Lin(TickFrac + 1.0f, prev, curr); break;       // continue previous slope
                default: t = (next == d.No) ? curr : Lin(TickFrac, curr, next); break;
            }
            // The original truncated into a byte/word, which wrapped on extrapolation
            // (InterpMode 2); clamp to the legal range instead.
            int r = (int)t;
            return d.Lim(r);
        }

        public int G(int i) => Interp(P.G[i], FilledPrev.G[i], FilledCurr.G[i], Next.G[i]);
        public int T(int set, int j) => Interp(P.T[j], FilledPrev.T[set, j], FilledCurr.T[set, j], Next.T[set, j]);
    }
}
