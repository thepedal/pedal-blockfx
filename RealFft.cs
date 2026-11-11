// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Based on DT Block Effects, Copyright (C) 2003-2004 Darrell Tam.
//
// Pedal BlockFX - real FFT in FFTW 2.x "halfcomplex" layout.
//
// The original DT BlockFx used FFTW 2.1.3 rfftw (FFTW_REAL_TO_COMPLEX /
// FFTW_COMPLEX_TO_REAL) and manipulated the spectrum directly in its packed
// layout:  hc[k] = Re X[k] for k = 0..N/2,  hc[N-k] = Im X[k] for k = 1..N/2-1.
// Several effects rely on that layout's aliasing quirks (the Nyquist bin's
// real and imaginary "slots" are the same element; reads past N/2 land on the
// mirrored bins), so this class reproduces the layout exactly rather than
// using separate re/im arrays.
//
// Both directions are unnormalised, like FFTW:
//   Forward: X[k] = sum x[n] e^(-2 pi i k n / N)
//   Inverse: x[n] = sum X[k] e^(+2 pi i k n / N)   (so Inverse(Forward(x)) = N x)
//
// Implementation: N-point real transform via an N/2-point complex radix-2 FFT
// plus the usual split/merge twiddle step.

using System;

namespace PedalBlockFX
{
    internal sealed class RealFft
    {
        public readonly int N;
        readonly int M;              // N/2, complex FFT size
        readonly int[] _bitrev;
        readonly float[] _cos, _sin; // complex FFT twiddles, M/2 entries
        readonly float[] _wr, _wi;   // split twiddles e^(-2 pi i k / N), M entries
        readonly float[] _zr, _zi;   // work buffers

        public RealFft(int n)
        {
            if (n < 4 || (n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two >= 4");
            N = n; M = n / 2;
            _bitrev = new int[M];
            int bits = 0; while ((1 << bits) < M) bits++;
            for (int i = 0; i < M; i++)
            {
                int r = 0, v = i;
                for (int b = 0; b < bits; b++) { r = (r << 1) | (v & 1); v >>= 1; }
                _bitrev[i] = r;
            }
            int h = Math.Max(1, M / 2);
            _cos = new float[h]; _sin = new float[h];
            for (int i = 0; i < h; i++)
            {
                double a = 2.0 * Math.PI * i / M;
                _cos[i] = (float)Math.Cos(a); _sin[i] = (float)Math.Sin(a);
            }
            _wr = new float[M]; _wi = new float[M];
            for (int k = 0; k < M; k++)
            {
                double a = 2.0 * Math.PI * k / N;
                _wr[k] = (float)Math.Cos(a); _wi[k] = (float)-Math.Sin(a);
            }
            _zr = new float[M]; _zi = new float[M];
        }

        // In-place complex FFT of _zr/_zi. sign = -1 forward, +1 inverse (unnormalised).
        void Complex(int sign)
        {
            float[] zr = _zr, zi = _zi;
            for (int i = 0; i < M; i++)
            {
                int j = _bitrev[i];
                if (j > i)
                {
                    float t = zr[i]; zr[i] = zr[j]; zr[j] = t;
                    t = zi[i]; zi[i] = zi[j]; zi[j] = t;
                }
            }
            for (int len = 2; len <= M; len <<= 1)
            {
                int half = len >> 1;
                int step = M / len;
                for (int start = 0; start < M; start += len)
                {
                    for (int j = 0, ti = 0; j < half; j++, ti += step)
                    {
                        float wr = _cos[ti];
                        float wi = sign * _sin[ti];
                        int a = start + j, b = a + half;
                        float br = zr[b] * wr - zi[b] * wi;
                        float bi = zr[b] * wi + zi[b] * wr;
                        zr[b] = zr[a] - br; zi[b] = zi[a] - bi;
                        zr[a] += br; zi[a] += bi;
                    }
                }
            }
        }

        /// <summary>Forward transform of x[xOff .. xOff+N) into hc[0..N) (halfcomplex).</summary>
        public void Forward(float[] x, int xOff, float[] hc)
        {
            for (int m = 0; m < M; m++) { _zr[m] = x[xOff + 2 * m]; _zi[m] = x[xOff + 2 * m + 1]; }
            Complex(-1);

            hc[0] = _zr[0] + _zi[0];
            hc[M] = _zr[0] - _zi[0];
            for (int k = 1; k < M; k++)
            {
                float ar = _zr[k], ai = _zi[k];
                float br = _zr[M - k], bi = -_zi[M - k];       // conj(Z[M-k])
                float er = 0.5f * (ar + br), ei = 0.5f * (ai + bi);
                float or_ = 0.5f * (ai - bi), oi = -0.5f * (ar - br); // (A-B)/(2i)
                float wr = _wr[k], wi = _wi[k];
                hc[k] = er + (wr * or_ - wi * oi);
                hc[N - k] = ei + (wr * oi + wi * or_);
            }
        }

        /// <summary>Inverse transform of hc[0..N) (halfcomplex) into y[0..N).</summary>
        public void Inverse(float[] hc, float[] y)
        {
            for (int k = 0; k < M; k++)
            {
                float ar, ai, br, bi;
                ar = hc[k]; ai = (k == 0) ? 0f : hc[N - k];
                int mk = M - k;                                // X[M-k], conjugated
                br = hc[mk]; bi = (mk == M || mk == 0) ? 0f : -hc[N - mk];
                float er = 0.5f * (ar + br), ei = 0.5f * (ai + bi);
                float dr = 0.5f * (ar - br), di = 0.5f * (ai - bi);
                float wr = _wr[k], wi = -_wi[k];               // e^(+2 pi i k / N)
                float or_ = dr * wr - di * wi, oi = dr * wi + di * wr;
                _zr[k] = er - oi;                              // E + iO
                _zi[k] = ei + or_;
            }
            Complex(+1);
            for (int m = 0; m < M; m++) { y[2 * m] = 2f * _zr[m]; y[2 * m + 1] = 2f * _zi[m]; }
        }
    }
}
