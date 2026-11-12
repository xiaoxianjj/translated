using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Translated
{
    internal static class ClipboardAccess
    {
        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] static extern uint EnumClipboardFormats(uint format);
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClipboardFormatName(uint format, StringBuilder name, int size);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr handle);
        [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
        [DllImport("ole32.dll")] static extern IntPtr OleDuplicateData(IntPtr source, ushort format, uint flags);
        [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CopyEnhMetaFile(IntPtr source, string file);
        internal static uint Sequence => GetClipboardSequenceNumber();
        internal static IntPtr Owner => GetClipboardOwner();

        static void Open(IntPtr owner)
        {
            for (int i = 0; i < 6; ++i)
            {
                if (OpenClipboard(owner)) return;
                Thread.Sleep(8);
            }
            throw new InvalidOperationException("剪贴板正在被占用，请重试。");
        }

        // Native copies keep HTML, RTF, file drops and images without deserializing clipboard objects.
        sealed class Item
        {
            public uint Format;
            public STGMEDIUM Medium;
        }

        internal sealed class Snapshot : IDisposable
        {
            readonly List<Item> items = new List<Item>();
            internal uint Revision;
            internal static Snapshot CaptureOpen()
            {
                var snapshot = new Snapshot();
                try
                {
                    uint format = 0;
                    while (true)
                    {
                        SetLastError(0);
                        format = EnumClipboardFormats(format);
                        if (format == 0)
                        {
                            if (Marshal.GetLastWin32Error() != 0) throw Unsupported();
                            break;
                        }
                        if (format >= 0x80 && format < 0xC000) throw Unsupported();
                        if (format >= 0xC000)
                        {
                            var name = new StringBuilder(128);
                            GetClipboardFormatName(format, name, name.Capacity);
                            // Marshalled OLE objects can refer back to a live source application.
                            string label = name.ToString();
                            if (label == "DataObject" || label == "Ole Private Data" ||
                                label == "Embed Source" || label == "Embedded Object" || label == "Link Source") throw Unsupported();
                        }
                        IntPtr source = GetClipboardData(format);
                        if (source == IntPtr.Zero) throw Unsupported();
                        IntPtr duplicate = format == 14 ? CopyEnhMetaFile(source, null) : OleDuplicateData(source, (ushort)format, 0);
                        if (duplicate == IntPtr.Zero) throw Unsupported();
                        var type = format == 14 ? TYMED.TYMED_ENHMF : format == 3 ? TYMED.TYMED_MFPICT :
                            format == 2 || format == 9 ? TYMED.TYMED_GDI : TYMED.TYMED_HGLOBAL;
                        snapshot.items.Add(new Item { Format = format, Medium = new STGMEDIUM { tymed = type, unionmember = duplicate } });
                    }
                    snapshot.Revision = Sequence;
                    return snapshot;
                }
                catch { snapshot.Dispose(); throw; }
            }

            static Exception Unsupported() => new InvalidOperationException("当前剪贴板格式无法完整保留，可在翻译窗口粘贴原文。");

            internal void RestoreOpen()
            {
                if (!EmptyClipboard()) throw new InvalidOperationException("无法恢复剪贴板。");
                bool failed = false;
                foreach (var item in items)
                {
                    if (SetClipboardData(item.Format, item.Medium.unionmember) == IntPtr.Zero) failed = true;
                    else item.Medium.unionmember = IntPtr.Zero;
                }
                if (failed) throw new InvalidOperationException("Windows 未能恢复全部剪贴板格式。");
            }
            public void Dispose()
            {
                foreach (var item in items)
                    if (item.Medium.unionmember != IntPtr.Zero) ReleaseStgMedium(ref item.Medium);
                items.Clear();
            }
        }

        static void Check(uint expected)
        {
            if (expected != Sequence) throw new InvalidOperationException("剪贴板内容已变化。");
        }

        // USER32 can synthesize CF_LOCALE and advance the sequence at CloseClipboard.
        // Accept the post-close generation only for our private, single-writer HWND.
        static uint ClosedGeneration(IntPtr owner, uint beforeClose)
        {
            uint after = Sequence;
            return GetClipboardOwner() == owner && Sequence == after ? after : beforeClose;
        }

        internal static async Task<uint?> Restore(IntPtr owner, Snapshot saved, uint expected)
        {
            for (int i = 0; i < 36; ++i)
            {
                if (Sequence != expected) return null;
                if (OpenClipboard(owner))
                {
                    uint restored;
                    try
                    {
                        if (Sequence != expected) return null;
                        saved.RestoreOpen();
                        restored = Sequence;
                    }
                    finally { CloseClipboard(); }
                    return ClosedGeneration(owner,restored);
                }
                await Task.Delay(40);
            }
            throw new InvalidOperationException("剪贴板持续被占用，未能恢复原内容。");
        }

        internal static uint Write(IntPtr owner, string text, uint expected, bool temporary, out Snapshot saved)
        {
            saved = null;
            string value = text.Replace("\r\n", "\n").Replace('\r','\n').Replace("\n","\r\n");
            var chars = (value + "\0").ToCharArray();
            IntPtr handle = GlobalAlloc(2, (UIntPtr)(chars.Length * 2));
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            try
            {
                var data = GlobalLock(handle);
                if (data == IntPtr.Zero) throw new OutOfMemoryException();
                try { Marshal.Copy(chars, 0, data, chars.Length); }
                finally { GlobalUnlock(handle); }
                Open(owner);
                uint written;
                try
                {
                    Check(expected);
                    if (temporary) saved = Snapshot.CaptureOpen();
                    if (!EmptyClipboard()) throw new InvalidOperationException("无法写入剪贴板。");
                    if (SetClipboardData(13, handle) == IntPtr.Zero)
                    {
                        saved?.RestoreOpen();
                        throw new InvalidOperationException("无法写入剪贴板。");
                    }
                    handle = IntPtr.Zero;
                    written = Sequence;
                }
                catch { saved?.Dispose(); saved = null; throw; }
                finally { CloseClipboard(); }
                return ClosedGeneration(owner,written);
            }
            finally { if (handle != IntPtr.Zero) GlobalFree(handle); }
        }

        internal sealed class CopyResult { public string Text; public uint After; }

        internal static async Task<CopyResult> ReadSelection(IntPtr owner, Selection selection, uint expected, Func<bool> stillCurrent)
        {
            Snapshot saved;
            Open(owner);
            try { Check(expected); saved = Snapshot.CaptureOpen(); }
            finally { CloseClipboard(); }
            using (saved)
            {
                if (!stillCurrent() || Sequence != saved.Revision) return null;
                bool sent = selection.Native ? Desktop.NativeCopy(selection.Focus) : Desktop.Chord('C');
                // A synchronous WM_COPY can change the clipboard even if the selection
                // is invalidated immediately afterward. Remember that exact generation.
                uint candidate = Sequence;
                uint copied = selection.Native && candidate != saved.Revision &&
                    Desktop.ProcessOf(GetClipboardOwner()) == Desktop.ProcessOf(selection.Window) && Sequence == candidate
                    ? candidate : saved.Revision;
                try
                {
                if (!sent) return null;
                for (int i = 0; i < 30; ++i)
                {
                    bool current = stillCurrent();
                    if (!current) return null;
                    // Observe the exact generation while the gesture is still stable,
                    // even if another process temporarily holds the clipboard lock.
                    // After interruption, an unobserved asynchronous copy is ambiguous:
                    // leave it untouched rather than overwrite a newer user copy.
                    candidate = Sequence;
                    if (copied == saved.Revision && candidate != saved.Revision &&
                        Desktop.ProcessOf(GetClipboardOwner()) == Desktop.ProcessOf(selection.Window) &&
                        Sequence == candidate && stillCurrent()) copied = candidate;
                    if (copied != saved.Revision && Sequence != copied) return null;
                    if (Sequence != saved.Revision && OpenClipboard(owner))
                    {
                        string text = null;
                        uint restored = 0;
                        try
                        {
                            if (Desktop.ProcessOf(GetClipboardOwner()) != Desktop.ProcessOf(selection.Window)) return null;
                            // Holding the clipboard prevents another writer between this
                            // generation check, reading, and restoring all saved formats.
                            if (copied != saved.Revision && Sequence != copied) return null;
                            copied = Sequence;
                            try
                            {
                                if (!stillCurrent()) return null;
                                var memory = GetClipboardData(13);
                                if (memory != IntPtr.Zero)
                                {
                                    var p = GlobalLock(memory);
                                    if (p != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            int units = checked((int)(GlobalSize(memory).ToUInt64() / 2));
                                            text = Marshal.PtrToStringUni(p, units);
                                            int terminator = text.IndexOf('\0');
                                            if (terminator >= 0) text = text.Substring(0, terminator);
                                        }
                                        finally { GlobalUnlock(memory); }
                                    }
                                }
                            }
                            finally { saved.RestoreOpen(); restored = Sequence; copied = saved.Revision; }
                        }
                        finally { CloseClipboard(); }
                        return string.IsNullOrWhiteSpace(text) ? null : new CopyResult { Text = text, After = ClosedGeneration(owner,restored) };
                    }
                    await Task.Delay(25);
                }
                return null;
                }
                finally
                {
                    // Runs for interruption, timeout, and exceptions as well as success.
                    // Restore checks the generation again under the clipboard lock.
                    if (copied != saved.Revision) await Restore(owner, saved, copied);
                }
            }
        }
    }
}
