#if NET48
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Translated.Tests
{
    internal static class VisualChecks
    {
        static void Initialize()
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            Theme.Initialize();
        }

        internal static void Run(string folder)
        {
            Initialize();
            Directory.CreateDirectory(folder);
            string settings = Path.Combine(Path.GetTempPath(),"translated-preview-"+Guid.NewGuid().ToString("N"),"settings.json");
            var prefs = new Preferences { Configured = true, Hotkey = new Shortcut { Modifiers = 7, Key = Shortcut.F1+9 } };
            prefs.Save(settings);
            Exception failure = null;
            try
            {
                using (var app = new TranslatorApp(settings,false))
                {
                    EventHandler render = null;
                    render = async (s,e) => {
                        Forms.Application.Idle -= render;
                        try { await Render(folder); }
                        catch (Exception ex) { failure = ex; }
                        finally { Forms.Application.ExitThread(); }
                    };
                    Forms.Application.Idle += render; Forms.Application.Run();
                }
            }
            finally { File.Delete(settings); Directory.Delete(Path.GetDirectoryName(settings)); }
            if (failure != null) throw failure;
        }

        static void Save(FrameworkElement view, string file, double scale = 2)
        {
            if (view is Window) view = (FrameworkElement)VisualTreeHelper.GetChild(view,0);
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth*scale),
                (int)Math.Ceiling(view.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) png.Save(stream);
        }
        static async Task Render(string folder)
        {
            var windows = Application.Current.Windows.Cast<Window>().ToList();
            var workspace = windows.OfType<Workspace>().Single();
            workspace.Open(false); await Task.Delay(130);
            Save(workspace,Path.Combine(folder,"Translate.png"));
            workspace.Input.Text="hello";
            workspace.ShowTranslation("hello","你好","");
            await Task.Delay(80); Save(workspace,Path.Combine(folder,"Inline-result.png"));
            Theme.ApplyPalette(true,false); await Task.Delay(70); Save(workspace,Path.Combine(folder,"Dark.png")); Theme.Refresh();
            workspace.Open(true); await Task.Delay(80);
            Save(workspace,Path.Combine(folder,"Settings.png"));
            workspace.SettingsPage.ScrollToBottom(); await Task.Delay(80);
            Save(workspace,Path.Combine(folder,"Settings-options.png"));
            workspace.Width = 465; workspace.Height = 510; workspace.Open(false); await Task.Delay(80);
            Save(workspace,Path.Combine(folder,"Inline-result-small.png"));
            workspace.EditorScroll.ScrollToBottom(); await Task.Delay(80);
            Save(workspace,Path.Combine(folder,"Inline-result-small-bottom.png"));
            if (workspace.Input.ActualHeight < 65 || workspace.ManualOutput.ActualHeight < 65 || workspace.TranslateButton.ActualWidth < 85)
                throw new Exception("Editor collapsed at minimum size: " + workspace.Input.ActualHeight + ", " + workspace.ManualOutput.ActualHeight);
            foreach (var area in new[]{new System.Drawing.Rectangle(-2304,-120,2304,1296),new System.Drawing.Rectangle(350,-1080,1920,1032)})
            foreach (double scale in new[]{1d,1.5,2})
            {
                var edge=new System.Drawing.Point(area.Right-1,area.Bottom-1);
                var near=OverlayPlacement.Bounds(area,edge,scale,88,31,true);
                if (!area.Contains(near) || near.Bottom>=edge.Y)
                    throw new Exception("Invalid overlay placement at "+area+" / "+scale);
            }
            Console.WriteLine("PASS  rendered both pages and inline translation at normal and minimum sizes");
            Console.WriteLine("PASS  dark palette and negative-coordinate monitor geometry at 100%, 150% and 200%");
        }
    }
}
#endif
