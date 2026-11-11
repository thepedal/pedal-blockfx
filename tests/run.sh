#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Pedal
# Linux (Ubuntu): apt-get install g++ libfftw3-dev mono-mcs libmono-system-xml-linq4.0-cil; pip install numpy
# Optional (syntax-checks EditWindow.cs, which mcs cannot parse): pip install tree-sitter tree-sitter-c-sharp
set -e
cd "$(dirname "$0")"
g++ -std=gnu++14 -O2 -fpermissive -w -Iref/shim -Iref/src -include stdafx.h \
    ref/ref.cpp ref/src/MIBlkFx.cpp ref/src/BlkFxProcess.cpp ref/src/params.cpp -lfftw3f -o refbin
SRC="../RealFft.cs ../Params.cs ../BlockFxEngine.cs ../Display.cs ../SpectrumTap.cs ../EditModel.cs"
mcs -langversion:experimental -optimize+ -out:harness.exe $SRC Harness.cs
mcs -langversion:experimental -optimize+ -out:glue.exe $SRC ../PedalBlockFX.cs Stubs.cs GlueTest.cs
mcs -langversion:experimental -optimize+ -out:stereo.exe $SRC ../PedalBlockFX.cs Stubs.cs Stereo.cs
mcs -langversion:experimental -optimize+ -out:em.exe $SRC EditModelTest.cs
mcs -langversion:experimental -optimize+ -out:nfx.exe $SRC NewFxTest.cs
mono harness.exe fft
mono em.exe
python3 compare.py
mcs -langversion:experimental -optimize+ -out:kdc.exe $SRC ../PedalBlockFX.cs Stubs.cs KeepDcTest.cs
mono glue.exe
mono kdc.exe
mono stereo.exe
mono nfx.exe
mcs -langversion:experimental -optimize+ -out:ft.exe $SRC ../PedalBlockFX.cs Stubs.cs FreezeTailTest.cs
mono ft.exe
mcs -langversion:experimental -optimize+ -out:v11.exe $SRC ../PedalBlockFX.cs Stubs.cs V11Test.cs
mono v11.exe
mcs -langversion:experimental -optimize+ -out:pitch.exe $SRC PitchTest.cs
mono pitch.exe
mcs -langversion:experimental -optimize+ -out:mx.exe $SRC ../PedalBlockFX.cs Stubs.cs MixTest.cs
mono mx.exe
mcs -langversion:experimental -optimize+ -out:fm.exe $SRC FormantTest.cs
mono fm.exe
mcs -langversion:experimental -optimize+ -out:nt.exe $SRC ../PedalBlockFX.cs Stubs.cs NoteTest.cs
mono nt.exe
mcs -langversion:experimental -optimize+ -out:lfo.exe $SRC ../PedalBlockFX.cs Stubs.cs LfoTest.cs
mono lfo.exe
mcs -langversion:experimental -optimize+ -out:sm.exe $SRC ../PedalBlockFX.cs Stubs.cs SoloMuteTest.cs
mono sm.exe
mcs -langversion:experimental -optimize+ -out:vt.exe $SRC ValueTextTest.cs
mono vt.exe
mcs -langversion:experimental -optimize+ -out:pf.exe $SRC ../Prefs.cs PrefsTest.cs
mono pf.exe
if python3 -c "import tree_sitter_c_sharp" 2>/dev/null; then python3 -I cs_syntax.py ../*.cs; else echo "(tree-sitter not installed: skipping the C# syntax check of the WPF code)"; fi
mcs -langversion:experimental -optimize+ -r:System.Xml.Linq.dll -r:System.Xml.dll -out:pt.exe $SRC ../PedalBlockFX.cs Stubs.cs PresetTest.cs
(cd .. && python3 -I gen_presets.py --check)
mono pt.exe "../Pedal BlockFX.prs.xml"
