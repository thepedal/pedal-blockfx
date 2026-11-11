#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Pedal
"""
Pedal BlockFX - preset bank generator.  Writes "Pedal BlockFX.prs.xml" (Build notes §3).

ReBuzz applies a multi-track preset by walking the machine's state parameters in
declaration order, group by group and track by track, matching names
(BuzzGUI.Common Preset.Apply, non-unique-names branch). So every preset lists:
  Group 1: the state globals in declaration order (stateless BlkSync/InterpMode omitted)
  Group 2: tracks 0..SETS-1, each with all six track parameters in declaration order
PARAM order below is checked against PedalBlockFX.cs by --check.

Not deployed; run it after changing presets:  python3 gen_presets.py [--check]
"""
import math, re, sys, os

MACHINE = "Pedal BlockFX"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), MACHINE + ".prs.xml")
SETS = 4                       # FX sets written per preset (machine needs >= 4 tracks to use them all)

# (name, index-in-group) - index counts stateless params too (IndexInGroup)
GLOBALS = [("MixBack", 2), ("OutAmp", 3), ("TickDelay", 4), ("BlockLen", 5), ("Overlap", 6),
           ("Stereo", 7), ("KeepDC", 8), ("Window", 9), ("LevelMatch", 10)]
ALL_GLOBALS = ["BlkSync", "InterpMode"] + [g for g, _ in GLOBALS]
TRACK = ["FreqA", "FreqB", "Amp", "Effect", "Value", "Soft",      # state track params (Capture is stateless)
         "LfoTarget", "LfoShape", "LfoRate", "LfoDepth", "LfoPhase", "Mute", "Solo", "Mix", "Formant",
         "Follow"]                                               # v2.2 (Note is stateless)

# index-in-group counts the stateless Capture (slot 6), which presets skip
ALL_TRACK = ["FreqA", "FreqB", "Amp", "Effect", "Value", "Soft", "Capture",
             "LfoTarget", "LfoShape", "LfoRate", "LfoDepth", "LfoPhase", "Mute", "Solo", "Mix", "Formant",
             "Note", "Follow"]
TRACK_INDEX = {n: i for i, n in enumerate(ALL_TRACK)}

GDEF = dict(MixBack=0, OutAmp=0xA0, TickDelay=0x400, BlockLen=7, Overlap=50, Stereo=1, KeepDC=1, Window=1, LevelMatch=1)
TDEF = dict(FreqA=0, FreqB=0, Amp=0xA0, Effect=0, Value=0, Soft=0,
            LfoTarget=0, LfoShape=0, LfoRate=10, LfoDepth=0, LfoPhase=0, Mute=0, Solo=0, Mix=100, Formant=0, Follow=0)

FX = dict(Contrast=0, Smear=1, Clip=2, Weed=3, ShiftAdd=4, ShiftReplace=5, Harmonic=6, AutoHarm=7,
          CopyHarm=8, Freeze=9, Gate=10, Blur=11, Tilt=12, RoboWhisper=13, Cross=14, PitchShift=15)

# ---- value helpers (mirror the machine's conversions) -------------------------------
A4 = 440.000031
def hz(f):                      # FreqA/FreqB value for a frequency
    v = (math.log2(f / A4) + 4.75) * 3072
    return max(0, min(132 * 256, int(v + 0.5)))
def db(d):                      # Amp / OutAmp code: 0xA0 = 0 dB, 0.5 dB steps, None = -inf
    return 0 if d is None else max(1, min(0xE0, 0xA0 + int(round(d * 2))))
def frac(f):                    # Value 0..1
    return max(0, min(0xA000, int(round(f * 0xA000))))
def pct2(p):                    # Contrast/Weed "+/-%" display -> Value
    return frac((p / 100 + 1) / 2)
def harm(kind, width):          # Harmonic family: kind both/odd/even, width 0..1
    t = {"both": 0, "odd": 1, "even": 2}[kind]
    r = t + (width if t & 1 else 1 - width)
    return frac(r / 3)
