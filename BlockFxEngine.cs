// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Based on DT Block Effects, Copyright (C) 2003-2004 Darrell Tam.
//
// Pedal BlockFX - block-FFT engine, ported from DT BlockFx (Darrell Tam, 2003-04).
//
// Structure follows MIBlkFx.cpp closely so it can be checked side by side:
//   Tick()                -> MIBlkFx::Tick
//   ParamsRdy/BlkRdy/NextBlk/Work -> same names
//   Channel.DoFFT/ProcessFFT/DoIFFT/MixToOutputBuffer -> same names
//   Channel.Fx*           -> the BlkFx* classes in BlkFxProcess.cpp
//
// The block scheduling (positions, block sizes, parameter FIFO) is shared;
// each audio channel has its own buffers and effect state, so stereo runs the
// original mono algorithm twice in lockstep.
//
// Audio scale is Buzz's +-32768, as in the original (outputs are clamped to
// -32768..32767 when mixed into the output FIFO, exactly as the original did).

using System;

namespace PedalBlockFX
{
    internal sealed class BlockFxEngine
    {
        public const int MaxChannels = 2;

        public readonly int SampleRate;
        readonly int _x3Len;          // output FIFO length (original: 44100*6)
        readonly long _maxX3Delay;    // x3_len - max_blk_sz - 256

        internal readonly ParamsVec Pv = new ParamsVec();
        readonly RealFft[] _fft = new RealFft[P.NFftBlkSz];
        internal RealFft FftOfSize(int n) { int i = 0; while (i < P.NFftBlkSz - 1 && P.BlkSz(i) < n) i++; return _fft[i]; }
        readonly Channel[] _ch = new Channel[MaxChannels];

        // ---- shared block-scheduling state (names as in MIBlkFx.h) -------------
        long x0i_abs, x0_i, x0_n, x0_lastreal_abs;
        long x3o_abs, x3_o, x3_lastreal_abs;
        long prev_blkend_abs, out_delay_n;
        bool buffering_block;
        long curr_blk_abs, blk_len, blk_len_fft;
        int plan_n;
        long fade_samps;
        float mix_back;
        long buf_n;
        bool prev_was_write;          // prev_work_mode == WM_WRITE

        public BlockFxEngine(int sampleRate, uint wideSeedSalt = 0x2545F491)
        {
            SampleRate = Math.Max(8000, sampleRate);
            _x3Len = SampleRate * 6;
            _maxX3Delay = _x3Len - P.MaxBlkSz - 256;
            for (int i = 0; i < P.NFftBlkSz; i++) _fft[i] = new RealFft(P.BlkSz(i));
            for (int c = 0; c < MaxChannels; c++) _ch[c] = new Channel(this, c, _x3Len);
            Reset();
        }

        public long MaxX3Delay => _maxX3Delay;

        /// <summary>
        /// false = original behaviour (DC bin zeroed every block, which removes everything
        /// below half a bin width: ~190 Hz at 128-sample blocks). true = bin 0 passes through.
        /// </summary>
        public bool KeepDC { get; set; }

        /// <summary>
        /// true = original: output power is restored to input power after the effects, so
        /// a set's Amp only rebalances bands. false = Amp and the effects' level changes are
        /// absolute.
        /// </summary>
        public bool LevelMatch { get; set; } = true;

        /// <summary>Spectrum capture for the Edit window (null or inactive = no cost).</summary>
        public SpectrumTap Tap { get; set; }

        /// <summary>
        /// false = original "Blocky" mode: unwindowed blocks joined by a linear crossfade.
        /// true = "Smooth": sqrt-Hann analysis and synthesis windows, overlap-add with a
        /// running normalisation (so any hop and any block size reconstructs exactly),
        /// hop limited to half a block.
        /// </summary>
        public bool Smooth { get; set; }
        bool _lastSmooth;

        // sidechain present for the current buffer (Cross effect)
        internal bool _scActive;
        // estimated hop of the block being processed (Blur time constant)
        internal long cur_hop = 1;

        // sqrt-Hann window cache (periodic), shared by the channels
        float[] _win = new float[P.MaxBlkSz];
        int _winLen = -1;
        internal readonly float[] _xw = new float[P.MaxBlkSz];
        internal float[] Window(int len)
        {
            if (len != _winLen)
            {
                if (_win.Length < len) _win = new float[len];
                for (int i = 0; i < len; i++)
                    _win[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / len));
                _winLen = len;
            }
            return _win;
        }

        public void Reset()
        {
            Pv.Reset();
            x0i_abs = 0; x0_i = 0; x0_n = 0; x0_lastreal_abs = 0;
            x3o_abs = 0; x3_o = 0; x3_lastreal_abs = 0;
            prev_blkend_abs = 0; out_delay_n = 0;
            buffering_block = false;
            curr_blk_abs = 0; blk_len = 0; blk_len_fft = 0;
            prev_was_write = false;
            foreach (var c in _ch) c.Clear();
        }

        /// <summary>
        /// Copy channel 0's buffers into channel 1. Used when switching from Mono to a
        /// two-channel mode so the right channel doesn't start with stale audio.
        /// </summary>
        public void CloneChannel0To1() => _ch[1].CopyFrom(_ch[0]);

        /// <summary>Select the Smear seed scheme: identical per channel (Stereo) or decorrelated (Wide).</summary>
        public bool WideSeeds
        {
            get => _wide;
            set { if (value != _wide) { _wide = value; _ch[1].SyncRandFrom(_ch[0], value); } }
        }
        bool _wide;

        // =====================================================================
        // Tick: append this tick's parameter changes (NoValue = unchanged) to the FIFO
        // =====================================================================
        /// <param name="songPos">song position in ticks at this point (PlayPosition + PosInTick/SamplesPerTick);
        /// NaN = unknown, derived from the audio position (headless use)</param>
        /// <param name="playing">transport running (the band LFO holds while stopped)</param>
        public void Tick(Params changed, long sampsPerTick, double songPos = double.NaN, bool playing = true)
        {
            if (double.IsNaN(songPos)) songPos = (double)x3o_abs / Math.Max(1, sampsPerTick);
            Node n;
            bool initial = Pv.InitialParam;
            if (initial ||                       // first param
                Pv.In.PosAbs == x3o_abs ||       // no new audio
                Pv.Full)                         // can't fit another param update
            {
                n = Pv.In;
                n.Update(changed);
                // a merged node keeps the song position of its PosAbs; only the first one is set here
                if (initial) { n.SongPos = songPos; n.Advancing = playing; }
            }
            else
            {
                Pv.IncIn();
                n = Pv.In;
                n.CopyFrom(changed);
                n.PosAbs = x3o_abs;
                n.SongPos = songPos;
                n.Advancing = playing;
            }
            if (sampsPerTick < 1) sampsPerTick = 1;
            n.SampsPerTick = sampsPerTick;
            n.TicksPerSamp = 1.0f / sampsPerTick;
            Pv.InUpdate();
        }

        // =====================================================================
        // Work
        // =====================================================================
        /// <param name="io">per-channel buffers; input on entry, output on exit</param>
        /// <param name="nch">channels to process (1 or 2)</param>
        /// <param name="hasInput">false = upstream silent (original's WM_WRITE)</param>
        /// <returns>false when the tail has fully played out and nothing was written</returns>
        public bool Work(float[][] io, int n, int nch, bool hasInput, long hostSampsPerTick, float[][] sc = null)
        {
            buf_n = n;
            _scActive = sc != null;
            if (Smooth != _lastSmooth)
            {
                // the two modes keep incompatible data in the output FIFO
                foreach (var c in _ch) c.ClearOut();
                _lastSmooth = Smooth;
            }

            if (!hasInput)
            {
                // writing has finished, determine whether we need to keep outputting
                if (!prev_was_write)
                {
                    x0_lastreal_abs = x3o_abs + buf_n;          // data remaining to be processed
                    x3_lastreal_abs = curr_blk_abs + blk_len;   // end of previous block
                }
                if (x0i_abs - x0_lastreal_abs <= 0)
                {
                    long t = curr_blk_abs + blk_len;
                    if (t - x3_lastreal_abs > 0) x3_lastreal_abs = t;
                }
                else if (x3o_abs - x3_lastreal_abs > 0)
                {
                    return false;   // passed the end of the last block containing real data
                }
                for (int c = 0; c < nch; c++) Array.Clear(io[c], 0, n);
            }

            // shift out used data if not enough room for the new buffer
            if (x0_i + x0_n + buf_n > _ch[0].X0Len)
            {
                for (int c = 0; c < MaxChannels; c++) _ch[c].ShiftX0(x0_i, x0_n, buf_n);
                x0_i = 0;
            }

            // copy input data into the pre-fft buffer
            for (int c = 0; c < nch; c++)
            {
                Array.Copy(io[c], 0, _ch[c].x0, x0_i + x0_n, n);
                if (sc != null) Array.Copy(sc[c], 0, _ch[c].xs0, x0_i + x0_n, n);
                else Array.Clear(_ch[c].xs0, (int)(x0_i + x0_n), n);
            }
            x0_n += buf_n;

            while (true)
            {
                if (!buffering_block)
                {
                    if (!ParamsRdy(hostSampsPerTick)) break;
                }
                if (buffering_block)
                {
                    if (!BlkRdy(nch)) break;

                    fade_samps = (long)(P.OverlapFrac(Pv.G(P.Overlap)) * blk_len);
                    if (Smooth && fade_samps < blk_len / 2) fade_samps = blk_len / 2;   // hop <= N/2
                    cur_hop = Math.Max(8, blk_len - fade_samps);
                    mix_back = P.G[P.MixBack].Frac(Pv.G(P.MixBack));
                    _lfoBlkStart = x0i_abs; _lfoBlkLen = Math.Max(1, blk_len);   // v1.7: LFO averaged over the block
                    var tap = Tap;
                    bool tapOn = tap != null && tap.Active;
                    for (int c = 0; c < nch; c++)
                    {
                        var ch = _ch[c];
                        if (mix_back < 1.0f)
                        {
                            ch.DoFFT(_fft[plan_n], (int)blk_len_fft, (int)x0_i);
                            if (tapOn) tap.Capture(false, plan_n, SampleRate, ch.x1, x0i_abs, 1f);
                            ch.ProcessFFT((int)blk_len_fft);
                            ch.DoIFFT(_fft[plan_n]);
                            if (tapOn) { float s = ch.X2Scale * 1e-3f; tap.Capture(true, plan_n, SampleRate, ch.x1, x0i_abs, s * s); }
                        }
                        ch.MixToOutputBuffer();
                    }
                    long hop = NextBlk();
                    if (tapOn) tap.Advance(hop);
                }
            }

            // copy from x3 (output FIFO) to the output buffer
            for (int c = 0; c < nch; c++) _ch[c].CopyOut(io[c], (int)x3_o, n);

            x3_o = Wrap(buf_n + x3_o);
            x3o_abs += buf_n;
            Tap?.SetPlayhead(x3o_abs, out_delay_n);
            prev_was_write = !hasInput;
            return true;
        }

