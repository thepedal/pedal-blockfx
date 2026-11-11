# Pedal BlockFX

A ReBuzz managed machine built on **DT Block Effects** (DT BlkFx), Darrell Tam's block-FFT
spectral effect for Buzz (2003–04). Free software under the GPL v3 or later; see
[Credits and licence](#credits-and-licence). **Version 2.2.0**: the original's audio engine, extended with Smooth mode,
soft band edges, seven new effects (including a pitch shifter with formant control), a band LFO,
note-following bands,
Mute/Solo and a wet/dry Mix per set, a sidechain input and a preset bank. The Edit window
opens from the machine's right-click menu (**Edit...**).

## What it does

It runs a block FFT on the input, applies up to 16 spectral FX sets, and resynthesises
the result. In **Blocky** mode (the original) it uses no analysis window, and blocks are
joined with a linear crossfade over the overlap, which is a large part of the original's
grainy sound. **Smooth** mode (the default) uses windowed overlap-add instead.

Each track is one **FX set**: `FreqA`, `FreqB`, `Amp`, `Effect`, `Value`. A set is off
while both frequencies are 0. If `FreqA > FreqB`, the set processes everything
*outside* the range.

| # | Effect | Value |
|---|---|---|
| 0 | Contrast | below 0.5 flattens, above 0.5 sharpens, power-matched |
| 1 | Smear | phase randomisation amount |
| 2 | Clip | magnitude ceiling |
| 3 | Weed | above 0.5 removes weak bins, below 0.5 removes strong bins |
| 4 | ShiftAdd | frequency shift, mixed with the original |
| 5 | ShiftReplace | frequency shift, replacing the original |
| 6 | Harmonic | type (both/odd/even) × width; FreqA is the fundamental |
| 7 | AutoHarm | as Harmonic, using the loudest bin in the range as the fundamental |
| 8 | CopyHarm | copies the FreqA harmonic up the spectrum |
| 9 | Freeze | raising Value from 0 captures the spectrum; Value sets how much of the held sound replaces the live one. It keeps sounding after the input stops |
| 10 | Gate | spectral noise gate: bins below −100..0 dBFS are pushed down |
| 11 | Blur | each bin's level is smoothed over time, 5 ms .. 2 s |
| 12 | Tilt | −12..+12 dB per octave around the band's centre |
| 13 | RoboWhisper | below 50% flattens phase (robot), above 50% randomises it (whisper) |
| 14 | Cross | the band takes the **Sidechain** input's spectrum shape (0..100%) |
| 15 | PitchShift | the band moves by −24..+24 semitones (50% = no shift); a true pitch shift by ratio, so harmonics stay harmonic |

Effects 0–8 are the original's. The original nine are level-matched: the overall output
power is restored to the input's, so Amp only rebalances bands. The appended effects'
own gain changes are real: Gate makes things quieter, and Tilt can boost by up to
+36 dB, so watch for clipping.

**PitchShift** is a peak-locked phase vocoder: each spectral peak moves, with its
neighbouring bins, to its frequency × the ratio, and its phase keeps running at the new
frequency. Unlike ShiftAdd/ShiftReplace (a fixed Hz offset, which makes harmonics
inharmonic), chords and harmonics keep their intervals. The band's content is removed and
the shifted copy is added wherever it lands, so shifting a band out of its range sums
with what is already there. For harmonies, turn the set's **Mix** down (v1.8; 50% =
original plus shifted copy), or mix in the dry signal globally with MixBack. It works
best in **Smooth** mode with **Overlap 75%**; larger blocks suit bass and sustained
material, smaller blocks keep drums tighter. In Blocky mode it still shifts, but rougher.
Its **Formant** setting (v2.1) keeps a voice's character while the pitch moves, or moves
the formants on their own.

**Inputs.** Input 1 is **Main** and input 2 is **Sidechain**. Connect a machine,
then use the connection's channel menu to pick input 2. Only Cross uses the sidechain.
Without one, Cross passes Main through.

**Capture** (per set, a pattern-only trigger column, plus a button in the Edit window).
For Freeze it captures a new spectrum, so the held sound can change in time with the
song. For Blur it restarts the smoothing from the current level. Like every parameter,
it takes effect TickDelay later, lined up with the audio it belongs to.

