using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Translated
{
    internal static class Desktop
    {
        public static readonly uint OwnProcess = (uint)Process.GetCurrentProcess().Id;
        internal const int SyntheticTag = 0x54726E73;
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flag);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        static extern IntPtr Send(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        static extern IntPtr GetSelection(IntPtr window, uint message, ref int start, ref int end, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr Replace(IntPtr window, uint message, IntPtr undo, string text, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
        static extern IntPtr ReadText(IntPtr window, uint message, IntPtr capacity, [Out] StringBuilder buffer, uint flags, uint timeout, out IntPtr length);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct GuiInfo
        {
            public int Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputData { [FieldOffset(0)] public Keyboard Key; [FieldOffset(0)] public Mouse Mouse; }
        [StructLayout(LayoutKind.Sequential)]
        struct Input { public uint Type; public InputData Data; }

        public static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        public static bool ModifiersDown => Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);
        public static uint ProcessOf(IntPtr window) { uint p; GetWindowThreadProcessId(window, out p); return p; }
        public static bool External(IntPtr window) => window != IntPtr.Zero && ProcessOf(window) != OwnProcess;
        public static IntPtr RootAt(Point p) => GetAncestor(WindowFromPoint(p), 2);
        public static IntPtr Focus(IntPtr foreground)
        {
            uint p; var thread = GetWindowThreadProcessId(foreground, out p);
            var info = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
            return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
        }

        static string ClassName(IntPtr window)
        {
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            return name.ToString();
        }
        static bool ClassicEdit(string cls) => cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase);
        public static bool NativeEdit(IntPtr window)
        {
            string cls = ClassName(window);
            return ClassicEdit(cls) ||
                cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                cls.StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase);
        }

        public static string ReadClassicRange(IntPtr edit, int start, int end)
        {
            // Standard EDIT uses the same UTF-16 offsets for WM_GETTEXT and EM_GETSEL.
            // RichEdit may normalize paragraph delimiters differently; it uses UIA or WM_COPY.
            if (!ClassicEdit(ClassName(edit)) || end > 1000000 || Password(edit)) return null;
            var buffer = new StringBuilder(end + 1);
            IntPtr copied;
            int a, b;
            if (ReadText(edit, 0xD, (IntPtr)buffer.Capacity, buffer, 2, 300, out copied) == IntPtr.Zero ||
                copied.ToInt64() < end || buffer.Length < end || !Range(edit, out a, out b) ||
                a != start || b != end || Password(edit)) return null;
            return buffer.ToString(start, end - start);
        }

        internal static Point FocusPoint(IntPtr window, IntPtr focus)
        {
            uint thread = GetWindowThreadProcessId(window,out _);
            var info = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
            if (GetGUIThreadInfo(thread,ref info) && info.Caret != IntPtr.Zero)
            {
                var p = new Point(info.Left,info.Bottom);
                if (ClientToScreen(info.Caret,ref p)) return p;
            }
            GetWindowRect(focus == IntPtr.Zero ? window : focus,out var rect);
            return new Point(rect.Left + (rect.Right-rect.Left)/2,rect.Top + (rect.Bottom-rect.Top)/2);
        }

        internal static Point? CharacterPoint(IntPtr edit, int index)
        {
            if (!ClassicEdit(ClassName(edit)) || Send(edit,0xD6,(IntPtr)index,IntPtr.Zero,2,200,out var result) == IntPtr.Zero ||
                result.ToInt32() == -1) return null;
            int xy = result.ToInt32();
            var p = new Point((short)xy,(short)(xy >> 16));
            return ClientToScreen(edit,ref p) ? p : (Point?)null;
        }

        internal static bool OverSelectedText(IntPtr edit, Point point, int start, int end)
        {
            if (WindowFromPoint(point) != edit) return false;
            var client = point;
            if (!ScreenToClient(edit,ref client)) return false;
            int xy = (client.X & 0xffff) | (client.Y << 16);
            if (Send(edit,0xD7,IntPtr.Zero,(IntPtr)xy,2,200,out var result) == IntPtr.Zero) return false;
            // EM_CHARFROMPOS truncates offsets to 16 bits. UIA handles longer documents.
            if (end > ushort.MaxValue) return false;
            int at = Math.Min(end-1,result.ToInt32() & 0xffff);
            if (at < start) return false;
            Point? pos = CharacterPoint(edit,at), next = CharacterPoint(edit,at+1);
            if (pos == null) return false;
            var p = pos.Value;
            int height = (int)Math.Ceiling(18*OverlayPlacement.Scale(point));
            int right = next.HasValue && next.Value.Y == p.Y ? next.Value.X : p.X+height;
            return point.Y >= p.Y-2 && point.Y < p.Y+height && point.X >= p.X-3 && point.X <= right+2;
        }

        public static bool Password(IntPtr edit)
        {
            if ((GetWindowLong(edit, -16) & 0x20) != 0) return true;
            IntPtr c;
            return Send(edit, 0xD2, IntPtr.Zero, IntPtr.Zero, 2, 200, out c) == IntPtr.Zero || c != IntPtr.Zero;
        }

        public static bool NativeWritable(IntPtr edit) => IsWindowEnabled(edit) && (GetWindowLong(edit, -16) & 0x800) == 0;
        static bool Offsets(IntPtr edit, out int start, out int end)
        {
            start = end = -1;
            IntPtr ignored;
            return GetSelection(edit, 0xB0, ref start, ref end, 2, 250, out ignored) != IntPtr.Zero && start >= 0 && end >= start;
        }
        public static bool Range(IntPtr edit, out int start, out int end) => Offsets(edit,out start,out end) && end > start;

        internal static bool VerifyClassicInsertion(IntPtr edit, int originalStart, string text)
        {
            if (!ClassicEdit(ClassName(edit)) || Password(edit) || !Offsets(edit,out int start,out int end) || start != end || end > 1000000)
                return false;
            string expected = text.Replace("\r\n","\n").Replace('\r','\n').Replace("\n","\r\n");
            if (end != originalStart + expected.Length) return false;
            var buffer = new StringBuilder(end+1);
            return ReadText(edit,0xD,(IntPtr)buffer.Capacity,buffer,2,300,out var copied) != IntPtr.Zero &&
                copied.ToInt64() >= end && buffer.Length >= end && buffer.ToString(originalStart,expected.Length) == expected;
        }

        public static bool NativeCopy(IntPtr edit)
        {
            IntPtr ignored;
            return Send(edit, 0x301, IntPtr.Zero, IntPtr.Zero, 2, 500, out ignored) != IntPtr.Zero;
        }

        public static bool NativeReplace(IntPtr edit, string text)
        {
            // EM_REPLACESEL is marshalled by USER32 and targets only this edit's current range.
            IntPtr ignored;
            return Replace(edit, 0xC2, new IntPtr(1), text.Replace("\r\n", "\n").Replace('\r','\n').Replace("\n","\r\n"),
                2, 1000, out ignored) != IntPtr.Zero;
        }

        public static bool Chord(int key)
        {
            Input K(int code, bool up) => new Input { Type = 1, Data = new InputData {
                Key = new Keyboard { Key = (ushort)code, Flags = up ? 2u : 0u, Extra = (UIntPtr)SyntheticTag } } };
            var keys = new[] { K(0x11, false), K(key, false), K(key, true), K(0x11, true) };
            return !ModifiersDown && SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) == keys.Length;
        }
    }
}