        long Wrap(long i) { i %= _x3Len; if (i < 0) i += _x3Len; return i; }

        // =====================================================================
        // Band LFO (v1.3): phase from the song position, carried through the
        // tick FIFO so it lines up with the audio exactly like every parameter
        // =====================================================================
        internal long _lfoBlkStart, _lfoBlkLen = 1;   // input span of the block being processed (v1.7)

        // v1.5: where the LFO put each set's edges on the latest block (channel 0), for the
        // Edit window overlay. FreqA/FreqB units; NaN until a block has moved them. Written on the
        // audio thread, read on the UI thread (a torn read only misplaces one frame's drawing).
        internal readonly float[] LiveA = NewNaN(P.MaxSets), LiveB = NewNaN(P.MaxSets);
        /// <summary>v2.2: the note each set is following on the latest block (Buzz note byte; 0 = none).</summary>
        internal readonly int[] LiveNote = new int[P.MaxSets];
        static float[] NewNaN(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = float.NaN; return a; }

        /// <summary>
        /// Song position (ticks) at an absolute input sample: interpolated between the current FIFO
        /// node and the next one. It holds still while the transport is stopped (equal positions). A
        /// backwards or large jump (song loop, cursor moved) keeps running at the node's tempo and is
        /// taken at the next node instead of being smeared across the gap. Past the next node it
        /// carries on from that node at its tempo (v1.7: a block can span several ticks).
        /// </summary>
        internal double LfoPos() => LfoPosAt(x0i_abs);

        internal double LfoPosAt(long abs)
        {
            var c = Pv.Curr;
            long el = abs - c.PosAbs;
            if (el <= 0) return c.SongPos;
            double tps = c.TicksPerSamp > 0 ? c.TicksPerSamp : 0;
            if (Pv.ExistsNext)
            {
                var nx = Pv.Next;
                long span = nx.PosAbs - c.PosAbs;
                if (span <= 0) return c.SongPos;
                double d = nx.SongPos - c.SongPos;
                bool normal = d >= 0 && d <= span * tps * 1.5 + 1e-6;   // advancing or stopped
                if (el < span) return normal ? c.SongPos + d * ((double)el / span) : c.SongPos + (c.Advancing ? el * tps : 0);
                double ntps = nx.TicksPerSamp > 0 ? nx.TicksPerSamp : tps;
                return nx.SongPos + (nx.Advancing ? (el - span) * ntps : 0);
            }
            // newest node (TickDelay shorter than a tick): run on at the node's tempo, at most one tick
            return c.SongPos + (c.Advancing ? Math.Min(el * tps, 1.0) : 0);
        }

        /// <summary>
        /// LFO output for the block being processed: the LFO averaged over the block's input span,
        /// weighted by how much each sample counts in the output (Hann in Smooth mode, flat in Blocky).
        /// v1.7: v1.3-1.6 took one value at the block start, so the modulation lagged half a block and
        /// rates near the block rate aliased into slow wobble. Averaging centres it and fades rates too
        /// fast for the block size instead of aliasing them. Slow rates are unchanged. Tempo rates
        /// read the song position; Hz rates (v1.7) run free on the audio sample position.
        /// </summary>
        internal float LfoAt(int set, int shape, int rate, float phaseOffset)
        {
            int nr = P.LfoRateCount;
            rate = rate < 0 ? 0 : (rate >= nr ? nr - 1 : rate);
            bool hz = P.LfoIsHz(rate);
            double tps = Pv.Curr.TicksPerSamp;
            double per = P.LfoPeriodSamples(rate, tps > 0 ? 1.0 / tps : 1e12, SampleRate);
            double cycles = _lfoBlkLen / Math.Max(1.0, per);
            int k = (int)Math.Min(256.0, 16.0 + cycles * 8.0);
            double freq = hz ? P.LfoHz[rate - P.LfoTicks.Length] / (double)SampleRate : 0;
            double ticks = hz ? 1 : P.LfoTicks[rate];
            double sum = 0, wsum = 0;
            for (int j = 0; j < k; j++)
            {
                double u = (j + 0.5) / k;
                double w = Smooth ? 0.5 - 0.5 * Math.Cos(6.283185307179586 * u) : 1.0;
                long abs = _lfoBlkStart + (long)(u * _lfoBlkLen);
                double ph = hz ? abs * freq : LfoPosAt(abs) / ticks;
                sum += w * P.LfoShapeAt(shape, ph + phaseOffset, set);
                wsum += w;
            }
            return (float)(sum / wsum);
        }

        // return true if we can interpolate the current block or the output delay is too short
        bool ParamsRdy(long hostSampsPerTick)
        {
            if (Pv.ExistsNext)
            {
                Pv.SetFrac(x0i_abs);
                // update the output delay only if the tick delay was explicitly set
                if (Pv.Curr.G[P.TickDelay] != P.G[P.TickDelay].No)
                    out_delay_n = (long)P.TickDelaySamples(Pv.G(P.TickDelay), Pv.SampsPerTick, _maxX3Delay);
                curr_blk_abs = out_delay_n + x0i_abs;

                plan_n = Pv.G(P.BlkLen);
                blk_len_fft = P.BlkSz(plan_n);
                buffering_block = true;
                return true;
            }

            // do nothing if this is the initial (default) param
            if (Pv.InitialParam) return false;

            // can't interpolate tick delay yet, but the last seen tick delay may force a block out
            int td = Pv.Curr.G[P.TickDelay];
            if (td != P.G[P.TickDelay].No)
                out_delay_n = (long)P.TickDelaySamples(td, hostSampsPerTick, _maxX3Delay);
            curr_blk_abs = out_delay_n + x0i_abs;

            // if the next block doesn't need to be output, do nothing
            if (curr_blk_abs - x3o_abs >= buf_n) return false;

            plan_n = P.G[P.BlkLen].Lim(Pv.FilledCurr.G[P.BlkLen]);
            blk_len_fft = P.BlkSz(plan_n);
            buffering_block = true;
            return true;
        }

        // return true if there's enough data to process the current block or we are forced to output
        bool BlkRdy(int nch)
        {
            if (curr_blk_abs - x3o_abs < buf_n)
            {
                // forced: shrink the block to what's buffered, to reduce load
                while (P.BlkSz(plan_n - 1) > x0_n) plan_n--;
                if (plan_n < 0) plan_n = 0;
                blk_len_fft = P.BlkSz(plan_n);

                long n = blk_len_fft - x0_n;
                if (n <= 0) blk_len = blk_len_fft;
                else
                {
                    // zero-pad the pre-fft buffer
                    blk_len = x0_n;
                    bool move = x0_i + blk_len_fft > _ch[0].X0Len;
                    for (int c = 0; c < MaxChannels; c++)
                    {
                        foreach (var x0 in new[] { _ch[c].x0, _ch[c].xs0 })
                        {
                            if (move && x0_n > 0) Array.Copy(x0, x0_i, x0, 0, x0_n);
                            Array.Clear(x0, (int)((move ? 0 : x0_i) + blk_len), (int)n);
                        }
                    }
                    if (move) x0_i = 0;
                }
                return true;
            }

            if (x0_n >= blk_len_fft) { blk_len = blk_len_fft; return true; }
            return false;
        }

        long NextBlk()
        {
            long nonoverlap_n = blk_len - fade_samps;
            if (nonoverlap_n < 8) nonoverlap_n = 8;
            if (nonoverlap_n > blk_len) nonoverlap_n = blk_len;

            // block sync & advance the param FIFO
            while (Pv.ExistsNext)
            {
                long tick_rel = Pv.AbsTickPosNext - x0i_abs;
                if (tick_rel > nonoverlap_n) break;
                Pv.IncOutPos();
                int bs = Pv.Curr.G[P.BlkSync];
                if (bs != P.G[P.BlkSync].No && P.G[P.BlkSync].Lim(bs) != 0)
                {
                    nonoverlap_n = Math.Max(0, tick_rel);   // force next block to start on this tick
                    break;
                }
            }

            x0_i += nonoverlap_n;
            x0_n -= nonoverlap_n;
            x0i_abs += nonoverlap_n;

            long blkend_abs = curr_blk_abs + blk_len;
            if (blkend_abs - prev_blkend_abs > 0) prev_blkend_abs = blkend_abs;
            buffering_block = false;
            return nonoverlap_n;
        }

        // =====================================================================
        // Per-channel processing
        // =====================================================================
        internal sealed class Channel
        {
            readonly BlockFxEngine e;
            readonly int _index;
            internal float[] x0;              // pre-FFT input
            internal float[] xs0;             // pre-FFT sidechain, kept in lockstep with x0
            readonly float[] xs1;             // sidechain spectrum (halfcomplex), on demand
            bool _xs1Valid;
            readonly float[] xn;              // Smooth mode: overlap-add normalisation FIFO
            RealFft _curFft; int _curX0i;
            readonly float[][] _fxState = new float[P.MaxSets][];   // Freeze / Blur per-bin state
            readonly int[] _fxStateN = new int[P.MaxSets];
            readonly bool[] _fxInit = new bool[P.MaxSets];
            readonly long[] _capSerial = new long[P.MaxSets];   // last tick node whose Capture was honoured
            readonly float[] _origR = new float[P.MaxBlkSz / 2 + 2], _origI = new float[P.MaxBlkSz / 2 + 2];
            // PitchShift phase-vocoder state per set (allocated on first use) and shared scratch
            readonly float[][] _pvPrev = new float[P.MaxSets][], _pvSum = new float[P.MaxSets][];
            readonly long[] _pvAbs = new long[P.MaxSets];
            readonly int[] _pvN = new int[P.MaxSets];
            float[] _synRe = new float[0], _synIm = new float[0], _synPh = new float[0], _synPk = new float[0];
            float[] _pm = new float[0], _pph = new float[0], _pw = new float[0];
            int[] _peaks = new int[0];
            int[] _rgRs = new int[0], _rgRe = new int[0], _rgS = new int[0]; float[] _rgTf = new float[0];   // per-peak regions (v2.1)
            // v2.1 formant control: spectral envelope scratch (log magnitude per bin)
            float[] _envA = new float[0], _envB = new float[0], _envL = new float[0], _envT = new float[0];
            internal readonly float[] x1;     // spectrum, halfcomplex, +1 for DC imag
            internal readonly float[] x2;     // post-IFFT (also CopyHarm scratch)
            internal readonly float[] x3;     // output FIFO
            float x2_scale, total_in_pwr, total_out_pwr;
            int N;                            // blk_len_fft for the block in progress

