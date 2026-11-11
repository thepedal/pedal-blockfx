// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Plays every preset in "Pedal BlockFX.prs.xml" through the machine class (via its declared
// setters, as Preset.Apply would) and reports level, peak and per-set Value meaning.
using System; using System.Linq; using System.Xml.Linq; using System.Reflection;
using Buzz.MachineInterface; using BuzzGUI.Interfaces; using PedalBlockFX;
class M6 : IMachine { public int TrackCount { get; set; } = 4; }
class H6 : IBuzzMachineHost { public int InputChannelCount { get; set; } public int OutputChannelCount { get; set; } public IMachine Machine => new M6(); public MasterInfo MasterInfo { get; } = new MasterInfo{ SamplesPerSec=44100, SamplesPerTick=5512 }; }
static class PresetTest { static int Main(string[] a) {
  var doc = XDocument.Load(a[0]); int fails = 0; var rnd = new Random(5);
  var noise = new float[1<<16]; for (int i=0;i<noise.Length;i++) noise[i]=(float)(rnd.NextDouble()*2-1);
  Func<long,float> music = t => (float)(5000*Math.Sin(2*Math.PI*110*t/44100.0) + 3000*Math.Sin(2*Math.PI*220.5*t/44100.0) + 2000*Math.Sin(2*Math.PI*331*t/44100.0) + 1500*Math.Sin(2*Math.PI*1650*t/44100.0) + 600*noise[t & 0xFFFF]);
  Func<long,float> drums = t => { long p = t % 11025; return (float)(12000*Math.Exp(-p/1500.0)*noise[(t*3)&0xFFFF]); };
  var mt = typeof(PedalBlockFXMachine);
  Console.WriteLine($"{"preset",-26} {"in rms",7} {"out rms",8} {"peak",7}  sets");
  foreach (var item in doc.Root.Elements("Item")) {
    string key = item.Attribute("Key").Value;
    var h = new H6(); var m = new PedalBlockFXMachine(h);
    var sets = new int[4,P.NT];
    string[] tn = { "FreqA","FreqB","Amp","Effect","Value","Soft","Capture","LfoTarget","LfoShape","LfoRate","LfoDepth","LfoPhase","Mute","Solo","Mix","Formant","Note","Follow" };
    foreach (var p in item.Descendants("Parameter")) {
      string n = p.Attribute("Name").Value; int g = int.Parse(p.Attribute("Group").Value), t = int.Parse(p.Attribute("Track").Value), v = int.Parse(p.Attribute("Value").Value);
      if (g == 1) { var pi = mt.GetProperty(n); if (pi.PropertyType == typeof(bool)) pi.SetValue(m, v != 0); else pi.SetValue(m, v); }
      else { var sm = mt.GetMethod("Set" + n); sm.Invoke(m, new object[]{ sm.GetParameters()[0].ParameterType == typeof(bool) ? (object)(v != 0) : v, t }); sets[t, Array.IndexOf(tn, n)] = v; if (int.Parse(p.Attribute("Index").Value) != Array.IndexOf(tn, n)) { Console.WriteLine("FAIL index " + n); fails++; } }
    }
    bool side = key.Contains("Cross");
    var inp = new Sample[212]; var sc = new Sample[212]; var o = new Sample[212];
    double ei=0, eo=0, peak=0; long cnt=0, pos=0; bool fin = true;
    for (int b = 0; b < 26*24; b++) {
      for (int i=0;i<212;i++){ float v = b < 22*24 ? music(pos+i) : 0f; inp[i].L=v; inp[i].R=v; float d = drums(pos+i); sc[i].L=d; sc[i].R=d; }
      bool r = m.Work(new[]{o}, new Sample[][]{inp, side ? sc : null}, 212, WorkModes.WM_WRITE);
      if (b >= 8*24 && b < 22*24) for (int i=0;i<212;i++){ double x = r ? o[i].L : 0; if (double.IsNaN(x)||double.IsInfinity(x)) fin=false; eo+=x*x; ei+=inp[i].L*(double)inp[i].L; peak=Math.Max(peak,Math.Abs(x)); cnt++; }
      pos+=212; h.MasterInfo.PosInTick=(h.MasterInfo.PosInTick+212)%5512;
    }
    double ri = Math.Sqrt(ei/cnt), ro = Math.Sqrt(eo/cnt);
    string desc = string.Join(" | ", Enumerable.Range(0,4).Where(t => sets[t,0]!=0||sets[t,1]!=0).Select(t =>
      $"{P.EffectNames[sets[t,3]]} {Display.Freq(P.FreqHz(sets[t,0])).Split(',')[0]}-{Display.Freq(P.FreqHz(sets[t,1])).Split(',')[0]} {EditModel.ValueDesc(sets[t,3], sets[t,4], f=>f, 44100)}" + (sets[t,P.LfoTarget] != 0 && sets[t,P.LfoDepth] > 0 ? $" ~ LFO {P.LfoTargetNames[sets[t,P.LfoTarget]]} {P.LfoShapeNames[sets[t,P.LfoShape]]} {EditModel.LfoRateText(sets[t,P.LfoRate],0)} {EditModel.LfoSwingText(sets[t,P.LfoTarget], sets[t,P.LfoDepth])}" : "")));
    bool ok = fin && ro > 0.05*ri && peak < 32767;
    if (!ok) fails++;
    Console.WriteLine($"{(ok?"":"FAIL ")}{key,-26} {ri,7:0} {ro,8:0} {peak,7:0}  {desc}");
  }
  Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILURES"); return fails; } }