def tilt(db_per_oct):           # -12..+12 dB/oct
    return frac((db_per_oct / 12 + 1) / 2)
def gate(dbfs):                 # threshold -100..0 dBFS
    return frac((dbfs + 100) / 100)
def blur(ms):                   # 5 ms .. 2 s
    return frac(math.log(ms / 5) / math.log(400))
def pitch(st):                  # PitchShift: -24..+24 semitones
    return frac((st / 24 + 1) / 2)
def robot(p): return frac(0.5 - p / 200)
def whisper(p): return frac(0.5 + p / 200)
def shift(hz_, nyq=22050):      # ShiftAdd/Replace: invert the original's freqShift (at 44.1 kHz)
    def fs(v):
        lin = 2048 / 32768; er = 8; es = (1 - lin) / (1 << er)
        v = (v - 0.5) * 2; s = 1 if v >= 0 else -1; v = abs(v)
        return s * ((2 ** (v * er) - 1) * es + v * lin) * nyq
    lo, hi = 0.0, 1.0
    for _ in range(60):
        m = (lo + hi) / 2
        if fs(m) > hz_: hi = m
        else: lo = m
    return frac(lo)

LFO_TARGET = dict(Off=0, Band=1, FreqA=2, FreqB=3, Width=4, Value=5)
LFO_SHAPE = dict(Sine=0, Triangle=1, SawUp=2, SawDown=3, Square=4, SH=5)
LFO_TICKS = [0.25, 0.5, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256]   # = P.LfoTicks

LFO_HZ = [0.05, 0.1, 0.15, 0.2, 0.25, 0.33, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12]   # = P.LfoHz (v1.7)

def lfo(target, shape="Sine", ticks=16, depth=0.0, phase=0, hz=None):
    """Band LFO: target, shape, period in ticks (must be in LFO_TICKS) or a free-running rate
    in Hz (hz=, must be in LFO_HZ), depth in % (0.5% steps), phase in degrees."""
    rate = len(LFO_TICKS) + LFO_HZ.index(hz) if hz is not None else LFO_TICKS.index(ticks)
    return dict(LfoTarget=LFO_TARGET[target], LfoShape=LFO_SHAPE[shape], LfoRate=rate,
                LfoDepth=int(round(depth * 2)), LfoPhase=int(round(phase / 360 * 256)) % 256)

FORMANT_KEEP = 25
def formant(st):                # PitchShift formants (v2.1): None = off, 0 = keep, else shift in st
    return 0 if st is None else FORMANT_KEEP + int(round(st * 2))

FOLLOW = dict(Off=0, Band=1, FreqA=2, Pitch=3)   # v2.2
C4 = 261.6256                   # the reference note: a following band is set as for C-4

def S(fx, a, b, value=0, amp=0.0, soft=0, mod=None, mix=100, fmt=None, follow="Off"):
    """One FX set: effect name, FreqA Hz, FreqB Hz (A > B = outside the range); mod = lfo(...);
    mix = wet % (v1.8); fmt = formant(...) (v2.1); follow = what the Note column moves (v2.2)."""
    d = dict(Effect=FX[fx], FreqA=hz(a), FreqB=hz(b), Value=value, Amp=db(amp), Soft=soft, Mix=mix,
             Formant=0 if fmt is None else fmt, Follow=FOLLOW[follow])
    if mod: d.update(mod)
    return d