            // current FX-set values (the mi->curr_* fields)
            float curr_amp, curr_fxval, fcurr_start_bin, fcurr_stop_bin;
            float curr_pitch_offset;          // v2.2: semitones added to PitchShift's interval (Follow Pitch)
            long curr_start_bin, curr_stop_bin;

            readonly int[] _effectNum = new int[P.MaxSets];
            readonly int[] _rand = new int[P.MaxSets];

            static readonly float[] SinCosRe = new float[4096], SinCosIm = new float[4096];
            static Channel()
            {
                for (int i = 0; i < 4096; i++)
                {
                    float a = (float)(i * 2.0 * 3.14159265358979323846 / 4096);
                    SinCosRe[i] = MathF.Cos(a); SinCosIm[i] = MathF.Sin(a);
                }
            }

            public Channel(BlockFxEngine engine, int index, int x3Len)
            {
                e = engine; _index = index;
                x0 = new float[P.MaxBlkSz + 2048];
                xs0 = new float[P.MaxBlkSz + 2048];
                xs1 = new float[P.MaxBlkSz + 1];
                xn = new float[x3Len];
                x1 = new float[P.MaxBlkSz + 1];
                x2 = new float[P.MaxBlkSz + 8];
                x3 = new float[x3Len];
            }

            public int X0Len => x0.Length;
            public float X2Scale => x2_scale;

            public void Clear()
            {
                Array.Clear(x0, 0, x0.Length); Array.Clear(x1, 0, x1.Length); Array.Clear(x2, 0, x2.Length); Array.Clear(x3, 0, x3.Length);
                Array.Clear(xs0, 0, xs0.Length); Array.Clear(xn, 0, xn.Length);
                for (int i = 0; i < P.MaxSets; i++) { _effectNum[i] = 0; _rand[i] = Seed(i); _fxInit[i] = false; _fxStateN[i] = 0; }
            }

            public void CopyFrom(Channel o)
            {
                if (x0.Length != o.x0.Length) { x0 = new float[o.x0.Length]; xs0 = new float[o.xs0.Length]; }
                Array.Copy(o.x0, x0, x0.Length);
                Array.Copy(o.xs0, xs0, xs0.Length);
                Array.Copy(o.x3, x3, x3.Length);
                Array.Copy(o.xn, xn, xn.Length);
                for (int i = 0; i < P.MaxSets; i++) { _effectNum[i] = o._effectNum[i]; _rand[i] = Seed(i); _fxInit[i] = false; _fxStateN[i] = 0; }
            }

            public void ClearOut()
            {
                Array.Clear(x3, 0, x3.Length);
                Array.Clear(xn, 0, xn.Length);
            }

            // Stereo: right follows left's Smear sequence exactly; Wide: offset it.
            public void SyncRandFrom(Channel o, bool wide)
            {
                for (int i = 0; i < P.MaxSets; i++)
                {
                    int s = o._rand[i];
                    if (wide) { s = (s ^ 0x2545F491) & 0x1FFFFFFF; if (s == 0) s = 1; }
                    _rand[i] = s;
                }
            }

            // The original left the Smear PRBS state uninitialised. Seed deterministically:
            // same sequence on both channels (Stereo) or a per-channel offset (Wide).
            int Seed(int set)
            {
                int s = 0x1234567 + set * 0x9E3779;
                if (e._wide && _index > 0) s ^= 0x2545F491;
                s &= 0x1FFFFFFF;
                return s == 0 ? 1 : s;
            }

            public void ShiftX0(long x0_i, long x0_n, long buf_n)
            {
                if (x0_n + buf_n > x0.Length) { Array.Resize(ref x0, (int)(x0_n + buf_n)); Array.Resize(ref xs0, (int)(x0_n + buf_n)); }
                if (x0_n > 0) { Array.Copy(x0, x0_i, x0, 0, x0_n); Array.Copy(xs0, x0_i, xs0, 0, x0_n); }
            }

            public void CopyOut(float[] buf, int start, int n)
            {
                int len = x3.Length, i = start;
                if (!e.Smooth)
                {
                    for (int k = 0; k < n; k++) { buf[k] = x3[i]; if (++i == len) i = 0; }
                    return;
                }
                // Smooth: normalise the overlap-add sum, then free the slot for reuse.
                // (Floor 0.05: below it, i.e. a stream start, a gap, or the far tail of a longer
                //  block after BlockLen shrinks, the sum fades in instead of being amplified.)
                for (int k = 0; k < n; k++)
                {
                    float nv = xn[i];
                    buf[k] = Sat(x3[i] / (nv > 0.05f ? nv : 0.05f));
                    x3[i] = 0; xn[i] = 0;
                    if (++i == len) i = 0;
                }
            }

            // ---- FFT -----------------------------------------------------------
            public void DoFFT(RealFft fft, int n, int x0_i)
            {
                N = n;
                _curFft = fft; _curX0i = x0_i; _xs1Valid = false;
                if (e.Smooth) fft.Forward(Windowed(x0, x0_i), 0, x1);
                else fft.Forward(x0, x0_i, x1);
                float scale = 1e-3f / N;
                x1[N] = 0;                     // imaginary part of DC

                x1[0] *= scale;
                float acc = x1[0] * x1[0];
                int half = N / 2;
                for (int k = 1; k < half; k++)
                {
                    x1[k] *= scale; x1[N - k] *= scale;
                    acc += x1[k] * x1[k] + x1[N - k] * x1[N - k];
                }
                x1[half] *= scale;             // nyquist is real only
                acc += x1[half] * x1[half];
                total_out_pwr = acc;
                total_in_pwr = acc;
            }

            // analysis window over the real data (blk_len), zero beyond
            float[] Windowed(float[] src, int off)
            {
                int L = (int)e.blk_len;
                var w = e.Window(L);
                var xw = e._xw;
                for (int i = 0; i < L; i++) xw[i] = src[off + i] * w[i];
                if (N > L) Array.Clear(xw, L, N - L);
                return xw;
            }

            // sidechain spectrum for the current block, same window and scale as x1
            void EnsureSidechain()
            {
                if (_xs1Valid) return;
                if (e.Smooth) _curFft.Forward(Windowed(xs0, _curX0i), 0, xs1);
                else _curFft.Forward(xs0, _curX0i, xs1);
                float scale = 1e-3f / N;
                for (int k = 0; k < N; k++) xs1[k] *= scale;
                xs1[N] = 0;
                _xs1Valid = true;
            }

            public void DoIFFT(RealFft fft)
            {
                fft.Inverse(x1, x2);
                double s = P.AmpMult(e.Pv.G(P.OutAmp)) * 1e3;
                // (only if the block had input: a silent block with Freeze output must not be muted)
                if (e.LevelMatch && total_out_pwr > 0 && total_in_pwr > 0) s *= Math.Sqrt(total_in_pwr / total_out_pwr);
                if (s > 1e30) x2_scale = 1.0f;
                else if (s < 1e-30) x2_scale = 0.0f;
                else x2_scale = (float)s;
            }

