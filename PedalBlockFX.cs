// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Based on DT Block Effects, Copyright (C) 2003-2004 Darrell Tam.
//
// Pedal BlockFX - ReBuzz managed port of DT Block Effects (DT BlkFx) by Darrell Tam.
// See README.md for what is identical, what changed, and why.
//
// Parameter layout matches the original (track build): seven globals in the
// original order, then the five FX-set track parameters. One global ("Stereo")
// is appended after the originals.

using System;
using System.Collections.Generic;
using System.Windows;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using BuzzGUI.Common;

namespace PedalBlockFX
{
    [MachineDecl(Name = "Pedal BlockFX", ShortName = "BlockFX", Author = "Pedal (after Darrell Tam)", MaxTracks = P.MaxSets, InputCount = 2, OutputCount = 1)]
    public class PedalBlockFXMachine : IBuzzMachine, IDisposable
    {
        internal const string Version = "2.2.0";

        readonly IBuzzMachineHost host;
        BlockFxEngine _engine;

        // Parameter changes since the last committed tick (NoValue = unchanged),
        // and the latest known value of everything (for display and engine rebuilds).
        readonly object _lock = new object();
        readonly Params _pending = new Params();
        readonly Params _disp = new Params();
        bool _dirty;
        int _maxTrackSeen = 1;

        float[][] _io = { new float[256], new float[256] };
        float[][] _sc = { new float[256], new float[256] };

        // Edit window
        readonly SpectrumTap _tap = new SpectrumTap();
        EditWindow _editWindow;
        int _stereo = 1;
        readonly float[] _dcX = new float[2], _dcY = new float[2];
        int _lastNch = 2;

        public PedalBlockFXMachine(IBuzzMachineHost host)
        {
            this.host = host;
            _pending.SetNoVal();
            _disp.SetDefaults();
            // Two inputs (Main, Sidechain), one output. The counts can only be set once
            // host.Machine exists, i.e. from Song.MachineAdded (Build notes §8.1).
            try
            {
                var song = Global.Buzz?.Song;
                if (song != null) { song.MachineAdded += OnMachineAdded; song.MachineRemoved += OnMachineRemoved; }
            }
            catch { }
        }

        void OnMachineAdded(IMachine m)
        {
            if (m != host.Machine) return;
            try { host.InputChannelCount = 2; host.OutputChannelCount = 1; } catch { }
        }

        void OnMachineRemoved(IMachine m)
        {
            if (m != host.Machine) return;
            Unsubscribe();
        }

        void Unsubscribe()
        {
            try
            {
                var song = Global.Buzz?.Song;
                if (song != null) { song.MachineAdded -= OnMachineAdded; song.MachineRemoved -= OnMachineRemoved; }
            }
            catch { }
        }

        public string GetChannelName(bool input, int index)
        {
            if (input) return index == 0 ? "Main" : index == 1 ? "Sidechain" : "";
            return index == 0 ? "Out" : "";
        }

        // ------------------------------------------------------------------
        // Global parameters (original order)
        // ------------------------------------------------------------------
        [ParameterDecl(Name = "BlkSync", IsStateless = true,
            Description = "Synchronize block with this tick")]
        public bool BlkSync { get => false; set => SetG(P.BlkSync, value ? 1 : 0); }

        [ParameterDecl(Name = "InterpMode", IsStateless = true, MinValue = 0, MaxValue = 2,
            Description = "Parameter Interpolation Mode (0=normal, 1=off, 2=continue previous)")]
        public int InterpMode { get => _disp.G[P.InterpMode]; set => SetG(P.InterpMode, value); }

        [ParameterDecl(Name = "MixBack", MinValue = 0, MaxValue = 0xA0, DefValue = 0,
            Description = "Percentage mix back of original audio (0=0%, 0xA0=100%)")]
        public int MixBack { get => _disp.G[P.MixBack]; set => SetG(P.MixBack, value); }