# ---- the bank -------------------------------------------------------------------------
# Levels: the original level-matches the whole output to the input, so a set's Amp only
# rebalances bands. A set covering (nearly) the whole spectrum needs OutAmp for make-up
# gain instead. Levels below were checked by playing every preset through the machine
# (tests/PresetTest.cs): all within about -6..+2 dB of the input, no clipping.
# Each entry: (name, comment, globals overrides, [sets])
PRESETS = [
    ("01 Init (clean)", "Smooth mode, no FX sets: transparent apart from the TickDelay latency.",
     {}, []),

    # --- the original's character (Blocky) ---
    ("02 DT Classic Grain", "The original's sound: Blocky mode, Smear on the mids, a little Contrast on top.",
     dict(Window=0), [S("Smear", 150, 6000, frac(0.55)), S("Contrast", 6000, 18000, pct2(35))]),
    ("03 DT Spectral Clip", "Blocky Clip across the spectrum: flattens loud partials into a buzzy, compressed tone.",
     dict(Window=0, OutAmp=db(4.5)), [S("Clip", 60, 16000, frac(0.6))]),
    ("04 Sharpen Partials", "Contrast +60%: strong partials get stronger, the noise between them recedes.",
     {}, [S("Contrast", 80, 16000, pct2(60), soft=6)]),
    ("05 Flatten to Noise", "Contrast -60%: evens the spectrum out towards a breathy, noisy version of the input.",
     {}, [S("Contrast", 80, 16000, pct2(-60), soft=6)]),
    ("06 Tonal Only", "Weed +40%: removes weak bins, leaving the tonal skeleton. Good on noisy material.",
     {}, [S("Weed", 40, 20000, pct2(40))]),
    ("07 Noise Only", "Weed -40%: removes the strongest bins, leaving the residue and air.",
     {}, [S("Weed", 40, 20000, pct2(-40))]),

    # --- smear, freeze, blur: pads and washes ---
    ("08 Freeze Pad", "Freeze holds the spectrum at load (or when Value rises from 0) and keeps sounding after the input stops. Re-trigger: set Value to 0, then back up.",
     dict(BlockLen=9), [S("Freeze", 40, 18000, frac(1.0), amp=2.5, soft=6)]),
    ("09 Freeze Blend", "Half frozen, half live: the held chord sits under whatever plays next.",
     dict(BlockLen=9), [S("Freeze", 40, 18000, frac(0.55), amp=4.0, soft=6)]),
    ("10 Bass Freeze, Live Top", "Freezes only the low end (below 250 Hz) for a drone floor, the rest stays live.",
     dict(BlockLen=10), [S("Freeze", 25, 250, frac(1.0), amp=2.5, soft=6)]),
    ("11 Long Blur Wash", "2 s per-bin smoothing: transients dissolve into a slow-moving wash.",
     dict(BlockLen=9), [S("Blur", 30, 20000, blur(2000))]),
    ("12 Shimmer Highs", "Blur and a little lift above 1 kHz: the top end trails and glows, the body stays tight.",
     {}, [S("Blur", 1000, 18000, blur(700), amp=2.0, soft=12)]),
    ("13 Smear Cloud", "Smooth-mode Smear plus a medium Blur: diffuse and reverb-like without a tail.",
     dict(BlockLen=9, OutAmp=db(4.0)), [S("Smear", 100, 16000, frac(0.85)), S("Blur", 100, 16000, blur(250))]),

    # --- tone shaping ---
    ("14 Noise Floor Cleaner", "Spectral gate at -70 dBFS per bin: removes hiss and low-level hash between notes.",
     {}, [S("Gate", 30, 20000, gate(-70), soft=3)]),
    ("15 Gated Sparkle", "Hard gate on the highs only: just the loudest top partials get through, glittery and sparse.",
     {}, [S("Gate", 2500, 20000, gate(-38), amp=3.0, soft=6)]),
    ("16 Dark Tilt", "-5 dB/oct above 200 Hz: warmer and duller without a filter's resonance.",
     {}, [S("Tilt", 200, 18000, tilt(-5), amp=-7.0, soft=6)]),
    ("17 Air Tilt", "+4 dB/oct above 1 kHz: adds air. Watch the level on bright sources.",
     {}, [S("Tilt", 1000, 20000, tilt(4), soft=12)]),

    # --- character ---
    ("18 Robot", "Blocky mode, 1024-sample blocks, phase flattened: the classic robot buzz (pitch set by the hop).",
     dict(Window=0, BlockLen=5, Overlap=50, OutAmp=db(7.0)), [S("RoboWhisper", 40, 16000, robot(100))]),
    ("19 Whisper", "Phase randomised over the voice range: pitched material turns into breath.",
     dict(BlockLen=8, OutAmp=db(3.5)), [S("RoboWhisper", 80, 16000, whisper(100))]),
    ("20 Metallic Shift", "ShiftAdd of about +150 Hz (at 44.1 kHz) on the mids: inharmonic, bell-like doubling.",
     {}, [S("ShiftAdd", 100, 5000, shift(150), amp=-3.0)]),
    ("21 Harmonic Glow", "AutoHarm boosts the harmonic series of the loudest partial: richer, more focused tone.",
     {}, [S("AutoHarm", 80, 8000, harm("both", 0.3), amp=6.0)]),
    ("22 Odd Harmonics Hollow", "Harmonic, odd series of 110 Hz cut by 12 dB: a hollow, clarinet-like colour on A-based material.",
     {}, [S("Harmonic", 110, 12000, harm("odd", 0.5), amp=-12.0)]),

    # --- sidechain ---
    ("23 Cross Vocoder", "Connect a second machine to input 2 (Sidechain). Main takes on the sidechain's spectral shape: pad on Main, drums or voice on the sidechain.",
     dict(BlockLen=9), [S("Cross", 60, 16000, frac(1.0), amp=9.0, soft=6)]),
    ("24 Cross Half", "50% cross-synthesis: Main keeps half its own character. Needs the sidechain connected.",
     dict(BlockLen=9), [S("Cross", 60, 16000, frac(0.5), amp=12.0, soft=6)]),

    # --- pitch (v1.2) ---
    ("25 Octave Up", "PitchShift +12 semitones over the whole range. Smooth mode, Overlap 75%.",
     dict(Overlap=75), [S("PitchShift", 30, 20000, pitch(12))]),
    ("26 Octave Down", "PitchShift -12 semitones; 8192-sample blocks keep bass notes clean.",
     dict(Overlap=75, BlockLen=8), [S("PitchShift", 30, 20000, pitch(-12))]),
    ("27 Fifth Harmony", "Up a fifth (+7) mixed 50/50 with the dry signal (MixBack): instant parallel harmony.",
     dict(Overlap=75, MixBack=0x50, OutAmp=db(3.0)), [S("PitchShift", 30, 20000, pitch(7))]),
    ("28 Detune Double", "+15 cents mixed with the dry signal: a chorus-like doubling without modulation.",
     dict(Overlap=75, MixBack=0x50, OutAmp=db(3.0)), [S("PitchShift", 30, 20000, pitch(0.15))]),

    # --- band LFO (v1.3); all locked to the song position ---
    ("29 Band-Pass Sweep", "Everything outside a two-octave band is removed, and the band sweeps up and down an octave every 32 ticks. LevelMatch off, so it gets quieter where the band finds little.",
     dict(LevelMatch=0, OutAmp=db(8.0)), [S("Contrast", 2000, 500, 0, amp=None, soft=6, mod=lfo("Band", "Sine", 32, 50))]),
    ("30 Twin Notch Sweep", "Two narrow notches sweeping in opposite directions (180 degrees apart): a phaser-like swirl.",
     {}, [S("Contrast", 700, 900, 0, amp=None, soft=2, mod=lfo("Band", "Triangle", 16, 60)),
          S("Contrast", 700, 900, 0, amp=None, soft=2, mod=lfo("Band", "Triangle", 16, 60, 180))]),
    ("31 Tempo Vibrato", "PitchShift with its Value wobbling about a quarter-semitone every 1.5 ticks: vibrato in time with the song.",
     dict(Overlap=75), [S("PitchShift", 30, 20000, pitch(0), mod=lfo("Value", "Sine", 1.5, 1.0))]),
    ("32 Square Gate Chop", "The spectral gate's threshold jumps between -10 and -70 dBFS every 2 ticks: rhythmic chopping.",
     {}, [S("Gate", 30, 20000, gate(-40), soft=3, mod=lfo("Value", "Square", 4, 60))]),
    ("33 S/H Smear Hop", "An octave-wide Smear band that jumps to a random place every 2 ticks.",
     dict(BlockLen=8), [S("Smear", 400, 800, frac(0.9), soft=6, mod=lfo("Band", "SH", 2, 50))]),
    # v1.7: free-running Hz rates
    ("34 Free Drift", "Two notches drifting at 0.15 and 0.1 Hz, free-running rather than locked to the song, so they never line up the same way twice: slow movement for pads.",
     {}, [S("Contrast", 500, 650, 0, amp=None, soft=3, mod=lfo("Band", "Sine", depth=50, hz=0.15)),
          S("Contrast", 1400, 1800, 0, amp=None, soft=3, mod=lfo("Band", "Triangle", depth=40, phase=90, hz=0.1))]),
    ("35 Free Chop 6 Hz", "The spectral gate's threshold jumps at 6 Hz whatever the tempo: a fast chop. Small blocks with Overlap 75% so the LFO can keep up.",
     dict(BlockLen=5, Overlap=75), [S("Gate", 30, 20000, gate(-40), soft=3, mod=lfo("Value", "Square", depth=60, hz=6))]),
    # v1.8: per-set Mix
    ("36 Octave Harmony", "PitchShift +12 with the set's Mix at 50%: the original plus an octave above, from one set (no MixBack needed).",
     dict(Overlap=75, OutAmp=db(3.0)), [S("PitchShift", 30, 20000, pitch(12), mix=50)]),
    ("37 Fifth Below Blend", "PitchShift down a fifth (-7) at Mix 40%: a darker parallel voice under the original.",
     dict(Overlap=75, OutAmp=db(3.0)), [S("PitchShift", 30, 20000, pitch(-7), mix=40)]),
    ("38 Parallel Smear", "Heavy Smear at Mix 35%: adds breath and air while the original keeps its pitch and attack.",
     dict(BlockLen=8, OutAmp=db(3.0)), [S("Smear", 100, 16000, frac(0.95), mix=35)]),
    # v2.1: PitchShift formant control
    ("39 Natural Octave Up", "PitchShift +12 with Formant keep: an octave up without the chipmunk. Best on voices below about 200 Hz.",
     dict(Overlap=75), [S("PitchShift", 30, 20000, pitch(12), fmt=formant(0))]),
    ("40 Natural Harmony", "Up a fifth (+7) at Mix 50% with Formant keep: a harmony that sounds like the same singer.",
     dict(Overlap=75, OutAmp=db(3.0)), [S("PitchShift", 30, 20000, pitch(7), mix=50, fmt=formant(0))]),
    ("41 Formant Up", "No pitch change, formants up 4 semitones: a smaller, brighter voice at the same note.",
     dict(Overlap=75), [S("PitchShift", 30, 20000, pitch(0), fmt=formant(4))]),
    # v2.2: note-following (play notes in the set's Note column, or from a MIDI keyboard)
    ("42 Melody Band-Pass", "A band-pass from G#3 to E4, set for C-4, that jumps to each note played in the Note column: the input sings the melody. LevelMatch off.",
     dict(LevelMatch=0, OutAmp=db(6.0), Overlap=75), [S("Contrast", 330, 208, 0, amp=None, soft=3, follow="Band")]),
    ("43 Harmonic Follow", "Harmonic boosts the harmonic series of the note played (FreqA follows, C-4 = as set): the input resonates in the key of the pattern.",
     {}, [S("Harmonic", C4, 12000, harm("both", 0.3), amp=9.0, follow="FreqA")]),
    ("44 Following Harmony", "PitchShift at Mix 50% with Formant keep; the Note column sets the interval (C-4 = unison, E-4 = a third up, G-3 = a fourth down).",
     dict(Overlap=75), [S("PitchShift", 30, 20000, pitch(0), mix=50, fmt=formant(0), follow="Pitch")]),
]