            public void ProcessFFT(int n)
            {
                var pv = e.Pv;
                int nsets = Math.Min(pv.NTrkVals, P.MaxSets);
                bool anySolo = AnySolo(pv, nsets);
                for (int i = 0; i < nsets; i++)
                {
                    int freq_a = pv.T(i, P.FreqA);
                    int freq_b = pv.T(i, P.FreqB);
                    if (freq_a == 0 && freq_b == 0) continue;   // effect off
                    if (!Audible(pv.FilledCurr, i, anySolo)) continue;   // v1.4 Mute / Solo

                    float fa = freq_a, fb = freq_b;
                    bool lfoEdges = false;
                    curr_fxval = P.T[P.FxVal].Frac(pv.T(i, P.FxVal));

                    // note-following (v2.2): the band (or FreqA, or PitchShift's interval) is as set
                    // for C-4 and moves with the last note played on this set's track. Read per tick
                    // (FilledCurr keeps the last note: notes are events, NoValue = 0); off = back home.
                    curr_pitch_offset = 0f;
                    int follow = P.T[P.Follow].Lim(pv.FilledCurr.T[i, P.Follow]);
                    int noteV = pv.FilledCurr.T[i, P.Note];
                    int noteI = follow == P.FollowOff ? -1 : P.NoteIndex(noteV);
                    if (_index == 0) e.LiveNote[i] = noteI >= 0 ? noteV : 0;
                    bool followEdges = follow == P.FollowBand || follow == P.FollowFreqA;
                    if (noteI >= 0)
                    {
                        float st = noteI - P.NoteRef;
                        if (follow == P.FollowBand) { fa += st * 256f; fb += st * 256f; }
                        else if (follow == P.FollowFreqA) fa += st * 256f;
                        else if (follow == P.FollowPitch) curr_pitch_offset = st;
                    }

                    // band LFO (v1.3)
                    int lt = P.T[P.LfoTarget].Lim(pv.FilledCurr.T[i, P.LfoTarget]);
                    if (lt != P.LfoOff)
                    {
                        float depth = P.T[P.LfoDepth].Frac(pv.T(i, P.LfoDepth));
                        if (depth > 0f)
                        {
                            float l = depth * e.LfoAt(i, P.T[P.LfoShape].Lim(pv.FilledCurr.T[i, P.LfoShape]),
                                                      P.T[P.LfoRate].Lim(pv.FilledCurr.T[i, P.LfoRate]),
                                                      pv.T(i, P.LfoPhase) / 256f);
                            if (lt == P.LfoValue) curr_fxval = Clamp(curr_fxval + l * P.LfoValueRange, 0f, 1f);
                            else
                            {
                                float d = l * P.LfoFreqRange * 256f;   // FreqA/FreqB units: 1/256 semitone
                                bool inside = freq_a <= freq_b;
                                if (lt == P.LfoBand || lt == P.LfoFreqA) fa += d;
                                if (lt == P.LfoBand || lt == P.LfoFreqB) fb += d;
                                if (lt == P.LfoWidth) { if (inside) { fa -= d; fb += d; } else { fa += d; fb -= d; } }
                                // never let the LFO flip a set between inside and outside the range:
                                // edges that cross meet in the middle (an empty band) instead
                                if (inside ? fa > fb : fa < fb) { float m = 0.5f * (fa + fb); fa = m; fb = m; }
                                float fmax = P.T[P.FreqA].Max;
                                fa = Clamp(fa, 1f, fmax); fb = Clamp(fb, 1f, fmax);
                                lfoEdges = true;
                            }
                        }
                    }
                    if (followEdges && !lfoEdges)
                    {
                        // the same guards as the LFO: never flip inside/outside, stay in range
                        bool inside = freq_a <= freq_b;
                        if (inside ? fa > fb : fa < fb) { float m = 0.5f * (fa + fb); fa = m; fb = m; }
                        float fmax = P.T[P.FreqA].Max;
                        fa = Clamp(fa, 1f, fmax); fb = Clamp(fb, 1f, fmax);
                        lfoEdges = true;
                    }
                    if (lfoEdges && _index == 0) { e.LiveA[i] = fa; e.LiveB[i] = fb; }

                    float s = (float)N / e.SampleRate;
                    fcurr_start_bin = Clamp(P.FreqHz(fa) * s, 1, N / 2);
                    fcurr_stop_bin = Clamp(P.FreqHz(fb) * s, 1, N / 2);
                    curr_start_bin = (long)(fcurr_start_bin + 0.5f);
                    curr_stop_bin = (long)(fcurr_stop_bin + 0.5f);
                    if (lfoEdges)
                    {
                        // the engine picks inside/outside from the bins: keep the set's own orientation
                        // when LFO-moved edges meet (inside: empty band; outside: nothing kept)
                        if (freq_a <= freq_b) { if (curr_start_bin > curr_stop_bin) curr_start_bin = curr_stop_bin; }
                        else if (curr_start_bin <= curr_stop_bin)
                        {
                            if (curr_stop_bin > 1) curr_stop_bin = curr_start_bin - 1;
                            else curr_start_bin = curr_stop_bin + 1;
                            if (curr_stop_bin < 1) { curr_stop_bin = 1; curr_start_bin = 2; }
                        }
                    }
                    curr_amp = P.AmpMult(pv.T(i, P.Amp));

                    int effect_n = P.T[P.Effect].Lim(pv.FilledCurr.T[i, P.Effect]);
                    if (_effectNum[i] != effect_n)
                    {
                        _effectNum[i] = effect_n; _rand[i] = Seed(i);
                        _fxInit[i] = false; _fxStateN[i] = 0;
                    }

                    // Capture trigger: once per tick node, on the first block processed with
                    // that tick current (so it lands TickDelay-aligned like every parameter)
                    var cur = pv.Curr;
                    if (cur.T[i, P.Capture] == 1 && cur.Serial != _capSerial[i])
                    {
                        _capSerial[i] = cur.Serial;
                        _fxInit[i] = false;          // Freeze recaptures, Blur restarts from the live level
                    }

                    int soft = pv.T(i, P.Soft);
                    float mix = SetMix(pv, i);                  // v1.8 wet/dry; 1 = effect only
                    if ((soft > 0 || mix < 1f) && P.IsPerBin(effect_n))
                    {
                        float before = total_out_pwr;
                        float pOrig = SaveRanges();
                        RunEffect(effect_n, i);
                        BlendSoft(soft / 12f, mix, before, pOrig);
                    }
                    else RunEffect(effect_n, i);
                }
                if (!e.KeepDC) x1[0] = 0;   // original: too hard to deal with DC
            }

            // v1.4: a set runs unless it's muted, or another active set is soloed (Mute wins over Solo).
            // Off sets (FreqA = FreqB = 0) don't count as soloed, so a forgotten Solo on an empty
            // set can't silence the rest. Read un-interpolated, TickDelay-aligned like Effect.
            internal static bool AnySolo(ParamsVec pv, int nsets)
            {
                var f = pv.FilledCurr;
                for (int i = 0; i < nsets; i++)
                    if (f.T[i, P.Solo] == 1 && f.T[i, P.Mute] != 1 && (f.T[i, P.FreqA] != 0 || f.T[i, P.FreqB] != 0)) return true;
                return false;
            }

            internal static bool Audible(Params f, int i, bool anySolo)
                => f.T[i, P.Mute] != 1 && (!anySolo || f.T[i, P.Solo] == 1);

            static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
            static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
            static float Lin(float frac, float a, float b) => (1.0f - frac) * a + frac * b;

            // ---- output mixing (mixToOutputBuffer + wrap_process.h) ------------
            public void MixToOutputBuffer()
            {
                if (e.Smooth) { MixOla(); return; }
                long prev_blkend_abs = e.prev_blkend_abs, curr_blk_abs = e.curr_blk_abs, x3o_abs = e.x3o_abs;
                long x3_o = e.x3_o, blk_len = e.blk_len;
                int x0_i = (int)e.x0_i;
                float x2s = x2_scale;

                long fadein_n = prev_blkend_abs - curr_blk_abs;
                if (fadein_n < 0)
                {
                    // gap: arbitrary fade out of the previous block's tail
                    long prev_blkend_rel = prev_blkend_abs - x3o_abs;
                    long fade_start_rel = prev_blkend_rel - P.BlkSz0;
                    if (fade_start_rel < 0) fade_start_rel = 0;   // don't fade data already output
                    long n = prev_blkend_rel - fade_start_rel;
                    if (n > 0) FadeOut(e.Wrap(x3_o + fade_start_rel), n);

                    // zero fill the gap, plus an arbitrary fade-in of this block
                    n = -fadein_n;
                    fadein_n = P.BlkSz0;
                    Zero(e.Wrap(x3_o + prev_blkend_rel), n + fadein_n);
                }
                fadein_n = Clamp(fadein_n, 0, e.fade_samps);

                long curr_blk_i = e.Wrap(x3_o + curr_blk_abs - x3o_abs);
                long rest = blk_len - fadein_n;
                long restI = e.Wrap(curr_blk_i + fadein_n);
                float mb = e.mix_back;

                if (mb <= 0.0f)
                {
                    if (fadein_n > 0) XFade(curr_blk_i, x2, 0, x2s, null, 0, 0, fadein_n);
                    ScaleCopy(restI, x2, (int)fadein_n, x2s, null, 0, 0, rest);
                }
                else if (mb >= 1.0f)
                {
                    if (fadein_n > 0) XFade(curr_blk_i, x0, x0_i, 1.0f, null, 0, 0, fadein_n);
                    ScaleCopy(restI, x0, x0_i + (int)fadein_n, 1.0f, null, 0, 0, rest);
                }
                else
                {
                    x2s *= 1.0f - mb;
                    float x0s = mb;
                    if (fadein_n > 0) XFade(curr_blk_i, x2, 0, x2s, x0, x0_i, x0s, fadein_n);
                    ScaleCopy(restI, x2, (int)fadein_n, x2s, x0, x0_i + (int)fadein_n, x0s, rest);
                }
            }

            // Smooth mode: windowed overlap-add into x3, window products into xn
            void MixOla()
            {
                int L = (int)e.blk_len;
                var w = e.Window(L);
                float mb = e.mix_back;
                float wet = mb >= 1.0f ? 0f : x2_scale * (1.0f - mb);
                float dry = mb <= 0.0f ? 0f : mb;
                int x0_i = (int)e.x0_i;
                long start = e.curr_blk_abs, outAbs = e.x3o_abs;
                int i0 = 0;
                if (start < outAbs) i0 = (int)Math.Min(L, outAbs - start);   // never write into the past
                int len = x3.Length;
                int k = (int)e.Wrap(e.x3_o + start + i0 - outAbs);
                for (int i = i0; i < L; i++)
                {
                    float wi = w[i];
                    float v = 0;
                    if (wet != 0f) v += x2[i] * wet;
                    if (dry != 0f) v += x0[x0_i + i] * wi * dry;
                    x3[k] += v * wi;
                    xn[k] += wi * wi;
                    if (++k == len) k = 0;
                }
            }

            static float Sat(float v) => v < -32768f ? -32768f : (v > 32767f ? 32767f : v);

            void FadeOut(long i, long n)
            {
                float fade = 1.0f, fs = -1.0f / n;
                int len = x3.Length, k = (int)i;
                for (long c = 0; c < n; c++) { x3[k] *= fade; fade += fs; if (++k == len) k = 0; }
            }

            void Zero(long i, long n)
            {
                int len = x3.Length, k = (int)i;
                for (long c = 0; c < n; c++) { x3[k] = 0; if (++k == len) k = 0; }
            }

            // PXFade / PXFade2: crossfade existing FIFO content out, new data (y [+ z]) in
            void XFade(long i, float[] y, int yi, float yScale, float[] z, int zi, float zScale, long n)
            {
                float xf = 1.0f, xs = -1.0f / n;
                float yf = 0, ys = yScale / n;
                float zf = 0, zs = zScale / n;
                int len = x3.Length, k = (int)i;
                if (z == null)
                    for (long c = 0; c < n; c++)
                    {
                        x3[k] = Sat(x3[k] * xf + y[yi++] * yf);
                        xf += xs; yf += ys; if (++k == len) k = 0;
                    }
                else
                    for (long c = 0; c < n; c++)
                    {
                        x3[k] = Sat(x3[k] * xf + y[yi++] * yf + z[zi++] * zf);
                        xf += xs; yf += ys; zf += zs; if (++k == len) k = 0;
                    }
            }