**Soft** (per set, in semitones) fades the effect in from each band edge, so it
doesn't cut off at a hard boundary. It applies to the per-bin effects (Contrast,
Smear, Clip, Weed, and 9–14), and to PitchShift, where a bin near an edge moves only part
of its content. ShiftAdd/ShiftReplace and the harmonic effects move content across the
band edges, so Soft doesn't apply to them.

## Edit window

The layout follows the original dialog. "Spec in" sits above "Spec out", with
log-frequency across (C0 to Nyquist, octave ticks between the two) and time scrolling
upward. The colour map is the original's, and Floor sets the bottom of the range in
dBFS. Additions:

- **FX set bands.** Every active set is a coloured band over both spectrograms. A set
  with `FreqA > FreqB` shades everything outside its range.
  - Drag an **edge** to move FreqA or FreqB.
  - Drag **inside** a band to move the whole range.
  - Drag on **empty space** to draw a new range for the selected set. Ctrl-drag does
    the same over an existing band.
  - **Shift** snaps to semitones.
  - The **mouse wheel** over a band changes its Value (Shift for fine steps).
  - **Delete** turns the selected set off.
- **Playhead.** A dashed white line marks the audio now leaving the machine. The
  spectrograms show input TickDelay ahead of the output, so you can see what's coming.
- **Hover readout.** Shows frequency, note, raw parameter value, and the input and
  output level at that frequency.
- **Set panel.** One row per track: Effect, Value with the original's per-effect
  description (Contrast/Weed ±%, Smear/Clip %, shift in Hz, harmonic type + width),
  Amp (double-click for 0 dB), and FreqA/FreqB as text. The text boxes accept `440`,
  `1.2k`, `c4`, `f#3:-20`, `0x3000` or `off`. The globals row sits above it.

Every edit goes through ReBuzz's own parameters, so the parameter window, pattern
recording, undo and saving all stay in sync. The spectrum capture only runs while the
window is open.

## Parameters

The globals are in the original's order: `BlkSync` and `InterpMode` (pattern-only,
stateless), then `MixBack`, `OutAmp`, `TickDelay`, `BlockLen` and `Overlap`. One
parameter that isn't in the original is appended after them:

- **Stereo** (default Stereo):
  - **Mono**: sums L+R, exactly like the original mono machine.
  - **Stereo**: processes L and R with identical random sequences, so a mono source
    stays mono.
  - **Wide**: gives the right channel its own Smear sequence, which widens the image.

- **Window** (default Smooth): Blocky is the original, with unwindowed blocks joined by
  a linear crossfade; it's grainier, which is part of its character. Smooth uses
  windowed overlap-add, which gives cleaner effects. It reconstructs the input exactly
  when no effect is active, and keeps the hop at no more than half a block. Latency is
  TickDelay in both modes, and longer blocks still need a longer TickDelay.
- **LevelMatch** (default On, the original): after the effects, the output is matched
  back to the input level, so a set's Amp only rebalances bands, and a set covering the
  whole spectrum has no effect on level. **Off** makes Amp and the effects' level
  changes absolute: −6 dB means −6 dB.
- **KeepDC** (default Keep): the original zeroed the lowest FFT bin on every block. At
  small block sizes that removes real bass: everything below about 190 Hz at
  128-sample blocks. Keep passes the bin through, and a 5 Hz DC blocker on the output
  removes true offset. Zero (original) restores the original behaviour.

The track parameters use the original names, ranges and defaults, so the raw pattern
values mean the same as in the original. Value labels follow the original's format,
for example `0Hz,-`, `129Hz,c3:-25`, `4.00` and `4096`.

### Band LFO (v1.3, Hz rates v1.7)

Each set has its own LFO, appended after Capture. With a rate in ticks it is **locked
to the song position**: the phase comes from where the song is, not from when you
pressed play, so starting mid-song, looping and rendering always line up. It holds
still while the transport is stopped, and it lands TickDelay later like every other
parameter. With a rate in **Hz** (v1.7) it runs free instead: same speed at any tempo,
and it keeps moving while the transport is stopped.