# ---- emit ---------------------------------------------------------------------------
def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")

def build():
    out = ['<?xml version="1.0" encoding="utf-8"?>', "<PresetDictionary>"]
    names = set()
    for name, comment, gov, sets in PRESETS:
        assert name not in names, name; names.add(name)
        assert len(sets) <= SETS, name
        out.append(f'  <Item Key="{xml_escape(name)}">')
        out.append(f'    <Preset Machine="{MACHINE}">')
        out.append("      <Parameters>")
        g = dict(GDEF); g.update(gov)
        for pname, idx in GLOBALS:
            out.append(f'        <Parameter Name="{pname}" Group="1" Index="{idx}" Track="0" Value="{g[pname]}" />')
        for t in range(SETS):
            tv = dict(TDEF)
            if t < len(sets): tv.update(sets[t])
            for pname in TRACK:
                out.append(f'        <Parameter Name="{pname}" Group="2" Index="{TRACK_INDEX[pname]}" Track="{t}" Value="{tv[pname]}" />')
        out.append("      </Parameters>")
        out.append("      <Attributes />")
        out.append(f"      <Comment>{xml_escape(comment)}</Comment>")
        out.append("    </Preset>")
        out.append("  </Item>")
    out.append("</PresetDictionary>")
    return "\n".join(out) + "\n"