        [ParameterDecl(Name = "OutAmp", MinValue = 0, MaxValue = 0xE0, DefValue = 0xA0,
            Description = "Output amplification (0=-inf, 0xA0=0dB, 0xE0=+32dB, 0.5dB steps)")]
        public int OutAmp { get => _disp.G[P.OutAmp]; set => SetG(P.OutAmp, value); }

        [ParameterDecl(Name = "TickDelay", MinValue = 0, MaxValue = 0x4000, DefValue = 0x400,
            Description = "Audio is delayed for this many ticks, units=1/0x100 ticks")]
        public int TickDelay { get => _disp.G[P.TickDelay]; set => SetG(P.TickDelay, value); }

        [ParameterDecl(Name = "BlockLen", MinValue = 0, MaxValue = 11, DefValue = 7,
            Description = "Audio block length (0=32 .. 7=4096 .. 11=65536 samples)")]
        public int BlockLen { get => _disp.G[P.BlkLen]; set => SetG(P.BlkLen, value); }

        [ParameterDecl(Name = "Overlap", MinValue = 5, MaxValue = 80, DefValue = 50,
            Description = "Percentage overlap of blocks")]
        public int Overlap { get => _disp.G[P.Overlap]; set => SetG(P.Overlap, value); }

        // ---- appended (not in the original) ----
        [ParameterDecl(Name = "Stereo", MinValue = 0, MaxValue = 2, DefValue = 1,
            Description = "Mono = original (L+R summed), Stereo = L/R processed alike, Wide = decorrelated Smear")]
        public int Stereo { get => _stereo; set => _stereo = value < 0 ? 0 : (value > 2 ? 2 : value); }

        [ParameterDecl(Name = "KeepDC", DefValue = true,
            Description = "Keep the lowest FFT bin (the original zeroed it, cutting bass at small block sizes). A 5 Hz DC blocker removes true offset")]
        public bool KeepDC { get => _keepDC; set => _keepDC = value; }
        volatile bool _keepDC = true;

        [ParameterDecl(Name = "Window", MinValue = 0, MaxValue = 1, DefValue = 1,
            Description = "Blocky = original (unwindowed blocks, linear crossfade). Smooth = windowed overlap-add: cleaner effects, hop at most half a block")]
        public int Window { get => _window; set => _window = value < 0 ? 0 : (value > 1 ? 1 : value); }
        volatile int _window = 1;

        // ---- appended v1.1 ----
        [ParameterDecl(Name = "LevelMatch", DefValue = true,
            Description = "On = original: output level is matched to the input, so Amp only rebalances bands. Off = Amp and level changes are absolute")]
        public bool LevelMatch { get => _levelMatch; set => _levelMatch = value; }
        volatile bool _levelMatch = true;

        // ------------------------------------------------------------------
        // Track (FX set) parameters
        // ------------------------------------------------------------------
        [ParameterDecl(Name = "FreqA", MinValue = 0, MaxValue = 132 * 256, DefValue = 0,
            Description = "Frequency A (c1=0c00 c2=1800 c3=2400 c4=3000 c5=3c00, units=1/0x100 note). A>B processes outside the range")]
        public void SetFreqA(int v, int track) => SetT(P.FreqA, v, track);

        [ParameterDecl(Name = "FreqB", MinValue = 0, MaxValue = 132 * 256, DefValue = 0,
            Description = "Frequency B (c1=0c00 c2=1800 c3=2400 c4=3000 c5=3c00, units=1/0x100 note)")]
        public void SetFreqB(int v, int track) => SetT(P.FreqB, v, track);

        [ParameterDecl(Name = "Amp", MinValue = 0, MaxValue = 0xE0, DefValue = 0xA0,
            Description = "Amplitude (0=-inf, 0xA0=0dB, 0xE0=+32dB)")]
        public void SetAmp(int v, int track) => SetT(P.Amp, v, track);

