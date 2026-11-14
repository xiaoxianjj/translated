using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;

namespace Translated
{
    internal partial class TranslationCard : ToolWindow
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        readonly TranslatorApp app;
        readonly Action requestClose;
        readonly DispatcherTimer foregroundCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        int? outsideSince;
        bool loadingPicker;
        internal Guid Session { get; private set; }
        internal string Source { get; private set; }
        internal string SourceLanguage { get; private set; }
        internal string Target => (TargetPicker.SelectedItem as TranslationLanguage)?.Code ?? "auto";
        internal DrawingPoint Anchor { get; private set; }
        internal IntPtr SourceWindow { get; private set; }
        internal System.Drawing.Rectangle PixelBounds { get; private set; }
        internal bool ChoosingLanguage => TargetPicker.IsDropDownOpen;

        internal TranslationCard(TranslatorApp app, Action requestClose = null)
        {
            this.app = app;
            this.requestClose = requestClose ?? (() => app.Cancel("card-close"));
            InitializeComponent();
            TargetPicker.ItemsSource = TranslationLanguages.All;
            foregroundCheck.Tick += (s,e) => CheckForegroundState();
            Closed += (s,e) => foregroundCheck.Stop();
        }

        internal void Begin(Guid session, string source, string language, string target, DrawingPoint point, IntPtr sourceWindow = default)
        {
            Session = session; Source = source; SourceLanguage = language; Anchor = point;
            SourceWindow = sourceWindow;
            loadingPicker = true;
            TargetPicker.SelectedItem = TranslationLanguages.Find(target);
            loadingPicker = false;
            SourcePreview.Text = source == null ? "" : source.Replace('\r',' ').Replace('\n',' ');
            SourcePreview.ToolTip = source == null ? null : source.Substring(0, Math.Min(1000, source.Length));
            SetLoading();
            // A nonactivating, ordinary WPF window can remain behind the foreground
            // application's window group. Raise it only for this preview session;
            // external clicks/app switches dismiss it and remove the topmost style.
            Topmost = true;
            ShowActivated = false;
            if (!IsVisible) Show();
            Position();
            outsideSince = null;
            if (sourceWindow != IntPtr.Zero) foregroundCheck.Start();
        }

        internal void CheckForegroundState()
        {
            if (!IsVisible || Session == Guid.Empty || SourceWindow == IntPtr.Zero) { foregroundCheck.Stop(); return; }
            if (!IsWindow(SourceWindow) || IsIconic(SourceWindow)) { app.DismissCard("source-unavailable"); return; }
            var foreground = Desktop.GetForegroundWindow();
            if (foreground == SourceWindow || OwnsWindow(foreground)) { outsideSince = null; return; }
            if (!outsideSince.HasValue) outsideSince = Environment.TickCount;
            // Use a second, independent check: some browser shells/desktop switches
            // do not deliver WinEvent notifications reliably. Brief transitions get a grace period.
            if (unchecked((uint)(Environment.TickCount-outsideSince.Value)) >= 220)
                app.DismissCard("foreground-check");
        }

        void Position()
        {
            var area = Screen.FromPoint(Anchor).WorkingArea;
            var bounds = OverlayPlacement.Bounds(area, Anchor, OverlayPlacement.Scale(Anchor), 450, 356, true);
            PixelBounds = bounds;
            SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10);
        }
        internal bool OwnsWindow(IntPtr window)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return false;
            for (int i=0; window!=IntPtr.Zero && i<16; i++,window=GetWindow(window,4))
                if (window==handle) return true;
            return false;
        }
        internal bool ContainsPoint(DrawingPoint point) => OwnsWindow(Desktop.RootAt(point));
        internal void SetLoading()
        {
            Translation.Text = ""; LoadingLabel.Visibility = Visibility.Visible;
            Status.Text = ""; CopyButton.IsEnabled = false; RetryButton.IsEnabled = false;
        }
        internal void Complete(Guid session, string text, string error = "")
        {
            if (session != Session || !IsVisible) return;
            LoadingLabel.Visibility = Visibility.Collapsed;
            Translation.Text = text ?? "";
            Translation.FlowDirection = TranslationLanguages.Find(Target).RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Status.SetResourceReference(TextBlock.ForegroundProperty, text == null ? "Error" : "Muted");
            Status.Text = error;
            CopyButton.IsEnabled = !string.IsNullOrEmpty(text);
            RetryButton.IsEnabled = !string.IsNullOrWhiteSpace(Source);
        }
        internal void Dismiss() {
            foregroundCheck.Stop(); outsideSince = null;
            Session = Guid.Empty; TargetPicker.IsDropDownOpen = false; Hide(); Topmost = false;
        }
        void TargetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingPicker && IsVisible && Source != null) app.RetranslateCard(Target);
        }
        void RetryClick(object sender, RoutedEventArgs e) => app.RetranslateCard(Target);
        void CloseClick(object sender, RoutedEventArgs e) => Close();
        void CopyClick(object sender, RoutedEventArgs e)
        {
            try { app.CopyManually(Translation.Text); Status.Text = "已复制"; }
            catch (Exception error) { Status.SetResourceReference(TextBlock.ForegroundProperty,"Error"); Status.Text = error.Message; }
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (ChoosingLanguage) TargetPicker.IsDropDownOpen = false;
                else Close();
                e.Handled = true;
            }
            else base.OnPreviewKeyDown(e);
        }
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!Disposing)
            {
                requestClose();
                Dismiss();
                e.Cancel = true;
            }
            base.OnClosing(e);
        }
    }
}
