using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Translated
{
    // A message-only listener. Raw input records interaction boundaries; no typed text is collected.
    internal sealed class InputWindow : NativeWindow, IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Device { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)]
        struct Header { public uint Type, Size; public IntPtr Device, Param; }
        [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint header);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr handle, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessagePos();
        delegate void ForegroundEvent(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, ForegroundEvent callback,
            uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        static readonly uint ReopenMessage = RegisterWindowMessage("Translated.Windows.Reopen.82878f02");
        const uint CustomShortcutMessage = 0x8000 + 117;
        readonly int header = Marshal.SizeOf<Header>();
        readonly IntPtr packet = Marshal.AllocHGlobal(64);
        readonly HotkeyBinding binding;
        readonly ShortcutHooks hooks = new ShortcutHooks();
        readonly ForegroundEvent foregroundEvent;
        readonly IntPtr foregroundHook;
        IntPtr lastForeground;
        int triggerDown, selectionKey, rawModifiers;
        int ignoredMouseDown;
        int ignoredMouseTick;
        bool escapeRegistered;
        int revision;
        readonly bool rawInput;
        public int Revision => Volatile.Read(ref revision);
        public bool Monitoring { get; }
        public bool Registered => binding.Current != null;
        internal bool ShortcutHeld => Desktop.ModifiersDown || (binding.Current?.Keys.Any(Desktop.Down) ?? false);
        public event Action ShortcutPressed, EscapePressed, Reopen, KeyboardSelection;
        public event Action<Point?> Interacted;
        public event Action<Point, bool> LeftButton;

        public InputWindow()
        {
            // A hidden top-level window also receives our second-instance broadcast.
            CreateHandle(new CreateParams { Caption = "Translated.Input" });
            binding = new HotkeyBinding((id, combo) => combo.Native
                ? RegisterHotKey(Handle, id, (uint)combo.Modifiers | 0x4000, (uint)combo.Key) : hooks.Register(id, combo),
                id => { UnregisterHotKey(Handle, id); hooks.Unregister(id); });
            hooks.Triggered += (id, key) => {
                PostMessage(Handle, CustomShortcutMessage, (IntPtr)id, (IntPtr)key);
            };
            var devices = new[] { DeviceFor(2, 0x100), DeviceFor(6, 0x100) };
            rawInput = RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<Device>());
            lastForeground = Desktop.GetForegroundWindow();
            foregroundEvent = (h, e, w, o, c, t, ms) => {
                if (w != lastForeground) { lastForeground = w; Changed(); }
            };
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundEvent, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
            Monitoring = rawInput && foregroundHook != IntPtr.Zero;
        }

        Device DeviceFor(ushort usage, uint flags) => new Device { Page = 1, Usage = usage, Flags = flags,
            Target = flags == 1 ? IntPtr.Zero : Handle };
        public static void ShowExisting() => PostMessage(new IntPtr(0xffff), ReopenMessage, IntPtr.Zero, IntPtr.Zero);

        // Keep the old registration until both persistence and the candidate registration succeed.
        public void Apply(Shortcut candidate, Action persist) => binding.Apply(candidate, persist);
        internal void BeginCapture(IntPtr window, Action<int, bool> receive) => hooks.Capture(window, receive);
        internal void EndCapture() => hooks.EndCapture();
        internal void WatchEscape(bool active)
        {
            if (active && !escapeRegistered) escapeRegistered = RegisterHotKey(Handle,3,0x4000,0x1B);
            else if (!active && escapeRegistered) { UnregisterHotKey(Handle,3); escapeRegistered = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if ((m.Msg == 0x312 || (uint)m.Msg == CustomShortcutMessage) && !hooks.Capturing &&
                binding?.Current != null && m.WParam.ToInt32() == binding.Id)
            {
                if ((uint)m.Msg == CustomShortcutMessage)
                {
                    int key = m.LParam.ToInt32();
                    ignoredMouseDown = key == 4 ? 0x10 : key == 5 ? 0x40 : key == 6 ? 0x100 : 0;
                    ignoredMouseTick = Environment.TickCount;
                }
                triggerDown = Desktop.Down(binding.Current.Key) ? binding.Current.Key : 0;
                ShortcutPressed?.Invoke();
            }
            else if (m.Msg == 0x312 && m.WParam.ToInt32() == 3 && escapeRegistered && !hooks.Capturing) EscapePressed?.Invoke();
            else if ((uint)m.Msg == ReopenMessage) Reopen?.Invoke();
            else if (m.Msg == 0xff) ReadInteraction(m.LParam);
            base.WndProc(ref m);
        }

        void Changed(Point? leftDown = null) { selectionKey = 0; Interlocked.Increment(ref revision); Interacted?.Invoke(leftDown); }
        void ReadInteraction(IntPtr raw)
        {
            uint bytes = 64;
            if (GetRawInputData(raw, 0x10000003, packet, ref bytes, (uint)header) == uint.MaxValue) return;
            int type = Marshal.ReadInt32(packet);
            if (type == 1)
            {
                int vk = (ushort)Marshal.ReadInt16(packet, header + 6);
                if (Marshal.ReadInt32(packet, header + 12) == Desktop.SyntheticTag) return;
                bool up = (Marshal.ReadInt16(packet, header + 2) & 1) != 0;
                int modifier = vk == 0x10 || vk == 0xA0 || vk == 0xA1 ? Shortcut.Shift :
                    vk == 0x11 || vk == 0xA2 || vk == 0xA3 ? Shortcut.Ctrl :
                    vk == 0x12 || vk == 0xA4 || vk == 0xA5 ? Shortcut.Alt :
                    vk == 0x5B || vk == 0x5C ? Shortcut.Win : 0;
                if (modifier != 0)
                {
                    rawModifiers = up ? rawModifiers & ~modifier : rawModifiers | modifier;
                    return;
                }
                if (up)
                {
                    if (vk == triggerDown) triggerDown = 0;
                    if (vk == selectionKey) { selectionKey = 0; KeyboardSelection?.Invoke(); }
                    return;
                }
                if (vk == triggerDown || (binding.Current?.Keys.Contains(vk) == true && binding.Current.Modifiers == rawModifiers &&
                    (binding.Current.Native || hooks.Active(binding.Id)))) return;
                Changed();
                selectionKey = SelectionPolicy.KeyboardGesture(vk,(rawModifiers & Shortcut.Shift)!=0 || Desktop.Down(0x10),
                    (rawModifiers & Shortcut.Ctrl)!=0 || Desktop.Down(0x11),(rawModifiers & Shortcut.Alt)!=0 || Desktop.Down(0x12),
                    (rawModifiers & Shortcut.Win)!=0 || Desktop.Down(0x5B) || Desktop.Down(0x5C)) ? vk : 0;
                if (vk == 0x1B) EscapePressed?.Invoke();
            }
            else if (type == 0)
            {
                int buttons = (ushort)Marshal.ReadInt16(packet, header + 4);
                if (unchecked((uint)(Environment.TickCount-ignoredMouseTick)) > 100) ignoredMouseDown = 0;
                int ignore = buttons & ignoredMouseDown;
                ignoredMouseDown &= ~ignore;
                buttons &= ~ignore;
                if (buttons == 0) return;
                int pos = GetMessagePos();
                var point = new Point((short)pos, (short)(pos >> 16));
                if ((buttons & 0xD55) != 0) Changed((buttons & 1) != 0 ? point : (Point?)null);
                if ((buttons & 1) != 0) LeftButton?.Invoke(point, true);
                if ((buttons & 2) != 0) LeftButton?.Invoke(point, false);
            }
        }

        public void Dispose()
        {
            binding.Release();
            hooks.Dispose();
            WatchEscape(false);
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            if (rawInput) RegisterRawInputDevices(new[] { DeviceFor(2, 1), DeviceFor(6, 1) }, 2, (uint)Marshal.SizeOf<Device>());
            DestroyHandle();
            Marshal.FreeHGlobal(packet);
        }
    }
}