        [ParameterDecl(Name = "Effect", MinValue = 0, MaxValue = P.LastEffect, DefValue = 0,
            Description = "Effect (0=contrast, 1=smear, 2=clip, 3=weed, 4=shift add, 5=shift replace, 6=harmonic, 7=auto harm, 8=copy harm, 9=freeze, 10=gate, 11=blur, 12=tilt, 13=robo/whisper, 14=cross with sidechain, 15=pitch shift)")]
        public void SetEffect(int v, int track) => SetT(P.Effect, v, track);

        [ParameterDecl(Name = "Value", MinValue = 0, MaxValue = 0xA000, DefValue = 0,
            Description = "Effect value")]
        public void SetValue(int v, int track) => SetT(P.FxVal, v, track);

        // ---- appended track parameter ----
        [ParameterDecl(Name = "Soft", MinValue = 0, MaxValue = 48, DefValue = 0,
            Description = "Soft band edges: the effect fades in over this many semitones inside each edge (per-bin effects and PitchShift)")]
        public void SetSoft(int v, int track) => SetT(P.Soft, v, track);

        // ---- appended v1.1 ----
        [ParameterDecl(Name = "Capture", IsStateless = true,
            Description = "Trigger: Freeze captures a new spectrum, Blur restarts from the live level (lands TickDelay later, like every parameter)")]
        public void SetCapture(bool v, int track) { if (v) SetT(P.Capture, 1, track); }

        // ---- appended v1.3: band LFO ----
        [ParameterDecl(Name = "LfoTarget", MinValue = 0, MaxValue = 5, DefValue = 0,
            Description = "Band LFO target: 0=off, 1=band (both edges), 2=FreqA, 3=FreqB, 4=width, 5=Value")]
        public void SetLfoTarget(int v, int track) => SetT(P.LfoTarget, v, track);

        [ParameterDecl(Name = "LfoShape", MinValue = 0, MaxValue = 5, DefValue = 0,
            Description = "Band LFO shape: 0=sine, 1=triangle, 2=saw up, 3=saw down, 4=square, 5=sample and hold")]
        public void SetLfoShape(int v, int track) => SetT(P.LfoShape, v, track);

        [ParameterDecl(Name = "LfoRate", MinValue = 0, MaxValue = 34, DefValue = 10,
            Description = "Band LFO rate. 0-18: period in ticks, locked to the song position (0=1/4 tick ... 10=16 ticks ... 18=256 ticks). 19-34: free-running Hz (0.05 ... 12 Hz)")]
        public void SetLfoRate(int v, int track) => SetT(P.LfoRate, v, track);

        [ParameterDecl(Name = "LfoDepth", MinValue = 0, MaxValue = 200, DefValue = 0,
            Description = "Band LFO depth, 0.5% steps (100% = 24 semitones each way on frequencies, half the Value range on Value)")]
        public void SetLfoDepth(int v, int track) => SetT(P.LfoDepth, v, track);

        [ParameterDecl(Name = "LfoPhase", MinValue = 0, MaxValue = 255, DefValue = 0,
            Description = "Band LFO phase offset (0x40 = 90 degrees); offset sets against each other")]
        public void SetLfoPhase(int v, int track) => SetT(P.LfoPhase, v, track);

        // ---- appended v1.4: audition ----
        [ParameterDecl(Name = "Mute", DefValue = false,
            Description = "Mute: this FX set is bypassed (lands TickDelay later, like every parameter)")]
        public void SetMute(bool v, int track) => SetT(P.Mute, v ? 1 : 0, track);

        [ParameterDecl(Name = "Solo", DefValue = false,
            Description = "Solo: while any active set is soloed, only soloed sets run. Mute wins over Solo")]
        public void SetSolo(bool v, int track) => SetT(P.Solo, v ? 1 : 0, track);

        // ---- appended v1.8 ----
        [ParameterDecl(Name = "Mix", MinValue = 0, MaxValue = 100, DefValue = 100,
            Description = "Wet/dry per set, in % (100 = effect only). Effects that take Soft, including PitchShift: 50 on PitchShift = a harmony")]
        public void SetMix(int v, int track) => SetT(P.Mix, v, track);

