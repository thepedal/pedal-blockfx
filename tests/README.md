# Pedal BlockFX tests (headless, Linux)

`run.sh` does four things:

1. Builds `ref/`, the **original DT BlockFx DSP**, almost unchanged, with shims:
   - MFC, the dialog and the Buzz callbacks are stubbed.
   - `rfftw_*` maps to FFTW 3 `R2HC`/`HC2R`, which uses the same halfcomplex layout,
     unnormalised.
   - Template code that MSVC 6 accepted is made standard C++. Functors are passed by
     value, and `vector::end()` comparisons are replaced with pointers.
   - Two deliberate patches make the reference match the port's intentional changes:
     the Smear seed (the original read uninitialised memory) and the CopyHarm `nb`
     fix. Search for `set_index` and `stop_bin - (beg+na)`.
2. Builds the C# engine harness, the machine against SDK stubs, and a stereo test.
3. Checks the FFT against a direct DFT.
4. Runs 31 parameter scenarios through both implementations and prints SNR and max
   difference per scenario, plus a tail check. Expect 110 dB or more everywhere.

The behaviour tests that follow cover the appended features: KeepDC, Stereo/Wide,
effects 9–14 and Soft (`NewFxTest.cs`), the Freeze tail, v1.1 (`V11Test.cs`), v1.2
PitchShift (`PitchTest.cs`: pitch accuracy in cents, level, band/inverted range, Amp,
Soft, Blocky mode, glides, finiteness), Edit window prefs and the level legend (`PrefsTest.cs`: parse/format, fitting saved
bounds to the screens, the file, legend ticks and colours; v2.0: view options round trip, the
full-rows or list-and-detail choice with its hysteresis, legend visibility, the list's band text), Value as text and the LFO
overlay (`ValueTextTest.cs`: every effect's label parses back
to itself, typed forms, edge travel, live edges from the engine), Mute/Solo (`SoloMuteTest.cs`: rules, timing, state kept
across a mute), the v1.3 band LFO (`LfoTest.cs`: timing against
the song position through TickDelay, mid-song start, phase, song loops, stopped
transport, every target, S/H determinism, the machine reading `PlayPosition`; v1.7: Hz rates
running free of the transport and song jumps, block averaging centred and anti-aliased,
rate labels and the speed hint), the v1.8 per-set Mix (`MixTest.cs`: PitchShift harmony
levels, per-bin blend with and without Soft, effects without Mix, automation, labels), the
v2.1 PitchShift formant control (`FormantTest.cs`: a synthetic vowel shifted ±12 and +7 st at
110, 150 and 220 Hz must follow its original envelope with Keep; a pure formant shift; Keep at
0 st and Formant on other effects bit-identical; Mix; robustness; labels), and v2.2 note-following
(`NoteTest.cs`: Band, FreqA and Pitch modes, TickDelay timing, hold and off, C-4 = no change, no flip
when FreqA passes FreqB, the LFO on top, MIDI with last-note priority through the machine,
labels), and the preset bank (`PresetTest.cs`). `mono fm.exe 150 -12 25` prints one case's harmonic levels.

Include names in `ref/` match the files' case, so the reference also builds on a
case-sensitive filesystem.

`cs_syntax.py` syntax-checks every source file with tree-sitter's C# grammar
(`pip install tree-sitter tree-sitter-c-sharp`). Mono's `mcs` can't parse
`EditWindow.cs` (local functions, tuples) and WPF doesn't exist on Linux, so this is the
only check the Edit window gets before the Windows build. It catches syntax only, not
type or definite-assignment errors.

Both harnesses read the same scenario format: one line per change,
`tick kind index set value`. `kind` is 0 for a global, where `index` is the global's
slot; it is 1 for a track parameter, where `index` is the FX set and `set` is the
parameter slot. Tick 0 starts from the defaults, and every later tick starts from
NoValue, exactly as Buzz delivered parameters to native machines.
