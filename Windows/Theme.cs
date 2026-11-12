using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Translated
{
    internal static class Theme
    {
        internal static bool Dark { get; private set; }
        internal static event Action Changed;

        internal static void Initialize()
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary {
                Source = new Uri("/Translated;component/Theme.xaml", UriKind.Relative) });
            Refresh();
            SystemEvents.UserPreferenceChanged += (s, e) => app.Dispatcher.BeginInvoke(new Action(Refresh));
        }

        internal static void Refresh()
        {
            bool high = SystemParameters.HighContrast;
            ApplyPalette(!high && Equals(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1), 0), high);
        }

        internal static void ApplyPalette(bool dark, bool high)
        {
            Dark = dark;
            var resources = Application.Current.Resources;
            void Set(string key, string light, string dark, Color accessible)
            {
                var brush = new SolidColorBrush(high ? accessible : (Color)ColorConverter.ConvertFromString(Dark ? dark : light));
                brush.Freeze();
                resources[key] = brush;
            }
            Set("Paper", "#FCF8ED", "#202724", SystemColors.WindowColor);
            Set("Ink", "#293E36", "#EDF2E6", SystemColors.WindowTextColor);
            Set("Muted", "#74786E", "#B3BDB0", SystemColors.GrayTextColor);
            Set("Field", "#F5F2E9", "#29322E", SystemColors.WindowColor);
            Set("Entry", "#FFFDF8", "#333D37", SystemColors.WindowColor);
            Set("Line", "#DEDACF", "#505B52", SystemColors.WindowTextColor);
            Set("Soft", "#EAE6DC", "#35423B", SystemColors.ControlColor);
            Set("Selected", "#CECABE", "#526258", SystemColors.HighlightColor);
            Set("OnSelected", "#292D29", "#F2F7EF", SystemColors.HighlightTextColor);
            Set("Peach", "#FFB381", "#F4AB80", SystemColors.HighlightColor);
            Set("PeachInk", "#2D4138", "#242C25", SystemColors.HighlightTextColor);
            Set("Sage", "#CFDFBE", "#415D4D", SystemColors.ControlColor);
            Set("Accent", "#496B56", "#ADCEAD", SystemColors.HighlightColor);
            Set("Error", "#AD4033", "#FFAC98", SystemColors.WindowTextColor);
            Changed?.Invoke();
        }
    }

    internal class ToolWindow : Window, IDisposable
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        protected bool Disposing { get; private set; }

        protected ToolWindow()
        {
            SetResourceReference(BackgroundProperty, "Paper");
            SetResourceReference(ForegroundProperty, "Ink");
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 14;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SourceInitialized += (s,e) => ApplyTitleBar();
            Theme.Changed += ApplyTitleBar;
            Closing += (s,e) => { if (!Disposing) { e.Cancel = true; Hide(); } };
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        }

        void ApplyTitleBar()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            int dark = Theme.Dark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }

        internal void Present(bool activate)
        {
            ShowActivated = activate;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            if (activate) Activate();
        }

        public void Dispose()
        {
            Theme.Changed -= ApplyTitleBar;
            Disposing = true;
            Close();
        }
    }
}