        // ---- appended v2.1 ----
        [ParameterDecl(Name = "Formant", MinValue = 0, MaxValue = 49, DefValue = 0,
            Description = "PitchShift formants: 0 off (they move with the pitch), 25 keep them in place, 1..49 shift them -12..+12 st in 0.5 st steps")]
        public void SetFormant(int v, int track) => SetT(P.Formant, v, track);

        // ---- appended v2.2: note-following bands ----
        [ParameterDecl(Name = "Note", IsStateless = true,
            Description = "Note to follow (with Follow on): the set's band, FreqA or PitchShift interval is as set for C-4 and moves with the note; off = back as set")]
        public void SetNote(Note v, int track) => SetT(P.Note, v.Value, track);

        [ParameterDecl(Name = "Follow", MinValue = 0, MaxValue = 3, DefValue = 0,
            Description = "What follows the Note column (and MIDI notes): 0 off, 1 band (both edges), 2 FreqA only (Harmonic fundamental), 3 PitchShift interval")]
        public void SetFollow(int v, int track) => SetT(P.Follow, v, track);

        void SetG(int i, int v)
        {
            v = P.G[i].Lim(v);
            lock (_lock) { _pending.G[i] = v; _disp.G[i] = v; _dirty = true; }
        }

        void SetT(int j, int v, int track)
        {
            if ((uint)track >= P.MaxSets) return;
            v = P.T[j].Lim(v);
            lock (_lock)
            {
                _pending.T[track, j] = v; _disp.T[track, j] = v; _dirty = true;
                if (track + 1 > _maxTrackSeen) _maxTrackSeen = track + 1;
            }
            if (j == P.FreqA || j == P.FreqB) MaybeGrowTracks(track);
        }

        // A preset (or MIDI, or another machine) switched on a set the machine has no track
        // for: ReBuzz still delivers the value (no bounds check in the host), so raise the
        // track count to make the set audible and visible. Growing keeps the stored values.
        int _growTarget;
        void MaybeGrowTracks(int track)
        {
            if (_disp.T[track, P.FreqA] == 0 && _disp.T[track, P.FreqB] == 0) return;
            int cur;
            try { cur = host.Machine?.TrackCount ?? 0; } catch { return; }
            if (cur <= 0 || track < cur || track + 1 <= _growTarget) return;
            _growTarget = track + 1;
            OnUi(() =>
            {
                var m = host.Machine;
                if (m != null && m.TrackCount < _growTarget) m.TrackCount = Math.Min(P.MaxSets, _growTarget);
                _growTarget = 0;
            });
        }

        // Runs an action on the UI thread. Replaceable so headless tests can run it inline.
#pragma warning disable 0649   // set by the headless tests only
        internal static Action<Action> UiInvoker;
#pragma warning restore 0649

