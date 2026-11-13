#if NET48
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Translated.Tests
{
    internal static class ShortcutDesktopChecks
    {
        [StructLayout(LayoutKind.Sequential)] struct KeyInput
        { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct MouseInput
        { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] struct Union
        { [FieldOffset(0)] public KeyInput Key; [FieldOffset(0)] public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public Union Value; }
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] input, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint key, uint mode);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        static IntPtr targetWindow;
        internal static void InjectKey(int key, bool down, long window)
        {
            if (down && Desktop.GetForegroundWindow() != (IntPtr)window) throw new Exception("Fixture lost focus before input.");
            key = key == 17 ? 0xA2 : key == 16 ? 0xA0 : key == 18 ? 0xA4 : key;
            ushort scan = (ushort)MapVirtualKey((uint)key,0);
            if(scan == 0) throw new Exception("Fixture key has no scan code: "+key);
            keybd_event((byte)key,(byte)scan,down ? 0u : 2u,UIntPtr.Zero);
            System.Threading.Thread.Sleep(100);
        }
        static Task Key(int key, bool down) => Task.Run(() => {
            using (var injector = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--shortcut-key "+key+" "+(down?"1":"0")+" "+targetWindow.ToInt64()) { UseShellExecute=false, CreateNoWindow=true }))
            {
                injector.WaitForExit();
                if (injector.ExitCode != 0) throw new Exception("Fixture keyboard injector failed.");
            }
        });
        static Task Mouse(int key, bool down) => Task.Run(() => Send(new Input { Type = 0, Value = new Union {
            Mouse = new MouseInput { Flags = key == 4 ? (down ? 0x20u : 0x40u) : (down ? 0x80u : 0x100u),
                Data = key == 4 ? 0u : key == 5 ? 1u : 2u } } }));
        static void Send(Input input)
        {
            if (SendInput(1,new[] { input },Marshal.SizeOf<Input>()) != 1) throw new Exception("Fixture input was not sent.");
        }
        static void Require(bool value, string label)
        { if (!value) throw new Exception(label); Console.WriteLine("PASS  "+label); }
        internal static void RunKeyboardFixture()
        {
            Application.EnableVisualStyles();
            using (var input = new InputWindow())
            using (var target = new Form { Text = "Translated keyboard chord fixture", Width = 520, Height = 220 })
            {
                var label = new Label { Dock = DockStyle.Fill, Text = "Press Ctrl + Shift + J + K", TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
                var close = new Button { Dock = DockStyle.Bottom, Text = "Finish keyboard test", Height = 44 };
                target.Controls.Add(label); target.Controls.Add(close);
                int count = 0;
                input.Apply(ShortcutKeys.FromKeys(new[] { 17, 16, 'J', 'K' }),() => {});
                input.ShortcutPressed += () => { count++; label.Text = "Full keyboard chord fired "+count+" time(s)"; };
                close.Click += (s,e) => target.Close();
                Application.Run(target);
                Require(count > 0,"multi-key keyboard shortcut fired through the real desktop input path");
            }
        }
        internal static void Run()
        {
            Application.EnableVisualStyles();
            Exception failure = null;
            using (var target = new Form { Text = "Translated shortcut fixture", Width = 420, Height = 200 })
            using (var input = new InputWindow())
            {
                int count = 0, nativeMouse = 0;
                target.MouseDown += (s,e) => { if(e.Button == MouseButtons.XButton1 || e.Button == MouseButtons.XButton2) nativeMouse++; };
                input.ShortcutPressed += () => count++;
                target.Shown += async (s,e) => {
                    try
                    {
                        SetForegroundWindow(target.Handle);
                        // A background test runner cannot steal foreground activation. Allow the
                        // fixture to be activated explicitly before emitting any test input.
                        for (int wait = 0; wait < 1200 && Desktop.GetForegroundWindow() != target.Handle; wait++)
                            await Task.Delay(100);
                        Require(Desktop.GetForegroundWindow() == target.Handle,"shortcut checks use only their own fixture");
                        targetWindow = target.Handle;
                        Cursor.Position = target.PointToScreen(new System.Drawing.Point(100,100));
                        var captured = new List<string>();
                        input.BeginCapture(target.Handle,(key,down) => captured.Add(key+":"+(down?"down":"up")));
                        await Mouse(5,true); await Task.Delay(70); await Mouse(5,false); await Task.Delay(70);
                        input.EndCapture();
                        Require(captured.Contains("5:down") && captured.Contains("5:up"),"recording sees mouse side-button press and release");
                        input.Apply(ShortcutKeys.FromKeys(new[] { 17, 16, 5 }),() => {});
                        await Task.Delay(100);
                        await Key(17,true); await Task.Delay(30); await Key(16,true); await Task.Delay(30); await Mouse(5,true);
                        await Task.Delay(100);
                        await Mouse(5,true); await Task.Delay(50);
                        Require(count == 1,"Ctrl+Shift+Mouse 4 fires once while held (actual="+count+")");
                        await Mouse(5,false); await Key(16,false); await Key(17,false); await Task.Delay(100);
                        Require(nativeMouse == 0,"bound mouse side button does not reach the fixture");
                        await Mouse(5,true); await Mouse(5,false); await Task.Delay(80);
                        Require(count == 1,"mouse button without required modifiers does not fire");
                        Require(nativeMouse == 1,"unbound side-button combinations retain their normal mouse behavior");
                        input.Apply(ShortcutKeys.FromKeys(new[] { 17, 5, 6 }),() => {});
                        await Key(17,true); await Mouse(5,true); await Task.Delay(50);
                        Require(count == 1,"partial mouse-button chord does not fire");
                        await Mouse(6,true); await Task.Delay(100);
                        await Mouse(6,false); await Mouse(5,false); await Key(17,false);
                        Require(count == 2,"multiple mouse buttons with a keyboard modifier fire globally");
                        input.BeginCapture(target.Handle,(key,down) => {});
                        await Key(17,true); await Mouse(5,true); await Mouse(6,true); await Task.Delay(80);
                        await Mouse(6,false); await Mouse(5,false); await Key(17,false);
                        Require(count == 2,"recording suspends the active custom shortcut");
                        input.EndCapture();
                        bool rolledBack = false;
                        try { input.Apply(new Shortcut { Key = 6 },() => { throw new InvalidOperationException("fixture"); }); }
                        catch (InvalidOperationException) { rolledBack = true; }
                        await Mouse(6,true); await Mouse(6,false); await Task.Delay(80);
                        Require(rolledBack && count == 2,"failed save removes the candidate mouse hook");
                        await Key(17,true); await Mouse(5,true); await Mouse(6,true); await Task.Delay(80);
                        await Mouse(6,false); await Mouse(5,false); await Key(17,false);
                        Require(count == 3,"previous chord still works after save rollback");
                        Shortcut native = null;
                        for (int key = 112; key < 123 && native == null; key++)
                        {
                            var candidate = new Shortcut { Modifiers = 7, Key = key };
                            try { input.Apply(candidate,() => {}); native = candidate; } catch (InvalidOperationException) {}
                        }
                        Require(native != null,"an unoccupied standard shortcut is available for the fixture");
                        await Mouse(5,true); await Mouse(5,false); await Task.Delay(80);
                        Require(count == 3,"switching back to a standard shortcut removes the old mouse binding");
                        await Key(17,true); await Key(16,true); await Key(18,true); await Key(native.Key,true); await Task.Delay(80);
                        await Key(native.Key,false); await Key(18,false); await Key(16,false); await Key(17,false);
                        Require(count == 4,"standard RegisterHotKey still works after custom bindings");
                        input.BeginCapture(target.Handle,(key,down) => {});
                        await Key(17,true); await Key(16,true); await Key(18,true); await Key(native.Key,true); await Task.Delay(80);
                        await Key(native.Key,false); await Key(18,false); await Key(16,false); await Key(17,false);
                        Require(count == 4,"recording also suspends the active RegisterHotKey shortcut");
                        input.EndCapture();
                    }
                    catch (Exception error) { failure = error; }
                    finally
                    {
                        await Mouse(6,false); await Mouse(5,false); await Key(119,false); await Key(18,false); await Key(16,false); await Key(17,false);
                        await Mouse(5,false); await Mouse(6,false);
                        target.Close();
                    }
                };
                Application.Run(target);
            }
            if (failure != null) throw failure;
        }
    }
}
#endif
