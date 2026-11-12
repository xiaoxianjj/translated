using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Translated
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var mutex = new Mutex(true, @"Local\Translated.Windows.4B57FB28", out bool first))
            {
                if (!first) { InputWindow.ShowExisting(); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Theme.Initialize();
                using (var app = new TranslatorApp()) Application.Run(app);
                GC.KeepAlive(mutex);
            }
        }
    }

    internal sealed class TranslatorApp : ApplicationContext
    {
        enum Kind { Selection, Manual, Retry, Test }
        sealed class PendingPaste
        {
            internal readonly uint Written;
            internal uint? Restored;
            internal PendingPaste(uint written) { Written = written; }
        }
        sealed class Job : IDisposable
        {
            internal readonly CancellationTokenSource Stop = new CancellationTokenSource();
            internal Kind Kind;
            internal Point StatusPoint;
            internal string Source, Language;
            internal CancellationToken Token => Stop.Token;
            public void Dispose() => Stop.Dispose();
        }

        readonly string configFile;
        readonly InputWindow listener = new InputWindow();
        readonly Popup popup = new Popup();
        readonly Motion motion = new Motion();
        readonly Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem reviewItem, cancelItem, floatingItem;
        readonly Workspace workspace;
        readonly ResultWindow result;
        readonly SemaphoreSlim jobGate = new SemaphoreSlim(1);
        readonly SemaphoreSlim copyProbe = new SemaphoreSlim(1);
        Job active;
        PendingPaste pendingPaste;
        Point down, lastClick;
        IntPtr downWindow, lastClickWindow;
        int clickTime, gestureVersion;
        public Preferences Prefs { get; private set; }
        public bool Closing { get; private set; }
        public bool HotkeyReady => listener.Registered;
        internal bool Busy => active != null;

        internal TranslatorApp(string settingsPath = null, bool open = true)
        {
            configFile = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Translated", "settings.json");
            string startup = "";
            try { Prefs = Preferences.Load(configFile); }
            catch { Prefs = new Preferences(); startup = "无法读取原设置，请重新配置。"; }
            try { listener.Apply(Prefs.Hotkey, () => {}); }
            catch (Exception e) { startup = e.Message; }
            if (!listener.Monitoring) startup = "系统输入监听不可用，译文会保留在结果窗口。";
            tray = new NotifyIcon { Icon = icon, Text = "Translated · 划词翻译", ContextMenuStrip = menu, Visible = true };
            workspace = new Workspace(this); result = new ResultWindow(this);
            menu.Items.Add("Translate", null, (s,e) => workspace.Open(false));
            reviewItem = (ToolStripMenuItem)menu.Items.Add("Translation result", null, (s,e) => result.Present(true));
            reviewItem.Enabled = false;
            menu.Items.Add("Settings", null, (s,e) => workspace.Open(true));
            floatingItem = (ToolStripMenuItem)menu.Items.Add("Show button after selection", null, (s,e) => {
                try { SaveOptions(Prefs.Traditional, Prefs.Replace, !Prefs.Floating, Prefs.WeChatCopy); workspace.RefreshOptions(); }
                catch (Exception ex) { workspace.Open(true); workspace.SettingsText(ex.Message); }
            });
            floatingItem.Checked = Prefs.Floating;
            cancelItem = (ToolStripMenuItem)menu.Items.Add("Cancel translation", null, (s,e) => Cancel());
            cancelItem.Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit / 退出", null, (s,e) => Quit());
            tray.DoubleClick += (s,e) => workspace.Open(false);
            listener.ShortcutPressed += () => {
                var window = Desktop.GetForegroundWindow();
                if (Desktop.External(window)) StartSelection(window, Cursor.Position, null);
            };
            listener.EscapePressed += Cancel;
            listener.Interacted += point => {
                if (popup.IsVisible && !(point.HasValue && popup.PixelBounds.Contains(point.Value))) popup.Dismiss();
            };
            listener.LeftButton += Mouse;
            listener.KeyboardSelection += () => {
                var window = Desktop.GetForegroundWindow();
                if (Desktop.External(window) && Prefs.Floating) ShowSelectionButton(window, null);
            };
            listener.Reopen += () => workspace.Open(false);
            if (open) workspace.Open(startup.Length != 0);
            if (startup.Length != 0) workspace.SettingsText(startup);
        }

        public void SaveProvider(Provider profile, string newKey)
        {
            string old = Credentials.Load(profile);
            profile.Validate(newKey.Length == 0 ? old : newKey);
            var prefs = Prefs.Copy();
            prefs.Active = profile.Name;
            prefs.Profiles = Provider.Choices.Select(n => n == profile.Name ? profile : prefs.Profile(n)).ToArray();
            prefs.Configured = true;
            if (newKey.Length != 0) Credentials.Save(profile, newKey);
            try { prefs.Save(configFile); }
            catch
            {
                if (newKey.Length != 0) { if (old.Length == 0) Credentials.Remove(profile); else Credentials.Save(profile, old); }
                throw;
            }
            Prefs = prefs;
        }

        public void SaveShortcut(Shortcut combo)
        {
            var prefs = Prefs.Copy(); prefs.Hotkey = combo;
            listener.Apply(combo, () => prefs.Save(configFile));
            Prefs = prefs;
        }
        public void SaveOptions(bool traditional, bool replace, bool floating, bool weChatCopy)
        {
            var prefs = Prefs.Copy();
            prefs.Traditional = traditional; prefs.Replace = replace; prefs.Floating = floating; prefs.WeChatCopy = weChatCopy;
            prefs.Save(configFile); Prefs = prefs;
            floatingItem.Checked = floating;
            if (!floating) { gestureVersion++; popup.Dismiss(); }
        }

        void Mouse(Point point, bool pressed)
        {
            if (Closing) return;
            if (pressed)
            {
                downWindow = popup.IsVisible && popup.PixelBounds.Contains(point) ? IntPtr.Zero : Desktop.RootAt(point);
                down = point; return;
            }
            IntPtr window = downWindow; downWindow = IntPtr.Zero;
            if (!Desktop.External(window) || !Prefs.Floating || Desktop.RootAt(point) != window) return;
            var dragSize = SystemInformation.DragSize;
            bool drag = Math.Abs(point.X-down.X) >= Math.Max(2,dragSize.Width/2) || Math.Abs(point.Y-down.Y) >= Math.Max(2,dragSize.Height/2);
            var doubleSize = SystemInformation.DoubleClickSize;
            int time = Environment.TickCount;
            bool twice = lastClickWindow == window && unchecked((uint)(time-clickTime)) <= SystemInformation.DoubleClickTime &&
                Math.Abs(point.X-lastClick.X) <= doubleSize.Width/2 && Math.Abs(point.Y-lastClick.Y) <= doubleSize.Height/2;
            lastClick = point; lastClickWindow = twice || drag ? IntPtr.Zero : window; clickTime = time;
            if (drag || twice || Desktop.Down(0x10)) ShowSelectionButton(window,new Gesture { Start = down, End = point });
        }

        async void ShowSelectionButton(IntPtr window, Gesture gesture)
        {
            int version = ++gestureVersion, revision = listener.Revision;
            var focus = Desktop.Focus(window);
            bool Current() => !Closing && Prefs.Floating && version == Volatile.Read(ref gestureVersion) &&
                listener.Revision == revision && Desktop.GetForegroundWindow() == window && Desktop.Focus(window) == focus;
            try
            {
                foreach (int delay in new[] { 140, 230 })
                {
                    await Task.Delay(delay);
                    if (!Current()) return;
                    bool compatibility = Prefs.WeChatCopy && SelectionReader.WeChat(window);
                    var target = await SelectionReader.Query(() => SelectionReader.Read(window,gesture,compatibility,Current),CancellationToken.None);
                    if (!Current()) return;
                    if (target == null) continue;
                    if (target.Text == null)
                    {
                        if (active != null || (!compatibility && (target.Range == null || SelectionReader.WeChat(window)))) return;
                        var read = await ReadWithCopy(target,ClipboardAccess.Sequence,Current,CancellationToken.None);
                        if (read == null || !Current() || !Passage.HasLetter(read.Text)) return;
                        target.Text = read.Text;
                    }
                    popup.Button(target.Location, window, () => StartSelection(window,target.Location,target));
                    return;
                }
            }
            catch (Exception) { /* Unavailable text providers cannot establish a selection. */ }
        }

        async Task<ClipboardAccess.CopyResult> ReadWithCopy(Selection selection, uint sequence, Func<bool> current, CancellationToken token)
        {
            await copyProbe.WaitAsync(token);
            try
            {
                bool Safe() => !token.IsCancellationRequested && current() && selection.Foreground &&
                    (!selection.Native || !Desktop.Password(selection.Focus));
                if (!Safe()) return null;
                if (await SelectionReader.Recheck(selection,token,Safe) == null || !Safe()) return null;
                return await ClipboardAccess.ReadSelection(listener.Handle,selection,sequence,Safe);
            }
            finally { copyProbe.Release(); }
        }

        void StartSelection(IntPtr window, Point pointer, Selection candidate)
        {
            int revision = listener.Revision;
            uint atTrigger = ClipboardAccess.Sequence;
            var previousPaste = pendingPaste?.Written == atTrigger ? pendingPaste : null;
            var focus = Desktop.Focus(window);
            bool Stable() => listener.Revision == revision && Desktop.GetForegroundWindow() == window && Desktop.Focus(window) == focus;
            Start(Kind.Selection,candidate?.Text,null,async job => {
                // The gate has finished the previous job's cleanup. Follow only the exact
                // restore of the temporary clipboard generation observed at this trigger.
                uint? clipboard = previousPaste == null ? atTrigger : previousPaste.Restored;
                var token = job.Token;
                job.StatusPoint = candidate?.Location ?? Desktop.FocusPoint(window,focus);
                bool Current() => !token.IsCancellationRequested && Stable();
                if (candidate == null)
                {
                    for (int i = 0; i < 65 && (Desktop.ModifiersDown || Desktop.Down(Prefs.Hotkey.Key)); i++) await Task.Delay(25,token);
                    if (Desktop.ModifiersDown || Desktop.Down(Prefs.Hotkey.Key)) throw new InvalidOperationException("请松开快捷键后重试。");
                }
                // A floating-button query may still be initializing a slow provider. Give
                // an explicit shortcut time to wait for it without running UIA in parallel.
                var selection = candidate == null
                    ? await SelectionReader.Query(() => SelectionReader.ReadShortcut(window,pointer,Current),token,2500)
                    : await SelectionReader.Recheck(candidate,token,Current);
                token.ThrowIfCancellationRequested();
                if (selection == null || !Current()) throw new InvalidOperationException("选区已失效或没有可读取的文字。");
                if (selection.Text == null)
                {
                    var read = clipboard.HasValue ? await ReadWithCopy(selection,clipboard.Value,Current,token) : null;
                    token.ThrowIfCancellationRequested();
                    if (read == null || !Current() || (candidate?.Text != null && read.Text != candidate.Text))
                        throw new InvalidOperationException("无法确认所选文字，请重新选择。");
                    selection.Text = read.Text; clipboard = read.After;
                }
                job.Source = selection.Text;
                job.StatusPoint = selection.Location;
                motion.Display(MotionState.Translating, job.StatusPoint);
                string translated = await Translate(job);
                token.ThrowIfCancellationRequested();
                try
                {
                    bool Unchanged() => Current() && ClipboardAccess.Sequence == clipboard;
                    string ReviewReason(SelectionIssue issue = SelectionIssue.Interrupted)
                    {
                        if (!listener.Monitoring) return "系统输入监听不可用，译文已保留。";
                        if (Desktop.GetForegroundWindow() != window || Desktop.Focus(window) != focus) return "焦点已离开原输入框，译文已保留。";
                        if (listener.Revision != revision) return "翻译期间检测到新的输入或窗口切换，译文已保留。";
                        if (!clipboard.HasValue) return "未能恢复上一轮粘贴前的剪贴板，译文已保留。";
                        if (ClipboardAccess.Sequence != clipboard.Value) return "翻译期间剪贴板内容已变化，译文已保留。";
                        return CheckMessage(issue);
                    }
                    if (!Unchanged() || !listener.Monitoring) { Review(job,translated,ReviewReason()); return; }
                    var check = await SelectionReader.CheckForDelivery(selection,token,Unchanged);
                    token.ThrowIfCancellationRequested();
                    var now = check.Value;
                    if (now == null || !Unchanged())
                    {
                        if (now == null) NoteSelectionFailure("before-delivery",check);
                        Review(job,translated,ReviewReason(check.Issue)); return;
                    }
                    if (now.Text == null)
                    {
                        var read = await ReadWithCopy(now,clipboard.Value,Current,token);
                        token.ThrowIfCancellationRequested();
                        if (read == null || selection.Text != read.Text || !Current())
                        { Review(job,translated,ReviewReason(read == null ? SelectionIssue.NoRange : SelectionIssue.TextChanged)); return; }
                        clipboard = read.After;
                    }
                    var delivery = SelectionPolicy.Delivery(now.Writable,Prefs.Replace,listener.Monitoring,Current(),ClipboardAccess.Sequence == clipboard);
                    if (delivery == SelectionDelivery.Copy)
                    {
                        ClipboardAccess.Write(listener.Handle,translated,clipboard.Value,false,out _);
                        tray.Text = "Translated · Copied";
                        motion.Display(MotionState.Copied, job.StatusPoint);
                        return;
                    }
                    if (delivery == SelectionDelivery.Review || Desktop.ModifiersDown) { Review(job,translated,"译文已就绪，请手动复制。"); return; }
                    string failure = null;
                    if (now.Native)
                    {
                        bool sent = !Desktop.Password(now.Focus) && Desktop.NativeWritable(now.Focus) &&
                            Desktop.Range(now.Focus,out int start,out int end) && start == now.Start && end == now.End &&
                            Unchanged() && Desktop.NativeReplace(now.Focus,translated);
                        bool inserted = sent && Current() && (Desktop.VerifyClassicInsertion(now.Focus,now.Start,translated) ||
                            (now.Range != null && await SelectionReader.ConfirmInsertion(now,translated,Current,token) != null));
                        if (!inserted) failure = "未能确认原位替换，请核对后手动复制。";
                    }
                    else failure = await PasteChecked(now,translated,clipboard.Value,Current,token);
                    token.ThrowIfCancellationRequested();
                    if (failure == null)
                    {
                        tray.Text = "Translated · Done";
                        motion.Display(MotionState.Replaced, job.StatusPoint);
                    }
                    else Review(job,translated,failure);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { token.ThrowIfCancellationRequested(); Review(job,translated,error.Message); }
            });
        }

        static string CheckMessage(SelectionIssue issue)
        {
            switch (issue)
            {
                case SelectionIssue.Busy: return "上一项选区读取仍在进行，译文已保留。";
                case SelectionIssue.TimedOut: return "编辑器读取选区超时，译文已保留。";
                case SelectionIssue.ProviderUnavailable: return "编辑器的文字接口暂不可用，译文已保留。";
                case SelectionIssue.FocusChanged: return "键盘焦点已离开原编辑控件，译文已保留。";
                case SelectionIssue.UnsafeSource: return "无法确认文字来自原输入框，译文已保留。";
                case SelectionIssue.NoRange: return "编辑器没有返回选中的文字，译文已保留。";
                case SelectionIssue.MultipleRanges: return "编辑器返回了多处选区，译文已保留。";
                case SelectionIssue.Collapsed: return "编辑器只返回了光标位置，译文已保留。";
                case SelectionIssue.TextChanged: return "选中的文字已变化，译文已保留。";
                case SelectionIssue.EditorChanged: return "输入框内的草稿已变化，译文已保留。";
                case SelectionIssue.PositionChanged: return "选区起止位置未能与原位置匹配，译文已保留。";
                case SelectionIssue.RangeUnmatched: return "编辑器返回的文字范围与原选区未能匹配，译文已保留。";
                case SelectionIssue.EditabilityChanged: return "选区的可编辑状态已变化，译文已保留。";
                default: return "核对选区时输入状态已变化，译文已保留。";
            }
        }

        void NoteSelectionFailure(string stage, SelectionCheck check)
        {
            // Only the last failed check. Never include text, draft hashes, window titles or exception messages.
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(configFile),"last-selection-check.txt");
                string version = typeof(TranslatorApp).Assembly.GetName().Version.ToString(3);
                File.WriteAllLines(path,new[] { "utc="+DateTime.UtcNow.ToString("O"), "version="+version, "stage="+stage,
                    "issue="+check.Issue, "retryAfter="+check.FirstIssue, "elapsedMs="+check.ElapsedMilliseconds });
            }
            catch (IOException) {} catch (UnauthorizedAccessException) {}
        }

        async Task<string> PasteChecked(Selection selection, string text, uint expected, Func<bool> current, CancellationToken token)
        {
            if (selection.Range == null || !selection.Writable || !current()) return "粘贴前原选区已失效，译文已保留。";
            uint own = ClipboardAccess.Write(listener.Handle,text,expected,true,out var saved);
            var paste = pendingPaste = new PendingPaste(own);
            using (saved)
            {
                try
                {
                    bool Safe() => current() && ClipboardAccess.Sequence == own;
                    var check = await SelectionReader.CheckForDelivery(selection,token,Safe);
                    token.ThrowIfCancellationRequested();
                    var last = check.Value;
                    if (last == null) { NoteSelectionFailure("before-paste",check); return CheckMessage(check.Issue); }
                    if (!last.Writable || !Safe() || !Prefs.Replace || !Desktop.Chord('V')) return "未能向原输入框发送粘贴，译文已保留。";
                    // SendInput reports queued input. Wait for the destination to expose the inserted span.
                    foreach (int wait in new[] { 70, 130, 250, 350 })
                    {
                        await Task.Delay(wait,token);
                        if (!Safe()) break;
                        if (await SelectionReader.ConfirmInsertion(last,text,Safe,token) != null) return null;
                    }
                    return "已发送粘贴，但未能确认写入，请核对输入框。";
                }
                finally
                {
                    try { paste.Restored = await ClipboardAccess.Restore(listener.Handle,saved,own); }
                    finally { pendingPaste = null; }
                }
            }
        }

        async Task<string> Translate(Job job)
        {
            Passage.Validate(job.Source);
            if (!Prefs.Configured) throw new InvalidOperationException("请在 Settings 选择翻译服务并保存设置。");
            var profile = Prefs.Profile(Prefs.Active);
            return await Translator.Translate(job.Source,profile,Credentials.Load(profile),Prefs.Traditional,job.Token,
                sourceLanguage:job.Language);
        }

        internal void Manual(string text, string language = null, bool retry = false)
        {
            Start(retry ? Kind.Retry : Kind.Manual,text,language,async job => {
                var translated = await Translate(job);
                job.Token.ThrowIfCancellationRequested();
                Review(job,translated,"");
            });
        }
        internal void TestProvider(Provider profile, string draftKey)
        {
            Start(Kind.Test,"Hello","English",async job => {
                string key = draftKey.Length == 0 ? Credentials.Load(profile) : draftKey;
                profile.Validate(key);
                workspace.SettingsText("Testing…", false);
                string text = await Translator.Translate(job.Source,profile,key,Prefs.Traditional,job.Token,sourceLanguage:"English");
                job.Token.ThrowIfCancellationRequested();
                workspace.SettingsText("Hello → " + text,false); tray.Text = "Translated · Ready";
            });
        }
        internal void CopyManually(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            ClipboardAccess.Write(listener.Handle,text,ClipboardAccess.Sequence,false,out _);
        }

        void Review(Job job, string translated, string reason)
        {
            job.Token.ThrowIfCancellationRequested();
            reviewItem.Enabled = true;
            result.Store(job.Source,translated,job.Language,reason);
            if (job.Kind == Kind.Selection)
                motion.Display(translated == null ? MotionState.Failed : MotionState.Review, job.StatusPoint, reason);
            bool inline = job.Kind != Kind.Manual || workspace.ShowTranslation(job.Source,translated,reason);
            tray.Text = translated == null ? "Translated · Translation failed" :
                job.Kind == Kind.Selection || !inline ? "Translated · Review result" : "Translated · Ready";
        }
        async void Start(Kind kind, string source, string language, Func<Job,Task> work)
        {
            if (Closing) return;
            var previous = active;
            var job = new Job { Kind = kind, Source = source, Language = language };
            active = job; gestureVersion++; popup.Dismiss();
            motion.Dismiss();
            previous?.Stop.Cancel();
            tray.Text = "Translated · Translating";
            if (kind == Kind.Manual) workspace.ShowTranslation(null,null,"");
            listener.WatchEscape(true);
            cancelItem.Enabled = true; workspace.SetBusy(true,kind == Kind.Test); workspace.StatusText("");
            bool entered = false;
            try
            {
                await jobGate.WaitAsync(job.Token); entered = true;
                job.Token.ThrowIfCancellationRequested();
                await work(job);
            }
            catch (Exception error)
            {
                if (!job.Stop.IsCancellationRequested)
                {
                    string message = error is OperationCanceledException ? "翻译服务响应超时。" :
                        error is HttpRequestException ? "无法连接翻译服务，请检查网络和接口地址。" : error.Message;
                    if (kind == Kind.Test) { workspace.SettingsText(message); tray.Text = "Translated · Translation failed"; }
                    else if (kind == Kind.Manual && (string.IsNullOrWhiteSpace(source) || !Passage.HasLetter(source) || Passage.Count(source) > Passage.Maximum))
                    { workspace.StatusText(message); tray.Text = "Translated · Translation failed"; }
                    else Review(job,null,message);
                }
            }
            finally
            {
                if (entered) jobGate.Release();
                if (active == job) { active = null; listener.WatchEscape(false); cancelItem.Enabled = false; workspace.SetBusy(false); }
                job.Dispose();
            }
        }

        public void Cancel()
        {
            if (Closing) return;
            var job = active;
            if (job != null)
            {
                if (job.Kind == Kind.Test) workspace.SettingsText("Cancelled.",false);
                else workspace.StatusText("已取消");
            }
            gestureVersion++; popup.Dismiss();
            motion.Dismiss();
            if (job != null) tray.Text = "Translated · Cancelled";
            job?.Stop.Cancel();
        }
        async void Quit()
        {
            if (Closing) return;
            Closing = true; active?.Stop.Cancel();
            await jobGate.WaitAsync(); jobGate.Release();
            await copyProbe.WaitAsync(); copyProbe.Release();
            tray.Visible = false; ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Closing = true; tray.Visible = false;
                listener.Dispose(); popup.Dispose(); motion.Dispose(); result.Dispose(); workspace.Dispose(); tray.Dispose(); menu.Dispose(); icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
