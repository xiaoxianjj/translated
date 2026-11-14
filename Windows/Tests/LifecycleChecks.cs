#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Translated.Tests
{
    // No visible windows, browser input, personal settings or real provider requests.
    internal static class LifecycleChecks
    {
        const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
        static void Pass(bool condition,string name)
        { if(!condition) throw new Exception(name); Console.WriteLine("PASS  "+name); }
        static void Set(object target,string name,object value) => target.GetType().GetField(name,Hidden).SetValue(target,value);
        static Preferences PreferencesForTest() => new Preferences { Floating=false,Hotkey=new Shortcut { Modifiers=7,Key=Shortcut.F1+16 } };

        internal static void ShutdownCycle()
        {
            SetErrorMode(0x0001|0x0002); // Owned child only: do not display a native crash dialog on failure.
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            Theme.Initialize();
            string folder=Path.Combine(Path.GetTempPath(),"translated-shutdown-"+Guid.NewGuid().ToString("N"));
            string path=Path.Combine(folder,"settings.json");
            PreferencesForTest().Save(path);
            try
            {
                using(var input=new InputWindow()) { input.Dispose(); input.Dispose(); }
                using(var app=new TranslatorApp(path,false,false))
                {
                    EventHandler done=null;
                    done=async(s,e)=> {
                        Forms.Application.Idle-=done;
                        // Pending, synthetic selection work must also be cancelled and drained.
                        typeof(TranslatorApp).GetMethod("ShowSelectionButton",Hidden).Invoke(app,new object[] { IntPtr.Zero,null });
                        await app.Shutdown();
                    };
                    Forms.Application.Idle+=done;
                    Forms.Application.Run(app); // Framework Dispose, followed by the using Dispose.
                    app.Dispose(); // Also exercise an explicit repeated application Dispose.
                }
                GC.Collect(); GC.WaitForPendingFinalizers();
                Console.WriteLine("Repeated listener/application cleanup completed normally.");
            }
            finally { File.Delete(path); Directory.Delete(folder); }
        }

        sealed class Stub : HttpMessageHandler
        {
            internal Uri Destination;
            internal Request Body;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                Destination=request.RequestUri; Body=Json.Read<Request>(await request.Content.ReadAsByteArrayAsync());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"fixture\"}}]}") };
            }
        }

        static void SnapshotRouting()
        {
            var appType=typeof(TranslatorApp);
            var app=FormatterServices.GetUninitializedObject(appType);
            var prefs=new Preferences { Configured=true,Active=Provider.Custom,Traditional=true };
            var profile=prefs.Profile(Provider.Custom);
            profile.Model="original-model";
            profile.BaseUrl="http://127.0.0.1/original/"+Guid.NewGuid().ToString("N");
            var snapshot=TranslationSettings.Capture(prefs);
            var job=Activator.CreateInstance(appType.GetNestedType("Job",BindingFlags.NonPublic),true);
            Set(job,"Source","hello"); Set(job,"Target","auto"); Set(job,"Settings",snapshot);
            Set(app,"<Prefs>k__BackingField",prefs);
            profile.Model="changed-model"; profile.BaseUrl="http://127.0.0.1/changed/"+Guid.NewGuid().ToString("N");
            prefs.Configured=false; prefs.Traditional=false;
            var field=typeof(Translator).GetField("Loopback",BindingFlags.Static|BindingFlags.NonPublic);
            var saved=field.GetValue(null);
            var stub=new Stub();
            using(var client=new HttpClient(stub))
            {
                field.SetValue(null,client);
                var previousContext=SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try {
                    var task=(Task<string>)appType.GetMethod("Translate",Hidden).Invoke(app,new[]{job});
                    Pass(task.GetAwaiter().GetResult()=="fixture" && stub.Destination==snapshot.Provider.Endpoint() &&
                        stub.Body.model=="original-model" && stub.Body.messages[0].content.Contains("Traditional Chinese"),
                        "existing job uses its original provider, model, configured state and Chinese preference after settings change");
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); field.SetValue(null,saved); ((IDisposable)job).Dispose(); }
            }
        }

        internal static void Run(string output=null)
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            Theme.Initialize();
            SnapshotRouting();
            using(var stop=new CancellationTokenSource())
            using(var card=new TranslationCard(null,()=>stop.Cancel()))
            {
                card.Topmost=true;
                card.Close();
                Pass(stop.IsCancellationRequested && !card.IsVisible && !card.Topmost && card.Session==Guid.Empty,
                    "standard card close cancels the request and clears visibility, session and topmost without showing a window");
            }
            int disposedCloseCalls=0;
            using(var card=new TranslationCard(null,()=>disposedCloseCalls++)) { }
            Pass(disposedCloseCalls==0,"disposing the card does not issue an extra user cancellation");

            string folder=Path.Combine(Path.GetTempPath(),"translated-editor-layout-"+Guid.NewGuid().ToString("N"));
            string path=Path.Combine(folder,"settings.json"); PreferencesForTest().Save(path);
            try
            {
                using(var app=new TranslatorApp(path,false,false))
                using(var editor=new ShortcutEditor(app))
                {
                    foreach(var work in new[]{new[]{1920d,1040d},new[]{960d,520d},new[]{1092.8,582.4},new[]{640d,340d}})
                    {
                        var size=EditorSizing.Fit(work[0],work[1],1190,790);
                        editor.MinWidth=size.MinWidth; editor.MinHeight=size.MinHeight;
                        editor.Width=size.Width; editor.Height=size.Height;
                        var view=(FrameworkElement)editor.Content;
                        var client=new Size(size.Width-16,size.Height-40);
                        view.Measure(client); view.Arrange(new Rect(client)); view.UpdateLayout();
                        var button=editor.SaveShortcutButton;
                        var point=button.TransformToAncestor(view).Transform(new Point(0,0));
                        Console.WriteLine("LAYOUT view="+view.RenderSize+" button="+button.RenderSize+" at "+point);
                        Pass(point.Y>=0 && point.Y+button.ActualHeight<=view.RenderSize.Height && button.ActualWidth>=100 && button.ActualHeight>=25,
                            "save button remains visible in shortcut editor work area "+work[0]+" x "+work[1]+" DIP");
                        if(output!=null)
                        {
                            Directory.CreateDirectory(output);
                            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(client.Width),(int)Math.Ceiling(client.Height),96,96,PixelFormats.Pbgra32);
                            var backdrop=new DrawingVisual();
                            using(var drawing=backdrop.RenderOpen()) drawing.DrawRectangle((Brush)Application.Current.FindResource("Paper"),null,new Rect(client));
                            bitmap.Render(backdrop);
                            bitmap.Render(view);
                            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                            using(var stream=File.Create(Path.Combine(output,"editor-"+(int)work[0]+"x"+(int)work[1]+".png"))) png.Save(stream);
                        }
                    }
                }
            }
            finally { File.Delete(path); Directory.Delete(folder); }

            for(int i=0;i<5;i++)
            {
                using(var child=new Process { StartInfo=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--shutdown-cycle") {
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } })
                {
                    child.Start();
                    if(!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Owned shutdown fixture timed out"); }
                    string outputText=child.StandardOutput.ReadToEnd(), errorText=child.StandardError.ReadToEnd();
                    if(child.ExitCode!=0) throw new Exception("Shutdown cycle "+(i+1)+" exit=0x"+child.ExitCode.ToString("X8")+" "+outputText+" "+errorText);
                    Pass(true,"real WinForms message-loop exit and repeated cleanup cycle "+(i+1)+" exits normally");
                }
            }
            Console.WriteLine("Lifecycle checks passed without visible windows or provider calls.");
        }
    }
}
#endif