        static void OnUi(Action a)
        {
            if (UiInvoker != null) { try { UiInvoker(a); } catch { } return; }
            try
            {
                var d = Application.Current?.Dispatcher;
                if (d == null) return;
                d.BeginInvoke(new Action(() => { try { a(); } catch { } }));
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Latency (delay compensation)
        // ------------------------------------------------------------------
        // ReBuzz reads GetLatency() (bound by name) when the machine is created or added,
        // and compensates parallel paths when Engine settings > Machine Delay Compensation
        // is on. The latency is TickDelay in samples, which changes with TickDelay and
        // tempo, so changes are pushed to MachineCore.Latency (public setter, not on
        // IMachine) once they have been stable for a quarter second.
        public int GetLatency() => ComputeLatency();

        int ComputeLatency()
        {
            var mi = host.MasterInfo;
            int sr = mi != null && mi.SamplesPerSec > 0 ? mi.SamplesPerSec : 44100;
            long spt = Math.Max(1, mi?.SamplesPerTick ?? 1);
            long maxDelay = sr * 6L - P.MaxBlkSz - 256;
            return (int)P.TickDelaySamples(_disp.G[P.TickDelay], spt, maxDelay);
        }

        int _reportedLatency = -1, _latCandidate = -1;
        long _latStable;
        void TrackLatency(int n, int sr)
        {
            int lat = ComputeLatency();
            if (lat == _reportedLatency) { _latCandidate = -1; return; }
            if (lat != _latCandidate) { _latCandidate = lat; _latStable = 0; return; }
            _latStable += n;
            if (_latStable < sr / 4) return;
            _reportedLatency = lat;
            _latCandidate = -1;
            OnUi(() =>
            {
                var m = host.Machine;
                var prop = m?.GetType().GetProperty("Latency");
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(int)) prop.SetValue(m, lat);
            });
        }

        bool FreezeHolding()
        {
            int nt = TrackCount();
            bool anySolo = false;
            for (int t = 0; t < nt; t++)
                if (_disp.T[t, P.Solo] == 1 && _disp.T[t, P.Mute] != 1 && (_disp.T[t, P.FreqA] != 0 || _disp.T[t, P.FreqB] != 0)) anySolo = true;
            for (int t = 0; t < nt; t++)
                if (_disp.T[t, P.Effect] == P.Freeze && _disp.T[t, P.FxVal] >= 0x100 &&
                    (_disp.T[t, P.FreqA] != 0 || _disp.T[t, P.FreqB] != 0) &&
                    _disp.T[t, P.Mute] != 1 && (!anySolo || _disp.T[t, P.Solo] == 1)) return true;
            return false;
        }

        // Song position in ticks for the band LFO: ISong.PlayPosition (field-backed, safe on the
        // audio thread) plus the position within the tick. Without a song (headless), count ticks.
        long _ticksSeen;
        void SongPosition(MasterInfo mi, long spt, out double pos, out bool playing)
        {
            if (mi.PosInTick == 0) _ticksSeen++;
            double inTick = (double)mi.PosInTick / spt;
            try
            {
                var b = Global.Buzz;
                var song = b?.Song;
                if (song != null) { pos = song.PlayPosition + inTick; playing = b.Playing; return; }
            }
            catch { }
            pos = _ticksSeen + inTick;
            playing = true;
        }

        // ------------------------------------------------------------------
        // v2.2: MIDI notes (bound by name by ManagedMachineHost) steer every set whose Follow is on,
        // like a Note in each of those tracks. Last-note priority: releasing the latest key sends off.
        // Not recorded in the pattern; lands TickDelay later, like every parameter.
        // ------------------------------------------------------------------
        int _midiNote = -1;
        public void MidiNote(int channel, int value, int velocity)
        {
            if (value < 0 || value > 119) return;
            int note;
            if (velocity > 0) { _midiNote = value; note = P.NoteFromIndex(value); }
            else if (value == _midiNote) { _midiNote = -1; note = P.NoteOff; }
            else return;
            int nt = TrackCount();
            for (int t = 0; t < nt; t++)
                if (_disp.T[t, P.Follow] != P.FollowOff) SetT(P.Note, note, t);
        }

        /// <summary>A track parameter's latest value as set (headless tests).</summary>
        internal int GetT(int track, int j) { lock (_lock) return _disp.T[track, j]; }

        /// <summary>v2.2: the note a set is following now (Buzz note byte; 0 = none), for the Edit window.</summary>
        internal int LiveNote(int set)
        {
            var e = _engine;
            return e == null || (uint)set >= P.MaxSets ? 0 : e.LiveNote[set];
        }

        /// <summary>v1.5: the set's edges as the LFO moved them on the latest block (Edit window overlay).</summary>
        internal bool LiveBand(int set, out float a, out float b)
        {
            a = b = float.NaN;
            var e = _engine;
            if (e == null || (uint)set >= P.MaxSets) return false;
            a = e.LiveA[set]; b = e.LiveB[set];
            return !float.IsNaN(a) && !float.IsNaN(b);
        }

        int TrackCount()
        {
            int n = 0;
            try { n = host.Machine?.TrackCount ?? 0; } catch { }
            if (n <= 0) n = _maxTrackSeen;
            return n < 1 ? 1 : (n > P.MaxSets ? P.MaxSets : n);
        }

        // ------------------------------------------------------------------
        // Work
        // ------------------------------------------------------------------
        public bool Work(IList<Sample[]> outputs, IList<Sample[]> inputs, int n, WorkModes mode)
        {
            // Multi-IO work is always called with WM_WRITE (MachineWorkInstance); input
            // presence is judged from the buffers themselves (null = not connected).
            Sample[] input = inputs != null && inputs.Count > 0 ? inputs[0] : null;
            Sample[] side = inputs != null && inputs.Count > 1 ? inputs[1] : null;
            Sample[] output = outputs != null && outputs.Count > 0 ? outputs[0] : null;

            var mi = host.MasterInfo;
            int sr = mi.SamplesPerSec > 0 ? mi.SamplesPerSec : 44100;
            long spt = Math.Max(1, mi.SamplesPerTick);

            if (_engine == null || _engine.SampleRate != sr)
            {
                _engine = new BlockFxEngine(sr) { Tap = _tap };
                // A fresh engine starts from defaults: re-assert everything we know.
                lock (_lock)
                {
                    int bs = _pending.G[P.BlkSync];
                    for (int i = 0; i < P.NG; i++) if (_pending.G[i] == P.G[i].No) _pending.G[i] = _disp.G[i];
                    _pending.G[P.BlkSync] = bs;
                    for (int s = 0; s < P.MaxSets; s++)
                        for (int j = 0; j < P.NT; j++)
                            if (j != P.Capture && _pending.T[s, j] == P.T[j].No) _pending.T[s, j] = _disp.T[s, j];
                    _dirty = true;
                }
                _lastNch = 2;
            }

            if (mi.PosInTick == 0 || _dirty)
            {
                lock (_lock)
                {
                    _pending.TvN = TrackCount();
                    SongPosition(mi, spt, out double songPos, out bool playing);
                    _engine.Tick(_pending, spt, songPos, playing);
                    _pending.SetNoVal();
                    _dirty = false;
                }
            }

            int stereo = _stereo;
            int nch = stereo == 0 ? 1 : 2;
            _engine.WideSeeds = stereo == 2;
            if (nch == 2 && _lastNch == 1) _engine.CloneChannel0To1();
            _lastNch = nch;

            if (_io[0].Length < n) { _io = new[] { new float[n], new float[n] }; _sc = new[] { new float[n], new float[n] }; }
            float[] a = _io[0], b = _io[1];

            if (side != null)
            {
                if (nch == 1) for (int i = 0; i < n; i++) _sc[0][i] = (side[i].L + side[i].R) * 0.5f;
                else for (int i = 0; i < n; i++) { _sc[0][i] = side[i].L; _sc[1][i] = side[i].R; }
            }

            bool hasInput = false;
            if (input != null)
            {
                if (nch == 1)
                    for (int i = 0; i < n; i++) { float m = (input[i].L + input[i].R) * 0.5f; a[i] = m; if (m != 0f) hasInput = true; }
                else
                    for (int i = 0; i < n; i++)
                    {
                        a[i] = input[i].L; b[i] = input[i].R;
                        if (a[i] != 0f || b[i] != 0f) hasInput = true;
                    }
            }

            // A held Freeze keeps sounding after the input goes silent: keep the engine running.
            if (!hasInput && FreezeHolding()) hasInput = true;

            bool keepDC = _keepDC;
            _engine.KeepDC = keepDC;
            _engine.Smooth = _window == 1;
            _engine.LevelMatch = _levelMatch;
            TrackLatency(n, sr);
            if (!_engine.Work(_io, n, nch, hasInput, spt, side != null ? _sc : null)) return false;

            if (keepDC)
            {
                // 5 Hz one-pole DC blocker; only needed once bin 0 is kept
                float rc = MathF.Exp(-2f * MathF.PI * 5f / sr);
                for (int c = 0; c < nch; c++)
                {
                    float[] x = _io[c];
                    float xp = _dcX[c], yp = _dcY[c];
                    for (int i = 0; i < n; i++) { float y = x[i] - xp + rc * yp; xp = x[i]; yp = y; x[i] = y; }
                    if (Math.Abs(yp) < 1e-20f) yp = 0;
                    _dcX[c] = xp; _dcY[c] = yp;
                }
            }

            if (output == null) return false;
            if (nch == 1)
                for (int i = 0; i < n; i++) { output[i].L = a[i]; output[i].R = a[i]; }
            else
                for (int i = 0; i < n; i++) { output[i].L = a[i]; output[i].R = b[i]; }
            return true;
        }

        // ------------------------------------------------------------------
        // Value text (as the original's DescribeValue)
        // ------------------------------------------------------------------
        public string DescribeValue(IParameter parameter, int value)
        {
            switch (parameter?.Name)
            {
                case "BlkSync": return value != 0 ? "Block sync" : "Free run";
                case "InterpMode":
                    return value == 1 ? "Off" : value == 2 ? "Continue Previous" : "Normal";
                case "MixBack": return (P.G[P.MixBack].Frac(value) * 100.0f).ToString("0.0") + "%";
                case "OutAmp":
                case "Amp": return value > 0 ? P.AmpDb(value).ToString("0.0") + "dB" : "-inf dB";
                case "TickDelay": return ((float)P.G[P.TickDelay].Lim(value) / P.Tick).ToString("0.00");
                case "BlockLen": return (P.BlkSz0 << BlkLenDisp(P.G[P.BlkLen].Lim(value))).ToString();
                case "Overlap": return P.G[P.Overlap].Lim(value) + "%";
                case "Window": return value == 0 ? "Blocky (original)" : "Smooth";
                case "Soft": return value == 0 ? "Hard" : value + " st";
                case "LevelMatch": return value != 0 ? "On (original)" : "Off";
                case "Capture": return value != 0 ? "Capture" : "";
                case "KeepDC": return value != 0 ? "Keep" : "Zero (original)";
                case "Stereo": return value == 0 ? "Mono" : value == 2 ? "Wide" : "Stereo";
                case "FreqA":
                case "FreqB": return Display.Freq(RoundFreqDisp(P.FreqHz(value)));
                case "Effect": return P.EffectNames[P.T[P.Effect].Lim(value)];
                case "Value": return P.T[P.FxVal].Frac(value).ToString("0.000");
                case "LfoTarget": return P.LfoTargetNames[P.T[P.LfoTarget].Lim(value)];
                case "LfoShape": return P.LfoShapeNames[P.T[P.LfoShape].Lim(value)];
                case "LfoRate": return EditModel.LfoRateText(value, host.MasterInfo?.TicksPerBeat ?? 0);
                case "LfoDepth": return EditModel.LfoDepthText(value);
                case "LfoPhase": return EditModel.LfoPhaseText(value);
                case "Mute": return value != 0 ? "Muted" : "";
                case "Solo": return value != 0 ? "Solo" : "";
                case "Mix": return EditModel.MixText(value);
                case "Formant": return EditModel.FormantText(value);
                case "Note": return P.NoteName(value);
                case "Follow": return value >= 0 && value < P.FollowNames.Length ? P.FollowNames[value] : null;
            }
            return null;
        }

        int DispSampleRate => host.MasterInfo?.SamplesPerSec > 0 ? host.MasterInfo.SamplesPerSec : 44100;
        long DispSampsPerTick => Math.Max(1, host.MasterInfo?.SamplesPerTick ?? 1);
        long DispMaxDelay => DispSampleRate * 6L - P.MaxBlkSz - 256;

        // getBlkLenDisp: the block can't usefully exceed ~2x the output delay
        int BlkLenDisp(int r)
        {
            ulong n = (ulong)P.TickDelaySamples(_disp.G[P.TickDelay], DispSampsPerTick, DispMaxDelay);
            while ((ulong)P.BlkSz(r - 1) > n) r--;
            return r;
        }

        internal float RoundFreqDispPublic(float freq) => RoundFreqDisp(freq);

        // v1.7: for the Edit window's LFO speed hint
        internal long CurrentHop => _engine?.cur_hop ?? 0;
        internal double SampsPerTickPublic => DispSampsPerTick;

        // roundFreqDisp: snap to the bin centre at the current block length
        float RoundFreqDisp(float freq)
        {
            float sps = DispSampleRate;
            long blk = P.BlkSz(BlkLenDisp(_disp.G[P.BlkLen]));
            float s = blk / sps;
            float fb = freq * s + 0.5f;
            if (fb < 0) fb = 0; if (fb > blk / 2) fb = blk / 2;
            long bin = (long)fb;
            return (float)bin / blk * sps;
        }

        // ------------------------------------------------------------------
        // Right-click menu
        // ------------------------------------------------------------------
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM
                {
                    Text = "Edit...",
                    Command = new BuzzGUI.Common.SimpleCommand
                    {
                        CanExecuteDelegate = _ => true,
                        ExecuteDelegate = _ => OpenEditWindow()
                    }
                };
                yield return new MenuItemVM
                {
                    Text = "About...",
                    Command = new BuzzGUI.Common.SimpleCommand
                    {
                        CanExecuteDelegate = _ => true,
                        ExecuteDelegate = _ => MessageBox.Show(
                            "Pedal BlockFX " + Version + "\n\n" +
                            "Built on DT Block Effects by Darrell Tam (2003-2004),\n" +
                            "the Buzz machine that inspired this one. Its engine is\n" +
                            "ported here and carried forward. Thank you, Darrell.\n\n" +
                            "Block FFT spectral effects: Contrast, Smear, Clip, Weed,\n" +
                            "ShiftAdd, ShiftReplace, Harmonic, AutoHarm, CopyHarm.\n" +
                            "Added: Freeze, Gate, Blur, Tilt, RoboWhisper, Cross, PitchShift.\n" +
                            "Band LFO per set: locked to the song position, or free-running in Hz.\n" +
                            "Mute, Solo and wet/dry Mix per set; formant control for PitchShift.\n" +
                            "Bands, FreqA or the PitchShift interval can follow notes (pattern or MIDI).\n\n" +
                            "Edit window: spectrograms in/out with draggable FX set ranges;\n" +
                            "adapts to its size, with a Compact mode.\n\n" +
                            "Copyright (C) 2026 Pedal. Free software under the\n" +
                            "GNU General Public License v3 or later; no warranty.\n" +
                            "See LICENSE and README for credits and terms.",
                            "About Pedal BlockFX")
                    }
                };
            }
        }
    
        // ------------------------------------------------------------------
        // Edit window lifecycle
        // ------------------------------------------------------------------
        void OpenEditWindow()
        {
            if (_editWindow != null)
            {
                if (_editWindow.WindowState == WindowState.Minimized) _editWindow.WindowState = WindowState.Normal;
                _editWindow.Activate();
                return;
            }
            _editWindow = new EditWindow(this, host.Machine, _tap);
            _editWindow.Show();
        }

        internal void EditWindowClosed(EditWindow w)
        {
            if (_editWindow == w) _editWindow = null;
            _tap.Active = false;
        }

        public void Dispose()
        {
            Unsubscribe();
            _tap.Active = false;
            var w = _editWindow;
            _editWindow = null;
            if (w != null)
            {
                try { w.Dispatcher.BeginInvoke(new Action(() => { try { w.Close(); } catch { } })); } catch { }
            }
        }
    }
}