- **LfoTarget**: Off, **Band** (moves both edges, so the band slides), **FreqA**,
  **FreqB**, **Width** (edges move apart and together), or **Value** (the effect's
  amount: Gate threshold, PitchShift interval, Smear amount, and so on). The LFO never
  flips a set between inside and outside the range: edges that cross meet in the
  middle.
- **LfoShape**: Sine, Triangle, Saw up, Saw down, Square, S/H (a new random level each
  cycle, the same on every play).
- **LfoRate**: values 0–18 are periods from 1/4 tick to 256 ticks (default 16 ticks);
  the label shows beats too. Values 19–34 (v1.7) are free-running rates: 0.05, 0.1,
  0.15, 0.2, 0.25, 0.33, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8 and 12 Hz.
- **LfoDepth**: 0–100% in 0.5% steps. 100% swings frequencies 24 semitones each way,
  and Value by half its range each way. The Edit window shows the swing in the
  target's own units.
- **LfoPhase**: offset, 0–360°. Two sets at 180° move against each other.

Each block gets one LFO value: since v1.7, the LFO averaged over that block (centred,
so it no longer lags half a block). A rate too fast for the block size is smoothed
away rather than turning into a slow, wrong wobble. For fast modulation use a smaller
BlockLen or more Overlap; the Edit window shows an orange hint on the LFO line when
the rate is too fast for the current blocks.

### Mute and Solo (v1.4)

Two switches per set, appended after the LFO, for auditioning: **M** and **S** in the
Edit window, and pattern columns too.

- **Mute** bypasses the set.
- **Solo**: while any active set is soloed, only the soloed sets run.
- **Mute wins over Solo**, and a Solo on a set that's off (no frequencies) is ignored,
  so a forgotten Solo can't silence everything.
- Both land TickDelay later, like every parameter, and save with the song. Muting
  doesn't reset a set: a muted Freeze still holds its spectrum when unmuted.
- Muted and not-soloed sets are drawn faded on the spectrograms and labelled.

### Mix (v1.8)

A wet/dry control per set, appended after Solo (default 100% = effect only). It works on
the effects that take Soft (Contrast, Smear, Clip, Weed and 9–15, including PitchShift)
and uses the same crossfade, so it combines with Soft. For the shift and harmonic effects
(4–8) it's greyed out.

- **PitchShift at 50%**: the original plus the shifted copy, at −6 dB each: a harmony from
  one set. Unlike MixBack it's per set, so other sets stay fully wet.
- **Per-bin effects**: parallel processing, for example heavy Smear at 35% adds air
  without losing the attack.
- It's a linear crossfade of the same bins: 50% puts each part at half amplitude
  (about −3 dB overall for unrelated material). Presets 36–38 add +3 dB OutAmp for that.
- In the Edit window it's the slider at the end of each set's second line.

### Formant (v2.1)

A per-set setting for **PitchShift**, appended after Mix (default **off**). A pitch shift
moves every partial, and with them the spectrum's broad shape, the *formants*: the
resonances that make a voice sound like a vowel, and like a particular person. Moved up an
octave, a voice sounds like a chipmunk; moved down, boomy and huge. Formant puts that
shape back:

- **off** (0): formants move with the pitch, as before v2.1.
- **keep** (25, hex 19): the formants stay where they were. An octave up still sounds like
  the same singer, only higher.
- **−12..+12 st** (1–49, 0.5 st steps): the formants move by that amount, on their own. With
  no pitch shift (Value at 0 st) that's a pure formant shift: up makes a voice smaller and
  brighter, down makes it bigger and darker, on the same note.

How it works: each block, the machine estimates the spectral envelope (a "true envelope":
cepstral smoothing that rides over the harmonics), then scales every moved peak by the
envelope at its new place ÷ the envelope where it came from. It costs roughly two thirds
of a plain PitchShift again (about 5% of a core instead of 3% for a stereo 4096-sample
set at Overlap 75%), and only while Formant is on.

Limits to expect:
- It's tuned for voices up to about 400 Hz. The higher the pitch, the more sparsely the
  harmonics sample the formants, so a high voice gets partial correction.
- The envelope is read from each block, so it follows the material; on chords or full
  mixes it keeps the overall tone colour rather than any one voice's formants.
- Formant is read per tick and not glided, so switching it on never sweeps through other
  shifts.
