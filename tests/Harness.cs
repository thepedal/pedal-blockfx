// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Drives the C# engine from the same scenario format as the C++ reference harness.
using System; using System.IO; using System.Collections.Generic; using System.Diagnostics;
using PedalBlockFX;
static class Harness {
  static int Main(string[] a) {
    if (a[0] == "fft") return FftTest();
    // harness in.f32 scen.txt out.f32 sr spt ntracks inLen total [nch]
    int sr=int.Parse(a[3]), spt=int.Parse(a[4]), nt=int.Parse(a[5]); long inLen=long.Parse(a[6]), total=long.Parse(a[7]);
    var inb = new float[total]; var bytes=File.ReadAllBytes(a[0]); Buffer.BlockCopy(bytes,0,inb,0,(int)Math.Min(bytes.Length, inLen*4));
    var ch = new List<long[]>();
    foreach (var line in File.ReadAllLines(a[1])) { var p=line.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries); if(p.Length==5) ch.Add(new[]{long.Parse(p[0]),long.Parse(p[1]),long.Parse(p[2]),long.Parse(p[3]),long.Parse(p[4])}); }
    var e = new BlockFxEngine(sr); var chg = new Params(); var outb = new float[total];
    var io = new[]{ new float[256], new float[256] };
    var sw = Stopwatch.StartNew();
    for (long pos=0, tick=0; pos<total; tick++) {
      if (tick==0) chg.SetDefaults(); else chg.SetNoVal(); chg.TvN = nt;
      foreach (var c in ch) if (c[0]==tick) { if (c[1]==0) chg.G[c[2]]=(int)c[4]; else chg.T[c[2],c[3]]=(int)c[4]; }
      e.Tick(chg, spt);
      for (long t=0; t<spt && pos<total;) {
        int n=(int)Math.Min(256, Math.Min(spt-t, total-pos)); bool has = pos<inLen;
        for (int i=0;i<n;i++) io[0][i] = has ? inb[pos+i] : 0f;
        bool r = e.Work(io, n, 1, has, spt);
        for (int i=0;i<n;i++) outb[pos+i] = r ? io[0][i] : 0f;
        pos+=n; t+=n;
      }
    }
    Console.Error.WriteLine("cs_ms=" + sw.ElapsedMilliseconds);
    var ob = new byte[total*4]; Buffer.BlockCopy(outb,0,ob,0,ob.Length); File.WriteAllBytes(a[2], ob);
    return 0;
  }
  static int FftTest() {
    var rnd = new Random(1); double worst=0, worstInv=0;
    for (int p=0;p<12;p++){ int N=32<<p; var f=new RealFft(N); var x=new float[N]; for(int i=0;i<N;i++) x[i]=(float)(rnd.NextDouble()*2-1);
      var hc=new float[N]; f.Forward(x,0,hc);
      if (N<=2048) { double err=0, mag=0; for(int k=0;k<=N/2;k++){ double re=0,im=0; for(int n=0;n<N;n++){ double ang=-2*Math.PI*k*n/N; re+=x[n]*Math.Cos(ang); im+=x[n]*Math.Sin(ang);} 
          double hr=hc[k], hi=(k==0||k==N/2)?0:hc[N-k]; err=Math.Max(err, Math.Max(Math.Abs(hr-re),Math.Abs(hi-im))); mag=Math.Max(mag,Math.Sqrt(re*re+im*im)); }
        worst=Math.Max(worst, err/mag); }
      var y=new float[N]; f.Inverse(hc,y); double e2=0; for(int i=0;i<N;i++) e2=Math.Max(e2, Math.Abs(y[i]/N-x[i])); worstInv=Math.Max(worstInv,e2);
    }
    Console.WriteLine("fwd rel err (vs DFT) " + worst.ToString("E2") + ", roundtrip abs err " + worstInv.ToString("E2"));
    return 0;
  }
}
