using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace Translated
{
    internal partial class ResultWindow : ToolWindow
    {
        const string Auto = "Auto-detect";
        readonly TranslatorApp app;
        string originalText, completeText;
        string targetLanguage;
        internal bool HasResult => originalText != null;

        internal ResultWindow(TranslatorApp app)
        {
            this.app = app;
            InitializeComponent();
            SourcePicker.ItemsSource = new[] { Auto, "English", "Chinese", "Japanese", "Korean", "French", "German", "Spanish" }
                .Concat(CultureInfo.GetCultures(CultureTypes.NeutralCultures).Where(c => c.Name.Length > 0)
                    .Select(c => c.EnglishName).OrderBy(n => n, StringComparer.Ordinal)).Distinct().ToArray();
            SourcePicker.SelectedIndex = 0;
        }
        internal void Store(string source, string translation, string language, string message, string target = null)
        {
            originalText = source ?? ""; completeText = translation;
            targetLanguage = target;
            Original.Text = originalText; Output.Text = translation ?? "";
            SourcePicker.SelectedItem = language ?? Auto;
            CopyButton.IsEnabled = !string.IsNullOrEmpty(translation);
            Retry.IsEnabled = !string.IsNullOrWhiteSpace(originalText);
            Reason.SetResourceReference(ForegroundProperty, translation == null ? "Error" : "Muted");
            Reason.Text = message;
        }
        void RetryClick(object sender, RoutedEventArgs e) =>
            app.Manual(originalText, SourcePicker.SelectedIndex > 0 ? (string)SourcePicker.SelectedItem : null, true, targetLanguage);
        void CopyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                app.CopyManually(completeText);
                Reason.Text = "Copied to clipboard.";
            }
            catch (Exception error) { Reason.Text = error.Message; }
        }
    }
}