def check():
    """Declaration order in PedalBlockFX.cs must match GLOBALS/TRACK, and values must be in range."""
    here = os.path.dirname(os.path.abspath(__file__))
    src = open(os.path.join(here, "PedalBlockFX.cs")).read()
    global LAST_EFFECT
    LAST_EFFECT = int(re.search(r"const int LastEffect = (\d+);", open(os.path.join(here, "Params.cs")).read()).group(1))
    assert LAST_EFFECT == max(FX.values()), f"FX table ends at {max(FX.values())}, Params.cs LastEffect = {LAST_EFFECT}"
    decls = re.findall(r'\[ParameterDecl\(Name = "([^"]+)"(.*?)\)\]\s*public\s+(\w+)\s+(\w+)', src, re.S)
    glob = [n for n, _, ret, _ in decls if ret != "void"]
    trk = [n for n, rest, ret, _ in decls if ret == "void" and "IsStateless = true" not in rest]
    assert glob == ALL_GLOBALS, f"global order mismatch: {glob}"
    assert trk == TRACK, f"track (state) order mismatch: {trk}"
    alltrk = [n for n, _, ret, _ in decls if ret == "void"]
    assert alltrk == ALL_TRACK, f"track (all) order mismatch: {alltrk}"
    rng = {}
    for n, rest, _, _ in decls:
        lo = re.search(r"MinValue = ([\w.]+)", rest); hi = re.search(r"MaxValue = ([\w.* ]+?)[,)]", rest + ")")
        if lo and hi:
            hv = hi.group(1).replace("P.LastEffect", str(LAST_EFFECT)).replace("P.MaxSets", "16")
            rng[n] = (int(lo.group(1), 0), int(eval(hv)))
    for b in ("KeepDC", "LevelMatch", "Mute", "Solo"): rng[b] = (0, 1)      # bool switches
    for name, _, gov, sets in PRESETS:
        g = dict(GDEF); g.update(gov)
        for k, v in g.items(): assert rng[k][0] <= v <= rng[k][1], (name, k, v)
        for s in sets:
            for k, v in s.items(): assert rng[k][0] <= v <= rng[k][1], (name, k, v)
    print(f"check ok: {len(glob)} globals, {len(trk)} track params, {len(PRESETS)} presets in range")

if __name__ == "__main__":
    if "--check" in sys.argv: check()
    with open(OUT, "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write(build())
    print("wrote", OUT, f"({len(PRESETS)} presets)")
