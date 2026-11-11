// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Based on DT Block Effects, Copyright (C) 2003-2004 Darrell Tam.
//
// Pedal BlockFX - Edit window.
//
// Layout follows the original DT BlkFx dialog: "Spec in" above "Spec out", log
// frequency across (linear in FreqA/FreqB parameter units, C0..Nyquist), time
// scrolling upward, same colour map. Added on top of the original:
//   * every active FX set drawn as a coloured band over both spectrograms;
//     drag an edge to move FreqA/FreqB, drag inside to move the range, drag on
//     empty space to draw a new range for the selected set (Shift = snap to
//     semitones), mouse wheel over a band changes its Value, Delete turns it off;
//   * a white line marking the audio currently leaving the machine (input is
//     shown TickDelay ahead of the output);
//   * a panel with every FX set (effect, Value with the original's per-effect
//     description, Amp, FreqA/FreqB as text) and the globals.
//
// All reads and writes go through ReBuzz's IParameter, so the parameter window,
// pattern recording, undo and song saving all stay in sync.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BuzzGUI.Interfaces;

namespace PedalBlockFX
{
    // ==========================================================================
    // Parameter access through ReBuzz
    // ==========================================================================
    internal sealed class ParamAccess
    {
        readonly IMachine _m;
        readonly Dictionary<string, IParameter> _g = new Dictionary<string, IParameter>();
        readonly Dictionary<string, IParameter> _t = new Dictionary<string, IParameter>();

        public ParamAccess(IMachine m) { _m = m; }

        IParameter Find(ParameterGroupType type, string name, Dictionary<string, IParameter> cache)
        {
            if (cache.TryGetValue(name, out var p) && p != null) return p;
            try
            {
                var grp = _m?.ParameterGroups?.FirstOrDefault(g => g != null && g.Type == type);
                p = grp?.Parameters?.FirstOrDefault(x => x != null && x.Name == name);
            }
            catch { p = null; }
            if (p != null) cache[name] = p;
            return p;
        }

        public IParameter G(string name) => Find(ParameterGroupType.Global, name, _g);
        public IParameter T(string name) => Find(ParameterGroupType.Track, name, _t);

        public int GetG(string name, int def) { try { return G(name)?.GetValue(0) ?? def; } catch { return def; } }
        public int GetT(string name, int track, int def) { try { return T(name)?.GetValue(track) ?? def; } catch { return def; } }
        public void SetG(string name, int v) { try { G(name)?.SetValue(0, v); } catch { } }
        public void SetT(string name, int track, int v) { try { T(name)?.SetValue(track, v); } catch { } }
        public string DescG(string name, int v) { try { return G(name)?.DescribeValue(v) ?? v.ToString(); } catch { return v.ToString(); } }
        public string DescT(string name, int v) { try { return T(name)?.DescribeValue(v) ?? v.ToString(); } catch { return v.ToString(); } }
        public int TrackCount { get { try { return Math.Max(1, Math.Min(P.MaxSets, _m?.TrackCount ?? 1)); } catch { return 1; } } }
        public string MachineName { get { try { return _m?.Name ?? ""; } catch { return ""; } } }
    }

    // ==========================================================================
    // The window
    // ==========================================================================
    internal sealed class EditWindow : Window
    {
        readonly PedalBlockFXMachine _machine;
        readonly SpectrumTap _tap;
        internal readonly ParamAccess Prm;

