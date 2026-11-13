using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Forms = System.Windows.Forms;

namespace Translated
{
    // Callbacks only update key state and enqueue messages; no UIA, I/O or rendering in a hook.
    internal sealed class ShortcutHooks : IDisposable
    {
        delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct KeyboardData
        { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct MouseData
        { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        readonly Hook keyboardCallback, mouseCallback;
        readonly Dictionary<int, Shortcut> bindings = new Dictionary<int, Shortcut>();
        readonly Dictionary<int, ShortcutChordState> states = new Dictionary<int, ShortcutChordState>();
        readonly HashSet<int> swallowed = new HashSet<int>();
        IntPtr keyboard, mouse, captureWindow;
        Action<int, bool> captured;
        Thread worker;
        Forms.Control messages;
        readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        Exception workerFailure;
        bool disposed;
        volatile bool captureActive;
        int activeBindings;
        internal bool Capturing => captureActive;
        internal event Action<int, int> Triggered;
        internal bool Active(int id) => (Volatile.Read(ref activeBindings) & (1 << id)) != 0;

        internal ShortcutHooks() { keyboardCallback = Keyboard; mouseCallback = Mouse; }
        T OnInputThread<T>(Func<T> action)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ShortcutHooks));
            if (worker == null)
            {
                worker = new Thread(() => {
                    try
                    {
                        using (var control = new Forms.Control())
                        {
                            messages = control;
                            var handle = control.Handle;
                            ready.Set();
                            Forms.Application.Run();
                        }
                    }
                    catch (Exception error) { workerFailure = error; ready.Set(); }
                    finally { StopHooks(); captureActive = false; }
                }) { IsBackground = true, Name = "Translated shortcut input" };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            if (!ready.Wait(4000) || workerFailure != null || messages == null || messages.IsDisposed)
                throw new InvalidOperationException("无法启动输入设备监听，请关闭窗口后重试。", workerFailure);
            return Thread.CurrentThread == worker ? action() : (T)messages.Invoke(action);
        }
        bool Ensure()
        {
            if (keyboard != IntPtr.Zero && mouse != IntPtr.Zero) return true;
            keyboard = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
            mouse = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
            if (keyboard != IntPtr.Zero && mouse != IntPtr.Zero) return true;
            StopHooks(); return false;
        }
        internal bool Register(int id, Shortcut shortcut) => OnInputThread(() =>
        {
            if (!Ensure()) return false;
            bindings[id] = shortcut;
            states[id] = new ShortcutChordState();
            return true;
        });
        internal void Unregister(int id)
        {
            if (worker == null) return;
            OnInputThread(() => {
                bindings.Remove(id); states.Remove(id);
                Volatile.Write(ref activeBindings, activeBindings & ~(1 << id));
                ReleaseIfUnused();
                return true;
            });
        }
        internal void Capture(IntPtr window, Action<int, bool> receive)
        {
            OnInputThread(() => {
                if (CapturedElsewhere(window)) throw new InvalidOperationException("另一个快捷键录制窗口正在使用。");
                if (!Ensure()) throw new InvalidOperationException("无法监听输入设备，请关闭窗口后重试。");
                captureWindow = window; captured = receive; captureActive = true;
                foreach (var state in states.Values) state.Reset();
                Volatile.Write(ref activeBindings, 0);
                return true;
            });
        }
        bool CapturedElsewhere(IntPtr window) => Capturing && captureWindow != window;
        internal void EndCapture()
        {
            if (worker == null) return;
            OnInputThread(() => {
                captured = null; captureWindow = IntPtr.Zero; captureActive = false;
                foreach (var state in states.Values) state.Reset();
                Volatile.Write(ref activeBindings, 0);
                ReleaseIfUnused();
                return true;
            });
        }
        // Keep one hook pair after first use. In particular, a capture may end with
        // key-up messages in flight; rehooking immediately can lose the next keyboard
        // transition. With no binding/capture the callbacks simply pass input through.
        void ReleaseIfUnused() { if (bindings.Count == 0 && !Capturing) states.Clear(); }
        IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                var input = Marshal.PtrToStructure<KeyboardData>(data);
                if (input.Extra.ToUInt64() != Desktop.SyntheticTag)
                {
                    int msg = message.ToInt32();
                    if ((msg == 0x100 || msg == 0x104 || msg == 0x101 || msg == 0x105) &&
                        Input((int)input.Key, msg == 0x100 || msg == 0x104)) return (IntPtr)1;
                }
            }
            return CallNextHookEx(keyboard, code, message, data);
        }
        IntPtr Mouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int msg = message.ToInt32();
                if (msg == 0x207 || msg == 0x208 || msg == 0x20B || msg == 0x20C)
                {
                    var input = Marshal.PtrToStructure<MouseData>(data);
                    int key = msg == 0x207 || msg == 0x208 ? ShortcutKeys.Middle :
                        ((input.Data >> 16) & 0xffff) == 1 ? ShortcutKeys.Mouse4 : ShortcutKeys.Mouse5;
                    if (input.Extra.ToUInt64() != Desktop.SyntheticTag && Input(key, msg == 0x207 || msg == 0x20B)) return (IntPtr)1;
                }
            }
            return CallNextHookEx(mouse, code, message, data);
        }
        bool Input(int key, bool pressed)
        {
            bool consumed = !pressed && swallowed.Remove(key);
            if (Capturing)
            {
                // Keep the recording local to the dialog. Switching apps never records outside it.
                if (Desktop.GetForegroundWindow() == captureWindow)
                {
                    captured(key, pressed);
                    if (pressed) swallowed.Add(key);
                    return true;
                }
                return consumed;
            }
            foreach (var item in bindings)
            {
                // A modifier can already be held when recording ends or a binding changes.
                // Reconcile real keyboard state at the mouse transition, before matching.
                if (ShortcutKeys.Mouse(key)) states[item.Key].SynchronizeKeyboard(Desktop.Down);
                bool fire = states[item.Key].Update(item.Value, key, pressed);
                int mask = 1 << item.Key;
                Volatile.Write(ref activeBindings, states[item.Key].Active ? activeBindings | mask : activeBindings & ~mask);
                if (fire)
                {
                    if (pressed) swallowed.Add(key);
                    Triggered?.Invoke(item.Key, ShortcutKeys.Normalize(key));
                    consumed = true;
                }
            }
            return consumed || pressed && swallowed.Contains(key);
        }
        void StopHooks()
        {
            if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
            if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
            keyboard = mouse = IntPtr.Zero;
            swallowed.Clear();
        }
        public void Dispose()
        {
            if (disposed) return;
            if (worker != null && messages != null && !messages.IsDisposed)
            {
                OnInputThread(() => {
                    captured = null; captureActive = false; bindings.Clear(); states.Clear();
                    StopHooks(); Forms.Application.ExitThread(); return true;
                });
                if (Thread.CurrentThread != worker) worker.Join(2000);
            }
            disposed = true; ready.Dispose();
        }
    }
}
