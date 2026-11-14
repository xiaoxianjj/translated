using System;
using System.Drawing;
using System.Collections.Generic;
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
        enum Kind { Selection, Card, Manual, Retry, Test }
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
            internal string Target;
            internal Guid CardId;
            internal IntPtr OriginWindow;
            internal TranslationSettings Settings;
            internal CancellationToken Token => Stop.Token;
            public void Dispose() => Stop.Dispose();
        }

        readonly string configFile;
        readonly bool diagnosticEnabled;
        readonly Queue<string> shortcutTrace = new Queue<string>();
        readonly InputWindow listener = new InputWindow();
        readonly Popup popup = new Popup();
        readonly Motion motion = new Motion();
        readonly Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem reviewItem, cancelItem, floatingItem;
        readonly Workspace workspace;
        readonly ResultWindow result;
        readonly TranslationCard card;
        readonly SemaphoreSlim jobGate = new SemaphoreSlim(1);
        readonly SemaphoreSlim copyProbe = new SemaphoreSlim(1);
        readonly CancellationTokenSource selectionStop = new CancellationTokenSource();
        readonly HashSet<Task> selectionTasks = new HashSet<Task>();
        Job active;
        PendingPaste pendingPaste;
        Point down, lastClick;
        IntPtr downWindow, lastClickWindow;
        int clickTime, gestureVersion;
        bool disposed;
        public Preferences Prefs { get; private set; }
        public bool Closing { get; private set; }
        public bool HotkeyReady => listener.Registered;
        internal void BeginShortcutCapture(IntPtr window, Action<int, bool> receive) => listener.BeginCapture(window, receive);
        internal void EndShortcutCapture() => listener.EndCapture();
        internal bool Busy => active != null;

        internal TranslatorApp(string settingsPath = null, bool open = true, bool showTray = true)
        {
            diagnosticEnabled = settingsPath == null;
            configFile = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Translated", "settings.json");
            string startup = "";
            try { Prefs = Preferences.Load(configFile); }
            catch { Prefs = new Preferences(); startup = "无法读取原设置，请重新配置。"; }
            try { listener.Apply(Prefs.Hotkey, () => {}); }
            catch (Exception e) { startup = e.Message; }
            if (!listener.Monitoring) startup = "系统输入监听不可用，译文会保留在结果窗口。";
            tray = new NotifyIcon { Icon = icon, Text = "Translated · 划词翻译", ContextMenuStrip = menu, Visible = showTray };
            workspace = new Workspace(this); result = new ResultWindow(this); card = new TranslationCard(this);
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
                NoteShortcutState("received");
                if (Desktop.External(window)) StartSelection(window, Cursor.Position, null);
            };
            listener.EscapePressed += () => {
                // Let the focused card handle Esc locally (including its language menu).
                if (Desktop.GetForegroundWindow() != new System.Windows.Interop.WindowInteropHelper(card).Handle) Cancel("escape");
            };
            listener.Interacted += point => {
                if (popup.IsVisible && !(point.HasValue && popup.PixelBounds.Contains(point.Value))) popup.Dismiss();
            };
            listener.PointerInteracted += point => {
                if (card.IsVisible && !card.ContainsPoint(point)) DismissCard("outside-pointer");
            };
            listener.ForegroundChanged += HandleCardForeground;
            listener.KeyboardInteracted += () => {
                if (card.IsVisible && !card.OwnsWindow(Desktop.GetForegroundWindow())) DismissCard("outside-keyboard");
            };
            listener.LeftButton += Mouse;
            listener.KeyboardSelection += () => {
                var window = Desktop.GetForegroundWindow();
                if (Desktop.External(window) && Prefs.Floating) ShowSelectionButton(window, null);
            };
            listener.Reopen += () => workspace.Open(false);
            NoteShortcutState(listener.Registered ? "ready" : "shortcut-unavailable");
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

        void HandleCardForeground(IntPtr window)
        {
            // Opening a window can generate transient activation notifications. Only
            // dismiss a card already shown, after confirming a sustained app switch.
            if (!card.IsVisible || window == IntPtr.Zero || window == card.SourceWindow || card.OwnsWindow(window)) return;
            if (!Closing) card.CheckForegroundState();
        }

        public void SaveShortcut(Shortcut combo)
        {
            var prefs = Prefs.Copy(); prefs.Hotkey = combo;
            listener.Apply(combo, () => prefs.Save(configFile));
            Prefs = prefs;
        }
        public void SaveOptions(bool traditional, bool replace, bool floating, bool weChatCopy, bool? preview = null)
        {
            var prefs = Prefs.Copy();
            prefs.Traditional = traditional; prefs.Replace = replace; prefs.Floating = floating; prefs.WeChatCopy = weChatCopy;
            if (preview.HasValue) prefs.SelectionMode = preview.Value ? "card" : "automatic";
            prefs.Save(configFile); Prefs = prefs;
            floatingItem.Checked = floating;
            if (!floating) { gestureVersion++; popup.Dismiss(); }
        }

        internal void SaveTarget(string target)
        {
            var prefs = Prefs.Copy(); prefs.TargetLanguage = TranslationLanguages.Find(target).Code;
            prefs.Save(configFile); Prefs = prefs;
            workspace.RefreshTarget();
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

        void ShowSelectionButton(IntPtr window, Gesture gesture)
        {
            if (Closing) return;
            selectionTasks.RemoveWhere(task=>task.IsCompleted);
            selectionTasks.Add(ReadSelectionButton(window,gesture,selectionStop.Token));
        }

        async Task ReadSelectionButton(IntPtr window, Gesture gesture, CancellationToken token)
        {
            int version = ++gestureVersion, revision = listener.Revision;
            var focus = Desktop.Focus(window);
            bool Current() => !Closing && Prefs.Floating && version == Volatile.Read(ref gestureVersion) &&
                listener.Revision == revision && Desktop.GetForegroundWindow() == window && Desktop.Focus(window) == focus;
            try
            {
                foreach (int delay in new[] { 140, 230 })
                {
                    await Task.Delay(delay,token);
                    if (!Current()) return;
                    bool compatibility = Prefs.WeChatCopy && SelectionReader.WeChat(window);
                    var target = await SelectionReader.Query(() => SelectionReader.Read(window,gesture,compatibility,Current),token);
                    if (!Current()) return;
                    if (target == null) continue;
                    if (target.Text == null)
                    {
                        if (active != null || (!compatibility && (target.Range == null || SelectionReader.WeChat(window)))) return;
                        var read = await ReadWithCopy(target,ClipboardAccess.Sequence,Current,token);
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
            bool preview = Prefs.SelectionMode != "automatic";
            Start(preview ? Kind.Card : Kind.Selection,candidate?.Text,null,async job => {
                // The gate has finished the previous job's cleanup. Follow only the exact
                // restore of the temporary clipboard generation observed at this trigger.
                uint? clipboard = previousPaste == null ? atTrigger : previousPaste.Restored;
                var token = job.Token;
                job.StatusPoint = candidate?.Location ?? Desktop.FocusPoint(window,focus);
                bool Current() => !token.IsCancellationRequested && Stable();
                if (candidate == null)
                {
                    for (int i = 0; i < 65 && listener.ShortcutHeld; i++) await Task.Delay(25,token);
                    if (listener.ShortcutHeld) throw new InvalidOperationException("请松开快捷键后重试。");
                    if (preview && Desktop.GetForegroundWindow() == window)
                    {
                        // Keyboard layout/IME and browser focus can settle while modifiers
                        // are released. Capture the current read-only selection after that.
                        focus = Desktop.Focus(window); revision = listener.Revision;
                    }
                }
                if (preview) motion.Display(MotionState.Translating,job.StatusPoint);
                if (preview) NoteShortcutState("reading-selection");
                // A floating-button query may still be initializing a slow provider. Give
                // an explicit shortcut time to wait for it without running UIA in parallel.
                var selection = candidate == null
                    ? await SelectionReader.Query(() => SelectionReader.ReadShortcut(window,pointer,Current),token,2500)
                    : await SelectionReader.Recheck(candidate,token,Current);
                token.ThrowIfCancellationRequested();
                if (selection == null && preview && candidate == null && Current())
                    selection = await SelectionReader.Query(() => SelectionReader.ReadCopyCandidate(window,pointer,Current),token,1500);
                if (!Current())
                {
                    if (Desktop.GetForegroundWindow() != window) { NoteShortcutState("cancel:source-changed-before-read"); job.Stop.Cancel(); token.ThrowIfCancellationRequested(); }
                    throw new InvalidOperationException("读取选区时焦点或输入发生变化，请重新划选并松开快捷键后重试。");
                }
                if (selection == null)
                {
                    NoteSelectionFailure("capture",new SelectionCheck(SelectionIssue.NoRange));
                    throw new InvalidOperationException("无法读取当前选区。请重新划选文字后按快捷键；也可复制原文到主窗口翻译。");
                }
                if (selection.Text == null)
                {
                    var read = clipboard.HasValue ? await ReadWithCopy(selection,clipboard.Value,Current,token) : null;
                    token.ThrowIfCancellationRequested();
                    if (!Current())
                    {
                        if (Desktop.GetForegroundWindow() != window) { NoteShortcutState("cancel:source-changed-during-copy"); job.Stop.Cancel(); token.ThrowIfCancellationRequested(); }
                        throw new InvalidOperationException("复制取词时选区状态发生变化，请重新划选后重试。");
                    }
                    if (read == null || (candidate?.Text != null && read.Text != candidate.Text))
                    {
                        NoteSelectionFailure("capture-copy",new SelectionCheck(SelectionIssue.NoRange));
                        throw new InvalidOperationException("无法通过复制读取选中文字。请重新划选后重试，或复制到主窗口翻译。");
                    }
                    selection.Text = read.Text; clipboard = read.After;
                }
                job.Source = selection.Text;
                job.StatusPoint = selection.Location;
                if (preview)
                {
                    motion.Dismiss();
                    card.Begin(job.CardId,job.Source,job.Language,job.Target,job.StatusPoint,job.OriginWindow);
                    NoteShortcutState("card-shown:visible-at-position="+card.ContainsPoint(
                        new Point(card.PixelBounds.Left+30,card.PixelBounds.Top+30)));
                    string text = await Translate(job);
                    job.Token.ThrowIfCancellationRequested();
                    Review(job,text,"");
                    return;
                }
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
            },originWindow:window);
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

        void NoteShortcutState(string stage)
        {
            if (!diagnosticEnabled) return;
            // One overwritten diagnostic, never source text, typed keys or window titles.
            try {
                var folder = Path.GetDirectoryName(configFile);
                Directory.CreateDirectory(folder);
                File.WriteAllLines(Path.Combine(folder,"last-shortcut-state.txt"),new[] {
                    "utc="+DateTime.UtcNow.ToString("O"), "stage="+stage, "registered="+listener.Registered });
                shortcutTrace.Enqueue(DateTime.UtcNow.ToString("O")+" "+stage);
                while (shortcutTrace.Count > 48) shortcutTrace.Dequeue();
                File.WriteAllLines(Path.Combine(folder,"last-shortcut-trace.txt"),shortcutTrace);
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
            if (!job.Settings.Configured) throw new InvalidOperationException("请在 Settings 选择翻译服务并保存设置。");
            var profile = job.Settings.Provider;
            return await Translator.Translate(job.Source,profile,Credentials.Load(profile),job.Settings.Traditional,job.Token,
                direction:TranslationLanguages.Find(job.Target).Direction,sourceLanguage:job.Language);
        }

        internal void Manual(string text, string language = null, bool retry = false, string target = null)
        {
            Start(retry ? Kind.Retry : Kind.Manual,text,language,async job => {
                var translated = await Translate(job);
                job.Token.ThrowIfCancellationRequested();
                Review(job,translated,"");
            },target);
        }
        internal void RetranslateCard(string target)
        {
            if (!card.IsVisible || string.IsNullOrWhiteSpace(card.Source)) return;
            try { SaveTarget(target); }
            catch (Exception error) {
                if (active?.Kind == Kind.Card) active.Stop.Cancel();
                card.Complete(card.Session,null,error.Message); return;
            }
            var source = card.Source; var language = card.SourceLanguage;
            var session = card.Session; var point = card.Anchor; var origin = card.SourceWindow;
            Start(Kind.Card,source,language,async job => {
                job.StatusPoint = point;
                card.Begin(session,source,language,target,point,origin);
                var translated = await Translate(job);
                job.Token.ThrowIfCancellationRequested();
                Review(job,translated,"");
            },target,session,origin);
        }
        internal void TestProvider(Provider profile, string draftKey)
        {
            Start(Kind.Test,"Hello","English",async job => {
                string key = draftKey.Length == 0 ? Credentials.Load(profile) : draftKey;
                profile.Validate(key);
                workspace.SettingsText("Testing…", false);
                string text = await Translator.Translate(job.Source,profile,key,job.Settings.Traditional,job.Token,sourceLanguage:"English");
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
            result.Store(job.Source,translated,job.Language,reason,job.Target);
            if (job.Kind == Kind.Card)
            {
                motion.Dismiss();
                if (card.Session != job.CardId) card.Begin(job.CardId,job.Source,job.Language,job.Target,job.StatusPoint,job.OriginWindow);
                card.Complete(job.CardId,translated,reason);
                tray.Text = translated == null ? "Translated · Translation failed" : "Translated · Ready";
                return;
            }
            if (job.Kind == Kind.Selection)
                motion.Display(translated == null ? MotionState.Failed : MotionState.Review, job.StatusPoint, reason);
            bool inline = job.Kind != Kind.Manual || workspace.ShowTranslation(job.Source,translated,reason);
            tray.Text = translated == null ? "Translated · Translation failed" :
                job.Kind == Kind.Selection || !inline ? "Translated · Review result" : "Translated · Ready";
        }
        async void Start(Kind kind, string source, string language, Func<Job,Task> work, string target = null, Guid? cardId = null, IntPtr originWindow = default)
        {
            if (Closing) return;
            var previous = active;
            var job = new Job { Kind = kind, Source = source, Language = language,
                Settings = TranslationSettings.Capture(Prefs), Target = target ?? Prefs.TargetLanguage,
                CardId = cardId ?? Guid.NewGuid(), OriginWindow = originWindow, StatusPoint = Cursor.Position };
            if (!cardId.HasValue) card.Dismiss();
            active = job; gestureVersion++; popup.Dismiss();
            motion.Dismiss();
            previous?.Stop.Cancel();
            if (kind == Kind.Card) NoteShortcutState("starting");
            tray.Text = "Translated · Translating";
            if (kind == Kind.Manual) workspace.ShowTranslation(null,null,"");
            listener.WatchEscape(kind != Kind.Card);
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
                    if (kind == Kind.Card) NoteShortcutState("failed");
                    if (kind == Kind.Card && job.OriginWindow != IntPtr.Zero &&
                        Desktop.GetForegroundWindow() != job.OriginWindow && !card.OwnsWindow(Desktop.GetForegroundWindow()))
                    { NoteShortcutState("cancel:source-changed-before-error"); job.Stop.Cancel(); return; }
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
                if (active == job) {
                    active = null; listener.WatchEscape(false); cancelItem.Enabled = false; workspace.SetBusy(false);
                    if (kind == Kind.Card) motion.Dismiss();
                    if (kind == Kind.Card && job.Stop.IsCancellationRequested) NoteShortcutState("cancelled");
                }
                job.Dispose();
            }
        }

        internal void DismissCard(string reason = "dismiss")
        {
            NoteShortcutState("dismiss:"+reason);
            card.Dismiss();
            if (active?.Kind == Kind.Card) { motion.Dismiss(); active.Stop.Cancel(); }
        }

        public void Cancel(string reason = "manual")
        {
            if (Closing) return;
            NoteShortcutState("cancel:"+reason);
            var job = active;
            if (job != null)
            {
                if (job.Kind == Kind.Test) workspace.SettingsText("Cancelled.",false);
                else workspace.StatusText("已取消");
            }
            gestureVersion++; popup.Dismiss();
            card.Dismiss();
            motion.Dismiss();
            if (job != null) tray.Text = "Translated · Cancelled";
            job?.Stop.Cancel();
        }
        internal async Task Shutdown()
        {
            if (Closing) return;
            Closing = true; selectionStop.Cancel(); active?.Stop.Cancel();
            await Task.WhenAll(selectionTasks.ToArray());
            await jobGate.WaitAsync(); jobGate.Release();
            await copyProbe.WaitAsync(); copyProbe.Release();
            tray.Visible = false; ExitThread();
        }
        async void Quit() => await Shutdown();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                Closing = true; selectionStop.Cancel(); tray.Visible = false;
                listener.Dispose(); popup.Dispose(); motion.Dispose(); card.Dispose(); result.Dispose(); workspace.Dispose(); tray.Dispose(); menu.Dispose(); icon.Dispose();
                selectionStop.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
