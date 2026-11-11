// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pedal
// Minimal stand-ins for the ReBuzz SDK types the machine touches.
using System; using System.Collections.Generic;
namespace Buzz.MachineInterface {
  [Flags] public enum WorkModes { WM_NOIO=0, WM_READ=1, WM_WRITE=2, WM_READWRITE=3 }
  public struct Sample { public float L, R; }
  public struct Note { public byte Value; public Note(int v) { Value = (byte)v; } }
  public class MasterInfo { public int BeatsPerMin, TicksPerBeat, SamplesPerSec, SamplesPerTick, PosInTick; public float TicksPerSec; }
  public interface IBuzzMachine {}
  public interface IBuzzMachineHost { BuzzGUI.Interfaces.IMachine Machine { get; } MasterInfo MasterInfo { get; } int InputChannelCount { get; set; } int OutputChannelCount { get; set; } }
  public class MachineDecl : Attribute { public string Name, ShortName, Author; public int MaxTracks, InputCount, OutputCount; }
  public class ParameterDecl : Attribute { public string Name, Description; public int MinValue, MaxValue; public object DefValue; public bool IsStateless; public string[] ValueDescriptions; }
}
namespace BuzzGUI.Interfaces {
  public interface IMachine { int TrackCount { get; set; } }
  public interface IParameter { string Name { get; } }
  public interface IMenuItem {}
  public interface ISong { event Action<IMachine> MachineAdded; event Action<IMachine> MachineRemoved; int PlayPosition { get; } }
  public interface IBuzz { ISong Song { get; } bool Playing { get; } }
}
namespace BuzzGUI.Common {
  public class MenuItemVM : BuzzGUI.Interfaces.IMenuItem { public string Text; public object Command; }
  public static class Global { public static BuzzGUI.Interfaces.IBuzz Buzz { get; set; } }
  public class SimpleCommand { public Predicate<object> CanExecuteDelegate; public Action<object> ExecuteDelegate; }
}
namespace System.Windows { public static class MessageBox { public static void Show(string a, string b) {} } }
namespace System.Windows {
  public class Application { public static Application Current => null; public FakeDispatcher Dispatcher => null; }
  public enum WindowState { Normal, Minimized, Maximized }
  public class FakeDispatcher { public void BeginInvoke(Delegate d) { d.DynamicInvoke(); } }
}
namespace PedalBlockFX {
  // Stand-in for the WPF EditWindow so the machine glue compiles headless.
  internal sealed class EditWindow {
    public EditWindow(PedalBlockFXMachine m, BuzzGUI.Interfaces.IMachine im, SpectrumTap t) {}
    public System.Windows.WindowState WindowState;
    public System.Windows.FakeDispatcher Dispatcher = new System.Windows.FakeDispatcher();
    public void Show() {} public void Activate() {} public void Close() {}
  }
}
