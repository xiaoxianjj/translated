#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Translated.Tests
{
    // Runs on an interactive Windows desktop. The external fixture is the only write target.
    internal static class WindowsDesktopChecks
    {
        const int SelectField = 0x8001, CheckText = 0x8002, HoldCopy = 0x8003, WasHeld = 0x8004;
        const string Original = "left hello middle hello right";
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, int msg, IntPtr w, IntPtr l,
            uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window, int id, uint mods, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();

        sealed class CopyBox : TextBox
        {
            internal bool HoldNextCopy, DidHold;
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x301 || !HoldNextCopy) return;
                HoldNextCopy = false;
                DidHold = false;
                for (int i=0;i<35 && !DidHold;i++) { DidHold=OpenClipboard(Handle); if(!DidHold)Thread.Sleep(4); }
                if (!DidHold) return;
                var release = new System.Windows.Forms.Timer { Interval = 1400 };
                release.Tick += (s,e) => { release.Stop(); CloseClipboard(); release.Dispose(); };
                release.Start();
            }
        }

        sealed class Fixture : Form
        {
            readonly TextBox edit = new TextBox { Text = Original, Dock = DockStyle.Top };
            readonly CopyBox read = new CopyBox { Text = Original, Dock = DockStyle.Top, ReadOnly = true };
            readonly TextBox password = new TextBox { Text = "fixture", Dock = DockStyle.Top, UseSystemPasswordChar = true };
            internal Fixture() { Text = "Translated desktop test"; Controls.AddRange(new Control[] { edit, read, password }); }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == SelectField)
                {
                    int field = (int)m.WParam;
                    var box = field == 2 ? read : field == 3 ? password : edit;
                    Activate(); box.Focus(); box.Select(field == 1 ? 18 : field == 3 ? 0 : 5, field == 4 ? 0 : 5);
                    m.Result = (IntPtr)1; return;
                }
                if (m.Msg == HoldCopy) { read.HoldNextCopy = true; m.Result = (IntPtr)1; return; }
                if (m.Msg == WasHeld) { m.Result = (IntPtr)(read.DidHold ? 1 : 0); return; }
                if (m.Msg == CheckText) { m.Result = edit.Text == "left hello middle 你好 right" ? (IntPtr)1 : IntPtr.Zero; return; }
                base.WndProc(ref m);
            }
        }

        static void Expect(bool value, string name)
        { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS  " + name); }

        static IntPtr MessageTo(IntPtr target, int message, int value)
        {
            IntPtr result;
            if (SendMessageTimeout(target, message, (IntPtr)value, IntPtr.Zero, 2, 1000, out result) == IntPtr.Zero)
                throw new InvalidOperationException("Fixture did not respond.");
            return result;
        }

        internal static void Run(bool fixture)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Use a Windows desktop.");
            Application.EnableVisualStyles();
            if (fixture) { Application.Run(new Fixture()); return; }
            Exception failure = null;
            EventHandler start = null;
            start = async (s,e) => {
                Application.Idle -= start;
                try { await Check(); } catch (Exception ex) { failure = ex; }
                finally { Application.ExitThread(); }
            };
            Application.Idle += start;
            Application.Run();
            if (failure != null) throw new InvalidOperationException("Windows desktop check failed", failure);
        }

        static async Task Check()
        {
            using (var input = new InputWindow())
            using (var process = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--fixture") { UseShellExecute = false }))
            {
                try
                {
                    Shortcut first = null;
                    foreach (int n in Enumerable.Range(0,11))
                    {
                        var candidate = new Shortcut { Modifiers = 7, Key = Shortcut.F1 + n };
                        try { input.Apply(candidate, () => {}); first = candidate; break; } catch (InvalidOperationException) {}
                    }
                    Expect(first != null, "Windows registers a global shortcut");
                    int taken = Enumerable.Range(Shortcut.F1,11).First(k => k != first.Key && RegisterHotKey(IntPtr.Zero, 3171, 7, (uint)k));
                    try
                    {
                        bool conflict = false;
                        try { input.Apply(new Shortcut { Key=taken, Modifiers=7 }, () => {}); } catch (InvalidOperationException) { conflict = true; }
                        bool couldStealOld = RegisterHotKey(IntPtr.Zero, 3172, 7, (uint)first.Key);
                        if (couldStealOld) UnregisterHotKey(IntPtr.Zero, 3172);
                        Expect(conflict && !couldStealOld, "conflict retains the original OS registration");
                    }
                    finally { UnregisterHotKey(IntPtr.Zero,3171); }

                    for (int i=0; i<100 && process.MainWindowHandle==IntPtr.Zero; ++i) { await Task.Delay(50); process.Refresh(); }
                    IntPtr window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) throw new InvalidOperationException("No fixture window.");
                    SetForegroundWindow(window);
                    for (int i=0; i<900 && Desktop.GetForegroundWindow()!=window; ++i) await Task.Delay(50);
                    if (Desktop.GetForegroundWindow()!=window) throw new InvalidOperationException("Fixture did not become the foreground window.");
                    async Task<Selection> Select(int field)
                    {
                        SetForegroundWindow(window); MessageTo(window,SelectField,field); await Task.Delay(120);
                        return await SelectionReader.Query(() => SelectionReader.Read(window,null,false),CancellationToken.None);
                    }
                    var firstWord = await Select(0);
                    Expect(firstWord != null && firstWord.Writable && firstWord.Text=="hello", "selected text in an editable external field");
                    var secondWord = await Select(1);
                    var changed = await SelectionReader.Recheck(firstWord, CancellationToken.None);
                    Expect(changed == null && secondWord != null && secondWord.Foreground && secondWord.Native, "moving to an identical word invalidates the old range");
                    Expect(Desktop.NativeReplace(secondWord.Focus, "你好") && MessageTo(window,CheckText,0) != IntPtr.Zero,
                        "native replacement preserves both surrounding spans");
                    var readOnly = await Select(2);
                    Expect(readOnly != null && !readOnly.Writable && readOnly.Text=="hello", "read-only selection routes away from replacement");
                    // Exercise only fixture text. Capture and restore the existing clipboard
                    // without interpreting it; never overwrite an intervening user's copy.
                    ClipboardAccess.Snapshot prior;
                    uint fixtureRevision = ClipboardAccess.Write(input.Handle, "fixture clipboard before", ClipboardAccess.Sequence, true, out prior);
                    using (prior)
                    {
                        try
                        {
                            var copied = await ClipboardAccess.ReadSelection(input.Handle, readOnly, fixtureRevision, () => true);
                            fixtureRevision = ClipboardAccess.Sequence;
                            Expect(copied != null && copied.Text == "hello" && Clipboard.GetText() == "fixture clipboard before",
                                "source copy restores original fixture clipboard");
                            int checks = 0;
                            copied = await ClipboardAccess.ReadSelection(input.Handle, readOnly, fixtureRevision, () => ++checks == 1);
                            fixtureRevision = ClipboardAccess.Sequence;
                            Expect(copied == null && Clipboard.GetText() == "fixture clipboard before",
                                "interruption after synchronous copy still restores clipboard");
                            MessageTo(window, HoldCopy, 0);
                            copied = await ClipboardAccess.ReadSelection(input.Handle, readOnly, fixtureRevision, () => true);
                            fixtureRevision = ClipboardAccess.Sequence;
                            bool held=MessageTo(window,WasHeld,0)!=IntPtr.Zero;
                            bool recovered=Clipboard.GetText()=="fixture clipboard before";
                            if(!held || copied!=null || !recovered)throw new InvalidOperationException("Clipboard lock fixture: held="+held+", returned="+(copied!=null)+", restored="+recovered);
                            Expect(held && recovered,
                                "timed-out copy polling still restores the observed generation after lock release");
                            checks = 0;
                            copied = await ClipboardAccess.ReadSelection(input.Handle, readOnly, fixtureRevision, () => {
                                if (++checks == 1) return true;
                                ClipboardAccess.Snapshot unused;
                                fixtureRevision = ClipboardAccess.Write(input.Handle, "fixture newer copy", ClipboardAccess.Sequence, false, out unused);
                                return false;
                            });
                            Expect(copied == null && ClipboardAccess.Sequence == fixtureRevision && Clipboard.GetText() == "fixture newer copy",
                                "cleanup never overwrites a newer clipboard generation");
                        }
                        finally
                        {
                            // No test writes share this private HWND with a user clipboard action.
                            uint last = ClipboardAccess.Sequence;
                            if (ClipboardAccess.Owner == input.Handle) await ClipboardAccess.Restore(input.Handle,prior,last);
                        }
                    }
                    Expect(await Select(3) == null, "password field excluded");
                    Expect(await Select(4) == null, "empty selection excluded");
                }
                finally { if (!process.HasExited) process.CloseMainWindow(); }
            }
        }
    }
}
#endif