- It's greyed out for every effect except PitchShift.

In the Edit window it's the drop-down at the end of each set's second line.

**Also in v2.1: cleaner downward shifts.** Shifting down packs the moved peaks closer
together than their own width. Until 2.0 the overlapping copies summed with different
phases and cancelled, so harmonic-rich sounds (voices, saws) lost harmonics, unevenly,
when shifted down. Each moved peak now only fills the bins up to halfway to its
neighbours. Upward shifts are unchanged.

### Note-following (v2.2)

A set can follow the notes you play, from its own **Note** column in the pattern or from a
MIDI keyboard. Two appended track parameters:

- **Note** (a note column, like a generator's): the note to follow. `off` sends the set
  back where it was. Notes are events: a note holds until the next note or `off`.
- **Follow** (default Off): what the note moves.
  - **Band**: both edges move by the note's distance from **C-4**. Set the band up as if
    C-4 were playing (a band-pass around 262 Hz, a notch on C-4's second harmonic at
    523 Hz…), then E-4 moves it up four semitones, C-3 down an octave.
  - **FreqA**: only FreqA moves. Meant for **Harmonic**, **AutoHarm** and **CopyHarm**, whose
    FreqA is the fundamental: set FreqA to C-4 (`c4` in the box, 262 Hz) and the harmonic
    series follows the melody while FreqB, the top of the range, stays put.
  - **Pitch**: **PitchShift**'s interval moves by the note's distance from C-4, added to
    Value. With Value at 0 st, C-4 is unison, E-4 a third up and G-3 a fourth down, so the
    pattern writes the harmony line. The total is limited to ±24 st.

C-4 is the reference for all three, so a set always sounds exactly as set when C-4 is
playing (or no note at all). Notes land **TickDelay later**, like every parameter. The band
LFO swings around the followed position. As with the LFO, edges that would cross meet in
the middle instead of flipping the set between inside and outside, and they stay in range.

**MIDI.** Notes from a keyboard steer every set whose Follow is on, as if typed into each
of those tracks: the latest key wins, and releasing it sends `off`. They aren't recorded
into the pattern. ReBuzz numbers MIDI notes like the pattern editor does (MIDI 60 = C-5).

**Edit window.** A **Follow** drop-down at the end of each set's second line. While a note
moves a band, the band is drawn where it is now, with the set's own (C-4) edges as thin
dotted lines, and its label shows the note (for example `0 Harmonic ♪ E-4`).

### Display options (v2.0)

The Edit window adapts to its size:

- **Wide (about 1360 px and up; 1290 before v2.2):** every set has its full row, as before.
- **Narrower:** the sets become a compact list (colour, number, effect, value, band,
  M and S), and the **selected set** is shown in full underneath, with its controls in
  labelled groups that wrap to the window width. Click a list line, or a band on the
  spectrogram, to choose which set is shown. M and S work straight from the list.
- The level legend steps aside below about 1000 px, and long status text is cut with
  an ellipsis instead of pushing the controls off the edge.
- The set area may take up to half the window height (it was fixed at 330 px), so tall
  windows show more sets.

Three options, remembered with the window size and position:

- **In + Out / Out only / In only** (status bar): one large spectrogram instead of two.
- **Globals fold** (the small arrow at the start of the globals row): folds MixBack,
  OutAmp, TickDelay and the rest to a one-line summary. Click the summary to unfold.
- **Compact** (status bar): Out only, globals folded, list plus detail, legend hidden,
  for a small window beside the song. Click again to restore the view you had.

### Edit window details (v1.5)

- **Bands follow the LFO.** When an LFO moves a set's edges, the band is drawn where
  it is right now, each moving edge's full travel is shaded faintly, and the set's own
  edges (the ones you drag) stay as thin dotted lines.
- **Type a Value.** The Value label beside the slider is a text box. Type in the
  effect's own units and press Enter: `+7` or `-12 st` (PitchShift), `-40` (Gate
  dBFS), `250 ms` or `1.5 s` (Blur), `+4` (Tilt dB/oct), `odd 50` (Harmonic),
  `robot 100`, `hold 55%`, `+150 Hz`, `50` (percent effects), or a raw `0x5000`.
  Anything the label shows can be typed back. The tooltip lists the forms for the
  current effect, and Esc cancels.