            // PScaleCopy / PScaleCopy2
            void ScaleCopy(long i, float[] y, int yi, float yScale, float[] z, int zi, float zScale, long n)
            {
                int len = x3.Length, k = (int)i;
                if (z == null)
                    for (long c = 0; c < n; c++) { x3[k] = Sat(y[yi++] * yScale); if (++k == len) k = 0; }
                else
                    for (long c = 0; c < n; c++) { x3[k] = Sat(y[yi++] * yScale + z[zi++] * zScale); if (++k == len) k = 0; }
            }

            // =================================================================
            // Effects (BlkFxProcess.cpp). Spectrum access is halfcomplex:
            // bin k -> re x1[k], im x1[N-k], exactly as the original's pcplxf.
            // =================================================================
            void RunEffect(int fx, int set)
            {
                switch (fx)
                {
                    case 0: DefaultSplit(fx, set); break;   // Contrast
                    case 1: DefaultSplit(fx, set); break;   // Smear
                    case 2: DefaultSplit(fx, set); break;   // Clip
                    case 3: DefaultSplit(fx, set); break;   // Weed
                    case 4: total_out_pwr += ShiftApply(add: true); break;
                    case 5: total_out_pwr += ShiftApply(add: false); break;
                    case 6: Harmonic(); break;
                    case 7: AutoHarmonic(); break;
                    case 8: CopyHarmonic(); break;
                    case P.PitchShift: PitchShift(set); break;
                    default: DefaultSplit(fx, set); break;   // appended per-bin effects
                }
            }

            // BlkFxProcess::process(): freqA <= freqB processes [A,B); otherwise everything outside
            void DefaultSplit(int fx, int set)
            {
                if (curr_start_bin <= curr_stop_bin) Range(fx, set, curr_start_bin, curr_stop_bin);
                else
                {
                    Range(fx, set, 1, curr_stop_bin + 1);
                    Range(fx, set, curr_start_bin, N / 2);
                }
            }

            void Range(int fx, int set, long b, long end)
            {
                switch (fx)
                {
                    case 0: Contrast(b, end); break;
                    case 1: Smear(set, b, end); break;
                    case 2: Clip(b, end); break;
                    case 3: Weed(b, end); break;
                    case P.Freeze: Freeze(set, b, end); break;
                    case P.Gate: Gate(b, end); break;
                    case P.Blur: Blur(set, b, end); break;
                    case P.Tilt: Tilt(b, end); break;
                    case P.RoboWhisper: RoboWhisper(set, b, end); break;
                    case P.Cross: Cross(b, end); break;
                }
            }

            // BlkFxProcess::process(range): plain amplitude
            void PlainAmp(long b, long end)
            {
                float amp = curr_amp;
                if (amp == 1.0f) return;
                float in_pwr = 0;
                for (long k = b; k < end; k++)
                {
                    long r = k, im = N - k;
                    in_pwr += x1[r] * x1[r] + x1[im] * x1[im];
                    float vr = x1[r] * amp, vi = x1[im] * amp;
                    x1[r] = vr; x1[im] = vi;
                }
                total_out_pwr += (amp * amp - 1) * in_pwr;
            }

            void MatchPwr(long b, long end, float in_pwr, float out_pwr)
            {
                float amp = curr_amp;
                double s = amp;
                if (out_pwr > 0) s *= Math.Sqrt(in_pwr / out_pwr);
                float scale;
                if (s > 1e30) scale = 1.0f;
                else if (s < 1e-30) scale = 0.0f;
                else scale = (float)s;
                for (long k = b; k < end; k++)
                {
                    float vr = x1[k] * scale, vi = x1[N - k] * scale;
                    x1[k] = vr; x1[N - k] = vi;
                }
                total_out_pwr += (amp * amp - 1) * in_pwr;
            }

            // --- Contrast: |X|^(2*raise+1), power matched --------------------------
            void Contrast(long b, long end)
            {
                float t = curr_fxval;
                if (t == 0.0f) { PlainAmp(b, end); return; }
                t -= 0.5f;
                if (t >= 0.0f) ContrastCore(b, end, Lin(t * t * 4.0f, 0.0f, 4.0f), false);
                else ContrastCore(b, end, Lin(t * t * 4.0f, 0.0f, -0.5f), true);
            }

            void ContrastCore(long b, long end, float raise, bool raise_neg)
            {
                float in_pwr = 0, out_pwr = 0;
                float limit;
                float r = raise * 2;
                if (r < 0) limit = (r >= -1) ? 1e-28f : MathF.Pow(1e28f, 1.0f / r);
                else limit = (r < 1) ? 1e28f : MathF.Pow(1e28f, 1.0f / r);

                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    float t0 = x1[k] * x1[k] + x1[im] * x1[im];
                    in_pwr += t0;
                    float t1;
                    if (raise_neg) t1 = (t0 < limit) ? 1.0f : MathF.Pow(t0, raise);
                    else { if (t0 > limit) t0 = limit; t1 = MathF.Pow(t0, raise); }
                    out_pwr += t0 * t1 * t1;
                    float vr = x1[k] * t1, vi = x1[im] * t1;
                    x1[k] = vr; x1[im] = vi;
                }
                MatchPwr(b, end, in_pwr, out_pwr);
            }

            // --- Smear: blend towards a pseudo-random phase -------------------------
            static int Prbs29(int x) => (x << 1) ^ ((x >> 22) & 1) ^ ((x >> 25) & 1) ^ ((x >> 28) & 1);

