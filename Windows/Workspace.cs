using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Translated
{
    internal partial class Workspace : ToolWindow
    {
        readonly TranslatorApp app;
        bool loading = true, busy;

        internal Workspace(TranslatorApp app)
        {
            this.app = app;
            InitializeComponent();
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Translated;component/Assets/Brand.png"));
            VersionLabel.Text = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            // The platform-only entry stays identifiable in the Mac provider list.
            ProviderPicker.Items.Add(new ComboBoxItem { Content = "Apple · macOS only", IsEnabled = false });
            foreach (var name in Provider.Choices)
                ProviderPicker.Items.Add(new ComboBoxItem { Content = ServiceLabel(name), Tag = name });
            foreach (var toggle in new[] { Replace, Floating, WeChat }) toggle.Click += OptionsChanged;
            Simplified.Click += OptionsChanged; Traditional.Click += OptionsChanged;
            Input.IsKeyboardFocusWithinChanged += (s,e) => EditorBorder.SetResourceReference(Border.BorderBrushProperty,
                Input.IsKeyboardFocusWithin ? "Accent" : "Line");
            IsVisibleChanged += (s,e) => { if (!IsVisible) Secret.Clear(); };
            RefreshSettings();
        }

        internal static string ServiceLabel(string name) => name == Provider.Custom ? "Custom / Local LLM" : name;
        void PageChanged(object sender, RoutedEventArgs e)
        {
            if (SettingsPage == null) return;
            bool settings = SettingsTab.IsChecked == true;
            SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
            EditorScroll.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        }
        internal void Open(bool settings)
        {
            (settings ? SettingsTab : TranslateTab).IsChecked = true;
            Present(true);
        }
        internal void SetBusy(bool value, bool test = false)
        {
            busy = value;
            StateLabel.Text = value ? "Translating" : app.Prefs.Configured ? "Ready" : "Set up";
            CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            // A fresh explicit translation supersedes the previous one. Keep its own source snapshot.
            foreach (var control in new Control[] { TestButton, ProviderPicker, Model, Address, Low, Off, Secret, SaveButton, Remove })
                control.IsEnabled = !test;
        }

        void InputChanged(object sender, TextChangedEventArgs e)
        {
            if (Count == null) return;
            int length = Passage.Count(Input.Text);
            Count.Text = length.ToString("N0") + " / 50,000";
            Count.SetResourceReference(TextBlock.ForegroundProperty, length > Passage.Maximum ? "Error" : "Muted");
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            if (OutputArea != null && OutputArea.Visibility == Visibility.Visible) ShowTranslation(null,null,"");
            StatusText(length > Passage.Maximum ? "每次最多翻译 50,000 个字符，请缩短原文。" : "");
        }

        void PasteClick(object sender, RoutedEventArgs e)
        {
            try { if (Clipboard.ContainsText()) { Input.SelectedText = Clipboard.GetText(); Input.Focus(); } }
            catch (Exception error) { StatusText(error.Message); }
        }
        void TranslateClick(object sender, RoutedEventArgs e) => app.Manual(Input.Text);
        void CancelClick(object sender, RoutedEventArgs e) => app.Cancel();
        void CopyOutputClick(object sender, RoutedEventArgs e)
        {
            try { app.CopyManually(ManualOutput.Text); StatusText("Copied to clipboard.",false); }
            catch (Exception error) { StatusText(error.Message); }
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && TranslateTab.IsChecked == true)
            { e.Handled = true; app.Manual(Input.Text); }
            else base.OnPreviewKeyDown(e);
        }
        internal bool ShowTranslation(string source, string text, string message)
        {
            if (source != null && Input.Text != source)
            {
                StatusText("Input changed. Open Translation result from the tray.",false);
                return false;
            }
            bool visible = !string.IsNullOrEmpty(text);
            ManualOutput.Text = text ?? "";
            OutputArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            EditorPage.RowDefinitions[6].MinHeight = visible ? 120 : 0;
            EditorPage.RowDefinitions[6].Height = visible ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
            StatusText(message ?? "");
            return true;
        }
        internal void StatusText(string text, bool error = true)
        {
            EditorStatus.SetResourceReference(TextBlock.ForegroundProperty,error ? "Error" : "Muted");
            EditorStatus.Text = text;
        }
        internal void SettingsText(string text, bool error = true)
        {
            SettingsStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "Error" : "Accent");
            SettingsStatus.Text = text;
        }

        void LoadShortcut()
        {
            var shortcut = app.Prefs.Hotkey;
            ActiveShortcut.Text = app.HotkeyReady ? "Active · " + shortcut.ToString().Replace(" + ", "+") : "Unavailable";
            ShortcutHint.Text = app.HotkeyReady ? "Select text, then press  " + shortcut : "Select text, then click Translate";
        }
        void SetShortcut(Shortcut shortcut)
        {
            try { app.SaveShortcut(shortcut); LoadShortcut(); ShortcutStatus.Text = ""; }
            catch (Exception e) { ShortcutStatus.Text = e.Message; }
        }
        void ResetShortcut(object sender, RoutedEventArgs e) => SetShortcut(Shortcut.Default());
        void CustomShortcutClick(object sender, RoutedEventArgs e)
        {
            app.Cancel();
            using (var editor = new ShortcutEditor(app) { Owner = this })
                if (editor.ShowDialog() == true) { LoadShortcut(); ShortcutStatus.Text = ""; }
        }

        string Service => (string)(ProviderPicker.SelectedItem as ComboBoxItem)?.Tag;
        Provider Draft() => new Provider { Name = Service, BaseUrl = Address.Text.Trim(), Model = Model.Text.Trim(), Reasoning = Low.IsChecked == true };
        void ProviderChanged(object sender, SelectionChangedEventArgs e) { if (!loading) LoadProvider(); }
        void AddressChanged(object sender, TextChangedEventArgs e)
        {
            if (loading) return;
            Secret.Clear(); UpdateDestination();
        }
        void SecretChanged(object sender, RoutedEventArgs e)
        {
            if (KeyHint != null)
                using (var key = Secret.SecurePassword) KeyHint.Visibility = key.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        }
        void UpdateDestination()
        {
            try
            {
                var p = Draft(); var uri = p.Endpoint();
                bool saved = Credentials.Exists(p);
                KeyHint.Text = saved ? "Saved in Credential Manager · leave blank to keep" :
                    Provider.IsLocal(uri) ? "Optional for a local service" : "Enter API key";
                KeyHint.ToolTip = KeyHint.Text; Remove.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
                Destination.Text = "Sends text to " + uri.Authority + ". Test translation calls this service and may be billed.";
            }
            catch (Exception error) { KeyHint.Text = "Enter API key"; Remove.Visibility = Visibility.Collapsed; Destination.Text = error.Message; }
        }
        void LoadProvider()
        {
            if (Service == null) return;
            loading = true;
            var p = app.Prefs.Profile(Service);
            Model.Text = p.Model; Address.Text = p.BaseUrl; Address.IsReadOnly = p.Name != Provider.Custom;
            Address.SetResourceReference(ForegroundProperty, Address.IsReadOnly ? "Muted" : "Ink");
            Secret.Clear(); Low.IsChecked = p.Reasoning; Off.IsChecked = !p.Reasoning;
            ReasonRow.Visibility = p.Name == Provider.DeepSeek ? Visibility.Visible : Visibility.Collapsed;
            SettingsText(""); loading = false; UpdateDestination();
        }
        void SaveProvider(object sender, RoutedEventArgs e)
        {
            try
            {
                app.SaveProvider(Draft(), Secret.Password.Trim()); Secret.Clear();
                UpdateDestination(); UpdateFooter(); SettingsText("Settings saved.", false);
            }
            catch (Exception ex) { SettingsText(ex.Message); }
        }
        void TestProvider(object sender, RoutedEventArgs e) => app.TestProvider(Draft(), Secret.Password.Trim());
        void RemoveKey(object sender, RoutedEventArgs e)
        {
            try
            {
                var p = Draft();
                if (MessageBox.Show(this, "Remove the saved key for " + p.Endpoint().Authority + "?", "Translated",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Credentials.Remove(p); Secret.Clear(); UpdateDestination(); SettingsText("Key removed.", false);
            }
            catch (Exception ex) { SettingsText(ex.Message); }
        }
        void OptionsChanged(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            try { app.SaveOptions(Traditional.IsChecked == true, Replace.IsChecked == true, Floating.IsChecked == true, WeChat.IsChecked == true); }
            catch (Exception ex) { RefreshOptions(); SettingsText(ex.Message); }
        }
        internal void RefreshOptions()
        {
            var p = app.Prefs;
            Simplified.IsChecked = !p.Traditional; Traditional.IsChecked = p.Traditional;
            Replace.IsChecked = p.Replace; Floating.IsChecked = p.Floating; WeChat.IsChecked = p.WeChatCopy;
        }
        void UpdateFooter()
        {
            ProviderFooter.Text = ServiceLabel(app.Prefs.Active);
            StateLabel.Text = busy ? "Translating" : app.Prefs.Configured ? "Ready" : "Set up";
        }
        internal void RefreshSettings()
        {
            loading = true;
            ProviderPicker.SelectedIndex = Array.IndexOf(Provider.Choices,app.Prefs.Active) + 1;
            RefreshOptions(); UpdateFooter(); loading = false; LoadShortcut(); LoadProvider();
        }
    }
}