- **M and S** show a solid red or amber badge while on.
- **Level legend (v1.6)** next to Floor: the spectrogram colours from the Floor
  (black) to 0 dBFS (red), with dB marks. It follows the Floor setting.
- **The window remembers itself (v1.6):** size, position, maximised or not, and Floor
  are saved when it closes and restored when it opens. They're stored per user, in
  `%APPDATA%\Pedal BlockFX\EditWindow.txt`, not in the song. A position on a monitor
  that's no longer there is moved back onto the screen. Delete the file to reset.

## Latency and delay compensation

The machine reports its latency (TickDelay in samples) to ReBuzz. Turn on
**Machine Delay Compensation** in ReBuzz's engine settings, and other paths that
meet BlockFX's output at a later machine (such as Master) are delayed to match, so
parallel parts stay in time. Whenever TickDelay or the tempo changes, the new value is
passed on once it has held for a quarter second. ReBuzz rebuilds its compensation
delay buffers at that point, so expect a small click if you change it during playback.

## Behaviour carried over from the original

- **Latency equals TickDelay** (default 4 ticks). The delay is deliberate: it lets the
  machine interpolate parameters ahead of the audio they apply to. See the latency
  section above for compensation.
- **BlockLen is capped by the delay**: the effective block is at most about 2× TickDelay.
  The BlockLen label shows the effective size.
- **The DC bin is zeroed on every block** when KeepDC is set to Zero (original), even
  with no FX set active. This acts as a low cut below about half a bin width.
- **Output is clamped to ±32768**, Buzz scale.
- **The tail plays out after the input stops**, then the machine goes idle.

## Changes from the original

1. **Smear seed.** The original never initialised Smear's random sequence. Here it is
   seeded per set, deterministically.
2. **CopyHarm.** When a copy crossed the stop bin, the original computed a negative length
   for its falling half and dropped it; here it is truncated instead. CopyHarm's missing
   power accounting, which makes it louder, is left as in the original.
3. **InterpMode 2 extrapolation.** This used to wrap around in the byte or word value;
   now it is clamped to the parameter's range.
4. **Sample rate.** The output buffer and the shift range now scale with the actual
   sample rate. The original hard-coded 44.1 kHz; behaviour at 44.1 kHz is unchanged.

## Verification

`tests/` contains a reference build of the **original C++ DSP**, with MFC and the GUI
stubbed out and FFTW 3 standing in for FFTW 2 (it uses the same halfcomplex layout). It
also contains a C# harness that runs both on the same input and parameter scenarios.
Across 31 scenarios (every effect at two values, inverted ranges, multiple sets,
MixBack, BlockLen 0/3/11, Overlap 5/80, automation, BlkSync, TickDelay changes,
InterpMode off, and the tail), the port matches the original at **110–138 dB SNR**,
which is float-rounding level. See `tests/README.md`.

## Presets

The 44 presets in `Pedal BlockFX.prs.xml` deploy next to the DLL and appear in the
machine's preset menu straight away. Each uses up to 4 FX sets. If a preset turns on a set
the machine has no track for, the machine adds the tracks itself (v1.1). Each
preset sets all 4 sets, plus LevelMatch, so loading one also clears whatever the
previous preset left behind. The bundle is generated by
`gen_presets.py`, which isn't deployed. `--check` verifies that its parameter order
matches the machine's declarations; ReBuzz applies multi-track presets by matching
parameters in order. Edit the script rather than the XML.