            void Smear(int set, long b, long end)
            {
                float smear = curr_fxval;
                if (smear == 0.0f) { PlainAmp(b, end); return; }
                float amp = curr_amp, in_pwr = 0;
                int rand = _rand[set];
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    int angle = rand & 4095;
                    float xr = x1[k], xi = x1[im];
                    in_pwr += xr * xr + xi * xi;
                    float mr = SinCosRe[angle] * smear + 1.0f - smear;
                    float mi = SinCosIm[angle] * smear;
                    float yr = (xr * mr - xi * mi) * amp;
                    float yi = (xr * mi + xi * mr) * amp;
                    x1[k] = yr; x1[im] = yi;
                    rand = Prbs29(rand);
                }
                total_out_pwr += (amp * amp - 1) * in_pwr;
                _rand[set] = rand;
            }

            // --- Weed: zero bins below (Value > 0.5) or above (Value < 0.5) a threshold
            void Weed(long b, long end)
            {
                float in_pwr = 0, out_pwr = 0;
                float thresh = curr_fxval * 2.0f - 1.0f;
                if (thresh >= 0)
                {
                    thresh *= thresh * 24.0f * total_out_pwr / N;
                    for (long k = b; k < end; k++)
                    {
                        long im = N - k;
                        float t0 = x1[k] * x1[k] + x1[im] * x1[im];
                        in_pwr += t0;
                        if (t0 < thresh) { x1[k] = 0; x1[im] = 0; }
                        else out_pwr += t0;
                    }
                }
                else
                {
                    thresh = 1 + thresh;
                    thresh *= thresh * 24.0f * total_out_pwr / N;
                    for (long k = b; k < end; k++)
                    {
                        long im = N - k;
                        float t0 = x1[k] * x1[k] + x1[im] * x1[im];
                        in_pwr += t0;
                        if (t0 >= thresh) { x1[k] = 0; x1[im] = 0; }
                        else out_pwr += t0;
                    }
                }
                MatchPwr(b, end, in_pwr, out_pwr);
            }

            // --- Clip: limit bin magnitudes to a threshold ---------------------------
            void Clip(long b, long end)
            {
                float in_pwr = 0, out_pwr = 0;
                float thresh = 1 - curr_fxval;
                thresh *= thresh * 8.0f * total_out_pwr / N;
                float rt = MathF.Sqrt(thresh);
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    float t0 = x1[k] * x1[k] + x1[im] * x1[im];
                    in_pwr += t0;
                    if (t0 >= thresh && t0 > 1e-37f)
                    {
                        float t1 = rt / MathF.Sqrt(t0);
                        float vr = x1[k] * t1, vi = x1[im] * t1;
                        x1[k] = vr; x1[im] = vi;
                        out_pwr += thresh;
                    }
                    else out_pwr += t0;
                }
                MatchPwr(b, end, in_pwr, out_pwr);
            }


            // =================================================================
            // Soft band edges (appended feature)
            // =================================================================
            // The processed ranges, exactly as DefaultSplit uses them.
            int Ranges(out long b0, out long e0, out long b1, out long e1)
            {
                if (curr_start_bin <= curr_stop_bin) { b0 = curr_start_bin; e0 = curr_stop_bin; b1 = e1 = 0; return 1; }
                b0 = 1; e0 = curr_stop_bin + 1; b1 = curr_start_bin; e1 = N / 2; return 2;
            }

            float SaveRanges()
            {
                float p = 0;
                int nr = Ranges(out long b0, out long e0, out long b1, out long e1);
                for (int r = 0; r < nr; r++)
                {
                    long b = r == 0 ? b0 : b1, end = r == 0 ? e0 : e1;
                    for (long k = b; k < end; k++)
                    {
                        float vr = x1[k], vi = x1[N - k];
                        _origR[k] = vr; _origI[k] = vi;
                        p += vr * vr + vi * vi;
                    }
                }
                return p;
            }

            // Blend effected and original bins with a ramp that is 0 at each band edge and
            // reaches 1 softOct octaves inside. The power bookkeeping the effect did is scaled
            // by how much of its real change survived the blend.
            // v1.8: per-set wet/dry (0..1), interpolated like any parameter
            internal static float SetMix(ParamsVec pv, int set) => P.T[P.Mix].Frac(pv.T(set, P.Mix));

            void BlendSoft(float softOct, float mix, float before, float pOrig)
            {
                float eA = Math.Max(1f, fcurr_start_bin), eB = Math.Max(1f, fcurr_stop_bin);
                float pFx = 0, pBlend = 0, wSum = 0; long nBins = 0;
                int nr = Ranges(out long b0, out long e0, out long b1, out long e1);
                for (int r = 0; r < nr; r++)
                {
                    long b = r == 0 ? b0 : b1, end = r == 0 ? e0 : e1;
                    for (long k = b; k < end; k++)
                    {
                        long im = N - k;
                        float fr = x1[k], fi = x1[im];
                        pFx += fr * fr + fi * fi;
                        float lk = Log2(k);
                        float d = Math.Min(Math.Abs(lk - Log2(eA)), Math.Abs(lk - Log2(eB)));
                        float w = (d >= softOct ? 1f : d / softOct) * mix;
                        float vr = w * fr + (1 - w) * _origR[k], vi = w * fi + (1 - w) * _origI[k];
                        x1[k] = vr; x1[im] = vi;
                        pBlend += vr * vr + vi * vi;
                        wSum += w; nBins++;
                    }
                }
                float dTracked = total_out_pwr - before;
                float dReal = pFx - pOrig, dBlend = pBlend - pOrig;
                float ratio = Math.Abs(dReal) > 1e-30f ? dBlend / dReal : (nBins > 0 ? wSum / nBins : 1f);
                if (ratio < 0) ratio = 0; else if (ratio > 2) ratio = 2;
                total_out_pwr = before + dTracked * ratio;
            }

            // =================================================================
            // Appended effects. Unlike the original nine, their own gain changes are
            // real (not level-matched away by the final power normalisation); only
            // their Amp is tracked, the same way the original tracks Amp.
            // =================================================================
            void AmpTracked(long b, long end)
            {
                float amp = curr_amp;
                if (amp == 1.0f) return;
                float p = 0;
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    p += x1[k] * x1[k] + x1[im] * x1[im];
                    float vr = x1[k] * amp, vi = x1[im] * amp;
                    x1[k] = vr; x1[im] = vi;
                }
                total_out_pwr += (amp * amp - 1) * p;
            }

            static float Log2(float x) => MathF.Log(x) * 1.44269504f;

            float[] State(int set)
            {
                int len = N / 2 + 1;
                if (_fxStateN[set] != N || _fxState[set] == null || _fxState[set].Length < len)
                {
                    if (_fxState[set] == null || _fxState[set].Length < len) _fxState[set] = new float[P.MaxBlkSz / 2 + 1];
                    _fxStateN[set] = N;
                    _fxInit[set] = false;
                }
                return _fxState[set];
            }

            float Mag(long k)
            {
                int half = N / 2;
                if (k == 0) return Math.Abs(x1[0]);
                if (k >= half) return Math.Abs(x1[half]);
                return MathF.Sqrt(x1[k] * x1[k] + x1[N - k] * x1[N - k]);
            }

            // --- Freeze: capture the whole spectrum's magnitudes when Value rises from 0,
            //     then crossfade towards them (random phase) by Value ----------------
            void Freeze(int set, long b, long end)
            {
                float v = curr_fxval;
                var st = State(set);
                if (v < 0.01f) { _fxInit[set] = false; PlainAmp(b, end); return; }
                if (!_fxInit[set])
                {
                    for (int k = 0; k <= N / 2; k++) st[k] = Mag(k);
                    _fxInit[set] = true;
                }
                int rand = _rand[set];
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    int a = rand & 4095;
                    float fr = st[k] * SinCosRe[a], fi = st[k] * SinCosIm[a];
                    x1[k] = (1 - v) * x1[k] + v * fr;
                    x1[im] = (1 - v) * x1[im] + v * fi;
                    rand = Prbs29(rand);
                }
                _rand[set] = rand;
                AmpTracked(b, end);
            }

            // --- Gate: downward expander per bin; threshold -100..0 dBFS -------------
            void Gate(long b, long end)
            {
                float thDb = -100f + 100f * curr_fxval;
                float th = SpectrumTap.FullScalePower * MathF.Pow(10f, thDb / 10f);
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    float p = x1[k] * x1[k] + x1[im] * x1[im];
                    if (p < th)
                    {
                        float g = p / th;          // amplitude gain falls 2x faster than the level
                        x1[k] *= g; x1[im] *= g;
                    }
                }
                AmpTracked(b, end);
            }

            // --- Blur: per-bin magnitude smoothing over time (5 ms .. 2 s) -----------
            void Blur(int set, long b, long end)
            {
                float v = curr_fxval;
                var st = State(set);
                if (v < 0.01f) { _fxInit[set] = false; PlainAmp(b, end); return; }
                if (!_fxInit[set])
                {
                    for (int k = 0; k <= N / 2; k++) st[k] = Mag(k);
                    _fxInit[set] = true;
                }
                float tau = 0.005f * MathF.Pow(400f, v);
                float a = MathF.Exp(-e.cur_hop / (tau * e.SampleRate));
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    float m = Mag(k);
                    st[k] = a * st[k] + (1 - a) * m;
                    if (m > 1e-30f)
                    {
                        float g = st[k] / m;
                        x1[k] *= g; x1[im] *= g;
                    }
                }
                AmpTracked(b, end);
            }

            // --- Tilt: -12..+12 dB/octave around the band's geometric centre --------
            void Tilt(long b, long end)
            {
                float slope = (curr_fxval * 2f - 1f) * 12f;
                if (slope == 0f) { PlainAmp(b, end); return; }
                float lc = 0.5f * (Log2(Math.Max(1f, fcurr_start_bin)) + Log2(Math.Max(1f, fcurr_stop_bin)));
                for (long k = b; k < end; k++)
                {
                    float db = slope * (Log2(k) - lc);
                    if (db > 36f) db = 36f; else if (db < -36f) db = -36f;
                    float g = MathF.Pow(10f, db / 20f);
                    x1[k] *= g; x1[N - k] *= g;
                }
                AmpTracked(b, end);
            }

            // --- RoboWhisper: Value < 0.5 towards zero phase (robot),
            //     > 0.5 towards random phase (whisper) -------------------------------
            void RoboWhisper(int set, long b, long end)
            {
                float v = curr_fxval;
                float amt = v < 0.5f ? (0.5f - v) * 2f : (v - 0.5f) * 2f;
                if (amt <= 0f) { PlainAmp(b, end); return; }
                bool whisper = v > 0.5f;
                int rand = _rand[set];
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    float m = Mag(k), tr, ti;
                    if (whisper) { int a = rand & 4095; tr = m * SinCosRe[a]; ti = m * SinCosIm[a]; rand = Prbs29(rand); }
                    else { tr = m; ti = 0; }
                    x1[k] = (1 - amt) * x1[k] + amt * tr;
                    x1[im] = (1 - amt) * x1[im] + amt * ti;
                }
                _rand[set] = rand;
                AmpTracked(b, end);
            }

            // --- Cross: magnitude = |main|^(1-v) * |sidechain|^v, phase from main ----
            void Cross(long b, long end)
            {
                if (!e._scActive) { PlainAmp(b, end); return; }
                EnsureSidechain();
                float v = curr_fxval;
                for (long k = b; k < end; k++)
                {
                    long im = N - k;
                    float m = Mag(k);
                    float sr = xs1[k], si = xs1[N - k];
                    float sm = MathF.Sqrt(sr * sr + si * si);
                    float om = v <= 0f ? m : (v >= 1f ? sm : MathF.Pow(m, 1f - v) * MathF.Pow(sm, v));
                    if (m > 1e-30f) { float g = om / m; x1[k] *= g; x1[im] *= g; }
                    else if (sm > 1e-30f) { float g = om / sm; x1[k] = sr * g; x1[im] = si * g; }
                }
                AmpTracked(b, end);
            }

            // --- PitchShift (appended v1.2): phase-vocoder shift by a ratio ----------
            // Peak-locked phase vocoder (Laroche & Dolson 1999, "New phase-vocoder
            // techniques for pitch-shifting"). Per block:
            //  1. find the spectral peaks in the band; each peak owns the bins out to the
            //     lowest point between it and its neighbours (its window lobe);
            //  2. the peak's true frequency comes from its phase advance since the previous
            //     block (hop = distance between block starts);
            //  3. the whole region moves rigidly by the whole number of bins that takes the
            //     peak to frequency x ratio, so the lobe keeps its shape and level;
            //  4. the peak's output phase continues from the previous block's output at that
            //     bin, advanced at the shifted frequency; the other bins of the region keep
            //     their phase offset from the peak (phase locking).
            // The band's own content is removed and the shifted copy is added wherever it
            // lands, so content shifted out of the band sums with what is already there.
            // Soft: a bin near a band edge only moves part of its content (crossfade).
            // Best in Smooth mode (windowed, hop <= N/2); Overlap 75% reduces phasiness.
            void PitchShift(int set)
            {
                int half = N / 2;
                if (half < 4) return;
                const float TwoPi = 6.28318531f;
                float semis = P.PitchSemis(curr_fxval) + curr_pitch_offset;   // v2.2: Follow Pitch
                if (semis > P.PitchRange) semis = P.PitchRange; else if (semis < -P.PitchRange) semis = -P.PitchRange;
                float ratio = MathF.Pow(2f, semis / 12f);
                bool unity = Math.Abs(semis) < 0.005f;
                float softOct = e.Pv.T(set, P.Soft) / 12f;
                float mix = SetMix(e.Pv, set);      // v1.8: the dry part stays where it is
                float amp = curr_amp;
                // v2.1 formant control: 0 = off (formants move with the pitch). Read per tick,
                // not interpolated, so switching it on never sweeps through other shifts.
                int fv = P.T[P.Formant].Lim(e.Pv.FilledCurr.T[set, P.Formant]);
                float fr = fv == P.FormantOff ? 1f : MathF.Pow(2f, P.FormantSemis(fv) / 12f);
                bool formant = fv != P.FormantOff && (!unity || Math.Abs(fr - 1f) > 1e-4f);
                if (formant) SpectralEnvelope();

                int sz = P.MaxBlkSz / 2 + 1;
                if (_pvPrev[set] == null) { _pvPrev[set] = new float[sz]; _pvSum[set] = new float[sz]; }
                if (_synRe.Length < sz)
                {
                    _synRe = new float[sz]; _synIm = new float[sz]; _synPh = new float[sz]; _synPk = new float[sz];
                    _pm = new float[sz]; _pph = new float[sz]; _pw = new float[sz]; _peaks = new int[sz];
                    _rgRs = new int[sz]; _rgRe = new int[sz]; _rgS = new int[sz]; _rgTf = new float[sz];
                }
                long hop = e.x0i_abs - _pvAbs[set];
                bool fresh = !_fxInit[set] || _pvN[set] != N || hop <= 0 || hop >= N;
                _pvAbs[set] = e.x0i_abs; _pvN[set] = N; _fxInit[set] = true;

                var prev = _pvPrev[set];     // analysis phase per bin, previous block
                var sum = _pvSum[set];       // output peak phase per bin, previous block
                float expect = TwoPi * hop / N;              // expected phase advance per bin index
                Array.Clear(_synRe, 0, half + 1);
                Array.Clear(_synIm, 0, half + 1);
                Array.Clear(_synPk, 0, half + 1);

                float lA = Log2(Math.Max(1f, fcurr_start_bin)), lB = Log2(Math.Max(1f, fcurr_stop_bin));
                float p = 0;
                int nr = Ranges(out long b0, out long e0, out long b1, out long e1);
                for (int r = 0; r < nr; r++)
                {
                    int b = (int)Math.Max(1, r == 0 ? b0 : b1), end = (int)Math.Min(half, r == 0 ? e0 : e1);
                    if (end <= b) continue;

                    // analysis: magnitude, phase, edge weight
                    for (int k = b; k < end; k++)
                    {
                        float xr = x1[k], xi = x1[N - k];
                        _pm[k] = MathF.Sqrt(xr * xr + xi * xi);
                        _pph[k] = MathF.Atan2(xi, xr);
                        float w = 1f;
                        if (softOct > 0f)
                        {
                            float lk = Log2(k);
                            float dd = Math.Min(Math.Abs(lk - lA), Math.Abs(lk - lB));
                            if (dd < softOct) w = dd / softOct;
                        }
                        _pw[k] = w * mix;
                    }

                    if (unity)
                    {
                        for (int k = b; k < end; k++)
                        {
                            float w = _pw[k], g = 1f - w + w * amp * (formant ? EnvGain(k / fr, k) : 1f);
                            x1[k] *= g; x1[N - k] *= g;
                            p += w * w * _pm[k] * _pm[k];
                            sum[k] = _pph[k]; prev[k] = _pph[k];   // a later shift continues from here
                        }
                        continue;
                    }

                    // peaks (local maxima)
                    int np = 0;
                    for (int k = b; k < end; k++)
                    {
                        float m = _pm[k];
                        if (m <= 0f) continue;
                        if ((k == b || m > _pm[k - 1]) && (k == end - 1 || m >= _pm[k + 1])) _peaks[np++] = k;
                    }

                    // pass 1: each peak's region, true frequency and shift
                    int rs0 = b;
                    for (int i = 0; i < np; i++)
                    {
                        int pk = _peaks[i];
                        int re = end;                         // region end: lowest bin before the next peak
                        if (i + 1 < np)
                        {
                            int nx = _peaks[i + 1]; re = pk + 1;
                            for (int k = pk + 1; k < nx; k++) if (_pm[k] < _pm[re]) re = k;
                            if (re == pk + 1 && _pm[pk + 1] >= _pm[nx]) re = nx;
                        }
                        float tf = pk;                        // peak's true frequency, in bins
                        if (!fresh)
                        {
                            float d = _pph[pk] - prev[pk] - expect * pk;
                            d -= TwoPi * MathF.Floor(d / TwoPi + 0.5f);
                            tf = pk + d / expect;
                        }
                        _rgRs[i] = rs0; _rgRe[i] = re; _rgTf[i] = tf;
                        _rgS[i] = (int)MathF.Floor(tf * (ratio - 1f) + 0.5f);
                        rs0 = re;
                    }

                    // pass 2: move each region. v2.1: when shifting down, regions land closer
                    // together than their width and used to overlap; summed with different
                    // phases they cancelled, so harmonic-rich sounds lost harmonics. Each
                    // region now only writes up to halfway to its neighbours' destinations.
                    bool down = ratio < 1f;
                    for (int i = 0; i < np; i++)
                    {
                        int pk = _peaks[i], rs = _rgRs[i], re = _rgRe[i], s = _rgS[i];
                        float tf = _rgTf[i];
                        int jp = pk + s;
                        int lo = 1, hi = half;
                        if (down)
                        {
                            if (i > 0) lo = Math.Max(lo, (_peaks[i - 1] + _rgS[i - 1] + jp + 1) >> 1);
                            if (i + 1 < np) hi = Math.Min(hi, (jp + _peaks[i + 1] + _rgS[i + 1] + 1) >> 1);
                        }
                        if (jp >= 1 && jp < half)
                        {
                            float th = fresh ? _pph[pk] : sum[jp] + expect * tf * ratio;
                            th -= TwoPi * MathF.Floor(th / TwoPi);
                            float rot = th - _pph[pk];
                            float fg = formant ? EnvGain(jp / fr, pk) : 1f;   // v2.1: put the envelope back
                            float cr = MathF.Cos(rot) * fg, ci = MathF.Sin(rot) * fg;
                            for (int k = rs; k < re; k++)
                            {
                                int dk = k + s;
                                if (dk < lo || dk >= hi) continue;
                                float w = _pw[k];
                                float xr = x1[k] * w, xi = x1[N - k] * w;
                                _synRe[dk] += xr * cr - xi * ci;
                                _synIm[dk] += xr * ci + xi * cr;
                                float wm = w * _pm[pk];
                                if (wm > _synPk[dk]) { _synPk[dk] = wm; _synPh[dk] = th; }
                            }
                        }
                    }

                    // remove the moved part from the source bins
                    for (int k = b; k < end; k++)
                    {
                        float g = 1f - _pw[k];
                        x1[k] *= g; x1[N - k] *= g;
                        prev[k] = _pph[k];
                    }
                }

                if (!unity)
                {
                    for (int j = 1; j < half; j++)
                    {
                        if (_synPk[j] > 0f) sum[j] = _synPh[j];
                        float sr = _synRe[j], si = _synIm[j];
                        if (sr == 0f && si == 0f) continue;
                        x1[j] += sr * amp; x1[N - j] += si * amp;
                        p += sr * sr + si * si;
                    }
                }
                total_out_pwr += (amp * amp - 1) * p;         // only Amp is tracked (appended effects)
            }

            // --- v2.1: spectral envelope for PitchShift's formant control ------------
            // A pitch shift moves every partial, and with it the spectrum's broad shape (the
            // formants: what makes a voice sound like a vowel, or like a person rather than
            // a chipmunk). To keep the formants, each moved region is scaled by
            // Env(destination / fr) / Env(source), where Env is the spectral envelope of the
            // block before the shift and fr the formant ratio (1 = keep).
            //
            // Env is a "true envelope" (Roebel & Rodet 2005): the log magnitude is smoothed by
            // cepstral liftering, then raised to the larger of itself and the smoothed curve,
            // a few times, so the envelope rides over the harmonics instead of averaging them
            // with the gaps between. The lifter keeps quefrencies below FormantQuefrency
            // seconds, so the envelope ignores harmonic spacing for pitches below about 1/that.
            //
            // Cost: the envelope is only ~400 Hz-smooth, so it is computed on a spectrum
            // decimated by D (each point the loudest of D bins, which keeps the peaks the true
            // envelope has to ride over), with FFTs of N/D instead of N (D = 8 at 4096 blocks).
            const float FormantQuefrency = 0.0025f;   // 2.5 ms: harmonics up to ~400 Hz apart are smoothed out
            const int FormantIterations = 8;
            const float FormantMaxBoost = 3.45f, FormantMaxCut = -4.6f;   // +30 dB / -40 dB, natural log
            int _envD = 1, _envH = 0;                 // decimation, last envelope point (M/2)

            void SpectralEnvelope()
            {
                int half = N / 2;
                int nc = (int)(FormantQuefrency * e.SampleRate);
                nc = Math.Max(4, Math.Min(nc, N / 4));
                // Decimating keeps the quefrency scale (index n is still n samples: points are D
                // bins apart and there are N/D of them), so the cutoff stays nc; D is as large
                // as leaves the cepstrum room for it (M >= 4 nc).
                int D = 1;
                while (N / (D * 2) >= 4 * nc && N / (D * 2) >= P.BlkSz0) D *= 2;
                int M = N / D, mh = M / 2, ncD = nc;
                var fft = e.FftOfSize(M);
                if (_envA.Length < M + 1)
                {
                    int sz = Math.Max(M + 1, P.MaxBlkSz + 1);
                    _envA = new float[sz]; _envB = new float[sz]; _envL = new float[sz / 2 + 2]; _envT = new float[sz / 2 + 2];
                }
                // decimated log magnitude: each point the loudest bin within +-D/2
                float mx = 0f;
                for (int j = 0; j <= mh; j++)
                {
                    int k0 = Math.Max(0, j * D - D / 2), k1 = Math.Min(half, j * D + D / 2);
                    float best = 0f;
                    for (int k = k0; k <= k1; k++)
                    {
                        float xr = x1[k], xi = (k == 0 || k == half) ? 0f : x1[N - k];
                        float m2 = xr * xr + xi * xi;
                        if (m2 > best) best = m2;
                    }
                    best = MathF.Sqrt(best);
                    _envT[j] = best; if (best > mx) mx = best;
                }
                float floor = mx * 1e-5f + 1e-30f;            // -100 dB below the loudest bin
                for (int j = 0; j <= mh; j++) _envT[j] = MathF.Log(Math.Max(_envT[j], floor));
                Array.Copy(_envT, _envL, mh + 1);
                for (int it = 0; it < FormantIterations; it++)
                {
                    Lifter(fft, M, ncD);                      // _envA = smoothed _envL
                    for (int j = 0; j <= mh; j++) _envL[j] = Math.Max(_envT[j], _envA[j]);
                }
                Lifter(fft, M, ncD);
                Array.Copy(_envA, _envL, mh + 1);
                _envD = D; _envH = mh;
            }

            // _envA[0..M/2] = cepstrally smoothed _envL[0..M/2] (even sequence, length M)
            void Lifter(RealFft fft, int M, int nc)
            {
                int mh = M / 2;
                for (int k = 0; k <= mh; k++) _envA[k] = _envL[k];
                for (int k = 1; k < mh; k++) _envA[M - k] = _envL[k];
                fft.Forward(_envA, 0, _envB);                 // real, even: the cepstrum x M
                int t0 = nc * 3 / 4;
                for (int n = 0; n <= mh; n++)
                {
                    float l = n <= t0 ? 1f : n >= nc ? 0f : 0.5f + 0.5f * MathF.Cos(3.14159265f * (n - t0) / (nc - t0));
                    _envA[n] = _envB[n] * l;
                }
                for (int n = 1; n < mh; n++) _envA[M - n] = _envA[n];
                fft.Forward(_envA, 0, _envB);                 // back to log magnitude x M
                float inv = 1f / M;
                for (int k = 0; k <= mh; k++) _envA[k] = _envB[k] * inv;
            }

            // log envelope at a (fractional) bin of the full spectrum
            float LogEnvAt(float bin)
            {
                float x = bin / _envD;
                if (x < 0f) x = 0f; else if (x > _envH - 1) x = _envH - 1;
                int i = (int)x; float f = x - i;
                return _envL[i] + (_envL[i + 1] - _envL[i]) * f;
            }

            // gain that gives the content of bin src the envelope level at bin dst (fractional)
            float EnvGain(float dst, int src)
            {
                int half = N / 2;
                if (dst < 1f) dst = 1f; else if (dst > half - 1) dst = half - 1;
                float d = LogEnvAt(dst) - LogEnvAt(src);
                if (d > FormantMaxBoost) d = FormantMaxBoost; else if (d < FormantMaxCut) d = FormantMaxCut;
                return MathF.Exp(d);
            }

            // --- ShiftAdd / ShiftReplace -------------------------------------------
            float ShiftApply(bool add)
            {
                float sps = e.SampleRate;
                long shift = (long)(P.FreqShift(curr_fxval, sps * 0.5f) / sps * N + 0.5f);
                if (curr_start_bin <= curr_stop_bin)
                    return ShiftRange(add, curr_start_bin, curr_start_bin + shift, curr_stop_bin - curr_start_bin + 1);
                float pwr = ShiftRange(add, 1, 1 + shift, curr_stop_bin);
                pwr += ShiftRange(add, curr_start_bin, curr_start_bin + shift, N / 2 - curr_start_bin);
                return pwr;
            }

            float ShiftRange(bool add, long start_bin, long dest_bin, long n_bins)
            {
                if (dest_bin < 1)
                {
                    start_bin += (1 - dest_bin);
                    n_bins -= (1 - dest_bin);
                    dest_bin = 1;
                }
                if (dest_bin + n_bins >= N / 2 - 1) n_bins = N / 2 - 1 - dest_bin;
                if (n_bins < 1) return 0.0f;

                float amp = curr_amp, pwr = 0;
                if (start_bin > dest_bin)
                {
                    for (long c = 0; c < n_bins; c++) pwr += ShiftOne(add, start_bin + c, dest_bin + c, amp);
                }
                else
                {
                    for (long c = n_bins - 1; c >= 0; c--) pwr += ShiftOne(add, start_bin + c, dest_bin + c, amp);
                }
                return pwr;
            }

            float ShiftOne(bool add, long s, long d, float amp)
            {
                long si = N - s, di = N - d;
                float before = x1[d] * x1[d] + x1[di] * x1[di];
                float sr = x1[s] * amp, sim = x1[si] * amp;
                float vr, vi;
                if (add) { vr = sr + x1[d]; vi = sim + x1[di]; }
                else { vr = sr; vi = sim; }
                x1[d] = vr; x1[di] = vi;
                return (x1[d] * x1[d] + x1[di] * x1[di]) - before;
            }

            // --- Harmonic family ---------------------------------------------------
            // SplitHarmonicParam: Value 0..1 -> {both, odd, even} x width (alternating direction)
            static void SplitHarm(float param, out int type, out float width)
            {
                param *= 3.0f;
                type = (int)param; if (type < 0) type = 0; if (type > 2) type = 2;
                width = param - type;
                if ((type & 1) == 0) width = 1 - width;
            }

            void HarmonicCore(float centre, float spacing, float frac, long stop_bin)
            {
                float n = Clamp(spacing * frac / 2, 0, spacing / 2);
                if (n < 1) return;

                float amp = curr_amp;
                float pwr = 0;
                float bin = centre - n;
                long stop = stop_bin;
                n *= 2;
                bool looping = true;
                while (looping)
                {
                    long bb = Clamp((long)(bin + 0.5f), 0, N / 2);
                    long xe = (long)(bin + 0.5f + n);
                    if (xe >= stop) { xe = stop - 1; looping = false; }
                    for (long k = bb; k <= xe; k++)
                    {
                        long im = N - k;
                        pwr += x1[k] * x1[k] + x1[im] * x1[im];
                        float vr = x1[k] * amp, vi = x1[im] * amp;
                        x1[k] = vr; x1[im] = vi;
                    }
                    bin += spacing;
                }
                total_out_pwr += (amp * amp - 1) * pwr;
            }

            void Harmonic()
            {
                SplitHarm(curr_fxval, out int type, out float w);
                float f = fcurr_start_bin;
                switch (type)
                {
                    case 0: HarmonicCore(f, f, w, curr_stop_bin); break;          // both
                    case 1: HarmonicCore(f, f * 2, w, curr_stop_bin); break;      // odd
                    case 2: HarmonicCore(f * 2, f * 2, w, curr_stop_bin); break;  // even
                }
            }

            void AutoHarmonic()
            {
                long start_bin = curr_start_bin, stop_bin = curr_stop_bin;
                long process_stop_bin = stop_bin;
                if (start_bin > stop_bin)
                {
                    (start_bin, stop_bin) = (stop_bin, start_bin);
                    process_stop_bin = N / 2;
                }

                // loudest bin in the range
                long pos = -1;
                float max_pwr = 0.0f;
                for (long k = start_bin; k < stop_bin; k++)
                {
                    float t = x1[k] * x1[k] + x1[N - k] * x1[N - k];
                    if (t > max_pwr) { max_pwr = t; pos = k; }
                }
                if (max_pwr == 0.0f) return;

                SplitHarm(curr_fxval, out int type, out float w);
                float h = pos;
                switch (type)
                {
                    case 0: HarmonicCore(h, h, w, process_stop_bin); break;
                    case 1: HarmonicCore(h, h * 2, w, process_stop_bin); break;
                    case 2: HarmonicCore(h * 2, h * 2, w, process_stop_bin); break;
                }
            }

            void CopyHarmonic()
            {
                SplitHarm(curr_fxval, out int type, out float w);
                float s = curr_start_bin;
                switch (type)
                {
                    case 0: CopyHarmCore(s, s, w, curr_stop_bin); break;
                    case 1: CopyHarmCore(s, s * 2, w, curr_stop_bin); break;
                    case 2: CopyHarmCore(s * 2, s * 2, w, curr_stop_bin); break;
                }
            }

            // halfcomplex accessors with an explicit array length (x2 scratch uses length 2n)
            void CopyHarmCore(float centre, float spacing, float frac, long stop_bin)
            {
                int half = N / 2;
                float t = spacing * frac / 2;
                float fbeg = Clamp(centre - t, 1.0f, half);
                long beg = (long)(fbeg + 0.5f);
                long end = (long)Clamp(centre + t + 0.5f, 1.0f, half);

                long na = (long)(centre - beg + 1);
                long nb = (long)(end - centre + 1);
                if (na < 0) na = 0;
                if (nb < 0) nb = 0;
                long n = na + nb;
                if (n < 1 || spacing < 10) return;

                float scale_step = 1.0f;
                // stop below centre: arbitrary decay on the copies, run to the top of the spectrum
                if (stop_bin < centre && centre > 0)
                {
                    t = Lin(stop_bin / centre, centre, half) / spacing;
                    if (t == 0.0f) return;
                    scale_step = MathF.Pow(0.25f, 1 / t);
                    stop_bin = half;
                }

                float amp = curr_amp;
                long x2_len = n + n;

                // ramp the source harmonic into x2 (a copy, so destination may overlap source)
                float step_a = 0, step_b = 0;
                if (na > 0)
                {
                    step_a = amp / na;
                    RampCopy(x2, x2_len, 0, x1, N, beg, 0, step_a, na);
                }
                if (nb > 0)
                {
                    step_b = -amp / nb;
                    RampCopy(x2, x2_len, na, x1, N, beg + na, amp, step_b, nb);
                }

                float src_scale = 1.0f;
                while (true)
                {
                    beg = (long)(fbeg + 0.5);
                    if (beg < 1) beg = 1;
                    if (beg > N / 2) break;

                    if (beg + na > stop_bin) { na = stop_bin - beg; nb = 0; }
                    // original: nb = stop_bin - (beg+na+nb), which always went negative and
                    // dropped the falling half of the last copy; truncate it instead.
                    if (beg + na + nb > stop_bin) nb = stop_bin - (beg + na);

                    if (na > 0) RampMix1(x1, N, beg, x2, x2_len, 0, src_scale, 0, step_a, na);
                    if (nb > 0) RampMix1(x1, N, beg + na, x2, x2_len, na, src_scale, amp, step_b, nb);

                    fbeg += spacing;
                    src_scale *= scale_step;
                    if (src_scale < 1e-6) break;
                }
                // (the original never accumulated power for CopyHarm, so the final
                //  level-match doesn't compensate for the added energy - kept as is)
            }

            // dst = src * scale, scale ramping (process_loops.h rampCopy)
            static void RampCopy(float[] dst, long dLen, long dBin, float[] src, long sLen, long sBin,
                                 float scale, float step, long n)
            {
                for (long i = 0; i < n; i++)
                {
                    float sr = src[sBin + i], si = src[sLen - (sBin + i)];
                    float vr = sr * scale, vi = si * scale;
                    dst[dBin + i] = vr; dst[dLen - (dBin + i)] = vi;
                    scale += step;
                }
            }

            // dst = src * src_scale + dst * ramp, ramp stepping (process_loops.h rampMix1)
            static void RampMix1(float[] dst, long dLen, long dBin, float[] src, long sLen, long sBin,
                                 float src_scale, float ramp, float step, long n)
            {
                for (long i = 0; i < n; i++)
                {
                    long dr = dBin + i, di = dLen - dr, sr = sBin + i, si = sLen - sr;
                    float vr = src[sr] * src_scale + dst[dr] * ramp;
                    float vi = src[si] * src_scale + dst[di] * ramp;
                    dst[dr] = vr; dst[di] = vi;
                    ramp += step;
                }
            }
        }
    }
}
