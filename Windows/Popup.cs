using System;
using Rectangle = System.Drawing.Rectangle;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;

namespace Translated
{
    internal static class OverlayPlacement
    {
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(DrawingPoint point, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        internal static double Scale(DrawingPoint anchor) =>
            GetDpiForMonitor(MonitorFromPoint(anchor, 2), 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;

        internal static Rectangle Bounds(Rectangle area, DrawingPoint anchor, double scale, double width, double height, bool near)
        {
            int Px(double dip) => (int)Math.Round(dip * scale);
            int w = Math.Min(area.Width, Px(width)), h = Math.Min(area.Height, Px(height));
            int x = near ? anchor.X + Px(8) : area.Left + (area.Width - w) / 2;
            int y = near ? anchor.Y + Px(13) : area.Bottom - h - Px(20);
            if (near && y + h > area.Bottom) y = anchor.Y - h - Px(8);
            return new Rectangle(Math.Max(area.Left, Math.Min(area.Right-w,x)),
                Math.Max(area.Top,Math.Min(area.Bottom-h,y)), w, h);
        }
    }

    internal abstract class OverlayWindow : Window, IDisposable
    {
        [DllImport("user32.dll", EntryPoint="GetWindowLongW")] static extern int GetStyle(IntPtr hwnd,int index);
        [DllImport("user32.dll", EntryPoint="SetWindowLongW")] static extern int SetStyle(IntPtr hwnd,int index,int style);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
        readonly bool near;
        DrawingPoint anchor;
        bool positioning;
        protected OverlayWindow(bool near)
        {
            this.near = near;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            Focusable = false; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000;
            SourceInitialized += (s,e) => {
                var handle = new WindowInteropHelper(this).Handle;
                const int noActivate = 0x08000000, toolWindow = 0x80, transparent = 0x20;
                SetStyle(handle,-20,GetStyle(handle,-20) | noActivate | toolWindow | (near ? 0 : transparent));
                HwndSource.FromHwnd(handle).AddHook(Hook);
            };
        }
        IntPtr Hook(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled)
        {
            if (msg == 0x21) { handled = true; return (IntPtr)3; } // WM_MOUSEACTIVATE
            if (msg == 0x84 && !near) { handled = true; return (IntPtr)(-1); }
            if ((msg == 0x02E0 || msg == 0x007E || msg == 0x001A) && IsVisible && !positioning)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(() => { if (IsVisible) Position(); }));
            return IntPtr.Zero;
        }
        internal Rectangle PixelBounds { get; private set; }
        protected void ShowAt(DrawingPoint point, double width, double height)
        {
            anchor = point; Width = width; Height = height;
            new WindowInteropHelper(this).EnsureHandle();
            Position();
            if (!IsVisible) Show();
            Position();
        }
        void Position()
        {
            positioning = true;
            try
            {
                PixelBounds = OverlayPlacement.Bounds(Screen.FromPoint(anchor).WorkingArea,anchor,
                    OverlayPlacement.Scale(anchor),Width,Height,near);
                var r = PixelBounds;
                SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),r.X,r.Y,r.Width,r.Height,0x10);
            }
            finally { positioning = false; }
        }
        public virtual void Dispose() => Close();
    }

    internal sealed class Popup : OverlayWindow
    {
        readonly Button trigger = new Button { Content = "Translate", Focusable = false, FontSize = 14,
            FontWeight = FontWeights.Bold, Padding = new Thickness(10,2,10,2) };
        Action action;
        IntPtr source;
        internal Popup() : base(true)
        {
            trigger.SetResourceReference(StyleProperty,"Primary");
            trigger.MinHeight = 0;
            AutomationProperties.SetName(trigger, "Translate selection");
            trigger.Click += (s,e) => { if (source == Desktop.GetForegroundWindow()) action?.Invoke(); else Dismiss(); };
            Content = trigger;
        }
        internal void Button(DrawingPoint point, IntPtr hwnd, Action click)
        {
            source = hwnd; action = click; ShowAt(point,88,31);
        }
        internal void Dismiss() { action = null; if (IsVisible) Hide(); }
    }
}