| Preset | What it does |
|---|---|
| 01 Init (clean) | Smooth mode, no FX sets: transparent apart from the TickDelay latency. |
| 02 DT Classic Grain | The original's sound: Blocky mode, Smear on the mids, a little Contrast on top. |
| 03 DT Spectral Clip | Blocky Clip across the spectrum: flattens loud partials into a buzzy, compressed tone. |
| 04 Sharpen Partials | Contrast +60%: strong partials get stronger, the noise between them recedes. |
| 05 Flatten to Noise | Contrast -60%: evens the spectrum out towards a breathy, noisy version of the input. |
| 06 Tonal Only | Weed +40%: removes weak bins, leaving the tonal skeleton. Good on noisy material. |
| 07 Noise Only | Weed -40%: removes the strongest bins, leaving the residue and air. |
| 08 Freeze Pad | Freeze holds the spectrum at load (or when Value rises from 0) and keeps sounding after the input stops. Re-trigger: set Value to 0, then back up. |
| 09 Freeze Blend | Half frozen, half live: the held chord sits under whatever plays next. |
| 10 Bass Freeze, Live Top | Freezes only the low end (below 250 Hz) for a drone floor, the rest stays live. |
| 11 Long Blur Wash | 2 s per-bin smoothing: transients dissolve into a slow-moving wash. |
| 12 Shimmer Highs | Blur and a little lift above 1 kHz: the top end trails and glows, the body stays tight. |
| 13 Smear Cloud | Smooth-mode Smear plus a medium Blur: diffuse and reverb-like without a tail. |
| 14 Noise Floor Cleaner | Spectral gate at -70 dBFS per bin: removes hiss and low-level hash between notes. |
| 15 Gated Sparkle | Hard gate on the highs only: just the loudest top partials get through, glittery and sparse. |
| 16 Dark Tilt | -5 dB/oct above 200 Hz: warmer and duller without a filter's resonance. |
| 17 Air Tilt | +4 dB/oct above 1 kHz: adds air. Watch the level on bright sources. |
| 18 Robot | Blocky mode, 1024-sample blocks, phase flattened: the classic robot buzz (pitch set by the hop). |
| 19 Whisper | Phase randomised over the voice range: pitched material turns into breath. |
| 20 Metallic Shift | ShiftAdd of about +150 Hz (at 44.1 kHz) on the mids: inharmonic, bell-like doubling. |
| 21 Harmonic Glow | AutoHarm boosts the harmonic series of the loudest partial: richer, more focused tone. |
| 22 Odd Harmonics Hollow | Harmonic, odd series of 110 Hz cut by 12 dB: a hollow, clarinet-like colour on A-based material. |
| 23 Cross Vocoder | Connect a second machine to input 2 (Sidechain). Main takes on the sidechain's spectral shape: pad on Main, drums or voice on the sidechain. |
| 24 Cross Half | 50% cross-synthesis: Main keeps half its own character. Needs the sidechain connected. |
| 25 Octave Up | PitchShift +12 semitones over the whole range. Smooth mode, Overlap 75%. |
| 26 Octave Down | PitchShift -12 semitones; 8192-sample blocks keep bass notes clean. |
| 27 Fifth Harmony | Up a fifth (+7) mixed 50/50 with the dry signal (MixBack): instant parallel harmony. |
| 28 Detune Double | +15 cents mixed with the dry signal: a chorus-like doubling without modulation. |
| 29 Band-Pass Sweep | Everything outside a two-octave band is removed, and the band sweeps up and down an octave every 32 ticks. LevelMatch off, so it gets quieter where the band finds little. |
| 30 Twin Notch Sweep | Two narrow notches sweeping in opposite directions (180 degrees apart): a phaser-like swirl. |
| 31 Tempo Vibrato | PitchShift with its Value wobbling about a quarter-semitone every 1.5 ticks: vibrato in time with the song. |
| 32 Square Gate Chop | The spectral gate's threshold jumps between -10 and -70 dBFS every 2 ticks: rhythmic chopping. |
| 33 S/H Smear Hop | An octave-wide Smear band that jumps to a random place every 2 ticks. |
| 34 Free Drift | Two notches drifting at 0.15 and 0.1 Hz, free-running, so they never line up the same way twice: slow movement for pads. |
| 35 Free Chop 6 Hz | The spectral gate's threshold jumps at 6 Hz whatever the tempo. Small blocks, Overlap 75%. |
| 36 Octave Harmony | PitchShift +12 with the set's Mix at 50%: the original plus an octave above, from one set. |
| 37 Fifth Below Blend | PitchShift down a fifth at Mix 40%: a darker parallel voice under the original. |
| 38 Parallel Smear | Heavy Smear at Mix 35%: adds breath and air while the original keeps its pitch and attack. |
| 39 Natural Octave Up | PitchShift +12 with Formant keep: an octave up without the chipmunk. Best on voices below about 200 Hz. |
| 40 Natural Harmony | Up a fifth at Mix 50% with Formant keep: a harmony that sounds like the same singer. |
| 41 Formant Up | No pitch change, formants up 4 semitones: a smaller, brighter voice on the same note. |
| 42 Melody Band-Pass | A band-pass from G#3 to E4, set for C-4, that jumps to each note played in the Note column: the input sings the melody. LevelMatch off. |
| 43 Harmonic Follow | Harmonic boosts the harmonic series of the note played (FreqA follows): the input resonates in the key of the pattern. |
| 44 Following Harmony | PitchShift at Mix 50% with Formant keep; the Note column sets the interval (C-4 = unison, E-4 = a third up, G-3 = a fourth down). |

