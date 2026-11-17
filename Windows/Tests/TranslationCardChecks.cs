#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Wpf = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace Translated.Tests
{
    internal static class TranslationCardChecks
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint key,uint mode);
        static async Task Wait(Func<bool> ready, string name, int timeout = 7000)
        {
            var timer=Stopwatch.StartNew();
            while (!ready() && timer.ElapsedMilliseconds<timeout) await Task.Delay(30);
            if (!ready()) throw new Exception("Timed out: "+name);
        }
        static void Pass(bool value, string name)
        { if (!value) throw new Exception(name); Console.WriteLine("PASS  "+name); }

        internal static void Preview()
        {
            Forms.Application.EnableVisualStyles();
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            Theme.Initialize();
            string folder=Path.Combine(Path.GetTempPath(),"translated-card-preview-"+Guid.NewGuid().ToString("N"));
            string settings=Path.Combine(folder,"settings.json");
            using (var server=new BrowserChecks.LocalProvider())
            {
                server.Translation=(instruction,source)=>instruction.Contains("into Japanese as") ? "こんにちは。翻訳プレビューへようこそ。" : "你好，欢迎使用划词翻译卡片。";
                var prefs=new Preferences { Active=Provider.Custom, Configured=true, Hotkey=new Shortcut { Modifiers=7,Key=Shortcut.F1+9 } };
                prefs.Profile(Provider.Custom).BaseUrl=server.Address;
                prefs.Profile(Provider.Custom).Model="desktop-fixture"; prefs.Save(settings);
                using (var app=new TranslatorApp(settings,false))
                {
                    var card=Wpf.Current.Windows.OfType<TranslationCard>().Single();
                    card.ShowInTaskbar=true;
                    var id=Guid.NewGuid();
                    card.Begin(id,"Hello, welcome to the translation preview.",null,"zh-Hans",new System.Drawing.Point(600,280));
                    card.Complete(id,"你好，欢迎使用划词翻译卡片。");
                    card.IsVisibleChanged+=(s,e)=> { if (!card.IsVisible) Forms.Application.ExitThread(); };
                    Forms.Application.Run();
                }
            }
            File.Delete(settings); Directory.Delete(folder);
        }

        internal static void Run(bool realShortcut = false)
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            Theme.Initialize();
            string folder=Path.Combine(Path.GetTempPath(),"translated-cards-"+Guid.NewGuid().ToString("N"));
            string settings=Path.Combine(folder,"settings.json");
            Exception failure=null;
            using (var server=new BrowserChecks.LocalProvider())
            {
                server.Translation=(instruction,source)=>instruction.Contains("into Japanese as") ? "こんにちは" :
                    instruction.Contains("into French as") ? "Bonjour" : instruction.Contains("into Arabic as") ? "مرحبا" : "你好";
                var prefs=new Preferences { Active=Provider.Custom, Configured=true, Floating=false,
                    Hotkey=new Shortcut { Modifiers=7, Key=Shortcut.F1+8 } };
                prefs.Profile(Provider.Custom).BaseUrl=server.Address;
                prefs.Profile(Provider.Custom).Model="desktop-fixture";
                prefs.Save(settings);
                using (var app=new TranslatorApp(settings,false))
                using (var fixture=new Process { StartInfo=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--fixture") { UseShellExecute=false } })
                {
                    EventHandler start=null;
                    start=async(s,e)=> {
                        Forms.Application.Idle-=start;
                        try
                        {
                            Pass(app.HotkeyReady,"card fixture registers an independent shortcut");
                            fixture.Start();
                            await Wait(()=>{ fixture.Refresh(); return fixture.MainWindowHandle!=IntPtr.Zero; },"source fixture");
                            var window=fixture.MainWindowHandle;
                            SetForegroundWindow(window);
                            SendMessage(window,0x8001,(IntPtr)1,IntPtr.Zero);
                            await Task.Delay(140);
                            uint clipboard=ClipboardAccess.Sequence;
                            IntPtr focus=Desktop.Focus(window);
                            var listener=(InputWindow)typeof(TranslatorApp).GetField("listener",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app);
                            // Enter through the same native-message handler used by the registered hotkey.
                            if (realShortcut)
                            {
                                Console.WriteLine("Waiting for the independent desktop fixture to be activated.");
                                await Wait(()=>Desktop.GetForegroundWindow()==window,"activate independent shortcut fixture",30000);
                                SendMessage(window,0x8001,(IntPtr)1,IntPtr.Zero);
                                await Task.Delay(100);
                                Pass(Desktop.GetForegroundWindow()==window,"physical shortcut input targets only the independent fixture");
                                foreach(byte key in new byte[]{17,18,16,120}) keybd_event(key,(byte)MapVirtualKey(key,0),0,UIntPtr.Zero);
                                await Task.Delay(80);
                                foreach(byte key in new byte[]{120,16,18,17}) keybd_event(key,(byte)MapVirtualKey(key,0),2,UIntPtr.Zero);
                            }
                            else SendMessage(listener.Handle,0x312,(IntPtr)1,IntPtr.Zero);
                            var card=Wpf.Current.Windows.OfType<TranslationCard>().Single();
                            await Wait(()=>card.IsVisible && server.Calls==1,"selection capture and request");
                            Pass(card.LoadingLabel.Visibility==System.Windows.Visibility.Visible && !card.CopyButton.IsEnabled,
                                "card displays loading and prevents copying an incomplete result");
                            Pass(Desktop.GetForegroundWindow()==window && Desktop.Focus(window)==focus,"first card show preserves original input focus");
                            Pass(card.Topmost && card.SourceWindow==window,"card is raised for its active source preview session");
                            Pass(card.ContainsPoint(new System.Drawing.Point(card.PixelBounds.Left+30,card.PixelBounds.Top+30)),
                                "preview card is visibly above the original window without stealing focus");
                            await Wait(()=>!app.Busy,"first translation");
                            Pass(card.Source=="hello" && card.Translation.Text=="你好" && card.CopyButton.IsEnabled,"hotkey captures native selection and displays translation directly");
                            int revision=listener.Revision;
                            listener.ObserveForeground(IntPtr.Zero);
                            Pass(card.IsVisible && listener.Revision==revision,"late foreground notifications cannot cancel or hide a newer translation card");
                            Pass(ClipboardAccess.Sequence==clipboard,"preview does not change the user's clipboard");
                            Pass(Desktop.Range(focus,out int from,out int to) && from==18 && to==23,
                                "preview keeps the original editable selection untouched");

                            int before=server.Calls;
                            card.TargetPicker.SelectedItem=TranslationLanguages.Find("ja");
                            await Wait(()=>server.Calls==before+1 && !app.Busy,"Japanese target");
                            Pass(card.Translation.Text=="こんにちは" && server.LastSource=="hello" && Preferences.Load(settings).TargetLanguage=="ja",
                                "target switch reuses captured original and persists language");

                            var release=server.HoldNextResponse();
                            card.TargetPicker.SelectedItem=TranslationLanguages.Find("fr");
                            await Wait(()=>server.Calls==before+2,"held French request");
                            card.TargetPicker.SelectedItem=TranslationLanguages.Find("ar");
                            release();
                            await Wait(()=>server.Calls==before+3 && !app.Busy,"newer Arabic request");
                            Pass(card.Translation.Text=="مرحبا" && card.Translation.FlowDirection==System.Windows.FlowDirection.RightToLeft,
                                "new target supersedes an in-flight response and supports right-to-left text");

                            server.Fail=true;
                            app.RetranslateCard("ar");
                            await Wait(()=>!app.Busy,"failure state");
                            Pass(card.Status.Text.Contains("503") && !card.CopyButton.IsEnabled && card.RetryButton.IsEnabled,
                                "service error stays visible with retry and no stale copy");
                            server.Fail=false;
                            app.RetranslateCard("ar");
                            await Wait(()=>!app.Busy,"retry");
                            Pass(card.Translation.Text=="مرحبا" && card.Status.Text=="","retry recovers from an error using the same source");

                            before=server.Calls;
                            release=server.HoldNextResponse();
                            app.RetranslateCard("ja");
                            await Wait(()=>server.Calls==before+1,"request before closing");
                            app.Cancel(); release();
                            await Wait(()=>!app.Busy,"card cancellation");
                            await Task.Delay(150);
                            Pass(!card.IsVisible && !card.Topmost,"closing cancels the request, clears topmost and a late response cannot reopen the card");
                            Pass(ClipboardAccess.Sequence==clipboard,"language switching, errors, retry and cancellation preserve clipboard contents");

                            app.SaveTarget("zh-Hans");
                            app.SaveOptions(false,true,false,false,preview:false);
                            SetForegroundWindow(window); SendMessage(window,0x8001,(IntPtr)1,IntPtr.Zero);
                            await Task.Delay(140);
                            SendMessage(listener.Handle,0x312,(IntPtr)1,IntPtr.Zero);
                            await Wait(()=>!app.Busy,"legacy automatic replacement");
                            Pass(!card.IsVisible && Desktop.VerifyClassicInsertion(focus,18,"你好"),
                                "optional automatic mode still replaces the verified native selection");
                            Pass(ClipboardAccess.Sequence==clipboard,"native automatic replacement preserves clipboard contents");

                            var workspace=Wpf.Current.Windows.OfType<Workspace>().Single();
                            before=server.Calls; release=server.HoldNextResponse();
                            app.Manual("hello");
                            await Wait(()=>server.Calls==before+1,"manual translation before target change");
                            workspace.TargetPicker.SelectedItem=TranslationLanguages.Find("fr");
                            release(); await Wait(()=>!app.Busy,"manual target change cancellation");
                            Pass(app.Prefs.TargetLanguage=="fr" && workspace.ManualOutput.Text=="",
                                "main-page target changes cancel old work and cannot label an old translation as the new language");
                        }
                        catch (Exception error) { failure=error; }
                        finally { app.Cancel(); if (!fixture.HasExited) fixture.CloseMainWindow(); Forms.Application.ExitThread(); }
                    };
                    Forms.Application.Idle+=start; Forms.Application.Run();
                }
            }
            File.Delete(settings); Directory.Delete(folder);
            if (failure!=null) throw failure;
        }
    }
}
#endif
