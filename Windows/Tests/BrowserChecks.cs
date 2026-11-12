#if NET48
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Forms = System.Windows.Forms;
using Wpf = System.Windows.Application;

namespace Translated.Tests
{
    // A fresh browser profile, local HTML fixture, ephemeral provider and temporary settings.
    // All input must target either this browser or this test's own nonactivating selection button.
    internal static class BrowserChecks
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr wnd,IntPtr after,int x,int y,int w,int h,uint flags);
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint key,uint mode);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags,int dx,int dy,uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
        const string FirstParagraph = "周六的读书会将在**图书馆的第二间阅览室**举行。大家可以提前到场，在书架旁领取本期的阅读材料。";
        const string LastParagraph = "本期材料的**修订版本是 1.2.**";
        const string Page = @"<!doctype html><meta charset=utf-8><title>Translated fixture ready</title>
<style>body{font:20px sans-serif;margin:40px}textarea, input{display:block;width:360px;padding:10px;font:inherit}p{width:max-content}button{margin:20px}</style>
<textarea id=editor aria-label='Fixture editor'>left  hello  right</textarea>
<div id=chat>
<p id=passage tabindex='0'><span>hello</span></p><input type=password id=secret aria-label='Fixture password' value='fixture'>
<div id=rich class=ProseMirror contenteditable role=textbox aria-label='Fixture rich editor'><p>left hello right</p></div>
</div>
<button type=button>Fixture button</button>
<script>
const edit=document.querySelector('#editor'), para=document.querySelector('#passage'), rich=document.querySelector('#rich');
// Codex wraps its chat and ProseMirror composer in a deep accessibility tree.
let branch=document.querySelector('#chat');
for(let i=0;i<32;i++){const group=document.createElement('div');group.setAttribute('role','group');
 group.setAttribute('aria-label','Chat layer '+i);branch.replaceWith(group);group.appendChild(branch);branch=group}
addEventListener('keydown',e=>{
 if(e.ctrlKey||e.altKey||e.shiftKey)return;
 if(e.key==='F2'){e.preventDefault();edit.value='left  hello  right';edit.focus();edit.setSelectionRange(4,13);document.title='Translated fixture edit'}
 if(e.key==='F3'){e.preventDefault();para.focus();let r=new Range();r.selectNodeContents(para);let s=getSelection();s.removeAllRanges();s.addRange(r);document.title='Translated fixture passage'}
 if(e.key==='F4'){e.preventDefault();document.querySelector('#secret').focus();document.querySelector('#secret').select();document.title='Translated fixture secure'}
 if(e.key==='F6'){e.preventDefault();const p=document.createElement('p');p.textContent='left hello right';rich.replaceChildren(p);rich.focus();
 let r=new Range();r.setStart(p.firstChild,5);r.setEnd(p.firstChild,10);let s=getSelection();s.removeAllRanges();s.addRange(r);document.title='Translated fixture rich'}
});
edit.oninput=()=>document.title=edit.value==='left  你好  right'?'Translated fixture EDITED':'Translated fixture changed';
rich.oninput=()=>document.title=rich.textContent==='left 你好 right'?'Translated fixture RICH EDITED':'Translated fixture changed';
for(const key of ['F2','F3','F4','F6']){let b=document.createElement('button');b.textContent='Prepare '+key;
b.onclick=()=>window.dispatchEvent(new KeyboardEvent('keydown',{key}));document.body.appendChild(b)}
function prepareRange(label,setup) {
 const button=document.createElement('button');button.textContent=label;
 button.onclick=()=>{rich.setAttribute('role','textbox');rich.setAttribute('aria-multiline','true');rich.contentEditable='true';
   const range=new Range();setup(range);const selection=getSelection();selection.removeAllRanges();selection.addRange(range);
   document.title='Translated fixture '+label;};
 document.body.appendChild(button);
}
prepareRange('Multiple paragraphs',r=>{rich.innerHTML='<p>first</p><p>second</p>';rich.focus();r.selectNodeContents(rich)});
prepareRange('Editable group',r=>{rich.removeAttribute('role');rich.innerHTML='<p>left hello right</p>';rich.focus();const t=rich.firstChild.firstChild;r.setStart(t,5);r.setEnd(t,10)});
prepareRange('Read-only group',r=>{rich.removeAttribute('role');rich.contentEditable='false';rich.tabIndex=0;rich.innerHTML='<p>left hello right</p>';rich.focus();const t=rich.firstChild.firstChild;r.setStart(t,5);r.setEnd(t,10)});
prepareRange('Outside focused group',r=>{rich.removeAttribute('role');rich.focus();r.selectNodeContents(para)});
for (const mode of ['textbox','group','immediate']) prepareRange('Repeat '+mode,r=>{
 rich.innerHTML='<p>Left: See you tomorrow. Right.</p>';
 rich.focus(); const t=rich.firstChild.firstChild;r.setStart(t,6);r.setEnd(t,23);
 rich.oninput=()=>{
   if(mode==='immediate' && rich.textContent==='Left: 明天见。 Right.'){
     // Reselect the inserted span before the translator has finished confirming this paste.
     const selected=getSelection(), inserted=selected.getRangeAt(0).cloneRange();
     inserted.setStart(inserted.endContainer,inserted.endOffset-'明天见。'.length);
     selected.removeAllRanges();selected.addRange(inserted);
   }
   document.title='Translated fixture text '+rich.textContent;
 };
 rich.onpointerdown=()=>{if(mode==='group')rich.removeAttribute('role')};
});
const firstParagraph='" + FirstParagraph + @"', lastParagraph='" + LastParagraph + @"';
function selectPassage(range,copy) {
 const blocks=rich.children, start=blocks[copy*3].firstChild, end=blocks[copy*3+2].firstChild;
 range.setStart(start,'Before '.length);range.setEnd(end,lastParagraph.length);
}
prepareRange('Prepare draft',r=>{
 const passage='<p>Before '+firstParagraph+'</p><p><br></p><p>'+lastParagraph+' After</p>';
 rich.innerHTML=passage+passage;rich.dataset.added='';rich.focus();selectPassage(r,0);
 let pastes=0;rich.onpaste=()=>{pastes++};
 rich.oninput=()=>{document.title=pastes===1 && rich.textContent.replace(/[\r\n]/g,'').replace(/\u00a0/g,' ')===
   'Before First.Second. AfterBefore '+firstParagraph+lastParagraph+' After'+rich.dataset.added && /First\.(?:\r?\n){2,}Second\./.test(rich.innerText) ?
   'Translated fixture draft replaced':'Translated fixture wrong draft';};
});
for(const mode of ['Refresh draft','Move selection']) {
 const b=document.createElement('button');b.textContent=mode;document.body.appendChild(b);
 b.onclick=()=>{rich.innerHTML=rich.innerHTML;rich.focus();const r=new Range();
   selectPassage(r,mode==='Move selection'?1:0);const s=getSelection();s.removeAllRanges();s.addRange(r);
   document.title='Translated fixture '+mode;};
}
const appendButton=document.createElement('button');appendButton.textContent='Append outside selection';document.body.appendChild(appendButton);
appendButton.onclick=()=>{rich.dataset.added='Changed suffix';const node=document.createElement('p');node.textContent=rich.dataset.added;
 rich.appendChild(node);rich.focus();const r=new Range();selectPassage(r,0);getSelection().removeAllRanges();getSelection().addRange(r);
 document.title='Translated fixture Append outside selection';};