        readonly SpectroView _inView, _outView;
        readonly FreqAxis _axis;
        readonly TextBlock _status;
        readonly StackPanel _setRowsPanel;
        // v2.0 display options
        readonly List<ListRow> _listRows = new List<ListRow>();
        readonly Border _detailHost = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0),
                                                   Margin = new Thickness(0, 4, 0, 0) };
        readonly Grid _specGrid;
        readonly RowDefinition _setsRowDef;
        readonly ComboBox _viewCombo;
        readonly System.Windows.Controls.Primitives.ToggleButton _compactBtn;
        bool _fullRows = true, _compact;
        int _specView, _restoreSpec = -1;
        bool _restoreGlobals;
        readonly List<SetRow> _rows = new List<SetRow>();
        readonly GlobalsRow _globals;
        readonly DispatcherTimer _timer;
        readonly CheckBox _freeze;
        readonly ComboBox _floor;
        readonly LevelLegend _legend;          // v1.6
        readonly WindowPrefs _prefs;           // v1.6: remembered size, position, Floor

        // cached per-frame parameter snapshot (drawing + hit tests)
        internal readonly int[] A = new int[P.MaxSets], B = new int[P.MaxSets], Fx = new int[P.MaxSets], Val = new int[P.MaxSets], Soft = new int[P.MaxSets];
        internal readonly int[] Mute = new int[P.MaxSets], Solo = new int[P.MaxSets];   // v1.4
        internal readonly int[] LfoT = new int[P.MaxSets], LfoD = new int[P.MaxSets];   // v1.5 overlay
        internal readonly int[] Follow = new int[P.MaxSets];                             // v2.2 overlay
        internal int Tracks = 1;
        internal int Selected;
        internal float FloorDb = -90f;
        internal double HoverX = -1;
        internal int MaxVal => _tap.MaxVal > 0 ? _tap.MaxVal : SpectrumTap.HzToVal(22050);
        internal int SampleRate => _tap.SampleRate > 0 ? _tap.SampleRate : 44100;
        internal double SampsPerTick => _machine.SampsPerTickPublic;
        internal long Hop => _machine.CurrentHop;

        // latest row, for the hover readout
        internal float[] LastIn = new float[SpectrumTap.Bands], LastOut = new float[SpectrumTap.Bands];

        int _readRow = -1;
        int _frame;
        internal bool Syncing;

        public EditWindow(PedalBlockFXMachine machine, IMachine im, SpectrumTap tap)
        {
            _machine = machine;
            _tap = tap;
            Prm = new ParamAccess(im);

            Title = "Pedal BlockFX - " + Prm.MachineName;
            Width = 1180; Height = 740;
            MinWidth = 640; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // v1.6: reopen where it was last closed, kept inside the current screens
            _prefs = WindowPrefs.Load();
            if (_prefs.HasBounds)
            {
                _prefs.FitTo(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                             SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight, MinWidth, MinHeight);
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _prefs.Left; Top = _prefs.Top; Width = _prefs.Width; Height = _prefs.Height;
            }
            if (_prefs.Maximized) WindowState = WindowState.Maximized;
            ShowInTaskbar = false;
            try { Owner = Application.Current?.MainWindow; } catch { }

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _setsRowDef = new RowDefinition { Height = GridLength.Auto, MaxHeight = 330 };   // v2.0: cap follows the window height
            root.RowDefinitions.Add(_setsRowDef);

            // spectrograms
            _specGrid = new Grid { Background = Brushes.Black };
            _specGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _specGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            _specGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _inView = new SpectroView(this, false);
            _axis = new FreqAxis(this);
            _outView = new SpectroView(this, true);
            Grid.SetRow(_inView, 0); Grid.SetRow(_axis, 1); Grid.SetRow(_outView, 2);
            _specGrid.Children.Add(_inView); _specGrid.Children.Add(_axis); _specGrid.Children.Add(_outView);
            Grid.SetRow(_specGrid, 0);
            root.Children.Add(_specGrid);

            // status line + display options
            var statusBar = new DockPanel { Margin = new Thickness(6, 3, 6, 3), LastChildFill = true };
            _freeze = new CheckBox { Content = "Freeze", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _floor = new ComboBox { Width = 90, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var f in new[] { "-60 dB", "-90 dB", "-120 dB" }) _floor.Items.Add(f);
            _floor.SelectedIndex = _prefs.FloorIndex >= 0 ? _prefs.FloorIndex : WindowPrefs.DefaultFloorIndex;
            FloorDb = WindowPrefs.FloorChoices[_floor.SelectedIndex];
            _floor.SelectionChanged += (s, e) =>
            {
                FloorDb = WindowPrefs.FloorChoices[Math.Max(0, _floor.SelectedIndex)];
                _legend?.InvalidateVisual();
            };
            var floorLbl = new TextBlock { Text = "Floor", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            _legend = new LevelLegend(this);
            DockPanel.SetDock(_freeze, Dock.Right); DockPanel.SetDock(_floor, Dock.Right); DockPanel.SetDock(floorLbl, Dock.Right);
            DockPanel.SetDock(_legend, Dock.Right);
            statusBar.Children.Add(_freeze); statusBar.Children.Add(_floor); statusBar.Children.Add(floorLbl);
            statusBar.Children.Add(_legend);
            // v2.0 view options
            _viewCombo = new ComboBox { Width = 88, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Which spectrograms to show" };
            foreach (var n in WindowPrefs.SpecViewNames) _viewCombo.Items.Add(n);
            _viewCombo.SelectionChanged += (s, e) => { if (_viewCombo.SelectedIndex >= 0) ApplySpecView(_viewCombo.SelectedIndex); };
            _compactBtn = new System.Windows.Controls.Primitives.ToggleButton { Content = "Compact", Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 0, 6, 0), ToolTip = "Compact: Spec out only, globals folded, a set list with the selected set in full. Click again to restore." };
            _compactBtn.Click += (s, e) => SetCompact(_compactBtn.IsChecked == true);
            DockPanel.SetDock(_viewCombo, Dock.Right); DockPanel.SetDock(_compactBtn, Dock.Right);
            statusBar.Children.Add(_viewCombo); statusBar.Children.Add(_compactBtn);
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis,
                Text = "Drag on the spectrogram to set the selected FX set's range. Shift snaps to semitones, wheel changes Value, Delete turns it off." };
            statusBar.Children.Add(_status);
            Grid.SetRow(statusBar, 1);
            root.Children.Add(statusBar);

            // globals
            _globals = new GlobalsRow(this);
            Grid.SetRow(_globals.Panel, 2);
            root.Children.Add(_globals.Panel);

            // FX sets
            _setRowsPanel = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            var scroll = new ScrollViewer { Content = _setRowsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 3);
            root.Children.Add(scroll);

            Content = root;

            // v2.0: view options from the prefs, then lay out for the current size
            _globals.SetCollapsed(_prefs.GlobalsCollapsed);
            _viewCombo.SelectedIndex = _prefs.SpecView;
            ApplySpecView(_prefs.SpecView);
            if (_prefs.Compact) SetCompact(true);
            SizeChanged += (s, e) => ApplyLayout(false);

            PreviewKeyDown += OnKey;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => OnFrame();
            Loaded += (s, e) => { _tap.ResetAccumulator(); _tap.Active = true; SyncFromParams(); _timer.Start(); };
            Closing += (s, e) => SavePrefs();
            Closed += (s, e) => { _timer.Stop(); _tap.Active = false; _machine.EditWindowClosed(this); };
        }

        // v1.6: remember the normal (restored) bounds, whether it was maximised, and the Floor
        void SavePrefs()
        {
            try
            {
                Rect b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (!b.IsEmpty && b.Width > 0 && b.Height > 0)
                {
                    _prefs.Left = b.Left; _prefs.Top = b.Top; _prefs.Width = b.Width; _prefs.Height = b.Height;
                }
                _prefs.Maximized = WindowState == WindowState.Maximized;
                _prefs.FloorIndex = _floor.SelectedIndex;
                // v2.0: in compact mode keep the user's own choices, which compact mode only overrides
                _prefs.SpecView = _compact && _restoreSpec >= 0 ? _restoreSpec : _specView;
                _prefs.GlobalsCollapsed = _compact ? _restoreGlobals : _globals.Collapsed;
                _prefs.Compact = _compact;
                _prefs.Save();
            }
            catch { }
        }

        // ---- per-frame update ------------------------------------------------------
        void OnFrame()
        {
            _frame++;
            int written = _tap.Written;
            if (_readRow < 0 || written - _readRow > SpectrumTap.RingRows) _readRow = Math.Max(0, written - SpectrumTap.RingRows / 2);
            if (written > _readRow)
            {
                if (_freeze.IsChecked == true) _readRow = written;
                else
                {
                    var rows = new List<SpectrumTap.Row>();
                    for (int i = _readRow; i < written; i++) rows.Add(_tap.Get(i));
                    _readRow = written;
                    _inView.AddRows(rows);
                    _outView.AddRows(rows);
                    var last = rows[rows.Count - 1];
                    Array.Copy(last.In, LastIn, LastIn.Length);
                    Array.Copy(last.Out, LastOut, LastOut.Length);
                }
            }

            SyncFromParams();
            _inView.InvalidateVisual();
            _outView.InvalidateVisual();
            if (_frame % 15 == 0) _axis.InvalidateVisual();
            if (HoverX >= 0) UpdateHoverText();
        }

        void SyncFromParams()
        {
            int tracks = Prm.TrackCount;
            for (int t = 0; t < tracks; t++)
            {
                if (_drag != DragMode.None && t == _dragTrack) continue;   // keep local values while dragging
                A[t] = Prm.GetT("FreqA", t, 0);
                B[t] = Prm.GetT("FreqB", t, 0);
                Fx[t] = Prm.GetT("Effect", t, 0);
                Val[t] = Prm.GetT("Value", t, 0);
                Soft[t] = Prm.GetT("Soft", t, 0);
                Mute[t] = Prm.GetT("Mute", t, 0);
                LfoT[t] = Prm.GetT("LfoTarget", t, 0);
                LfoD[t] = Prm.GetT("LfoDepth", t, 0);
                Follow[t] = Prm.GetT("Follow", t, 0);
                Solo[t] = Prm.GetT("Solo", t, 0);
            }
            if (tracks != Tracks || _rows.Count != tracks) RebuildRows(tracks);
            Tracks = tracks;
            if (Selected >= Tracks) Selected = Tracks - 1;

            Syncing = true;
            try
            {
                foreach (var r in _rows) r.Sync();
                foreach (var lr in _listRows) lr.Sync();
                _globals.Sync();
            }
            finally { Syncing = false; }
        }

        void RebuildRows(int tracks)
        {
            _setRowsPanel.Children.Clear();
            _detailHost.Child = null;
            _rows.Clear();
            _listRows.Clear();
            for (int t = 0; t < tracks; t++)
            {
                _rows.Add(new SetRow(this, t));
                _listRows.Add(new ListRow(this, t));
            }
            Tracks = tracks;
            if (Selected >= tracks) Selected = Math.Max(0, tracks - 1);
            RebuildSetsArea();
        }

        internal void Select(int t)
        {
            if (t < 0 || t >= Tracks) return;
            bool changed = t != Selected;
            Selected = t;
            foreach (var r in _rows) r.UpdateSelection();
            foreach (var lr in _listRows) lr.UpdateSelection();
            // swap the detail panel after the click that caused this has finished
            if (changed && !_fullRows) Dispatcher.BeginInvoke(new Action(ShowDetail), DispatcherPriority.Background);
        }

        internal float RoundFreq(float f) => _machine.RoundFreqDispPublic(f);

        /// <summary>
        /// v2.0: the rows already shown (up to the read position), newest last, at most n of them,
        /// for a spectrogram repainting itself after a resize. Kept well inside the capture ring so
        /// the audio thread isn't writing the oldest slot being read. Empty while Freeze is on (the
        /// held picture isn't in the ring any more).
        /// </summary>
        internal List<SpectrumTap.Row> RecentRows(int n)
        {
            var list = new List<SpectrumTap.Row>();
            if (_freeze == null || _freeze.IsChecked == true || _readRow <= 0) return list;
            int end = Math.Min(_readRow, _tap.Written);
            int count = Math.Min(n, Math.Min(end, SpectrumTap.RingRows - 64));
            for (int i = end - count; i < end; i++) list.Add(_tap.Get(i));
            return list;
        }

        // ---- v2.0 layout -----------------------------------------------------------
        /// <summary>Full rows when there's room; otherwise (or in compact mode) the set list plus the selected set in full.</summary>
        void ApplyLayout(bool force)
        {
            double w = ActualWidth;
            bool full = EditModel.UseFullRows(w, _compact, _fullRows);
            _legend.Visibility = EditModel.ShowLegend(w, _compact) ? Visibility.Visible : Visibility.Collapsed;
            if (ActualHeight > 0) _setsRowDef.MaxHeight = Math.Max(170, ActualHeight * 0.5);
            if (full != _fullRows || force) { _fullRows = full; RebuildSetsArea(); }
        }

        void RebuildSetsArea()
        {
            _setRowsPanel.Children.Clear();
            _detailHost.Child = null;
            if (_fullRows)
            {
                _setRowsPanel.Children.Add(SetRow.Header());
                foreach (var r in _rows) _setRowsPanel.Children.Add(r.BuildFull());
            }
            else
            {
                _setRowsPanel.Children.Add(ListRow.Header());
                foreach (var lr in _listRows) _setRowsPanel.Children.Add(lr.Panel);
                _setRowsPanel.Children.Add(_detailHost);
                ShowDetail();
            }
        }

        void ShowDetail()
        {
            if (_fullRows) return;
            _detailHost.Child = null;
            if (Selected >= 0 && Selected < _rows.Count) _detailHost.Child = _rows[Selected].BuildDetail();
        }

        void ApplySpecView(int v)
        {
            if (v < WindowPrefs.SpecBoth || v > WindowPrefs.SpecInOnly) v = WindowPrefs.SpecBoth;
            _specView = v;
            bool showIn = v != WindowPrefs.SpecOutOnly, showOut = v != WindowPrefs.SpecInOnly;
            _specGrid.RowDefinitions[0].Height = showIn ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            _specGrid.RowDefinitions[2].Height = showOut ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            _inView.Visibility = showIn ? Visibility.Visible : Visibility.Collapsed;
            _outView.Visibility = showOut ? Visibility.Visible : Visibility.Collapsed;
            if (_viewCombo.SelectedIndex != v) _viewCombo.SelectedIndex = v;
        }

        /// <summary>Compact: Spec out only, globals folded, list + detail. Switching it off restores the previous view.</summary>
        void SetCompact(bool on)
        {
            if (on != _compact)
            {
                _compact = on;
                if (on)
                {
                    _restoreSpec = _specView; _restoreGlobals = _globals.Collapsed;
                    ApplySpecView(WindowPrefs.SpecOutOnly);
                    _globals.SetCollapsed(true);
                }
                else
                {
                    if (_restoreSpec >= 0) ApplySpecView(_restoreSpec);
                    _globals.SetCollapsed(_restoreGlobals);
                    _restoreSpec = -1;
                }
            }
            if (_compactBtn.IsChecked != on) _compactBtn.IsChecked = on;
            ApplyLayout(true);
        }

        internal string ValueDesc(int fx, int v) => EditModel.ValueDesc(fx, v, _machine.RoundFreqDispPublic, SampleRate);

        // ---- hover readout ---------------------------------------------------------
        internal void SetHover(double x, bool output)
        {
            HoverX = x;
            _hoverOutput = output;
            UpdateHoverText();
            _inView.InvalidateVisual(); _outView.InvalidateVisual();
        }
        bool _hoverOutput;

        void UpdateHoverText()
        {
            double w = _inView.ActualWidth;
            if (HoverX < 0 || w <= 1) return;
            int v = EditModel.XToVal(HoverX, MaxVal, w);
            float hz = P.FreqHz(v);
            int band = (int)Math.Round((double)v / MaxVal * (SpectrumTap.Bands - 1));
            band = Math.Max(0, Math.Min(SpectrumTap.Bands - 1, band));
            float din = EditModel.PowerToDbfs(LastIn[band]), dout = EditModel.PowerToDbfs(LastOut[band]);
            string db(float d) => float.IsNegativeInfinity(d) ? "  -inf" : d.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6);
            _status.Text = string.Format(CultureInfo.InvariantCulture,
                "{0} {1,7:0} Hz / {2,-8} / 0x{3:X4}    in {4} dBFS   out {5} dBFS",
                _hoverOutput ? "Spec out" : "Spec in ", hz, Display.HzToNote(hz), v, db(din), db(dout));
        }

        internal void ClearHover()
        {
            HoverX = -1;
            _inView.InvalidateVisual(); _outView.InvalidateVisual();
        }

        // ---- band drawing ------------------------------------------------------------
        // v1.4: same rule as the engine (BlockFxEngine.Channel.AnySolo / Audible)
        internal bool Silenced(int t)
        {
            if (Mute[t] == 1) return true;
            bool anySolo = false;
            for (int i = 0; i < Tracks; i++)
                if (Solo[i] == 1 && Mute[i] != 1 && (A[i] != 0 || B[i] != 0)) { anySolo = true; break; }
            return anySolo && Solo[t] != 1;
        }

        internal void DrawBands(DrawingContext dc, double w, double h, bool output, double pixelsPerDip)
        {
            int maxVal = MaxVal;
            for (int pass = 0; pass < 2; pass++)          // unselected first, selected on top
            for (int t = 0; t < Tracks; t++)
            {
                bool sel = t == Selected;
                if ((pass == 1) != sel) continue;
                int a = A[t], b = B[t];
                if (a == 0 && b == 0) continue;
                var col = SetColour(t);
                bool silent = Silenced(t);

                // v1.5: an LFO that moves the edges -> show how far each edge travels (faint strips),
                // draw the band where it is now, and keep the set's own edges as thin dotted lines
                // (those are what dragging edits)
                int baseA = a, baseB = b;
                bool travels = EditModel.LfoEdgeTravel(LfoT[t], LfoD[t], a, b, out float aLo, out float aHi, out float bLo, out float bHi);
                bool lfoMoves = travels && _drag == DragMode.None && !silent;
                if (lfoMoves)
                {
                    // travel: a tint plus dashed limit lines, so it reads on a bright spectrogram too
                    var strip = new SolidColorBrush(Color.FromArgb(0x30, col.R, col.G, col.B));
                    var limit = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, col.R, col.G, col.B)), 1) { DashStyle = new DashStyle(new double[] { 6, 4 }, 0) };
                    foreach (var (lo, hi) in new[] { (aLo, aHi), (bLo, bHi) })
                    {
                        if (hi <= lo) continue;
                        double x0 = EditModel.ValToX((int)lo, maxVal, w), x1 = EditModel.ValToX((int)hi, maxVal, w);
                        dc.DrawRectangle(strip, null, new Rect(new Point(x0, 0), new Point(x1, h)));
                        dc.DrawLine(limit, new Point(x0, 0), new Point(x0, h));
                        dc.DrawLine(limit, new Point(x1, 0), new Point(x1, h));
                    }
                    var basePen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, col.R, col.G, col.B)), 1) { DashStyle = DashStyles.Dot };
                    double xba = EditModel.ValToX(baseA, maxVal, w), xbb = EditModel.ValToX(baseB, maxVal, w);
                    dc.DrawLine(basePen, new Point(xba, 0), new Point(xba, h));
                    dc.DrawLine(basePen, new Point(xbb, 0), new Point(xbb, h));
                    if (_machine.LiveBand(t, out float la, out float lb)) { a = (int)Math.Round(la); b = (int)Math.Round(lb); }
                }
                else if ((Follow[t] == P.FollowBand || Follow[t] == P.FollowFreqA) && _drag == DragMode.None && !silent
                         && _machine.LiveNote(t) != 0 && _machine.LiveBand(t, out float fla, out float flb))
                {
                    // v2.2: a note moved the band: draw it where it is, keep the set's own (C-4) edges dotted
                    var basePen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, col.R, col.G, col.B)), 1) { DashStyle = DashStyles.Dot };
                    double xba = EditModel.ValToX(baseA, maxVal, w), xbb = EditModel.ValToX(baseB, maxVal, w);
                    dc.DrawLine(basePen, new Point(xba, 0), new Point(xba, h));
                    dc.DrawLine(basePen, new Point(xbb, 0), new Point(xbb, h));
                    a = (int)Math.Round(fla); b = (int)Math.Round(flb);
                }
                if (silent) col = Color.FromArgb(0x70, col.R, col.G, col.B);   // muted / not soloed: faded
                var fill = new SolidColorBrush(Color.FromArgb((byte)(silent ? 0x0C : (sel ? 0x48 : 0x20)), col.R, col.G, col.B));
                var pen = new Pen(new SolidColorBrush(col), sel ? 2 : 1);
                if (!sel || silent) pen.DashStyle = silent ? DashStyles.Dot : DashStyles.Dash;
                double xa = EditModel.ValToX(a, maxVal, w), xb = EditModel.ValToX(b, maxVal, w);
                if (a <= b) dc.DrawRectangle(fill, null, new Rect(new Point(xa, 0), new Point(Math.Max(xa + 1, xb), h)));
                else
                {
                    dc.DrawRectangle(fill, null, new Rect(new Point(0, 0), new Point(xb, h)));
                    dc.DrawRectangle(fill, null, new Rect(new Point(xa, 0), new Point(w, h)));
                }
                dc.DrawLine(pen, new Point(xa, 0), new Point(xa, h));
                dc.DrawLine(pen, new Point(xb, 0), new Point(xb, h));

                // soft edges: dotted line where the effect reaches full strength
                if (Soft[t] > 0 && P.HasSoft(P.T[P.Effect].Lim(Fx[t])))
                {
                    int sv = Soft[t] * 256;
                    var sp = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, col.R, col.G, col.B)), 1) { DashStyle = DashStyles.Dot };
                    int ia = a <= b ? a + sv : a + sv, ib = a <= b ? b - sv : b - sv;
                    if (a > b || ia < ib)
                    {
                        double xia = EditModel.ValToX(Math.Min(ia, maxVal), maxVal, w), xib = EditModel.ValToX(Math.Max(ib, 0), maxVal, w);
                        dc.DrawLine(sp, new Point(xia, 0), new Point(xia, h));
                        dc.DrawLine(sp, new Point(xib, 0), new Point(xib, h));
                    }
                }
                if (!output)
                {
                    string tag = Mute[t] == 1 ? " (muted)" : (silent ? " (not soloed)" : (Solo[t] == 1 ? " (solo)" : ""));
                    int liveNote = Follow[t] != P.FollowOff ? _machine.LiveNote(t) : 0;
                    if (liveNote != 0) tag += " \u266A " + P.NoteName(liveNote);   // v2.2: the note it's following
                    var ft = new FormattedText(t + " " + P.EffectNames[P.T[P.Effect].Lim(Fx[t])] + tag,
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                        11, new SolidColorBrush(col), pixelsPerDip);
                    double lx = (a <= b ? Math.Min(xa, xb) : xa) + 3;
                    if (lx + ft.Width > w) lx = w - ft.Width - 4;
                    lx = Math.Max(2, lx);
                    double ly = 20 + 15 * (t % 4);
                    dc.DrawRoundedRectangle(LabelBack, null, new Rect(lx - 2, ly - 1, ft.Width + 4, ft.Height + 2), 2, 2);
                    dc.DrawText(ft, new Point(lx, ly));
                }
            }
        }

        static readonly Brush LabelBack = MakeLabelBack();
        static Brush MakeLabelBack() { var b = new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0)); b.Freeze(); return b; }

        internal static Color SetColour(int t)
        {
            int c = EditModel.SetColours[t % EditModel.SetColours.Length];
            return Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        }

        internal long AudiblePos => _tap.AudiblePos;

        // ---- mouse editing -----------------------------------------------------------
        enum DragMode { None, EdgeA, EdgeB, Move, Create }
        DragMode _drag = DragMode.None;
        int _dragTrack;
        int _grabVal, _a0, _b0, _anchor;
        double _downX;
        bool _moved;

        internal void BandMouseDown(double x, double w)
        {
            int maxVal = MaxVal;
            int v = EditModel.XToVal(x, maxVal, w);
            _downX = x; _moved = false;

            const double grab = 6;
            int hitT = -1; DragMode hitMode = DragMode.None;
            // edges: selected set first, then the others
            foreach (int t in Enumerable.Range(0, Tracks).OrderBy(k => k == Selected ? 0 : 1))
            {
                if (A[t] == 0 && B[t] == 0) continue;
                double xa = EditModel.ValToX(A[t], maxVal, w), xb = EditModel.ValToX(B[t], maxVal, w);
                if (Math.Abs(x - xa) <= grab) { hitT = t; hitMode = DragMode.EdgeA; break; }
                if (Math.Abs(x - xb) <= grab) { hitT = t; hitMode = DragMode.EdgeB; break; }
            }
            // inside a band: selected first
            if (hitT < 0)
                foreach (int t in Enumerable.Range(0, Tracks).OrderBy(k => k == Selected ? 0 : 1))
                {
                    if (A[t] == 0 && B[t] == 0) continue;
                    bool inside = A[t] <= B[t] ? (v >= A[t] && v <= B[t]) : (v <= B[t] || v >= A[t]);
                    if (inside) { hitT = t; hitMode = DragMode.Move; break; }
                }

            if (hitT >= 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                Select(hitT);
                _dragTrack = hitT; _drag = hitMode;
                _grabVal = v; _a0 = A[hitT]; _b0 = B[hitT];
            }
            else
            {
                _dragTrack = Selected; _drag = DragMode.Create;
                _anchor = Math.Max(1, v);
            }
        }

        internal void BandMouseMove(double x, double w)
        {
            if (_drag == DragMode.None) return;
            if (!_moved && Math.Abs(x - _downX) < 3) return;
            _moved = true;
            int maxVal = MaxVal;
            int v = EditModel.XToVal(x, maxVal, w);
            bool snap = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (snap) v = EditModel.SnapSemitone(v);
            v = Math.Max(1, Math.Min(maxVal, v));
            int t = _dragTrack, a = A[t], b = B[t];
            switch (_drag)
            {
                case DragMode.EdgeA: a = v; break;
                case DragMode.EdgeB: b = v; break;
                case DragMode.Move:
                {
                    int d = v - _grabVal;
                    if (snap) d = EditModel.SnapSemitone(d);
                    int lo = Math.Min(_a0, _b0), hi = Math.Max(_a0, _b0);
                    d = Math.Max(1 - lo, Math.Min(maxVal - hi, d));
                    a = _a0 + d; b = _b0 + d;
                    break;
                }
                case DragMode.Create:
                {
                    int an = snap ? Math.Max(1, EditModel.SnapSemitone(_anchor)) : _anchor;
                    a = Math.Min(an, v); b = Math.Max(an, v);
                    break;
                }
            }
            if (a != A[t]) { A[t] = a; Prm.SetT("FreqA", t, a); }
            if (b != B[t]) { B[t] = b; Prm.SetT("FreqB", t, b); }
            _inView.InvalidateVisual(); _outView.InvalidateVisual();
        }

        internal void BandMouseUp() { _drag = DragMode.None; }

        internal void BandMouseWheel(double x, double w, int delta)
        {
            int maxVal = MaxVal;
            int v = EditModel.XToVal(x, maxVal, w);
            int target = -1;
            foreach (int t in Enumerable.Range(0, Tracks).OrderBy(k => k == Selected ? 0 : 1))
            {
                if (A[t] == 0 && B[t] == 0) continue;
                bool inside = A[t] <= B[t] ? (v >= A[t] && v <= B[t]) : (v <= B[t] || v >= A[t]);
                if (inside) { target = t; break; }
            }
            if (target < 0) return;
            Select(target);
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0x40 : 0x400;
            int nv = P.T[P.FxVal].Lim(Val[target] + Math.Sign(delta) * step);
            Val[target] = nv;
            Prm.SetT("Value", target, nv);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !(Keyboard.FocusedElement is TextBox))
            {
                SetOff(Selected);
                e.Handled = true;
            }
        }

        internal void SetOff(int t)
        {
            if (t < 0 || t >= Tracks) return;
            A[t] = 0; B[t] = 0;
            Prm.SetT("FreqA", t, 0);
            Prm.SetT("FreqB", t, 0);
        }
    }

    // ==========================================================================
    // Spectrogram view
    // ==========================================================================
    internal sealed class SpectroView : FrameworkElement
    {
        readonly EditWindow _w;
        readonly bool _output;
        WriteableBitmap _bmp;
        int[] _pix;
        long[] _rowPos;
        int _pw, _ph;
        int[] _xb0, _xb1;     // band range per pixel column

        static readonly Pen PlayPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
        static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1));
        static Pen Freeze(Pen p) { p.Freeze(); return p; }

        public SpectroView(EditWindow w, bool output)
        {
            _w = w; _output = output;
            ClipToBounds = true;
            Cursor = Cursors.Cross;
            Focusable = false;
        }

        void Ensure()
        {
            int pw = Math.Max(2, (int)ActualWidth), ph = Math.Max(2, (int)ActualHeight);
            if (_bmp != null && pw == _pw && ph == _ph) return;
            _pw = pw; _ph = ph;
            _bmp = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgr32, null);
            _pix = new int[pw * ph];
            _rowPos = new long[ph];
            for (int i = 0; i < ph; i++) _rowPos[i] = long.MinValue;
            _xb0 = new int[pw]; _xb1 = new int[pw];
            int nb = SpectrumTap.Bands;
            for (int x = 0; x < pw; x++)
            {
                int b0 = (int)Math.Round((double)x * (nb - 1) / (pw - 1));
                int b1 = (int)Math.Round((double)(x + 1) * (nb - 1) / (pw - 1)) - 1;
                _xb0[x] = Math.Min(nb - 1, b0);
                _xb1[x] = Math.Min(nb - 1, Math.Max(b0, b1));
            }
            int black = unchecked((int)0xFF000000);
            for (int i = 0; i < _pix.Length; i++) _pix[i] = black;
            // v2.0: repaint the recent history at the new size, so a layout change (Out only,
            // Compact, folding the globals) doesn't wipe the picture
            var hist = _w.RecentRows(ph);
            if (hist.Count > 0) Paint(hist);
            else _bmp.WritePixels(new Int32Rect(0, 0, pw, ph), _pix, pw * 4, 0);
        }

        public void AddRows(List<SpectrumTap.Row> rows)
        {
            if (ActualWidth < 2 || ActualHeight < 2) return;
            Ensure();
            Paint(rows);
        }

        void Paint(List<SpectrumTap.Row> rows)
        {
            int k = Math.Min(rows.Count, _ph);
            int start = rows.Count - k;
            // scroll up by k rows
            Array.Copy(_pix, k * _pw, _pix, 0, (_ph - k) * _pw);
            Array.Copy(_rowPos, k, _rowPos, 0, _ph - k);
            float floor = _w.FloorDb;
            for (int r = 0; r < k; r++)
            {
                var row = rows[start + r];
                float[] src = _output ? row.Out : row.In;
                int y = _ph - k + r, o = y * _pw;
                for (int x = 0; x < _pw; x++)
                {
                    float m = 0;
                    for (int b = _xb0[x]; b <= _xb1[x]; b++) if (src[b] > m) m = src[b];
                    _pix[o + x] = EditModel.PowerToPixel(m, floor);
                }
                _rowPos[y] = row.PosAbs;
            }
            _bmp.WritePixels(new Int32Rect(0, 0, _pw, _ph), _pix, _pw * 4, 0);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _bmp = null;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (_bmp == null && w >= 2 && h >= 2) Ensure();
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            if (_bmp != null) dc.DrawImage(_bmp, new Rect(0, 0, _pw, _ph));

            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _w.DrawBands(dc, w, h, _output, ppd);

            // playhead: the row holding the audio now leaving the machine
            if (_rowPos != null)
            {
                long pos = _w.AudiblePos;
                for (int y = _ph - 1; y >= 0; y--)
                {
                    if (_rowPos[y] == long.MinValue) break;
                    if (_rowPos[y] <= pos)
                    {
                        if (y < _ph - 1) dc.DrawLine(PlayPen, new Point(0, y + 0.5), new Point(w, y + 0.5));
                        break;
                    }
                }
            }

            if (_w.HoverX >= 0) dc.DrawLine(HoverPen, new Point(_w.HoverX, 0), new Point(_w.HoverX, h));

            var title = new FormattedText(_output ? "Spec out" : "Spec in", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, ppd);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), null, new Rect(2, 1, title.Width + 6, title.Height + 1));
            dc.DrawText(title, new Point(5, 1));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            CaptureMouse();
            _w.BandMouseDown(e.GetPosition(this).X, ActualWidth);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            double x = Math.Max(0, Math.Min(ActualWidth - 1, e.GetPosition(this).X));
            _w.SetHover(x, _output);
            if (IsMouseCaptured) _w.BandMouseMove(x, ActualWidth);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            _w.BandMouseUp();
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (!IsMouseCaptured) _w.ClearHover();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            _w.BandMouseWheel(e.GetPosition(this).X, ActualWidth, e.Delta);
            e.Handled = true;
        }
    }

    // ==========================================================================
    // Frequency axis between the two spectrograms: one tick per octave (C)
    // ==========================================================================
    internal sealed class FreqAxis : FrameworkElement
    {
        readonly EditWindow _w;
        public FreqAxis(EditWindow w) { _w = w; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), null, new Rect(0, 0, w, h));
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var pen = new Pen(Brushes.Gray, 1);
            int maxVal = _w.MaxVal;
            double lastLabel = -100;
            for (int oct = 0; oct <= 11; oct++)
            {
                int v = oct * 12 * 256;
                if (v > maxVal) break;
                double x = EditModel.ValToX(v, maxVal, w);
                dc.DrawLine(pen, new Point(x, 0), new Point(x, 5));
                dc.DrawLine(pen, new Point(x, h - 5), new Point(x, h));
                float hz = P.FreqHz(v);
                string hzs = hz >= 1000 ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : hz.ToString("0", CultureInfo.InvariantCulture);
                var ft = new FormattedText("c" + oct + " " + hzs, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10, Brushes.LightGray, ppd);
                if (x - lastLabel > ft.Width + 6 && oct > 0)
                {
                    dc.DrawText(ft, new Point(x + 2, (h - ft.Height) / 2));
                    lastLabel = x;
                }
            }
        }
    }

    // ==========================================================================
    // One FX set row
    // ==========================================================================
    // v1.6: colour scale for the spectrograms, Floor (left) to 0 dBFS (right), drawn with the
    // same mapping as the rows (EditModel.PowerToPixel), redrawn when Floor changes
    internal sealed class LevelLegend : FrameworkElement
    {
        readonly EditWindow _w;
        public LevelLegend(EditWindow w)
        {
            _w = w;
            Width = 240; Height = 26;
            Margin = new Thickness(12, 0, 0, 0);
            VerticalAlignment = VerticalAlignment.Center;
            ToolTip = "Spectrogram colours: level per band in dBFS, from the Floor (left) to 0 dBFS (right)";
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, barH = 9, labelW = 34;
            double bw = Math.Max(10, w - labelW);           // room on the right for "dBFS"
            float floor = _w.FloorDb;
            int n = (int)Math.Max(2, Math.Round(bw));
            for (int i = 0; i < n; i++)
            {
                float db = floor - floor * i / (n - 1f);    // floor .. 0
                int px = EditModel.PowerToPixel(EditModel.DbfsToPower(db), floor);
                var brush = new SolidColorBrush(Color.FromRgb((byte)(px >> 16), (byte)(px >> 8), (byte)px));
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(i * bw / n, 0, bw / n + 0.6, barH));
            }
            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(0, 0, bw, barH));
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface("Segoe UI");
            foreach (float db in EditModel.LegendTicks(floor))
            {
                double x = EditModel.LegendFrac(db, floor) * bw;
                dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(x, barH), new Point(x, barH + 3));
                var ft = new FormattedText(EditModel.LegendLabel(db), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 10, Brushes.Black, ppd);
                double lx = Math.Max(0, Math.Min(bw - ft.Width, x - ft.Width / 2));
                dc.DrawText(ft, new Point(lx, barH + 3));
            }
            var unit = new FormattedText("dBFS", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, Brushes.Black, ppd);
            dc.DrawText(unit, new Point(bw + 4, barH + 3));
        }
    }

    internal sealed class SetRow
    {
        static readonly double[] Cols = { 22, 24, 110, 170, 100, 100, 60, 150, 150, 70, 46, 40, 62, 28, 28 };

        readonly EditWindow _w;
        readonly int _t;
        // v2.0: the same controls are placed either in the full one-line row (BuildFull) or in a
        // wrapping detail panel for the selected set (BuildDetail); Panel is whichever was built last
        public System.Windows.Controls.Panel Panel { get; private set; }
        readonly List<(FrameworkElement e, int col)> _cells = new List<(FrameworkElement, int)>();
        readonly Dictionary<FrameworkElement, double> _detailW = new Dictionary<FrameworkElement, double>();
        readonly TextBlock _num;
        readonly Button _off;
        readonly StackPanel _gLfo, _gDepth, _gPhase, _gMix, _gFormant, _gFollow;
        readonly Border _swatch;
        readonly ComboBox _effect;
        readonly Slider _value, _amp;
        readonly TextBlock _ampText;
        readonly TextBox _valueText;   // v1.5: type a value
        readonly TextBox _fa, _fb;
        readonly Slider _soft;
        readonly Button _capture;
        readonly TextBlock _softText;
        // band LFO (v1.3), second line of the row
        readonly ComboBox _lfoTarget, _lfoShape, _lfoRate;
        // v1.4 audition switches
        readonly System.Windows.Controls.Primitives.ToggleButton _mute, _solo;
        readonly Slider _lfoDepth, _lfoPhase;
        readonly TextBlock _lfoDepthText, _lfoPhaseText;
        // v1.8 per-set wet/dry
        readonly Slider _mix;
        readonly TextBlock _mixText;
        // v2.1 PitchShift formant control
        readonly ComboBox _formant;
        // v2.2 note-following
        readonly ComboBox _follow;

        static Grid MakeGrid()
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            foreach (var c in Cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c) });
            // v2.2: the rest of the row, so the second line can run past the fixed columns
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return g;
        }

        static void Add(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

        public static Grid Header()
        {
            var g = MakeGrid();
            string[] h = { "", "#", "Effect", "Value", "", "Amp", "", "FreqA", "FreqB", "Soft", "", "", "", "M", "S" };
            for (int i = 0; i < h.Length; i++)
                Add(g, new TextBlock { Text = h[i], FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 0, 0) }, i);
            return g;
        }

        public SetRow(EditWindow w, int t)
        {
            _w = w; _t = t;

            _swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(EditWindow.SetColour(t)), BorderThickness = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            _swatch.MouseLeftButtonDown += (s, e) => _w.Select(_t);
            Cell(_swatch, 0);
            _num = new TextBlock { Text = t.ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            Cell(_num, 1);

            _effect = new ComboBox { Margin = new Thickness(2, 0, 2, 0) };
            foreach (var n in P.EffectNames) _effect.Items.Add(n);
            _effect.SelectionChanged += (s, e) =>
            {
                if (_w.Syncing || _effect.SelectedIndex < 0) return;
                _w.Select(_t);
                _w.Prm.SetT("Effect", _t, _effect.SelectedIndex);
                _effect.ToolTip = EditModel.EffectHint(_effect.SelectedIndex);
            };
            Cell(_effect, 2);

            _value = new Slider { Minimum = 0, Maximum = 0xA000, SmallChange = 0x40, LargeChange = 0x800,
                IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
            _value.ValueChanged += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                _w.Prm.SetT("Value", _t, (int)Math.Round(_value.Value));
                UpdateValueText();
            };
            Cell(_value, 3);
            _valueText = MakeValueBox();
            Cell(_valueText, 4);

            _amp = new Slider { Minimum = 0, Maximum = 0xE0, SmallChange = 1, LargeChange = 8,
                IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
            _amp.ValueChanged += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                _w.Prm.SetT("Amp", _t, (int)Math.Round(_amp.Value));
                _ampText.Text = _w.Prm.DescT("Amp", (int)Math.Round(_amp.Value));
            };
            _amp.MouseDoubleClick += (s, e) => { _amp.Value = 0xA0; };   // reset to 0 dB
            Cell(_amp, 5);
            _ampText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            Cell(_ampText, 6);

            _fa = MakeFreqBox("FreqA"); Cell(_fa, 7);
            _fb = MakeFreqBox("FreqB"); Cell(_fb, 8);

            _soft = new Slider { Minimum = 0, Maximum = 48, SmallChange = 1, LargeChange = 6, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0),
                ToolTip = "Soft edges: the effect fades in over this many semitones inside each band edge" };
            _soft.ValueChanged += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                int v = (int)Math.Round(_soft.Value);
                _w.Prm.SetT("Soft", _t, v);
                _softText.Text = _w.Prm.DescT("Soft", v);
            };
            Cell(_soft, 9);
            _softText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            Cell(_softText, 10);

            _off = new Button { Content = "Off", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Turn this FX set off (FreqA = FreqB = 0)" };
            _off.Click += (s, e) => _w.SetOff(_t);
            Cell(_off, 11);

            _capture = new Button { Content = "Capture", Margin = new Thickness(2, 0, 2, 0),
                ToolTip = "Freeze: capture a new spectrum. Blur: restart from the live level. (Also a pattern column; lands TickDelay later.)" };
            _capture.Click += (s, e) => { _w.Select(_t); _w.Prm.SetT("Capture", _t, 1); };
            Cell(_capture, 12);

            // ---- Mute / Solo (v1.4) ----
            _mute = MakeToggle("M", "Mute", "Mute: bypass this set (lands TickDelay later). Also a pattern column.");
            Cell(_mute, 13);
            _solo = MakeToggle("S", "Solo", "Solo: while any active set is soloed, only soloed sets run. Mute wins.");
            Cell(_solo, 14);

            // ---- band LFO line (v1.3) ----
            var lfo = new StackPanel { Orientation = Orientation.Horizontal };   // split into groups below
            lfo.Children.Add(new TextBlock { Text = "LFO", Width = 30, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
                ToolTip = "Band LFO, locked to the song position (lands TickDelay later, like every parameter)" });

            _lfoTarget = MakeCombo(P.LfoTargetNames, 70, "LfoTarget", "What the LFO moves: the whole band, one edge, the width, or Value");
            lfo.Children.Add(_lfoTarget);
            _lfoShape = MakeCombo(P.LfoShapeNames, 80, "LfoShape", "LFO shape (S/H = a new random level each cycle)");
            lfo.Children.Add(_lfoShape);
            var rates = new string[P.LfoRateCount];
            for (int i = 0; i < rates.Length; i++) rates[i] = _w.Prm.DescT("LfoRate", i);
            _lfoRate = MakeCombo(rates, 140, "LfoRate", "LFO rate: ticks follow the song position; Hz runs free (v1.7)");
            lfo.Children.Add(_lfoRate);

            lfo.Children.Add(new TextBlock { Text = "Depth", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) });
            _lfoDepth = new Slider { Minimum = 0, Maximum = 200, SmallChange = 1, LargeChange = 10, Width = 110, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "100% = 24 semitones each way (frequencies) or half the Value range (Value)" };
            _lfoDepth.ValueChanged += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                _w.Prm.SetT("LfoDepth", _t, (int)Math.Round(_lfoDepth.Value));
                UpdateLfoText();
            };
            _lfoDepth.MouseDoubleClick += (s, e) => { _lfoDepth.Value = 0; };
            lfo.Children.Add(_lfoDepth);
            _lfoDepthText = new TextBlock { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            lfo.Children.Add(_lfoDepthText);

            lfo.Children.Add(new TextBlock { Text = "Phase", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) });
            _lfoPhase = new Slider { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, Width = 90, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Phase offset: put sets at 90 or 180 degrees to move against each other" };
            _lfoPhase.ValueChanged += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                _w.Prm.SetT("LfoPhase", _t, (int)Math.Round(_lfoPhase.Value));
                UpdateLfoText();
            };
            _lfoPhase.MouseDoubleClick += (s, e) => { _lfoPhase.Value = 0; };
            lfo.Children.Add(_lfoPhase);
            _lfoPhaseText = new TextBlock { Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            lfo.Children.Add(_lfoPhaseText);

            // ---- v1.8: wet/dry Mix (not part of the LFO; it just has room on this line) ----
            lfo.Children.Add(new TextBlock { Text = "Mix", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 2, 0),
                ToolTip = "Wet/dry for this set: 100% = effect only. 50% on PitchShift = original plus shifted copy (a harmony)" });
            _mix = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, Width = 90, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Wet/dry (double-click = 100%)" };
            _mix.ValueChanged += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                int v = (int)Math.Round(_mix.Value);
                _w.Prm.SetT("Mix", _t, v);
                _mixText.Text = EditModel.MixText(v);
            };
            _mix.MouseDoubleClick += (s, e) => { _mix.Value = 100; };
            lfo.Children.Add(_mix);
            _mixText = new TextBlock { MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            lfo.Children.Add(_mixText);

            // ---- v2.1: PitchShift formants ----
            lfo.Children.Add(new TextBlock { Text = "Formant", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0),
                ToolTip = "PitchShift only. off: formants move with the pitch. keep: the voice keeps its character. A shift moves them on their own" });
            var fnames = new string[P.T[P.Formant].Max + 1];
            for (int i = 0; i < fnames.Length; i++) fnames[i] = EditModel.FormantText(i);
            _formant = MakeCombo(fnames, 80, "Formant", "PitchShift formants: off, keep, or a shift of -12..+12 st");
            _formant.MaxDropDownHeight = 320;
            lfo.Children.Add(_formant);

            // ---- v2.2: note-following ----
            lfo.Children.Add(new TextBlock { Text = "Follow", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0),
                ToolTip = "What follows the Note column (and MIDI notes). The band, FreqA or the PitchShift interval is as set for C-4; a note moves it, off puts it back" });
            _follow = MakeCombo(P.FollowNames, 70, "Follow", "Off, Band (both edges), FreqA (Harmonic fundamental), Pitch (PitchShift interval)");
            lfo.Children.Add(_follow);

            // the second line in six groups (LFO, Depth, Phase, Mix, Formant, Follow), so the detail panel can wrap them
            var kids = new List<UIElement>();
            foreach (UIElement k in lfo.Children) kids.Add(k);
            lfo.Children.Clear();
            _gLfo = Group(kids, 0, 4); _gDepth = Group(kids, 4, 3); _gPhase = Group(kids, 7, 3); _gMix = Group(kids, 10, 3); _gFormant = Group(kids, 13, 2); _gFollow = Group(kids, 15, 2);

            // detail-panel widths (the full row's grid columns size these controls instead)
            _detailW[_effect] = 130; _detailW[_value] = 170; _detailW[_valueText] = 110; _detailW[_amp] = 100;
            _detailW[_ampText] = 60; _detailW[_fa] = 150; _detailW[_fb] = 150; _detailW[_soft] = 80;
            _detailW[_softText] = 50; _detailW[_mute] = 28; _detailW[_solo] = 28;
        }

        // ---- v2.0 layouts ------------------------------------------------------------
        void Cell(FrameworkElement e, int col) => _cells.Add((e, col));

        static StackPanel Group(List<UIElement> kids, int from, int n)
        {
            var g = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = from; i < from + n; i++) g.Children.Add(kids[i]);
            return g;
        }

        StackPanel[] Groups => new[] { _gLfo, _gDepth, _gPhase, _gMix, _gFormant, _gFollow };

        static void Detach(FrameworkElement e)
        {
            var p = e.Parent as System.Windows.Controls.Panel;
            if (p != null) p.Children.Remove(e);
        }

        void DetachAll()
        {
            foreach (var c in _cells) Detach(c.e);
            foreach (var g in Groups) Detach(g);
        }

        System.Windows.Controls.Panel Attach(System.Windows.Controls.Panel p)
        {
            Panel = p;
            p.MouseLeftButtonDown += (s, e) => _w.Select(_t);
            UpdateSelection();
            return p;
        }

        /// <summary>Today's layout: one fixed-width line per set plus the LFO/Mix line under it.</summary>
        public System.Windows.Controls.Panel BuildFull()
        {
            DetachAll();
            foreach (var kv in _detailW) kv.Key.Width = double.NaN;
            var g = MakeGrid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var c in _cells) { Grid.SetRow(c.e, 0); Grid.SetColumn(c.e, c.col); g.Children.Add(c.e); }
            // v2.2: the second line spans to the end of the row and wraps if it still doesn't fit.
            // It used to stop at column 12 (about 1060 px), which clipped Formant in v2.1 and hid
            // Follow entirely in v2.2's first build.
            var lfo = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 2) };
            foreach (var grp in Groups) { grp.Margin = new Thickness(0, 0, 0, 1); lfo.Children.Add(grp); }
            Grid.SetRow(lfo, 1); Grid.SetColumn(lfo, 2); Grid.SetColumnSpan(lfo, Cols.Length - 1);
            g.Children.Add(lfo);
            return Attach(g);
        }

        /// <summary>Narrow layout: the selected set's controls in labelled groups that wrap to any width.</summary>
        public System.Windows.Controls.Panel BuildDetail()
        {
            DetachAll();
            foreach (var kv in _detailW) kv.Key.Width = kv.Value;
            foreach (var c in _cells) { Grid.SetRow(c.e, 0); Grid.SetColumn(c.e, 0); }
            var wp = new WrapPanel { Margin = new Thickness(2, 4, 2, 2) };
            wp.Children.Add(Labelled(null, _swatch, _num, _effect));
            wp.Children.Add(Labelled("Value", _value, _valueText));
            wp.Children.Add(Labelled("Amp", _amp, _ampText));
            wp.Children.Add(Labelled("FreqA", _fa));
            wp.Children.Add(Labelled("FreqB", _fb));
            wp.Children.Add(Labelled("Soft", _soft, _softText));
            wp.Children.Add(Labelled(null, _off, _capture, _mute, _solo));
            foreach (var grp in Groups) { grp.Margin = new Thickness(0, 3, 14, 3); wp.Children.Add(grp); }
            return Attach(wp);
        }

        static StackPanel Labelled(string label, params FrameworkElement[] items)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 14, 3) };
            if (label != null)
                sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            foreach (var it in items) sp.Children.Add(it);
            return sp;
        }

        System.Windows.Controls.Primitives.ToggleButton MakeToggle(string text, string param, string tip)
        {
            var b = new System.Windows.Controls.Primitives.ToggleButton { Content = text, Margin = new Thickness(2, 0, 2, 0), ToolTip = tip, Padding = new Thickness(0) };
            b.Click += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                _w.Prm.SetT(param, _t, b.IsChecked == true ? 1 : 0);
                SetToggleLook(b, text, b.IsChecked == true);
            };
            return b;
        }

        // v1.5: a checked toggle shows a solid badge (the theme's checked background would win over
        // Background, so the colour goes in the content): M = red with white, S = amber with black
        internal static void SetToggleLook(System.Windows.Controls.Primitives.ToggleButton b, string text, bool on)
        {
            if (!on) { if (!(b.Content is string)) b.Content = text; return; }
            if (b.Content is Border) return;
            bool mute = text == "M";
            b.Content = new Border
            {
                Background = new SolidColorBrush(mute ? Color.FromRgb(0xC6, 0x28, 0x28) : Color.FromRgb(0xFF, 0xB3, 0x00)),
                CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 0, 4, 0),
                Child = new TextBlock { Text = text, FontWeight = FontWeights.Bold,
                    Foreground = mute ? Brushes.White : Brushes.Black }
            };
        }

        ComboBox MakeCombo(string[] items, double width, string param, string tip)
        {
            var c = new ComboBox { Width = width, Margin = new Thickness(2, 0, 2, 0), ToolTip = tip };
            foreach (var it in items) c.Items.Add(it);
            c.SelectionChanged += (s, e) =>
            {
                if (_w.Syncing || c.SelectedIndex < 0) return;
                _w.Select(_t);
                _w.Prm.SetT(param, _t, c.SelectedIndex);
                UpdateLfoText();
            };
            return c;
        }

        void UpdateLfoText()
        {
            int target = _lfoTarget.SelectedIndex < 0 ? 0 : _lfoTarget.SelectedIndex;
            int depth = (int)Math.Round(_lfoDepth.Value);
            _lfoDepthText.Text = EditModel.LfoDepthText(depth) + "  " + EditModel.LfoSwingText(target, depth);
            string hint = target == P.LfoOff || depth == 0 || _lfoRate.SelectedIndex < 0 ? ""
                : EditModel.LfoSpeedHint(_lfoRate.SelectedIndex, _w.SampsPerTick, _w.SampleRate, _w.Hop);
            _lfoDepthText.ToolTip = hint.Length > 0 ? hint : null;
            _lfoDepthText.Foreground = hint.Length > 0 ? Brushes.DarkOrange : SystemColors.ControlTextBrush;
            if (hint.Length > 0) _lfoDepthText.Text += "  (" + hint + ")";
            _lfoPhaseText.Text = EditModel.LfoPhaseText((int)Math.Round(_lfoPhase.Value));
            bool on = target != P.LfoOff;
            _lfoShape.IsEnabled = _lfoRate.IsEnabled = _lfoDepth.IsEnabled = _lfoPhase.IsEnabled = on;
        }

        void SyncCombo(ComboBox c, string param, int def)
        {
            int v = _w.Prm.GetT(param, _t, def);
            if (!c.IsDropDownOpen && c.SelectedIndex != v && v >= 0 && v < c.Items.Count) c.SelectedIndex = v;
        }

        TextBox MakeFreqBox(string name)
        {
            var tb = new TextBox { Margin = new Thickness(2, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Hz (440, 1.2k), note (c4, f#3:-20), 0x3000, or off" };
            void commit()
            {
                int? v = EditModel.ParseFreq(tb.Text);
                if (v.HasValue)
                {
                    if (name == "FreqA") _w.A[_t] = v.Value; else _w.B[_t] = v.Value;
                    _w.Prm.SetT(name, _t, v.Value);
                }
                tb.Text = _w.Prm.DescT(name, name == "FreqA" ? _w.A[_t] : _w.B[_t]);
            }
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) { commit(); Keyboard.ClearFocus(); e.Handled = true; } };
            tb.LostKeyboardFocus += (s, e) => commit();
            tb.GotKeyboardFocus += (s, e) => { _w.Select(_t); tb.SelectAll(); };
            return tb;
        }

        void UpdateValueText()
        {
            int fx = _effect.SelectedIndex < 0 ? 0 : _effect.SelectedIndex;
            _valueText.ToolTip = "Type a value: " + EditModel.ValueHint(fx) + ", or raw 0x0000..0xA000";
            if (_valueText.IsKeyboardFocusWithin) return;
            _valueText.Text = _w.ValueDesc(fx, (int)Math.Round(_value.Value));
        }

        // v1.5: the Value label is editable; Enter or leaving the box applies it
        TextBox MakeValueBox()
        {
            var tb = new TextBox { Margin = new Thickness(3, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
            Action commit = () =>
            {
                int fx = _effect.SelectedIndex < 0 ? 0 : _effect.SelectedIndex;
                int? v = EditModel.ParseValue(fx, tb.Text, _w.SampleRate);
                if (v.HasValue)
                {
                    _w.Val[_t] = v.Value;
                    _w.Prm.SetT("Value", _t, v.Value);
                    _value.Value = v.Value;
                }
                tb.Text = _w.ValueDesc(fx, (int)Math.Round(_value.Value));
            };
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { commit(); Keyboard.ClearFocus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Keyboard.ClearFocus(); e.Handled = true; }
            };
            tb.LostKeyboardFocus += (s, e) => commit();
            tb.GotKeyboardFocus += (s, e) => { _w.Select(_t); tb.SelectAll(); };
            return tb;
        }

        public void UpdateSelection()
        {
            _swatch.BorderBrush = _w.Selected == _t ? Brushes.Black : Brushes.Transparent;
            if (Panel == null) return;
            bool hi = _w.Selected == _t && Panel is Grid;   // the detail panel is always the selected set
            Panel.Background = hi ? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0xFF)) : Brushes.Transparent;
        }

        public void Sync()
        {
            int fx = P.T[P.Effect].Lim(_w.Fx[_t]);
            if (!_effect.IsDropDownOpen && _effect.SelectedIndex != fx)
            {
                _effect.SelectedIndex = fx;
                _effect.ToolTip = EditModel.EffectHint(fx);
            }
            if (!_value.IsMouseCaptureWithin && Math.Abs(_value.Value - _w.Val[_t]) > 0.5) _value.Value = _w.Val[_t];
            UpdateValueText();
            int amp = _w.Prm.GetT("Amp", _t, 0xA0);
            if (!_amp.IsMouseCaptureWithin && Math.Abs(_amp.Value - amp) > 0.5) _amp.Value = amp;
            _ampText.Text = _w.Prm.DescT("Amp", amp);
            int soft = _w.Soft[_t];
            if (!_soft.IsMouseCaptureWithin && Math.Abs(_soft.Value - soft) > 0.5) _soft.Value = soft;
            _softText.Text = _w.Prm.DescT("Soft", soft);
            _soft.IsEnabled = P.HasSoft(fx);
            int mix = _w.Prm.GetT("Mix", _t, 100);
            if (!_mix.IsMouseCaptureWithin && Math.Abs(_mix.Value - mix) > 0.5) _mix.Value = mix;
            _mixText.Text = EditModel.MixText(mix);
            _mix.IsEnabled = P.HasMix(fx);
            SyncCombo(_formant, "Formant", P.FormantOff);
            _formant.IsEnabled = P.HasFormant(fx);
            SyncCombo(_follow, "Follow", P.FollowOff);
            _capture.IsEnabled = fx == P.Freeze || fx == P.Blur;
            if (!_fa.IsKeyboardFocusWithin) _fa.Text = _w.Prm.DescT("FreqA", _w.A[_t]);
            if (!_fb.IsKeyboardFocusWithin) _fb.Text = _w.Prm.DescT("FreqB", _w.B[_t]);

            bool mute = _w.Mute[_t] == 1, solo = _w.Solo[_t] == 1;
            if (_mute.IsChecked != mute) _mute.IsChecked = mute;
            if (_solo.IsChecked != solo) _solo.IsChecked = solo;
            SetToggleLook(_mute, "M", mute);
            SetToggleLook(_solo, "S", solo);

            SyncCombo(_lfoTarget, "LfoTarget", 0);
            SyncCombo(_lfoShape, "LfoShape", 0);
            SyncCombo(_lfoRate, "LfoRate", 10);
            int depth = _w.Prm.GetT("LfoDepth", _t, 0);
            if (!_lfoDepth.IsMouseCaptureWithin && Math.Abs(_lfoDepth.Value - depth) > 0.5) _lfoDepth.Value = depth;
            int phase = _w.Prm.GetT("LfoPhase", _t, 0);
            if (!_lfoPhase.IsMouseCaptureWithin && Math.Abs(_lfoPhase.Value - phase) > 0.5) _lfoPhase.Value = phase;
            UpdateLfoText();
        }
    }

    // ==========================================================================
    // v2.0: one compact line per FX set, for narrow windows. Click to select (the selected
    // set is shown in full below the list); M and S work from here too.
    // ==========================================================================
    internal sealed class ListRow
    {
        readonly EditWindow _w;
        readonly int _t;
        public readonly Grid Panel;
        readonly Border _swatch;
        readonly TextBlock _fx, _val, _band;
        readonly System.Windows.Controls.Primitives.ToggleButton _mute, _solo;

        static Grid MakeGrid()
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 200 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MaxWidth = 280 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) });   // spare width on wide windows
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            return g;
        }

        static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

        public static Grid Header()
        {
            var g = MakeGrid();
            string[] h = { "", "#", "Effect", "Value", "Band", "", "M", "S" };
            for (int i = 0; i < h.Length; i++)
                Put(g, new TextBlock { Text = h[i], FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 0, 0) }, i);
            return g;
        }

        static TextBlock Cell(double left = 4) => new TextBlock { VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(left, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };

        public ListRow(EditWindow w, int t)
        {
            _w = w; _t = t;
            Panel = MakeGrid();
            Panel.Cursor = Cursors.Hand;
            Panel.ToolTip = "Click to edit this set below";
            _swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(EditWindow.SetColour(t)), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center };
            Put(Panel, _swatch, 0);
            var num = Cell(); num.Text = t.ToString(); Put(Panel, num, 1);
            _fx = Cell(); Put(Panel, _fx, 2);
            _val = Cell(); Put(Panel, _val, 3);
            _band = Cell(); Put(Panel, _band, 4);
            _mute = MakeToggle("M", "Mute"); Put(Panel, _mute, 6);
            _solo = MakeToggle("S", "Solo"); Put(Panel, _solo, 7);
            Panel.MouseLeftButtonDown += (s, e) => _w.Select(_t);
            UpdateSelection();
        }

        System.Windows.Controls.Primitives.ToggleButton MakeToggle(string text, string param)
        {
            var b = new System.Windows.Controls.Primitives.ToggleButton { Content = text, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(0),
                ToolTip = param };
            b.Click += (s, e) =>
            {
                if (_w.Syncing) return;
                _w.Select(_t);
                _w.Prm.SetT(param, _t, b.IsChecked == true ? 1 : 0);
                SetRow.SetToggleLook(b, text, b.IsChecked == true);
            };
            return b;
        }

        public void Sync()
        {
            int fx = P.T[P.Effect].Lim(_w.Fx[_t]);
            bool off = _w.A[_t] == 0 && _w.B[_t] == 0;
            _fx.Text = P.EffectNames[fx];
            _val.Text = off ? "" : _w.ValueDesc(fx, _w.Val[_t]);
            _band.Text = EditModel.BandText(_w.A[_t], _w.B[_t], _w.RoundFreq);
            bool mute = _w.Mute[_t] == 1, solo = _w.Solo[_t] == 1;
            if (_mute.IsChecked != mute) _mute.IsChecked = mute;
            if (_solo.IsChecked != solo) _solo.IsChecked = solo;
            SetRow.SetToggleLook(_mute, "M", mute);
            SetRow.SetToggleLook(_solo, "S", solo);
            Panel.Opacity = off || _w.Silenced(_t) ? 0.55 : 1.0;
        }

        public void UpdateSelection()
        {
            bool sel = _w.Selected == _t;
            _swatch.BorderBrush = sel ? Brushes.Black : Brushes.Transparent;
            Panel.Background = sel ? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0xFF)) : Brushes.Transparent;
        }
    }

    // ==========================================================================
    // Globals
    // ==========================================================================
    internal sealed class GlobalsRow
    {
        readonly EditWindow _w;
        // v2.0: [fold button | controls (wrapping) or the one-line summary]
        public readonly DockPanel Panel = new DockPanel { Margin = new Thickness(4, 2, 4, 2), LastChildFill = true };
        readonly WrapPanel _controls = new WrapPanel();
        readonly List<(string name, Slider s, TextBlock t)> _sliders = new List<(string, Slider, TextBlock)>();
        readonly List<(string name, ComboBox c, int def)> _combos = new List<(string, ComboBox, int)>();

        // v2.0: fold to a one-line summary
        public bool Collapsed { get; private set; }
        readonly Button _fold;
        readonly TextBlock _summary;

        public GlobalsRow(EditWindow w)
        {
            _w = w;
            _fold = new Button { Content = "\u25BE", Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(2, 3, 2, 2),
                VerticalAlignment = VerticalAlignment.Top, ToolTip = "Fold the globals to a one-line summary" };
            _fold.Click += (s, e) => SetCollapsed(!Collapsed);
            DockPanel.SetDock(_fold, Dock.Left);
            Panel.Children.Add(_fold);
            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2, 4, 2),
                TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
            _summary.MouseLeftButtonDown += (s, e) => SetCollapsed(false);
            var fill = new Grid();
            fill.Children.Add(_controls);
            fill.Children.Add(_summary);
            Panel.Children.Add(fill);
            AddSlider("MixBack", 0, 0xA0, 90);
            AddSlider("OutAmp", 0, 0xE0, 90);
            AddSlider("TickDelay", 0, 0x4000, 110);
            AddSlider("BlockLen", 0, 11, 80);
            AddSlider("Overlap", 5, 80, 80);

            AddCombo("Window", new[] { "Blocky (original)", "Smooth" }, 1, 130);
            AddCombo("Stereo", new[] { "Mono", "Stereo", "Wide" }, 1, 80);
            AddCombo("KeepDC", new[] { "Zero (original)", "Keep" }, 1, 120);
            AddCombo("LevelMatch", new[] { "Off", "On (original)" }, 1, 120);
        }

        void AddCombo(string name, string[] items, int def, double width)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 2) };
            sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var c = new ComboBox { Width = width };
            foreach (var n in items) c.Items.Add(n);
            c.SelectionChanged += (s, e) => { if (!_w.Syncing && c.SelectedIndex >= 0) _w.Prm.SetG(name, c.SelectedIndex); };
            sp.Children.Add(c);
            _controls.Children.Add(sp);
            _combos.Add((name, c, def));
        }

        void AddSlider(string name, int min, int max, double width)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 2) };
            sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var s = new Slider { Minimum = min, Maximum = max, Width = width, IsMoveToPointEnabled = true,
                SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 16), VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Width = 58, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            s.ValueChanged += (o, e) =>
            {
                if (_w.Syncing) return;
                int v = (int)Math.Round(s.Value);
                _w.Prm.SetG(name, v);
                t.Text = _w.Prm.DescG(name, v);
            };
            sp.Children.Add(s); sp.Children.Add(t);
            _controls.Children.Add(sp);
            _sliders.Add((name, s, t));
        }

        public void SetCollapsed(bool c)
        {
            Collapsed = c;
            _controls.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
            _summary.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
            _fold.Content = c ? "\u25B8" : "\u25BE";
            _fold.ToolTip = c ? "Show all the global controls" : "Fold the globals to a one-line summary";
            if (c) UpdateSummary();
        }

        void UpdateSummary()
        {
            var parts = new List<string>();
            foreach (var (name, s, t) in _sliders) parts.Add(name + " " + t.Text);
            foreach (var (name, c, def) in _combos) if (c.SelectedItem != null) parts.Add(name + " " + c.SelectedItem);
            _summary.Text = string.Join("  \u00B7  ", parts);
            _summary.ToolTip = string.Join("\n", parts) + "\n(click to show the controls)";
        }

        public void Sync()
        {
            foreach (var (name, s, t) in _sliders)
            {
                int v = _w.Prm.GetG(name, (int)s.Value);
                if (!s.IsMouseCaptureWithin && Math.Abs(s.Value - v) > 0.5) s.Value = v;
                t.Text = _w.Prm.DescG(name, v);
            }
            foreach (var (name, c, def) in _combos)
            {
                int v = _w.Prm.GetG(name, def);
                if (!c.IsDropDownOpen && c.SelectedIndex != v && v >= 0 && v < c.Items.Count) c.SelectedIndex = v;
            }
            if (Collapsed) UpdateSummary();
        }
    }
}
