using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Translated
{
    internal partial class ShortcutEditor : Window, IDisposable
    {
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { internal int Left,Top,Right,Bottom; }
        readonly TranslatorApp app;
        readonly ShortcutRecorder recorder;
        readonly List<ToggleButton> keycaps = new List<ToggleButton>();
        readonly List<ToggleButton> extraFunctions = new List<ToggleButton>();
        bool capturing, closed;
        HwndSource nativeSource;
        internal Shortcut Draft => recorder.Candidate;
        internal ShortcutEditor(TranslatorApp app)
        {
            this.app = app;
            recorder = new ShortcutRecorder(app.Prefs.Hotkey);
            InitializeComponent();
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Translated;component/Assets/Brand.png"));
            Fit(SystemParameters.WorkArea.Width,SystemParameters.WorkArea.Height);
            BuildKeyboard();
            foreach (var cap in new[] { MiddleKey, Mouse4Key, Mouse5Key })
            {
                cap.Tag = int.Parse((string)cap.Tag);
                AutomationProperties.SetName(cap, Shortcut.KeyName((int)cap.Tag));
                keycaps.Add(cap);
            }
            Refresh();
            Loaded += OnLoaded;
            SourceInitialized += (s,e) => {
                var handle = new WindowInteropHelper(this).Handle;
                nativeSource = HwndSource.FromHwnd(handle);
                nativeSource.AddHook(NativeMessage);
                FitToMonitor(true);
            };
            Deactivated += (s, e) => { recorder.Release(); if (capturing) Pause(); };
            Closed += (s, e) => {
                closed = true;
                if (nativeSource != null && !nativeSource.IsDisposed) nativeSource.RemoveHook(NativeMessage);
                app.EndShortcutCapture();
            };
        }
        void Fit(double workWidth, double workHeight)
        {
            var size = EditorSizing.Fit(workWidth,workHeight,Width,Height);
            MinWidth=size.MinWidth; MinHeight=size.MinHeight;
            Width=size.Width; Height=size.Height;
        }
        void FitToMonitor(bool initial)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var reference = initial && Owner != null ? new WindowInteropHelper(Owner).Handle : handle;
            var screen = reference == IntPtr.Zero ? Forms.Screen.FromPoint(Forms.Cursor.Position) : Forms.Screen.FromHandle(reference);
            var area = screen.WorkingArea;
            double scale = 0;
            try { if (reference != IntPtr.Zero) scale=GetDpiForWindow(reference)/96d; }
            catch (EntryPointNotFoundException) { /* Earlier Windows 10 uses the monitor DPI fallback. */ }
            if (scale <= 0) scale=OverlayPlacement.Scale(new System.Drawing.Point(area.Left+area.Width/2,area.Top+area.Height/2));
            if (scale <= 0) scale=1;
            Fit(area.Width/scale,area.Height/scale);
            if (!initial && GetWindowRect(handle,out var rect))
            {
                int x=Math.Max(area.Left,Math.Min(rect.Left,area.Right-(rect.Right-rect.Left)));
                int y=Math.Max(area.Top,Math.Min(rect.Top,area.Bottom-(rect.Bottom-rect.Top)));
                SetWindowPos(handle,IntPtr.Zero,x,y,0,0,0x15); // no size, Z-order or activation change
            }
        }
        IntPtr NativeMessage(IntPtr window,int message,IntPtr w,IntPtr l,ref bool handled)
        {
            if (message == 0x02E0 || message == 0x007E || message == 0x001A)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(() => { if (!closed) FitToMonitor(false); }));
            return IntPtr.Zero;
        }
        async void OnLoaded(object sender, RoutedEventArgs args)
        {
            Resume();
            var devices = await Task.Run(InputDevices.Read);
            if (closed) return;
            KeyboardDevice.Text = devices.Keyboards; MouseDevice.Text = devices.Mice;
        }
        ToggleButton Key(int key, string label = null, double units = 1, bool enabled = true)
        {
            var cap = new ToggleButton { Tag = key, Content = label ?? Shortcut.KeyName(key), Width = units * 40 - 4,
                Style = (Style)FindResource("Keycap"), IsEnabled = enabled, ToolTip = Shortcut.KeyName(key) };
            AutomationProperties.SetName(cap, "Key " + Shortcut.KeyName(key));
            cap.Click += (s, e) => { recorder.Toggle((int)cap.Tag); Refresh(); };
            keycaps.Add(cap); return cap;
        }
        StackPanel Row(Panel parent)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            parent.Children.Add(row); return row;
        }
        void BuildKeyboard()
        {
            var fn = Row(MainKeys);
            fn.Children.Add(Key(27, "Esc", 1.5, false));
            for (int i = 0; i < 12; i++) fn.Children.Add(Key(112 + i, null, 1, i != 11));
            var digits = Row(MainKeys);
            digits.Children.Add(Key(192));
            foreach (char c in "1234567890") digits.Children.Add(Key(c));
            digits.Children.Add(Key(189)); digits.Children.Add(Key(187)); digits.Children.Add(Key(8, "Backspace", 2));
            var q = Row(MainKeys); q.Children.Add(Key(9, "Tab", 1.5));
            foreach (char c in "QWERTYUIOP") q.Children.Add(Key(c));
            q.Children.Add(Key(219)); q.Children.Add(Key(221)); q.Children.Add(Key(220, "\\", 1.5));
            var a = Row(MainKeys); a.Children.Add(Key(20, "Caps", 1.8));
            foreach (char c in "ASDFGHJKL") a.Children.Add(Key(c));
            a.Children.Add(Key(186)); a.Children.Add(Key(222)); a.Children.Add(Key(13, "Enter", 2.2));
            var z = Row(MainKeys); z.Children.Add(Key(16, "Shift", 2.3));
            foreach (char c in "ZXCVBNM") z.Children.Add(Key(c));
            z.Children.Add(Key(188)); z.Children.Add(Key(190)); z.Children.Add(Key(191)); z.Children.Add(Key(16, "Shift", 2.7));
            var bottom = Row(MainKeys);
            bottom.Children.Add(Key(17, "Ctrl", 1.5)); bottom.Children.Add(Key(91, "Win", 1.25)); bottom.Children.Add(Key(18, "Alt", 1.25));
            bottom.Children.Add(Key(32, "Space", 7)); bottom.Children.Add(Key(18, "Alt", 1.25)); bottom.Children.Add(Key(17, "Ctrl", 2.75));
            var top = Row(NavigationKeys);
            top.Children.Add(Key(145, "ScrLk")); top.Children.Add(Key(19, "Pause"));
            var nav1 = Row(NavigationKeys);
            nav1.Children.Add(Key(45, "Ins")); nav1.Children.Add(Key(36, "Home")); nav1.Children.Add(Key(33, "PgUp"));
            var nav2 = Row(NavigationKeys);
            nav2.Children.Add(Key(46, "Del")); nav2.Children.Add(Key(35, "End")); nav2.Children.Add(Key(34, "PgDn"));
            NavigationKeys.Children.Add(new Border { Height = 50 });
            var up = Row(NavigationKeys); up.Children.Add(new Border { Width = 40 }); up.Children.Add(Key(38, "↑"));
            var arrows = Row(NavigationKeys); arrows.Children.Add(Key(37, "←")); arrows.Children.Add(Key(40, "↓")); arrows.Children.Add(Key(39, "→"));
            var num1 = Row(NumpadKeys);
            num1.Children.Add(Key(144, "Num")); num1.Children.Add(Key(111, "/")); num1.Children.Add(Key(106, "*")); num1.Children.Add(Key(109, "−"));
            foreach (var values in new[] { new[] { 103, 104, 105, 107 }, new[] { 100, 101, 102 }, new[] { 97, 98, 99 } })
            {
                var row = Row(NumpadKeys);
                foreach (int key in values) row.Children.Add(Key(key, key == 107 ? "+" : (key - 96).ToString()));
            }
            var num0 = Row(NumpadKeys); num0.Children.Add(Key(96, "0", 2)); num0.Children.Add(Key(110, "."));
            // F13–F24 are uncommon physical keys; show the pressed key as a supplementary keycap.
            var extra = Row(MainKeys);
            for (int key = 124; key <= 135; key++)
            {
                var cap = Key(key); cap.Visibility = Visibility.Collapsed;
                extraFunctions.Add(cap); extra.Children.Add(cap);
            }
            var iso = Key(226, "ISO \\", 1.5); iso.Visibility = Visibility.Collapsed;
            extraFunctions.Add(iso); extra.Children.Add(iso);
        }
        void Refresh()
        {
            var selected = recorder.Selected;
            var candidate = recorder.Candidate;
            Combination.Text = selected.Length == 0 ? "等待选择按键…" : string.Join(" + ", selected
                .OrderBy(k => ShortcutKeys.Modifier(k) != 0 ? 0 : ShortcutKeys.Mouse(k) ? 2 : 1)
                .ThenBy(k => k == 17 ? 0 : k == 18 ? 1 : k == 16 ? 2 : k == 91 ? 3 : k)
                .Select(Shortcut.KeyName));
            foreach (var cap in keycaps)
            {
                int key = (int)cap.Tag;
                cap.IsChecked = selected.Contains(key);
                cap.RenderTransform = new TranslateTransform(0, recorder.Holding(key) ? 3 : 0);
            }
            foreach (var cap in extraFunctions) cap.Visibility = selected.Contains((int)cap.Tag) ? Visibility.Visible : Visibility.Collapsed;
            if (selected.Any(k => k >= 96 && k <= 111)) ShowNumpad.IsChecked = true;
            NumpadKeys.Visibility = ShowNumpad.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            bool hasPrimary = candidate.Key != 0;
            Validation.Text = !hasPrimary ? "选择一个普通按键或鼠标侧键，可搭配 Ctrl、Alt、Shift、Win。" :
                candidate.ValidationMessage ?? (candidate.Native ? "组合可用。保存时会检查是否被其他程序占用。" :
                "组合可用。鼠标或多键组合请避开其他程序中正在使用的相同组合。");
            Validation.SetResourceReference(TextBlock.ForegroundProperty, !hasPrimary || candidate.Valid ? "Muted" : "Error");
            SaveShortcutButton.IsEnabled = hasPrimary && candidate.Valid;
        }
        void Resume()
        {
            try
            {
                app.BeginShortcutCapture(new WindowInteropHelper(this).EnsureHandle(), (key, down) => {
                    Dispatcher.BeginInvoke(new Action(() => {
                        if (closed || !capturing) return;
                        if (key == 27 && down) { Close(); return; }
                        if (key == 123)
                        {
                            if (down)
                            {
                                Validation.Text = "F12 是 Windows 保留按键，请选择其他组合。";
                                Validation.SetResourceReference(TextBlock.ForegroundProperty, "Error");
                                SaveShortcutButton.IsEnabled = false;
                            }
                            return;
                        }
                        recorder.Input(key, down); Refresh();
                    }));
                });
                capturing = true; RecordingState.Text = "● 正在录制"; CaptureButton.Content = "暂停录制";
            }
            catch (Exception error)
            {
                capturing = false; RecordingState.Text = "○ 未录制"; CaptureButton.Content = "重试录制";
                Validation.Text = error.Message; Validation.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            }
        }
        void Pause()
        {
            app.EndShortcutCapture(); capturing = false; recorder.Release();
            RecordingState.Text = "○ 已暂停"; CaptureButton.Content = "继续录制"; Refresh();
        }
        void ToggleCapture(object sender, RoutedEventArgs e) { if (capturing) Pause(); else Resume(); }
        void ClearClick(object sender, RoutedEventArgs e) { recorder.Clear(); Refresh(); }
        void MouseKeyClick(object sender, RoutedEventArgs e) { recorder.Toggle((int)((ToggleButton)sender).Tag); Refresh(); }
        void NumpadClick(object sender, RoutedEventArgs e) => NumpadKeys.Visibility = ShowNumpad.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        void CancelClick(object sender, RoutedEventArgs e) => Close();
        void SaveClick(object sender, RoutedEventArgs e)
        {
            Pause();
            try { app.SaveShortcut(Draft); DialogResult = true; }
            catch (Exception error) { Validation.Text = error.Message; Validation.SetResourceReference(TextBlock.ForegroundProperty, "Error"); }
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
            else base.OnPreviewKeyDown(e);
        }
        public void Dispose() { if (!closed) Close(); }
    }
}