## Credits and licence

### Darrell Tam and DT Block Effects

This machine would not exist without **Darrell Tam**. In 2003 he wrote **DT Block Effects**
for Jeskola Buzz: a block-FFT effect that let you reach into the spectrum and contrast,
smear, clip, weed, shift and harmonise it, band by band, in time with the song. He shipped
it with its full source and an invitation to modify it and experiment. In 2005 he ported it
to VST as DtBlkFx, which he released under the GNU GPL.

Pedal BlockFX started in 2026 as a faithful port of his Buzz engine to ReBuzz. The port is
checked against his original C++ to 110 dB SNR or better (see Verification), and his
design choices are kept on purpose (see Behaviour carried over from the original).
Everything since then (Smooth mode, the new effects, the pitch shifter, the LFO, Mix, the
sidechain, the presets and the adaptive Edit window) builds on that foundation. The aim is
to carry his idea forward for people making music with Buzz and ReBuzz today.

- DT Block Effects 1.1 on the Buzz machine archive:
  <https://buzz.robotplanet.dk/machineinfo.php?id=7594>
- Darrell Tam: original author of DT Block Effects (Buzz, 2003–04) and DtBlkFx (VST, 2005–06).

The original's FFTW 2 dependency is not used: Pedal BlockFX has its own FFT (`RealFft.cs`).

### Licence

Copyright (C) 2026 Pedal. Portions are derived from DT Block Effects,
Copyright (C) 2003–2004 Darrell Tam.

Darrell Tam gave his permission in November 2026 for DT Block Effects to be carried forward
this way, and for this port, and the copy of his original source in `tests/ref`, to be
released under the GNU GPL v3 or later. Thank you, Darrell.

This program is free software: you can redistribute it and/or modify it under the terms
of the GNU General Public License as published by the Free Software Foundation, either
version 3 of the License, or (at your option) any later version. It is distributed in the
hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the full
text.

### Third-party material

- `tests/ref/src` and `tests/ref/shim/cplx.h`, `SinCosTable.h`: Darrell Tam's original
  DT Block Effects source (© 2003–2004 Darrell Tam), under the GPL v3 or later with his
  permission. It is kept, lightly modified so it builds with g++, as the reference the
  tests compare against, and is not part of the built machine. Each file says so at the
  top; the modifications are listed in `tests/README.md`.
- `tests/ref/shim/MachineInterface.h` and the other shims are minimal stand-ins written
  for the test harness. They are not Jeskola Buzz's SDK headers.
- The machine runs on **ReBuzz** (MIT licence) and references its `BuzzGUI` assemblies at
  build time; they are not included here.

## Changelog

- **2.2.0**: **note-following**: a **Note** column and **Follow** (off / band / FreqA / pitch)
  per set, both appended. A note moves the band, FreqA or the PitchShift interval by its
  distance from C-4; it holds until the next note or `off`. MIDI notes steer every
  following set (last-note priority). The Edit window has a Follow drop-down, and moved
  bands are drawn live with the note in their label. A set's second line now runs to the
  end of the row and wraps, instead of being cut off after Mix. Presets 42 Melody Band-Pass,
  43 Harmonic Follow, 44 Following Harmony; every preset now sets Follow off.
  **Songs saved with 2.1 or earlier** only open in a ReBuzz that has the parameter-loading
  fix (the machine has two more track parameters). Otherwise save them as `.bmxml` with the
  old version first.