</script>";

        sealed class LocalProvider : IDisposable
        {
            readonly TcpListener server = new TcpListener(IPAddress.Loopback,0);
            TaskCompletionSource<bool> nextResponse;
            internal int Calls;
            internal string LastModel, LastInstruction, LastSource;
            internal volatile bool Fail;
            internal string Address { get; }
            internal Action HoldNextResponse()
            {
                var signal = new TaskCompletionSource<bool>();
                if (Interlocked.CompareExchange(ref nextResponse,signal,null) != null)
                    throw new InvalidOperationException("A fixture response is already pending.");
                return () => signal.TrySetResult(true);
            }
            internal LocalProvider()
            {
                server.Start(); Address = "http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port+"/v1";
                Task.Run(async () => {
                    try { while (true) { var peer = await server.AcceptTcpClientAsync(); _ = Task.Run(() => Reply(peer)); } }
                    catch (ObjectDisposedException) {} catch (SocketException) {}
                });
            }
            void Reply(TcpClient peer)
            {
                try
                {
                    using (peer)
                    using (var stream=peer.GetStream())
                    {
                        stream.ReadTimeout=4000;
                        var headers=new List<byte>();
                        while(headers.Count<16000)
                        {
                            int b=stream.ReadByte(); if(b<0)return; headers.Add((byte)b); int n=headers.Count;
                            if(n>=4 && headers[n-4]==13 && headers[n-3]==10 && headers[n-2]==13 && headers[n-1]==10)break;
                        }
                        string head=Encoding.ASCII.GetString(headers.ToArray());
                        int length=int.Parse(head.Split(new[]{"\r\n"},StringSplitOptions.None).First(l=>l.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).Substring(15));
                        if(length<=0 || length>64000 || !head.StartsWith("POST /v1/chat/completions ")) return;
                        if(head.IndexOf("100-continue",StringComparison.OrdinalIgnoreCase)>=0) Send(stream,"HTTP/1.1 100 Continue\r\n\r\n");
                        var bytes=new byte[length];
                        for(int read=0;read<length;) { int got=stream.Read(bytes,read,length-read); if(got==0)return; read+=got; }
                        var request=Json.Read<Request>(bytes);
                        if(request.model!="desktop-fixture" && request.model!="draft-fixture")return;
                        LastModel=request.model;LastInstruction=request.messages[0].content;
                        var payload=Json.Read<Payload>(Encoding.UTF8.GetBytes(request.messages[1].content));
                        LastSource=payload.text;
                        string translation=payload.text.StartsWith(FirstParagraph,StringComparison.Ordinal) ?
                            payload.text.Replace(FirstParagraph,"First.").Replace(LastParagraph,"Second.") :
                            payload.text=="See you tomorrow." ? "明天见。" :
                            payload.text=="明天见。" ? "See you tomorrow!" : "你好";
                        var hold=Interlocked.Exchange(ref nextResponse,null);
                        Interlocked.Increment(ref Calls);
                        if (hold == null) Thread.Sleep(450);
                        else if (!hold.Task.Wait(12000)) return;
                        string json="{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":"+Encoding.UTF8.GetString(Json.Write(translation))+"}}]}";
                        Send(stream,"HTTP/1.1 "+(Fail?"503 Unavailable":"200 OK")+"\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: "+Encoding.UTF8.GetByteCount(json)+"\r\n\r\n"+json);
                        peer.Client.Shutdown(SocketShutdown.Send);
                        // Keep the socket open for the client to consume the complete response.
                        while (stream.ReadByte() >= 0) { }
                    }
                }
                catch(IOException) {} catch(SocketException) {} catch(ObjectDisposedException) {}
            }
            static void Send(Stream stream,string value) { var bytes=Encoding.UTF8.GetBytes(value);stream.Write(bytes,0,bytes.Length); }
            public void Dispose()=>server.Stop();
        }

        static async Task Wait(Func<bool> predicate,string name,int max=6500)
        {
            var clock=Stopwatch.StartNew();
            while (!predicate() && clock.ElapsedMilliseconds<max) await Task.Delay(45);
            if(!predicate()) throw new InvalidOperationException("Timed out: "+name);
        }
        static void Pass(bool ok,string name) { if(!ok)throw new InvalidOperationException(name); Console.WriteLine("PASS  "+name); }
        static bool NoResultPopup() => !Wpf.Current.Windows.OfType<ResultWindow>().Any(w => w.IsVisible);
        static Forms.NotifyIcon Tray(TranslatorApp app) => (Forms.NotifyIcon)typeof(TranslatorApp)
            .GetField("tray",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app);
        static Forms.ToolStripMenuItem ReviewMenu(TranslatorApp app) => (Forms.ToolStripMenuItem)typeof(TranslatorApp)
            .GetField("reviewItem",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app);
        static void Key(IntPtr window, params byte[] keys)
        {
            if(Desktop.GetForegroundWindow()!=window)throw new InvalidOperationException("Fixture lost focus before test input.");
            foreach(byte k in keys)keybd_event(k,(byte)MapVirtualKey(k,0),0,UIntPtr.Zero);
            foreach(byte k in keys.Reverse())keybd_event(k,(byte)MapVirtualKey(k,0),2,UIntPtr.Zero);
        }
        static async Task Click(IntPtr root,Point at, bool twice = false)
        {
            SetCursorPos(at.X,at.Y); await Task.Delay(35);
            for(int i=0;i<(twice?2:1);i++)
            {
                if(Desktop.RootAt(at)!=root)throw new InvalidOperationException("Fixture point unavailable: "+at+" expected "+root+" actual "+Desktop.RootAt(at));
                mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); await Task.Delay(70);
            }
        }
        static async Task<AutomationElement> FindElement(IntPtr window, string name)
        {
            AutomationElement element=null;
            for (int i=0;i<30 && element==null;i++)
            {
                element=await SelectionReader.Query(()=>AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty,name)),CancellationToken.None);
                if (element==null) await Task.Delay(150);
            }
            if(element==null)throw new InvalidOperationException("Browser did not expose fixture element "+name);
            return element;
        }
        static async Task<System.Windows.Rect> Find(IntPtr window,string name) =>
            (await FindElement(window,name)).Current.BoundingRectangle;
        static Point Center(System.Windows.Rect r) => new Point((int)(r.X+r.Width/2),(int)(r.Y+r.Height/2));

        internal static void Run(string executable, bool cold = false, bool busy = false, bool rangeOwner = false, bool repeat = false, bool multiline = false)
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext()); Theme.Initialize();
            string dir=Path.Combine(Path.GetTempPath(),"Translated-browser-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string page=Path.Combine(dir,"selection.html"), settings=Path.Combine(dir,"settings.json");
            string ready="Translated fixture ready "+Guid.NewGuid().ToString("N");
            string fixture=Page.Replace("Translated fixture ready",ready);
            if (cold) fixture=fixture.Replace("</script>", @"
let prepared=false;
function prepareCold(){if(prepared)return;prepared=true;rich.focus();let p=rich.firstElementChild;
 let r=new Range();r.setStart(p.firstChild,5);r.setEnd(p.firstChild,10);
 let s=getSelection();s.removeAllRanges();s.addRange(r);document.title='"+ready+@" fixture rich'}
addEventListener('load',prepareCold);addEventListener('focus',prepareCold);
</script>");
            File.WriteAllText(page,fixture,Encoding.UTF8);
            string arguments="--user-data-dir=\""+Path.Combine(dir,"profile")+"\" --no-first-run --no-default-browser-check --disable-default-apps --disable-sync --disable-background-networking "+
                "--window-size=760,620 --app=\""+new Uri(page).AbsoluteUri+"\"";
            if (string.Equals(Path.GetFileName(executable),"electron.exe",StringComparison.OrdinalIgnoreCase))
            {
                // Exercise the global Alt shortcut in an Electron window with an application menu.
                string entry=Path.Combine(dir,"main.cjs");
                File.WriteAllText(entry,@"const {app,BrowserWindow,Menu}=require('electron'), path=require('node:path');
app.setPath('userData',path.join(__dirname,'profile'));
app.whenReady().then(()=>{
 Menu.setApplicationMenu(Menu.buildFromTemplate([{label:'&File',submenu:[{role:'quit'}]},{label:'&Jobs',submenu:[{role:'minimize'}]}]));
 const win=new BrowserWindow({width:760,height:620,autoHideMenuBar:true});
 win.loadFile(path.join(__dirname,'selection.html'));
});app.on('window-all-closed',()=>app.quit());",Encoding.UTF8);
                arguments="\""+entry+"\"";
            }
            Exception failure=null;
            using(var server=new LocalProvider())
            {
                var p=new Preferences { Active=Provider.Custom, Configured=true, Hotkey=new Shortcut { Modifiers=7, Key=Shortcut.F1+8 } };
                if (cold) { p.Hotkey = new Shortcut { Modifiers=Shortcut.Alt, Key='J' }; p.Floating=false; }
                p.Profile(Provider.Custom).Model="desktop-fixture";p.Profile(Provider.Custom).BaseUrl=server.Address;p.Save(settings);
                using(var app=new TranslatorApp(settings,false))
                using(var owner=new Forms.Form())
                using(var launcher=new Process { StartInfo=new ProcessStartInfo(executable,arguments) { UseShellExecute=false } })
                {
                    EventHandler start=null;
                    start=async(s,e)=>{
                        Forms.Application.Idle-=start;
                        ClipboardAccess.Snapshot previous=null;
                        Process browser=null;
                        try
                        {
                            if(!app.HotkeyReady)throw new InvalidOperationException("Test shortcut already in use.");
                            ClipboardAccess.Write(owner.Handle,"translated fixture prior",ClipboardAccess.Sequence,true,out previous);
                            launcher.Start();
                            await Wait(()=>{
                                if (browser!=null)return true;
                                foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                                {
                                    if(p.MainWindowTitle.Contains(ready)){browser=p;return true;}
                                    p.Dispose();
                                }
                                return false;
                            }, "isolated fixture window",15000);
                            if (multiline) await CheckMultiline(browser,app,server,dir);
                            else if (repeat) await CheckRepeat(browser,app,server,owner.Handle);
                            else if (rangeOwner) await CheckRangeOwner(browser,app,server);
                            else if (cold) await CheckCold(browser,app,server,busy);
                            else await Check(browser,app,server,owner.Handle);
                        }
                        catch(Exception ex){failure=ex;}
                        finally
                        {
                            if(previous!=null)
                            {
                                uint seq=ClipboardAccess.Sequence;
                                if(Desktop.ProcessOf(ClipboardAccess.Owner)==Desktop.OwnProcess)await ClipboardAccess.Restore(owner.Handle,previous,seq);
                                previous.Dispose();
                            }
                            if(browser!=null) {if(!browser.HasExited)browser.CloseMainWindow();browser.Dispose();}
                            Forms.Application.ExitThread();
                        }
                    };
                    Forms.Application.Idle+=start;Forms.Application.Run();
                }
            }
            // The browser may briefly retain its profile after closing the isolated window.
            for(int attempt=0;attempt<12;attempt++)
            {
                try {Directory.Delete(dir,true);break;}
                catch(IOException){Thread.Sleep(200);} catch(UnauthorizedAccessException){Thread.Sleep(200);}
            }
            if(failure!=null)throw failure;
        }

        static async Task CheckRepeat(Process browser, TranslatorApp app, LocalProvider server, IntPtr owner)
        {
            var window=browser.MainWindowHandle;
            await ActivateFixture(browser);
            bool Titled(string title){browser.Refresh();return browser.MainWindowTitle.Contains(title);}
            async Task TranslateSelection(string source,string expected)
            {
                int before=server.Calls;
                Key(window,0x12,0x4A);
                await Wait(()=>server.Calls==before+1 && !app.Busy,"repeat shortcut");
                Pass(server.LastSource==source,"request contains exactly the newly selected text");
                if (Tray(app).Text!="Translated · Done")
                    throw new InvalidOperationException("Repeat delivery: "+Tray(app).Text+" / "+Wpf.Current.Windows.OfType<ResultWindow>().Single().Reason.Text);
                await Wait(()=>Titled("fixture text "+expected),"replacement preserves surrounding text");
                Pass(NoResultPopup() && Forms.Clipboard.GetText()=="translated fixture prior","result stays in place and prior clipboard is restored");
            }
            async Task StartImmediate()
            {
                var immediate=await FindElement(window,"Repeat immediate");
                ((InvokePattern)immediate.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                await Wait(()=>Titled("fixture Repeat immediate"),"prepare back-to-back translations");
                Key(window,0x12,0x4A);
                await Wait(()=>Titled("fixture text Left: 明天见。 Right."),"first paste is visible");
                Pass(app.Busy && Forms.Clipboard.GetText()=="明天见。","second shortcut starts during the first temporary paste");
            }
            await StartImmediate();
            await TranslateSelection("明天见。","Left: See you tomorrow! Right.");
            foreach (var mode in new[]{"textbox","group"})
            {
                var button=await FindElement(window,"Repeat "+mode);
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                await Wait(()=>Titled("fixture Repeat "+mode),"initial sentence");
                await TranslateSelection("See you tomorrow.","Left: 明天见。 Right.");
                var rect=await SelectionReader.Query(()=>{
                    var pattern=(TextPattern)AutomationElement.FocusedElement.GetCurrentPattern(TextPattern.Pattern);
                    var range=pattern.DocumentRange.FindText("明天见。",false,false);
                    return range?.GetBoundingRectangles();
                },CancellationToken.None);
                if (rect==null || rect.Length!=1) throw new InvalidOperationException("Cannot locate inserted sentence.");
                var from=new Point((int)rect[0].Left+1,(int)(rect[0].Top+rect[0].Height/2));
                var to=new Point((int)rect[0].Right-1,from.Y);
                if (Desktop.GetForegroundWindow()!=window || Desktop.RootAt(from)!=window || Desktop.RootAt(to)!=window)
                    throw new InvalidOperationException("Repeat fixture lost focus before selection.");
                SetCursorPos(from.X,from.Y); mouse_event(2,0,0,0,UIntPtr.Zero);
                try { await Task.Delay(35); SetCursorPos(to.X,to.Y); await Task.Delay(45); }
                finally { mouse_event(4,0,0,0,UIntPtr.Zero); }
                await TranslateSelection("明天见。","Left: See you tomorrow! Right.");
                Pass(true,"mouse reselected and retranslated the inserted sentence in the "+mode+" editor");
            }
            await StartImmediate();
            int beforeSecond=server.Calls;
            var release=server.HoldNextResponse();
            try
            {
                Key(window,0x12,0x4A);
                await Wait(()=>server.Calls==beforeSecond+1,"second translation started");
                var generation=ClipboardAccess.Sequence;
                if (Desktop.ProcessOf(ClipboardAccess.Owner)!=Desktop.OwnProcess) throw new InvalidOperationException("Fixture clipboard changed.");
                ClipboardAccess.Write(owner,"new clipboard during second translation",generation,false,out _);
            }
            finally { release(); }
            await Wait(()=>!app.Busy,"interrupted second translation");
            Pass(Titled("fixture text Left: 明天见。 Right.") && Forms.Clipboard.GetText()=="new clipboard during second translation"
                && Tray(app).Text=="Translated · Review result" && NoResultPopup(),"an independent copy still prevents replacement and keeps the new clipboard");
        }

        static async Task CheckMultiline(Process browser, TranslatorApp app, LocalProvider server, string directory)
        {
            var window=browser.MainWindowHandle;
            await ActivateFixture(browser);
            bool Titled(string suffix){browser.Refresh();return browser.MainWindowTitle.Contains("fixture "+suffix);}
            async Task Invoke(string label)
            {
                var button=await FindElement(window,label);
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                await Wait(()=>Titled(label),label);
            }
            var result=Wpf.Current.Windows.OfType<ResultWindow>().Single();
            foreach (string action in new[]{null,"Refresh draft","Busy reader","Append outside selection","Move selection"})
            {
                await Invoke("Prepare draft");
                int before=server.Calls;
                var release=server.HoldNextResponse();
                Task<string> backgroundRead=null;
                try
                {
                    Key(window,0x12,0x4A);
                    await Wait(()=>server.Calls==before+1,"multiline request");
                    Pass(server.LastSource.StartsWith(FirstParagraph,StringComparison.Ordinal) &&
                        server.LastSource.EndsWith(LastParagraph,StringComparison.Ordinal),"entire cross-paragraph selection reaches the provider");
                    if(action=="Busy reader")
                    {
                        var entered=new TaskCompletionSource<bool>();
                        backgroundRead=SelectionReader.Query(()=>{
                            entered.TrySetResult(true); Thread.Sleep(1100); return "complete";
                        },CancellationToken.None);
                        await entered.Task;
                    }
                    else if(action!=null) await Invoke(action);
                }
                finally {release();}
                if(backgroundRead!=null) await backgroundRead;
                await Wait(()=>!app.Busy,"multiline delivery");
                bool moved=action=="Move selection";
                if(!moved && Tray(app).Text=="Translated · Done") await Wait(()=>Titled("draft replaced") || Titled("wrong draft"),"browser rendered replacement");
                if (!Titled(moved?action:"draft replaced") || Tray(app).Text!=(moved?"Translated · Review result":"Translated · Done")
                    || server.Calls!=before+1 || !NoResultPopup() || Forms.Clipboard.GetText()!="translated fixture prior")
                    throw new InvalidOperationException((action??"Static draft")+" / "+Tray(app).Text+" / "+result.Reason.Text+" / "+browser.MainWindowTitle);
                Pass(true,moved ? "moving to the identical second passage blocks the paste" :
                    (action??"Static draft")+" replaces only the original multi-paragraph selection");
                if(moved)
                {
                    var report=File.ReadAllLines(Path.Combine(directory,"last-selection-check.txt"));
                    Pass(report.Contains("stage=before-delivery") && report.Contains("issue=PositionChanged")
                        && report.Contains("retryAfter=None") && report.Length==6,"last check identifies the changed position without retrying");
                }
            }
            await Invoke("Prepare draft");
            int requests=server.Calls;
            var finish=server.HoldNextResponse();
            using (var releaseReader=new ManualResetEventSlim())
            {
                Task<string> activeRead=null;
                try
                {
                    Key(window,0x12,0x4A);
                    await Wait(()=>server.Calls==requests+1,"request before interrupted delivery");
                    var entered=new TaskCompletionSource<bool>();
                    activeRead=SelectionReader.Query(()=>{
                        entered.TrySetResult(true); releaseReader.Wait(3500); return "complete";
                    },CancellationToken.None);
                    await entered.Task;
                    finish();
                    await Task.Delay(730);
                    Key(window,0x27);
                    await Task.Delay(100);
                }
                finally
                {
                    finish(); releaseReader.Set();
                    if(activeRead!=null) await activeRead;
                }
            }
            await Wait(()=>!app.Busy,"interrupted delivery check");
            Pass(Titled("Prepare draft") && Tray(app).Text=="Translated · Review result" && result.Reason.Text.Contains("新的输入")
                && server.Calls==requests+1 && NoResultPopup() && Forms.Clipboard.GetText()=="translated fixture prior",
                "moving the caret during recovery keeps the current text and clipboard");
        }

        static async Task CheckRangeOwner(Process browser, TranslatorApp app, LocalProvider server)
        {
            var window=browser.MainWindowHandle;
            await ActivateFixture(browser);
            await Wait(()=>{browser.Refresh();return browser.MainWindowTitle.Contains("fixture rich");},"prepare range fixture");
            await Click(window,Center(await Find(window,"Prepare F6")));
            await Wait(()=>{browser.Refresh();return browser.MainWindowTitle.Contains("fixture rich");},"restore range fixture selection");
            var editor=await FindElement(window,"Fixture rich editor");
            object value;
            if (!editor.TryGetCurrentPattern(TextPattern.Pattern,out value)) throw new InvalidOperationException("Fixture editor has no text pattern.");
            var ranges=((TextPattern)value).GetSelection();
            if (ranges.Length!=1) throw new InvalidOperationException("Fixture editor has no unique selection.");
            var inside=ranges[0].GetEnclosingElement();
            var focus=AutomationElement.FocusedElement;
            var chain=new List<string>();
            for (var el=inside; el!=null && chain.Count<12; el=TreeWalker.RawViewWalker.GetParent(el)) chain.Add(el.Current.ControlType.ProgrammaticName);
            Console.WriteLine("Range ancestry="+string.Join(" > ",chain)+"; focus="+focus.Current.ControlType.ProgrammaticName);
            object childPattern;
            if (!inside.TryGetCurrentPattern(TextPattern.Pattern,out childPattern)) throw new InvalidOperationException("Fixture text run has no text pattern.");
            var childRanges=((TextPattern)childPattern).GetSelection();
            if (childRanges.Length!=1 || childRanges[0].CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
                childRanges[0],System.Windows.Automation.Text.TextPatternRangeEndpoint.End)==0)
                throw new InvalidOperationException("Fixture text run did not expose a selection.");
            var method=typeof(SelectionReader).GetMethod("EditableRange",BindingFlags.NonPublic|BindingFlags.Static);
            bool writable=(bool)method.Invoke(null,new object[]{window,inside,focus,childRanges[0]});
            Pass(inside.Current.ControlType!=ControlType.Edit && writable,
                "editable selection remains writable when its text-range owner is a child of the editor");
            var unrelated=await FindElement(window,"Fixture password");
            Pass(!(bool)method.Invoke(null,new object[]{window,inside,unrelated,childRanges[0]}),
                "a text run cannot inherit editability from an unrelated focused control");

            var selection=new Selection { Window=window, Focus=Desktop.Focus(window), Process=Desktop.ProcessOf(window),
                Location=Desktop.FocusPoint(window,Desktop.Focus(window)), Native=false, Writable=true,
                Focused=focus, Owner=inside, Range=childRanges[0].Clone(), Text=childRanges[0].GetText(-1) };
            if (await SelectionReader.Recheck(selection,CancellationToken.None)==null)
                throw new InvalidOperationException("Child-owned selection did not survive revalidation.");
            var result=Wpf.Current.Windows.Cast<System.Windows.Window>().OfType<ResultWindow>().Single();
            var start=typeof(TranslatorApp).GetMethod("StartSelection",BindingFlags.NonPublic|BindingFlags.Instance);
            start.Invoke(app,new object[]{window,selection.Location,selection});
            await Wait(()=>server.Calls>0 || result.HasResult,"child-owned selection request");
            await Wait(()=>{browser.Refresh();return browser.MainWindowTitle.Contains("RICH EDITED") ||
                (!app.Busy && Tray(app).Text!="Translated · Done");},"child-owned selection text",8000);
            await Wait(()=>!app.Busy,"child-owned selection delivery",8000);
            browser.Refresh();
            if (!browser.MainWindowTitle.Contains("RICH EDITED") || !NoResultPopup() || Tray(app).Text!="Translated · Done")
                throw new InvalidOperationException("Child-owned selection: "+browser.MainWindowTitle+" / "+Tray(app).Text+" / "+result.Reason.Text);
            Pass(true,"child-owned editor selection replaces only the selected word");
            foreach (var label in new[]{"Multiple paragraphs","Editable group","Read-only group","Outside focused group"})
            {
                var b=await FindElement(window,label);
                ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                await Wait(()=>{browser.Refresh();return browser.MainWindowTitle.Contains("fixture "+label);},"prepare "+label);
                var s=await SelectionReader.Query(()=>SelectionReader.ReadShortcut(window,Forms.Cursor.Position),CancellationToken.None);
                bool expectReplace=label=="Multiple paragraphs" || label=="Editable group";
                Pass(s!=null && s.Writable==expectReplace, label+" selection classified correctly");
                if (label!="Editable group" && label!="Read-only group") continue;
                Pass(s.Focused.Current.ControlType==ControlType.Group && s.Text=="hello","fixture exposes a focused group and the selected word");
                int before=server.Calls;
                Key(window,0x12,0x4A);
                await Wait(()=>server.Calls==before+1 && !app.Busy,"group shortcut delivery");
                var badge=Wpf.Current.Windows.OfType<Motion>().Single();
                if (expectReplace)
                    await Wait(()=>{browser.Refresh();return browser.MainWindowTitle.Contains("RICH EDITED");},"group replaced in place");
                else
                    Pass(Forms.Clipboard.GetText()=="你好" && browser.MainWindowTitle.Contains(label),"read-only group copies the translation");
                Pass(badge.Title==(expectReplace?"Replaced":"Copied") && NoResultPopup(),"badge reports actual group delivery");
            }
        }

        static async Task ActivateFixture(Process browser)
        {
            IntPtr window=browser.MainWindowHandle; ShowWindow(window,9);
            await Wait(()=>{browser.Refresh();return browser.MainWindowTitle.Contains("Translated fixture");},"fixture document");
            SetForegroundWindow(window);
            if (Desktop.GetForegroundWindow()!=window)
            {
                if (!GetWindowRect(window,out var r)) throw new InvalidOperationException("Cannot locate fixture window.");
                var point=new Point((r.Left+r.Right)/2,(r.Top+r.Bottom)/2);
                SetWindowPos(window,new IntPtr(-1),0,0,0,0,0x13);
                try { if (Desktop.RootAt(point)==window) await Click(window,point); }
                finally { SetWindowPos(window,new IntPtr(-2),0,0,0,0,0x13); }
            }
            await Wait(()=>Desktop.GetForegroundWindow()==window,"activate fixture",45000);
        }

        // No UI Automation calls before the first hotkey: Chromium's accessibility tree
        // may still be coming online when the user translates the first selection.
        static async Task CheckCold(Process browser, TranslatorApp app, LocalProvider server, bool busy)
        {
            var window=browser.MainWindowHandle;
            await ActivateFixture(browser);
            var windows=Wpf.Current.Windows.Cast<System.Windows.Window>().ToList();
            var result=windows.OfType<ResultWindow>().Single();
            bool Titled(string title){browser.Refresh();return browser.MainWindowTitle.Contains(title);}
            async Task PressShortcut()
            {
                if (Desktop.GetForegroundWindow()!=window) throw new InvalidOperationException("Cold fixture lost focus.");
                keybd_event(0x12,(byte)MapVirtualKey(0x12,0),0,UIntPtr.Zero);
                try
                {
                    await Task.Delay(65);
                    keybd_event(0x4A,(byte)MapVirtualKey(0x4A,0),0,UIntPtr.Zero);
                    try { await Task.Delay(140); }
                    finally { keybd_event(0x4A,(byte)MapVirtualKey(0x4A,0),2,UIntPtr.Zero); }
                    await Task.Delay(35);
                }
                finally { keybd_event(0x12,(byte)MapVirtualKey(0x12,0),2,UIntPtr.Zero); }
            }
            for (int attempt=1;attempt<=2;attempt++)
            {
                if (result.IsVisible) result.Hide();
                if (attempt>1) await Click(window,Center(await Find(window,"Prepare F6")));
                await Wait(()=>Titled("fixture rich"),"prepare cold rich selection");
                await Task.Delay(120);
                Task<string> previous=null;
                if (busy && attempt==1)
                {
                    var started=new TaskCompletionSource<bool>();
                    previous=SelectionReader.Query(() => { started.TrySetResult(true); Thread.Sleep(1400); return "done"; },CancellationToken.None);
                    await started.Task;
                }
                int calls=server.Calls;
                await PressShortcut();
                await Wait(()=>server.Calls>calls || result.HasResult,"cold shortcut request",8000);
                if (server.Calls>calls)
                {
                    await Wait(()=>Titled("RICH EDITED") || (!app.Busy && Tray(app).Text!="Translated · Done"),"cold shortcut text",8000);
                    await Wait(()=>!app.Busy,"cold shortcut delivery",8000);
                }
                if (previous!=null) await previous;
                Console.WriteLine("COLD attempt="+attempt+" calls="+(server.Calls-calls)+" replaced="+Titled("RICH EDITED")+
                    " result="+result.IsVisible+" reason="+result.Reason.Text+" status="+Tray(app).Text);
                Pass(Titled("RICH EDITED") && NoResultPopup() && Tray(app).Text=="Translated · Done",
                    "cold Alt shortcut replaces the selected word on attempt "+attempt);
            }
            if (busy)
            {
                await Click(window,Center(await Find(window,"Prepare F6")));
                await Wait(()=>Titled("fixture rich"),"prepare selection for interrupted wait");
                await Task.Delay(120);
                using (var release=new ManualResetEventSlim())
                {
                    var started=new TaskCompletionSource<bool>();
                    var previous=SelectionReader.Query(() => { started.TrySetResult(true); release.Wait(3000); return "done"; },CancellationToken.None);
                    await started.Task;
                    int calls=server.Calls;
                    try
                    {
                        await PressShortcut();
                        Key(window,0x31);
                        await Wait(()=>Titled("fixture changed"),"user edit during accessibility wait");
                    }
                    finally { release.Set(); }
                    await previous;
                    await Wait(()=>result.HasResult && !app.Busy,"interrupted shortcut result");
                    Pass(server.Calls==calls && Titled("fixture changed") && NoResultPopup(),
                        "typing while waiting cancels selection capture without sending or replacing text");
                }
            }
        }

        static async Task Check(Process browser,TranslatorApp app,LocalProvider server,IntPtr owner)
        {
            IntPtr window=browser.MainWindowHandle;ShowWindow(window,9);
            var windows=Wpf.Current.Windows.Cast<System.Windows.Window>().ToList();
            var popup=windows.OfType<Popup>().Single();var result=windows.OfType<ResultWindow>().Single();
            // Warm up browser accessibility in the ordinary user profile mode.
            await Wait(()=>{browser.Refresh();return browser.MainWindowTitle.Contains("Translated fixture");},"fixture document");
            SetForegroundWindow(window);
            if(Desktop.GetForegroundWindow()!=window)
            {
                SetWindowPos(window,new IntPtr(-1),0,0,0,0,0x13);
                var bounds=AutomationElement.FromHandle(window).Current.BoundingRectangle;
                try {if (Desktop.RootAt(Center(bounds))==window) await Click(window,Center(bounds));}
                finally {SetWindowPos(window,new IntPtr(-2),0,0,0,0,0x13);}
            }
            await Wait(()=>Desktop.GetForegroundWindow()==window,"activate fixture",45000);
            await Find(window,"Fixture editor");
            var composer = await FindElement(window,"Fixture rich editor");
            int depth = 0;
            while (composer != null && composer.Current.NativeWindowHandle == 0)
            {
                depth++;
                composer = TreeWalker.RawViewWalker.GetParent(composer);
            }
            Pass(composer != null && depth > 24,"Codex-like composer is nested more than 24 UIA levels below its native window");
            async Task Select(byte key)
            {
                await Click(window,Center(await Find(window,"Prepare F"+(key-0x6F))));
                string expected=key==0x71?"fixture edit":key==0x72?"fixture passage":key==0x75?"fixture rich":"fixture secure";
                await Wait(()=>Titled(expected),"fixture field "+expected);
                await Task.Delay(200);
            }
            async Task Trigger()
            {
                int count=server.Calls;Key(window,0x11,0x12,0x10,0x78);
                try {await Wait(()=>server.Calls>count,"hotkey translation");}
                catch {throw new InvalidOperationException("Hotkey after request "+count+": "+result.IsVisible+" / "+result.Reason.Text+" / "+Tray(app).Text+" / foreground="+(Desktop.GetForegroundWindow()==window));}
            }
            async Task WhileRequestPending(Func<Task> interact)
            {
                var finish = server.HoldNextResponse();
                try { await Trigger(); await interact(); }
                finally { finish(); }
            }
            async Task ClickSelectionButton()
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(popup).Handle;
                var bounds = popup.PixelBounds;
                var point = new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2);
                SetCursorPos(point.X,point.Y);
                await Task.Delay(60);
                // A separately installed translator can offer its own button at the same point.
                // Bring the fixture's nonactivating overlay to the top before delivering test input.
                SetWindowPos(handle,new IntPtr(-1),0,0,0,0,0x13);
                if (Desktop.GetForegroundWindow()!=window || Desktop.RootAt(point)!=handle)
                    throw new InvalidOperationException("The test selection button is not the pointer target.");
                mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero);
            }
            bool Titled(string title){browser.Refresh();return browser.MainWindowTitle.Contains(title);}
            var statusBar = windows.OfType<Motion>().Single();
            bool OnlyStatusBar(string title) => NoResultPopup() && !popup.IsVisible && statusBar.IsVisible &&
                statusBar.Title == title && Desktop.GetForegroundWindow() == window;
            void ClearReview() => result.Store(null,null,null,"fixture pending");
            await Select(0x71);
            await Trigger();
            Pass(OnlyStatusBar("Translating"),"shortcut shows translation progress and keeps focus in the editor");
            await Wait(()=>Titled("fixture EDITED") || (!app.Busy && Tray(app).Text!="Translated · Done"),"textarea text");
            await Wait(()=>!app.Busy,"textarea delivery");
            if (!Titled("fixture EDITED") || !OnlyStatusBar("Replaced") || Tray(app).Text!="Translated · Done")
                throw new InvalidOperationException("Textarea delivery: title="+browser.MainWindowTitle+" / popup="+popup.IsVisible+
                    " / visible="+result.IsVisible+" / status="+Tray(app).Text+" / reason="+result.Reason.Text);
            Pass(true,"replacement shows completion and keeps the result panel closed");
            await Wait(()=>!statusBar.IsVisible,"status bar dismisses after completion");
            Pass(Forms.Clipboard.GetText()=="translated fixture prior","browser paste restored prior clipboard");

            await Select(0x72);var word=Center(await Find(window,"hello"));
            await Click(window,word,true); await Wait(()=>popup.IsVisible,"double-click selection button");
            Pass(server.Calls==1 && Desktop.GetForegroundWindow()==window,"selecting a word did not translate or take focus");
            await ClickSelectionButton();
            await Wait(()=>server.Calls==2,"read-only button began this translation");
            Pass(OnlyStatusBar("Translating"),"selection button shows progress without opening the result panel");
            await Wait(()=>!app.Busy,"read-only copy");
            if(Forms.Clipboard.GetText().Trim()!="你好" || !OnlyStatusBar("Copied") || !Titled("fixture passage"))
                throw new InvalidOperationException("Read-only button: "+Tray(app).Text+" / retained="+result.IsVisible+" / "+result.Reason.Text+" / "+browser.MainWindowTitle);
            Pass(true,"selection button shows the copied status for read-only text");

            await Click(window,Center(await Find(window,"Fixture button")),true);await Task.Delay(500);
            Pass(!popup.IsVisible && server.Calls==2,"double-clicking an action button offers no translation");
            var rect=await Find(window,"hello");
            var blank=new Point((int)rect.Right+70,(int)rect.Top+8);
            await Click(window,blank,true);await Task.Delay(450);
            Pass(!popup.IsVisible,"double-clicking blank page space offers no translation");
            var from=new Point((int)rect.Left+1,(int)(rect.Top+rect.Height/2));
            var to=new Point((int)rect.Right-1,from.Y);
            if(Desktop.RootAt(from)!=window || Desktop.RootAt(to)!=window)throw new InvalidOperationException("Unexpected drag target.");
            SetCursorPos(from.X,from.Y);mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(35);
            SetCursorPos(to.X,to.Y);await Task.Delay(40);mouse_event(4,0,0,0,UIntPtr.Zero);
            await Wait(()=>popup.IsVisible,"drag selection");
            Pass(server.Calls==2,"dragging over text offers translation without a request");
            await Select(0x71);Key(window,0x11,0x41);
            await Wait(()=>popup.IsVisible,"Ctrl+A selection");
            Pass(server.Calls==2,"keyboard select all offers translation");
            Key(window,0x25);await Task.Delay(120);Key(window,0x10,0x27);
            await Wait(()=>popup.IsVisible,"Shift selection");
            Pass(server.Calls==2,"Shift-arrow selection offers translation");

            await Select(0x75);
            await Click(window,Center(await Find(window,"left hello right")),true);
            await Wait(()=>popup.IsVisible,"nested composer selection button");
            Pass(server.Calls==2,"double-clicking a nested composer word offers translation without a request");
            await ClickSelectionButton();
            await Wait(()=>server.Calls==3,"nested composer button began this translation");
            await Wait(()=>Titled("RICH EDITED") || (!app.Busy && Tray(app).Text!="Translated · Done"),"rich editor text");
            await Wait(()=>!app.Busy,"rich editor delivery");
            if (!Titled("RICH EDITED") || !OnlyStatusBar("Replaced") || Tray(app).Text!="Translated · Done")
                throw new InvalidOperationException("Nested editor delivery: "+browser.MainWindowTitle+" / "+Tray(app).Text+" / "+result.Reason.Text);
            Pass(true,"floating action replaced only the word inside the nested composer");

            ClearReview();
            await Select(0x71);await Trigger();
            var workspace=windows.OfType<Workspace>().Single();workspace.Open(false);
            await Wait(()=>!app.Busy && result.Reason.Text!="fixture pending","retained switched-window result");
            Pass(result.Output.Text.Trim()=="你好" && Titled("fixture edit") && NoResultPopup() && ReviewMenu(app).Enabled,
                "switching windows silently retained translation without writing to old selection");

            workspace.Hide();SetForegroundWindow(window);await Wait(()=>Desktop.GetForegroundWindow()==window,"browser focus");
            ClearReview();
            await Select(0x72);await Trigger();
            uint generation=ClipboardAccess.Sequence;
            if(Desktop.ProcessOf(ClipboardAccess.Owner)!=Desktop.OwnProcess)throw new InvalidOperationException("User clipboard changed during test.");
            ClipboardAccess.Write(owner,"fixture newer copy",generation,false,out _);
            await Wait(()=>!app.Busy && result.Reason.Text!="fixture pending","clipboard contention result");
            string captured=result.Original.Text;
            bool held=Forms.Clipboard.GetText()=="fixture newer copy";
            bool matched=captured.Trim()=="hello" && result.Output.Text==captured.Replace("hello","你好");
            if (!held || !matched) throw new InvalidOperationException("Clipboard contention: retained="+held+", source length="+captured.Length+
                ", output length="+result.Output.Text.Length+", state="+result.Reason.Text);
            Pass(NoResultPopup() && ReviewMenu(app).Enabled,"intervening clipboard update kept newer content and a silent manual result");

            ClearReview();
            await Select(0x71);await Trigger();
            var editPoint=Center(await Find(window,"Fixture editor"));
            if(Desktop.RootAt(editPoint)!=window)throw new InvalidOperationException("Unexpected scroll target.");
            SetCursorPos(editPoint.X,editPoint.Y);mouse_event(0x800,0,0,120,UIntPtr.Zero);
            await Wait(()=>!app.Busy && result.Reason.Text!="fixture pending","scroll revokes delivery");
            Pass(Titled("fixture edit") && result.Output.Text.Trim()=="你好" && Desktop.GetForegroundWindow()==window && NoResultPopup(),
                "scroll revokes write permission even with unchanged text and focus");
            ClearReview();
            await Select(0x71);
            await WhileRequestPending(async () => {
                Key(window,0x31);
                await Wait(()=>Titled("fixture changed"),"browser consumed typing in fixture editor");
            });
            await Wait(()=>!app.Busy && result.Reason.Text!="fixture pending","typing revokes delivery");
            Pass(Titled("fixture changed") && result.Output.Text.Trim()=="你好" && OnlyStatusBar("Review result"),"typing keeps user's edit and shows review status");
            await Select(0x71);await Trigger();Key(window,0x1B);await Task.Delay(750);
            Pass(Titled("fixture edit") && NoResultPopup() && !statusBar.IsVisible && !app.Busy,"Escape dismisses the status bar and cancels delivery");
            await Select(0x71);
            await WhileRequestPending(async () => {
                await Select(0x72);await Trigger();
                await Wait(()=>!app.Busy && Tray(app).Text=="Translated · Copied","superseding job");
            });
            Pass(NoResultPopup() && Forms.Clipboard.GetText().Trim()=="你好","new translation supersedes an older request");

            ClearReview();
            int before=server.Calls;
            await Select(0x73);Key(window,0x11,0x12,0x10,0x78);
            await Wait(()=>result.Reason.Text!="fixture pending" && !app.Busy,"secure selection rejection");
            Pass(before==server.Calls && result.Original.Text=="" && !result.CopyButton.IsEnabled && NoResultPopup(),"password text never entered a request or result");
            ClearReview();
            await Select(0x72);server.Fail=true;before=server.Calls;await Trigger();
            await Wait(()=>!app.Busy && result.Reason.Text!="fixture pending","provider failure result");
            Pass(server.Calls==before+1 && result.Original.Text.Trim()=="hello" && result.Output.Text=="" &&
                Tray(app).Text=="Translated · Translation failed" && OnlyStatusBar("Translation failed. "+result.Reason.Text) && ReviewMenu(app).Enabled,
                "failure shows the status bar and retains the error for review from the tray");
            server.Fail=false;
            ReviewMenu(app).PerformClick();
            Pass(result.IsVisible,"tray menu explicitly opens the retained result");
            result.SourcePicker.SelectedItem="French";before=server.Calls;
            result.Retry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Wait(()=>server.Calls==before+1 && !app.Busy && result.CopyButton.IsEnabled,"manual retry");
            Pass(server.LastInstruction.Contains("Source language: French. Translate into English."),
                "result source override makes one explicit request and enables manual copy");
            result.Hide();workspace.Open(true);
            workspace.ProviderPicker.SelectedIndex=1;
            Pass(workspace.ProviderFooter.Text=="Custom / Local LLM","unsaved provider choice kept active service");
            workspace.ProviderPicker.SelectedIndex=3;workspace.Model.Text="draft-fixture";before=server.Calls;
            workspace.TestButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Wait(()=>server.Calls==before+1 && workspace.SettingsStatus.Text.Contains("你好"),"real draft test request");
            Pass(server.LastModel=="draft-fixture","test translation used the draft endpoint and model");
            workspace.Open(false);workspace.Input.Text="hello";workspace.UpdateLayout();workspace.Input.Focus();
            await Wait(()=>workspace.IsActive && workspace.Input.IsKeyboardFocused,"manual editor keyboard focus");
            before=server.Calls;
            Key(new System.Windows.Interop.WindowInteropHelper(workspace).Handle,0x11,0x0D);
            await Wait(()=>server.Calls==before+1 && !app.Busy && workspace.ManualOutput.Text=="你好","manual Ctrl+Enter");
            Pass(server.LastModel=="desktop-fixture" && server.LastInstruction.Contains("Source language: English.") &&
                NoResultPopup() && workspace.OutputArea.IsVisible,"Ctrl+Enter shows the translation inline without a popup");
            before=server.Calls;
            workspace.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Wait(()=>server.Calls==before+1 && !app.Busy && workspace.ManualOutput.Text=="你好","manual button translation");
            Pass(NoResultPopup(),"manual translate button updates the existing window without a popup");
            workspace.CopyOutput.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Pass(Forms.Clipboard.GetText().Trim()=="你好" && NoResultPopup(),"inline copy does not open a popup");

            var releaseManual=server.HoldNextResponse();
            try
            {
                before=server.Calls;
                workspace.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Wait(()=>server.Calls==before+1,"manual request before edit");
                workspace.Input.Text="changed input";
            }
            finally { releaseManual(); }
            await Wait(()=>!app.Busy,"manual result after input edit");
            Pass(workspace.OutputArea.Visibility==System.Windows.Visibility.Collapsed && result.Output.Text=="你好" &&
                ReviewMenu(app).Enabled && NoResultPopup(),"editing manual input retains the old result in the tray without showing it under new text");
            workspace.Input.Text="hello"; server.Fail=true; before=server.Calls;
            workspace.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Wait(()=>server.Calls==before+1 && !app.Busy,"manual provider failure");
            Pass(workspace.EditorStatus.Text.Length>0 && workspace.OutputArea.Visibility==System.Windows.Visibility.Collapsed &&
                ReviewMenu(app).Enabled && NoResultPopup(),"manual errors remain visible inline without a popup");
            server.Fail=false;
        }
    }
}
#endif
