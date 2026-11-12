using System;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Accessibility;

namespace Translated
{
    // A Chromium contenteditable host can be a focused UIA Group with no ValuePattern.
    // IAccessible2 exposes the editable state separately from its control type.
    internal static class BrowserEditability
    {
        [DllImport("oleacc.dll")]
        static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IAccessible result);
        [DllImport("oleacc.dll")]
        static extern int WindowFromAccessibleObject(IAccessible element, out IntPtr hwnd);

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetStates(IntPtr self, out int states);

        static bool ExplicitlyEditable(IAccessible element)
        {
            if (!(element is IServiceProvider services)) return false;
            var iid = new Guid("E89F726E-C4F4-4C19-BB19-B647D7FA8478");
            if (services.QueryService(ref iid, ref iid, out var proxy) != 0 || proxy == IntPtr.Zero) return false;
            try
            {
                // Public Accessible2.idl ABI: 28 inherited IAccessible slots, then
                // nRelations through groupPosition, followed by the states property.
                var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(proxy), (28 + 7) * IntPtr.Size);
                var getStates = Marshal.GetDelegateForFunctionPointer<GetStates>(method);
                return getStates(proxy, out int states) == 0 && (states & 0x8) != 0;
            }
            finally { Marshal.Release(proxy); }
        }

        internal static bool FocusedGroup(IntPtr window, AutomationElement group)
        {
            try
            {
                if (group.Current.FrameworkId != "Chrome" || !group.Current.HasKeyboardFocus ||
                    !group.Current.IsEnabled || group.Current.IsPassword) return false;
                if (group.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly)
                    return false;
                var iid = typeof(IAccessible).GUID;
                if (AccessibleObjectFromWindow(Desktop.Focus(window), 0xfffffffc, ref iid, out var root) != 0 || root == null)
                    return false;
                var keyboardFocus = root.accFocus;
                var target = keyboardFocus as IAccessible;
                if (target == null)
                {
                    if (!(keyboardFocus is int child) || child != 0) return false;
                    target = root;
                }
                const int focused = 0x4, unavailable = 0x1, readOnly = 0x40, password = 0x20000000;
                if (!(target.get_accState(0) is int state) || (state & (focused | unavailable | readOnly | password)) != focused)
                    return false;
                if (WindowFromAccessibleObject(target, out var hwnd) != 0 || Desktop.GetAncestor(hwnd, 2) != window)
                    return false;
                target.accLocation(out int x, out int y, out int width, out int height, 0);
                var bounds = group.Current.BoundingRectangle;
                if (bounds.IsEmpty || width <= 0 || height <= 0 || Math.Abs(bounds.Left - x) > 1 ||
                    Math.Abs(bounds.Top - y) > 1 || Math.Abs(bounds.Width - width) > 1 || Math.Abs(bounds.Height - height) > 1)
                    return false;
                return ExplicitlyEditable(target);
            }
            catch (COMException) { return false; }
            catch (InvalidCastException) { return false; }
            catch (NotImplementedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