- **2.1.0**: **Formant** per set for PitchShift (appended track parameter, default off):
  keep the formants while the pitch moves, or shift them on their own (−12..+12 st);
  true-envelope estimate on a decimated spectrum. Downward pitch shifts no longer lose
  harmonics on harmonic-rich sounds (moved peaks stop overlapping). Formant drop-down in
  the Edit window; presets 39 Natural Octave Up, 40 Natural Harmony, 41 Formant Up. Every
  preset now sets Formant off.
- **2.0.0** (Edit window only, sound unchanged): the window adapts to its size: full rows
  when wide, a compact set list with the selected set in full when narrow. View options:
  In + Out / Out only / In only, foldable globals, and a Compact button, all remembered.
  The set area follows the window height, and the spectrograms keep their picture when
  they change size. Build warnings cleared. Released under the GPL v3 or later, with
  credits to Darrell Tam in the README and About box; the test harness's Buzz interface
  header replaced with a minimal stand-in.
- **1.8.0**: wet/dry **Mix** per set (appended track parameter, default 100%), for
  PitchShift and every effect that takes Soft; Mix slider in the Edit window; presets
  36 Octave Harmony, 37 Fifth Below Blend and 38 Parallel Smear. Every preset now sets
  Mix = 100, so loading one resets it; nothing else in presets 1–35 changed.
- **1.7.0**: band LFO upgrade.
  - Free-running Hz rates (LfoRate 19–34: 0.05–12 Hz), independent of tempo and transport.
  - Each block takes the LFO averaged over the block instead of its value at the block
    start: the modulation no longer lags half a block, and rates too fast for the block
    size fade out instead of aliasing. Slow LFOs sound the same; square and S/H edges
    are a block softer.
  - Edit window: Hz rates in the Rate list; an orange hint when the rate is too fast for
    BlockLen.
  - Presets 34 Free Drift and 35 Free Chop 6 Hz. Presets 1–33 unchanged.
- **1.6.0** (Edit window only, sound unchanged): level legend; the window remembers
  its size, position, maximised state and Floor.
- **1.5.0** (Edit window only, sound unchanged):
  - bands follow the LFO, with each edge's travel shaded;
  - Value can be typed in the effect's units;
  - clearer M/S buttons.
- **1.4.0**:
  - Mute and Solo per set (appended track switches), with M/S buttons in the Edit
    window and faded bands for silenced sets;
  - every preset sets them off.
- **1.3.0**:
  - band LFO per set (LfoTarget, LfoShape, LfoRate, LfoDepth, LfoPhase), locked to the
    song position;
  - LFO line in the Edit window's set panel;
  - presets 29–33; every preset now also sets the LFO (off), so loading one clears a
    previous preset's LFO.

  Five track parameters appended, so machines in songs saved with 1.2 or earlier pick
  them up with defaults (LFO off).
- **1.2.0**:
  - effect 15, PitchShift: a peak-locked phase vocoder, −24..+24 semitones, with Soft
    edges;
  - presets 25–28 (Octave Up, Octave Down, Fifth Harmony, Detune Double).

  No parameters added, so saved songs and presets 1–24 are unaffected.
- **1.1.0**:
  - machines add tracks automatically when a preset (or anything else) turns on a set
    beyond the current track count;
  - latency is reported for ReBuzz's delay compensation, and updated when TickDelay or
    the tempo changes;
  - LevelMatch switch;
  - Capture trigger per set (Freeze recapture, Blur restart);
  - presets now set LevelMatch too.

  Checked against the ReBuzz 1848-preview source.
- **1.0.0**:
  - preset bank (24);
  - Freeze keeps sounding after the input stops.
- **0.4.0**:
  - Smooth mode;
  - Soft band edges;
  - effects 9–14 (Freeze, Gate, Blur, Tilt, RoboWhisper, Cross);
  - Sidechain input (now Main + Sidechain inputs, so re-create machines made with 0.3 or earlier).
- **0.3.0**: KeepDC.
- **0.2.x**: Edit window (spectrograms, draggable bands, set panel).
- **0.1.0**: audio engine port, matching the original at 110–138 dB SNR.

## Build

Open `Pedal BlockFX.NET.csproj` and build. The DLL deploys to
`C:\Program Files\ReBuzz\Gear\Effects\`. If ReBuzz is running it holds the DLL open,
so close it first.
