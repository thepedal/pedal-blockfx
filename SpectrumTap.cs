// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Based on DT Block Effects, Copyright (C) 2003-2004 Darrell Tam.
//
// Pedal BlockFX - spectrum capture for the Edit window.
//
// Mirrors the original's Spectrogram::newData / PixelFreqMap: the audio thread
// reduces each block's spectrum to a fixed number of bands spaced linearly in
// FreqA/FreqB parameter units (so log-frequency, C0..Nyquist, and band b sits
// exactly under parameter value b * MaxVal / (Bands-1)). Bands take the max power
// over their bins. Blocks are max-accumulated into rows of a fixed duration, so the
// display scrolls at a steady rate whatever the block size.
//
// Only runs while the Edit window is open (Active). Rows go into a ring that the
// GUI thread reads; a torn row is a harmless one-frame glitch, so no locking.

using System;
using System.Threading;

namespace PedalBlockFX
{
    internal sealed class SpectrumTap
    {
        public const int Bands = 1024;
        public const int RingRows = 1024;

        public volatile bool Active;

        public sealed class Row
        {
            public readonly float[] In = new float[Bands];
            public readonly float[] Out = new float[Bands];
            public long PosAbs;      // input sample position of the first block in the row
        }

        readonly Row[] _ring = new Row[RingRows];
        int _written;               // rows published (monotonic)

        // accumulation for the row in progress
        readonly float[] _accIn = new float[Bands], _accOut = new float[Bands];
        bool _accEmpty = true;
        long _accPos;
        long _accSamples;

        // band -> [start, end) bin ranges per FFT size, rebuilt on sample-rate change
        int _sr;
        int _maxVal;                 // FreqA value at Nyquist
        readonly int[][] _bStart = new int[P.NFftBlkSz][];
        readonly int[][] _bEnd = new int[P.NFftBlkSz][];

        static int Cl(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public SpectrumTap()
        {
            for (int i = 0; i < RingRows; i++) _ring[i] = new Row();
        }

        public int SampleRate => _sr;
        public int MaxVal => _maxVal;

        /// <summary>FreqA/FreqB parameter value for a frequency (the original's _FreqParam::setHz).</summary>
        public static int HzToVal(float hz)
        {
            if (!(hz > 0)) return 0;
            float v = (MathF.Log(hz / P.A4Freq) / MathF.Log(2.0f) + (4.0f + 9.0f / 12.0f)) * (256 * 12);
            return P.T[P.FreqA].Lim((int)(v + 0.5f));
        }

        void Build(int sr)
        {
            _sr = sr;
            _maxVal = HzToVal(sr / 2f);
            float valPerBand = (float)_maxVal / (Bands - 1);
            float hzToFf = 2.0f / sr;
            for (int p = 0; p < P.NFftBlkSz; p++)
            {
                int half = P.BlkSz(p) / 2;
                float hzToBin = hzToFf * half;
                var bs = new int[Bands];
                var be = new int[Bands];
                int end = 0;
                for (int b = 0; b < Bands; b++)
                {
                    int start = Cl(end, 0, half + 1);
                    end = (int)(P.FreqHz((b + 0.5f) * valPerBand) * hzToBin + 0.5f);
                    bs[b] = start;
                    be[b] = Cl(Math.Max(end, start + 1), 0, half + 1);
                }
                _bStart[p] = bs; _bEnd[p] = be;
            }
        }

        /// <summary>Audio thread: fold one block's spectrum (halfcomplex x1, length N) into the row.</summary>
        public void Capture(bool output, int planN, int sr, float[] x1, long posAbs, float powerScale)
        {
            if (sr != _sr) Build(sr);
            int n = P.BlkSz(planN), half = n / 2;
            var bs = _bStart[planN]; var be = _bEnd[planN];
            float[] acc = output ? _accOut : _accIn;
            if (_accEmpty) { _accPos = posAbs; _accEmpty = false; }

            for (int b = 0; b < Bands; b++)
            {
                float m = 0;
                int e = be[b];
                for (int k = bs[b]; k < e; k++)
                {
                    float p;
                    if (k == 0) p = x1[0] * x1[0];
                    else if (k >= half) p = x1[half] * x1[half];
                    else p = x1[k] * x1[k] + x1[n - k] * x1[n - k];
                    if (p > m) m = p;
                }
                m *= powerScale;
                if (m > acc[b]) acc[b] = m;
            }
        }

        /// <summary>Audio thread: the block schedule advanced by hop input samples; publish rows.</summary>
        public void Advance(long hop)
        {
            if (_sr <= 0 || _accEmpty) return;
            _accSamples += hop;
            long rowSamples = Math.Max(1, _sr / 60);
            int emit = 0;
            while (_accSamples >= rowSamples && emit < 64)
            {
                var row = _ring[_written % RingRows];
                Array.Copy(_accIn, row.In, Bands);
                Array.Copy(_accOut, row.Out, Bands);
                row.PosAbs = _accPos + emit * rowSamples;
                Volatile.Write(ref _written, _written + 1);
                _accSamples -= rowSamples;
                emit++;
            }
            if (emit > 0)
            {
                if (_accSamples >= rowSamples) _accSamples = 0;   // hit the cap: drop the backlog
                Array.Clear(_accIn, 0, Bands);
                Array.Clear(_accOut, 0, Bands);
                _accEmpty = true;
            }
        }

        public void ResetAccumulator()
        {
            Array.Clear(_accIn, 0, Bands); Array.Clear(_accOut, 0, Bands);
            _accEmpty = true; _accSamples = 0;
        }

        /// <summary>GUI thread: rows published so far.</summary>
        public int Written => Volatile.Read(ref _written);

        /// <summary>GUI thread: a published row (only valid for Written - RingRows &lt;= i &lt; Written).</summary>
        public Row Get(int i) => _ring[((i % RingRows) + RingRows) % RingRows];

        // Engine position snapshot for the playhead line (written on the audio thread).
        long _outPosAbs, _outDelay;
        public void SetPlayhead(long outPosAbs, long outDelay)
        {
            Interlocked.Exchange(ref _outPosAbs, outPosAbs);
            Interlocked.Exchange(ref _outDelay, outDelay);
        }
        /// <summary>Input position of the audio currently leaving the machine.</summary>
        public long AudiblePos => Interlocked.Read(ref _outPosAbs) - Interlocked.Read(ref _outDelay);

        /// <summary>Power of a full-scale (±32768) sine at the engine's internal spectrum scale.</summary>
        public const float FullScalePower = 268.435456f;   // (32768 * 0.5e-3)^2
    }
}
