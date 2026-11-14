using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Translated
{
    internal enum SelectionIssue
    {
        None, Busy, TimedOut, ProviderUnavailable, Interrupted, FocusChanged, UnsafeSource,
        NoRange, MultipleRanges, Collapsed, TextChanged, EditorChanged, PositionChanged, RangeUnmatched, EditabilityChanged
    }

    internal sealed class SelectionCheck
    {
        internal readonly Selection Value;
        internal readonly SelectionIssue Issue;
        internal bool CanRetry => Issue == SelectionIssue.Busy || Issue == SelectionIssue.TimedOut ||
            Issue == SelectionIssue.ProviderUnavailable || Issue == SelectionIssue.NoRange || Issue == SelectionIssue.Collapsed ||
            Issue == SelectionIssue.RangeUnmatched || Issue == SelectionIssue.EditabilityChanged;
        internal SelectionIssue FirstIssue { get; private set; }
        internal long ElapsedMilliseconds { get; private set; }
        internal SelectionCheck(Selection value) { Value = value; }
        internal SelectionCheck(SelectionIssue issue) { Issue = issue; }
        internal SelectionCheck Finished(SelectionIssue first, Stopwatch clock)
        {
            FirstIssue = first;
            ElapsedMilliseconds = clock.ElapsedMilliseconds;
            return this;
        }
    }

    internal sealed class Gesture
    {
        public Point Start, End;
    }

    internal sealed class Selection
    {
        public IntPtr Window, Focus;
        public uint Process;
        public Point Location;
        public bool Native, Writable;
        internal bool CopyOnly;
        public int Start, End;
        public AutomationElement Owner, Focused;
        public TextPatternRange Range;
        public string Text;
        internal int EditorOffset;
        internal byte[] EditorDigest;
        internal int[] EditorRuntimeId;
        public Gesture Anchor;
        public bool Foreground => Window == Desktop.GetForegroundWindow() && Focus == Desktop.Focus(Window);

        internal bool MatchesFocus(AutomationElement current)
        {
            if (current == null || Focused == null) return false;
            if (EditorRuntimeId == null) return Automation.Compare(Focused,current);
            var id = current.GetRuntimeId();
            return id != null && EditorRuntimeId.SequenceEqual(id);
        }

        internal SelectionIssue Difference(Selection other)
        {
            if (other == null) return SelectionIssue.NoRange;
            if (Window != other.Window || Process != other.Process || Focus != other.Focus || Native != other.Native ||
                !MatchesFocus(other.Focused)) return SelectionIssue.FocusChanged;
            if (Native && (Start != other.Start || End != other.End)) return SelectionIssue.PositionChanged;
            if (Text != null && other.Text != null && Text != other.Text) return SelectionIssue.TextChanged;
            if (Writable != other.Writable) return SelectionIssue.EditabilityChanged;
            if (Range != null || other.Range != null)
            {
                if (Range == null || other.Range == null) return SelectionIssue.ProviderUnavailable;
                if (EditorDigest != null && other.EditorDigest != null)
                {
                    // Both snapshots come from their focused editor. MatchesFocus verified
                    // its runtime ID without asking the saved provider to identify itself.
                    if (EditorOffset != other.EditorOffset) return SelectionIssue.PositionChanged;
                    // An update outside the selected span need not invalidate a live range.
                    // The full draft fingerprint is required when recovering rebuilt nodes.
                    if (!EditorDigest.SequenceEqual(other.EditorDigest) && !Range.Compare(other.Range)) return SelectionIssue.EditorChanged;
                }
                else
                {
                    if (!Automation.Compare(Owner,other.Owner)) return SelectionIssue.ProviderUnavailable;
                    if (!Range.Compare(other.Range)) return SelectionIssue.RangeUnmatched;
                }
            }
            return SelectionIssue.None;
        }
    }

    internal static class SelectionReader
    {
        // UIA can block inside a third-party provider. Keep it off the window thread and bound
        // outstanding calls, including when a timed-out provider is still returning.
        static readonly SemaphoreSlim lane = new SemaphoreSlim(1);
        internal static Task<T> Query<T>(Func<T> work, CancellationToken token, int laneWaitMilliseconds = 650) where T : class
            => Query(work,token,laneWaitMilliseconds,null,2200);

        static async Task<T> Query<T>(Func<T> work, CancellationToken token, int laneWaitMilliseconds,
            Func<SelectionIssue,T> unavailable, int workMilliseconds) where T : class
        {
            if (!await lane.WaitAsync(laneWaitMilliseconds, token)) return unavailable?.Invoke(SelectionIssue.Busy);
            var task = Task.Run(() => {
                try { return work(); }
                catch (Exception e) when (e is ElementNotAvailableException || e is InvalidOperationException || e is COMException || e is NotSupportedException ||
                    e is UnauthorizedAccessException || e is TimeoutException || e is ArgumentException)
                { return unavailable?.Invoke(e is TimeoutException ? SelectionIssue.TimedOut : SelectionIssue.ProviderUnavailable); }
                finally { lane.Release(); }
            });
            if (await Task.WhenAny(task, Task.Delay(workMilliseconds, token)) != task) { token.ThrowIfCancellationRequested(); return unavailable?.Invoke(SelectionIssue.TimedOut); }
            token.ThrowIfCancellationRequested();
            return await task;
        }

        static bool Equal(AutomationElement a, AutomationElement b) => a != null && b != null && Automation.Compare(a, b);
        static readonly TreeWalker Tree = TreeWalker.RawViewWalker;

        // Electron editors can nest the selection dozens of nodes below their first HWND.
        // Stop at the source window or the end of the tree. A cyclic provider fails closed.
        static IEnumerable<AutomationElement> Ancestry(AutomationElement element)
        {
            var visited = new HashSet<AutomationElement>();
            for (; element != null && visited.Add(element); element = Tree.GetParent(element))
                yield return element;
        }

        static bool SafeSource(AutomationElement element, IntPtr window, AutomationElement checkedAncestor = null)
        {
            foreach (var node in Ancestry(element))
            {
                if (checkedAncestor != null && Equal(node,checkedAncestor)) return true;
                if (node.Current.IsPassword) return false;
                var handle = new IntPtr(node.Current.NativeWindowHandle);
                if (handle == window) return true;
                if (handle != IntPtr.Zero && Desktop.GetAncestor(handle,2) != window) return false;
            }
            return false;
        }

        static bool Ancestor(AutomationElement parent, AutomationElement child)
        {
            foreach (var node in Ancestry(child))
            {
                if (Equal(parent,node)) return true;
                if (node.Current.ControlType == ControlType.Window) break;
            }
            return false;
        }

        static bool ActionControl(ControlType t) => t == ControlType.Button || t == ControlType.MenuItem ||
            t == ControlType.MenuBar || t == ControlType.ToolBar || t == ControlType.TabItem || t == ControlType.TitleBar ||
            t == ControlType.ScrollBar || t == ControlType.CheckBox || t == ControlType.Image ||
            t == ControlType.Menu || t == ControlType.RadioButton || t == ControlType.SplitButton;

        static bool EditAllows(AutomationElement element, object rangeReadOnly)
        {
            if (!element.Current.IsEnabled || element.Current.IsPassword || object.Equals(rangeReadOnly, true) ||
                object.Equals(rangeReadOnly, TextPattern.MixedAttributeValue)) return false;
            object value;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out value))
                return !((ValuePattern)value).Current.IsReadOnly;
            // The default IsReadOnly text attribute is false even on some web documents.
            return element.Current.ControlType == ControlType.Edit && object.Equals(rangeReadOnly, false);
        }

        static bool EditableRange(IntPtr window, AutomationElement owner, AutomationElement focus, TextPatternRange range)
        {
            var attribute = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            var inside = range.GetEnclosingElement();
            if (!Ancestor(owner, inside)) return false;
            foreach (var el in Ancestry(inside))
            {
                var role = el.Current.ControlType;
                if (role == ControlType.Edit)
                {
                    // The range owner can be a text run inside the editor. The selected
                    // range and keyboard focus must both belong to this same edit control.
                    return Ancestor(el, focus) && EditAllows(el, attribute);
                }
                if (role == ControlType.Group && Equal(el, focus) && Equals(attribute, false) &&
                    BrowserEditability.FocusedGroup(window, el)) return true;
                // Never borrow permission from an editor outside a document boundary.
                if (role == ControlType.Document || role == ControlType.Window) break;
            }
            return false;
        }

        static bool Near(TextPatternRange range, Gesture gesture)
        {
            if (gesture == null) return true;
            return range.GetBoundingRectangles().Any(rect => {
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return false;
                rect.Inflate(gesture.Start == gesture.End ? 1 : 5, 2);
                return rect.Contains(new System.Windows.Point(gesture.End.X, gesture.End.Y)) ||
                    rect.Contains(new System.Windows.Point(gesture.Start.X, gesture.Start.Y));
            });
        }

        // A Chromium editor can recreate its text nodes while keeping the same draft and
        // selection. Save the position in the focused editor independently of those nodes.
        static void RememberEditorPosition(Selection selection, TextPattern pattern)
        {
            if (selection.Native || !selection.Writable || selection.Text == null) return;
            try
            {
                if (!Equal(selection.Owner,selection.Focused) || selection.Owner.Current.FrameworkId != "Chrome") return;
                var role = selection.Owner.Current.ControlType;
                if (role != ControlType.Edit && role != ControlType.Group) return;
                var document = pattern.DocumentRange;
                if (!Ancestor(selection.Owner,document.GetEnclosingElement())) return;
                var whole = document.GetText(Passage.Maximum+1);
                if (whole == null || whole.Length > Passage.Maximum) return;
                var before = document.Clone();
                before.MoveEndpointByRange(TextPatternRangeEndpoint.End,selection.Range,TextPatternRangeEndpoint.Start);
                var prefix = before.GetText(Passage.Maximum+1);
                if (prefix == null || !whole.StartsWith(prefix,StringComparison.Ordinal) ||
                    selection.Text.Length > whole.Length-prefix.Length ||
                    string.CompareOrdinal(whole,prefix.Length,selection.Text,0,selection.Text.Length) != 0) return;
                var id = selection.Focused.GetRuntimeId();
                if (id == null || id.Length == 0) return;
                using (var hash = SHA256.Create()) selection.EditorDigest = hash.ComputeHash(Encoding.Unicode.GetBytes(whole));
                selection.EditorRuntimeId = id;
                selection.EditorOffset = prefix.Length;
            }
            catch (Exception e) when (e is NotSupportedException || e is InvalidOperationException || e is COMException ||
                e is ElementNotAvailableException || e is ArgumentException) { /* Use the provider's range comparison. */ }
        }

        static Selection FromRange(Selection selection, AutomationElement owner, TextPattern pattern, TextPatternRange range,
            Func<bool> current, out SelectionIssue issue, bool rechecking = false)
        {
            issue = SelectionIssue.UnsafeSource;
            if (!SafeSource(range.GetEnclosingElement(),selection.Window,rechecking ? owner : selection.Focused)) return null;
            issue = SelectionIssue.Interrupted;
            if (current != null && !current()) return null;
            string text;
            try { text = range.GetText(-1); } catch (NotSupportedException) { text = null; }
            if (!string.IsNullOrEmpty(text) && !Passage.HasLetter(text)) { issue = SelectionIssue.TextChanged; return null; }
            selection.Owner = owner; selection.Range = range.Clone();
            selection.Text = string.IsNullOrEmpty(text) ? null : text;
            if (!rechecking && selection.Anchor == null)
            {
                var rect = range.GetBoundingRectangles().LastOrDefault(b => !b.IsEmpty && b.Width > 0 && b.Height > 0);
                if (rect.Width > 0) selection.Location = new Point((int)(rect.Right-1),(int)(rect.Bottom-1));
            }
            if (selection.Native)
            {
                var readOnly = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                selection.Writable &= !Equals(readOnly,true) && !Equals(readOnly,TextPattern.MixedAttributeValue);
            }
            else selection.Writable = EditableRange(selection.Window,owner,selection.Focused,range);
            RememberEditorPosition(selection,pattern);
            if (!selection.Foreground) return null;
            issue = SelectionIssue.None;
            return selection;
        }

        internal static bool WeChat(IntPtr window)
        {
            try
            {
                using (var process = Process.GetProcessById((int)Desktop.ProcessOf(window)))
                    return new[] { "WeChat", "Weixin" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }

        // An opaque WeChat surface needs a successful guarded copy before offering its button.
        internal static Selection Read(IntPtr window, Gesture gesture, bool allowOpaque, Func<bool> stillCurrent = null)
        {
            if (stillCurrent != null && !stillCurrent()) return null;
            if (!Desktop.External(window) || Desktop.GetForegroundWindow() != window) return null;
            if (gesture != null && (Desktop.RootAt(gesture.Start) != window || Desktop.RootAt(gesture.End) != window)) return null;
            var selection = new Selection { Window = window, Process = Desktop.ProcessOf(window), Focus = Desktop.Focus(window), Anchor = gesture };
            if (selection.Focus == IntPtr.Zero) return null;
            selection.Location = gesture?.End ?? Desktop.FocusPoint(window,selection.Focus);
            selection.Native = Desktop.NativeEdit(selection.Focus);
            if (selection.Native)
            {
                if (Desktop.Password(selection.Focus) || !Desktop.Range(selection.Focus, out selection.Start, out selection.End)) return null;
                selection.Writable = Desktop.NativeWritable(selection.Focus);
            }

            var focus = AutomationElement.FocusedElement;
            if (!SafeSource(focus,window)) return null;
            selection.Focused = focus;
            var hit = gesture == null ? focus : AutomationElement.FromPoint(new System.Windows.Point(gesture.End.X, gesture.End.Y));
            if (hit == null || (gesture != null && !SafeSource(hit,window)) || ActionControl(hit.Current.ControlType)) return null;
            AutomationElement childOwner = null;
            TextPatternRange childRange = null;
            TextPattern childPattern = null;
            foreach (var el in Ancestry(hit))
            {
                var role = el.Current.ControlType;
                if (gesture != null && ActionControl(role)) return null;
                // Legacy edit children often expose ValuePattern but no TextPattern. Never
                // borrow a selection from an enclosing window when a native edit owns focus.
                if (selection.Native && !Ancestor(el, focus) && !Ancestor(focus, el)) return null;
                bool container = role == ControlType.Edit || role == ControlType.Document || role == ControlType.Window;
                object pattern;
                if ((container || childRange == null) && el.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    var ranges = ((TextPattern)pattern).GetSelection() ?? new TextPatternRange[0];
                    if (ranges.Length > 1) return null;
                    if (ranges.Length == 1)
                    {
                        var r = ranges[0];
                        if (r.CompareEndpoints(TextPatternRangeEndpoint.Start, r, TextPatternRangeEndpoint.End) != 0 && Near(r, gesture))
                        {
                            // Chromium text runs can expose a selection even inside a writable
                            // ProseMirror paragraph. The editor owns the full range and survives a paste.
                            if (container) return FromRange(selection,el,(TextPattern)pattern,r,stillCurrent,out _);
                            childOwner = el;
                            childPattern = (TextPattern)pattern;
                            childRange = r;
                        }
                    }
                }
                if (container) break;
            }
            if (childRange != null) return FromRange(selection,childOwner,childPattern,childRange,stillCurrent,out _);
            if (!selection.Foreground) return null;
            if (selection.Native && (gesture == null || Desktop.OverSelectedText(selection.Focus,gesture.End,selection.Start,selection.End)))
            {
                if (stillCurrent != null && !stillCurrent()) return null;
                selection.Text = Desktop.ReadClassicRange(selection.Focus, selection.Start, selection.End);
                if (selection.Text != null && !Passage.HasLetter(selection.Text)) return null;
                if (gesture == null) selection.Location = Desktop.CharacterPoint(selection.Focus,selection.End-1) ?? selection.Location;
                return selection.Foreground ? selection : null;
            }
            if (!allowOpaque || !WeChat(window) || selection.Native) return null;
            var type = hit.Current.ControlType;
            if (type != ControlType.Text && type != ControlType.Edit && type != ControlType.Document) return null;
            if (focus.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty,true) as bool? != false ||
                hit.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty,true) as bool? != false) return null;
            // A successful Copy proves text, never an insertion point or an editable range.
            selection.Writable = false;
            return selection;
        }

        internal static Selection ReadShortcut(IntPtr window, Point pointer, Func<bool> stillCurrent = null)
        {
            var focused = Read(window, null, false, stillCurrent);
            if (focused != null) return focused;
            if (Desktop.RootAt(pointer) == window)
            {
                var atPointer = Read(window, new Gesture { Start = pointer, End = pointer }, false, stillCurrent);
                if (atPointer != null) return atPointer;
            }
            return WeChat(window) ? Read(window, null, true, stillCurrent) : null;
        }

        // Explicit card requests may copy a selection even when a Chromium fork does not
        // publish a text range. This snapshot grants no permission to replace or paste.
        internal static Selection ReadCopyCandidate(IntPtr window, Point pointer, Func<bool> current)
        {
            if (!current() || !Desktop.External(window) || Desktop.GetForegroundWindow() != window) return null;
            var focus = Desktop.Focus(window);
            if (!Desktop.ChromiumContent(window,focus)) return null;
            var element = AutomationElement.FocusedElement;
            if (element == null || element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty,true) as bool? != false ||
                !SafeSource(element,window) || !current()) return null;
            return new Selection { Window=window, Process=Desktop.ProcessOf(window), Focus=focus,
                Focused=element, CopyOnly=true, Writable=false, Location=Desktop.RootAt(pointer)==window ? pointer : Desktop.FocusPoint(window,focus) };
        }

        internal static async Task<Selection> Recheck(Selection original, CancellationToken token, Func<bool> stillCurrent = null)
            => (await CheckForDelivery(original,token,stillCurrent)).Value;

        internal static async Task<SelectionCheck> CheckForDelivery(Selection original, CancellationToken token, Func<bool> stillCurrent)
        {
            var clock = Stopwatch.StartNew();
            var check = await CheckOnce(original,token,stillCurrent);
            if (!check.CanRetry) return check.Finished(SelectionIssue.None,clock);

            // Start again from the same captured selection. Only repeat a read, before
            // any paste, while the original input and clipboard guards still hold.
            await Task.Delay(150,token);
            if (!original.Foreground || (stillCurrent != null && !stillCurrent()))
                return new SelectionCheck(SelectionIssue.Interrupted).Finished(check.Issue,clock);
            var next = await CheckOnce(original,token,stillCurrent,1800,3500);
            return next.Finished(check.Issue,clock);
        }

        internal static Task<SelectionCheck> CheckOnce(Selection original, CancellationToken token, Func<bool> stillCurrent,
            int laneWaitMilliseconds = 650, int workMilliseconds = 2200)
        {
            return Query(() => {
                if (!original.Foreground || original.Process != Desktop.ProcessOf(original.Window) ||
                    (stillCurrent != null && !stillCurrent())) return new SelectionCheck(SelectionIssue.Interrupted);
                Selection now;
                if (original.CopyOnly)
                {
                    now = ReadCopyCandidate(original.Window,original.Location,stillCurrent ?? (() => true));
                    return now != null && original.MatchesFocus(now.Focused)
                        ? new SelectionCheck(now) : new SelectionCheck(SelectionIssue.UnsafeSource);
                }
                if (original.Range == null)
                    now = Read(original.Window, original.Native ? null : original.Anchor, !original.Native, stillCurrent);
                else
                {
                    // Revalidate the recorded provider. A browser's selection toolbar may now
                    // cover the old mouse position without changing the actual text range.
                    var focus = AutomationElement.FocusedElement;
                    if (!original.MatchesFocus(focus)) return new SelectionCheck(SelectionIssue.FocusChanged);
                    if (!SafeSource(focus,original.Window)) return new SelectionCheck(SelectionIssue.UnsafeSource);
                    var owner = original.EditorDigest != null || Equal(original.Owner,focus) ? focus : original.Owner;
                    if (!ReferenceEquals(owner,focus) && !SafeSource(owner,original.Window,focus))
                        return new SelectionCheck(SelectionIssue.UnsafeSource);
                    var next = new Selection { Window=original.Window, Focus=original.Focus, Process=original.Process,
                        Native=original.Native, Focused=focus, Anchor=original.Anchor, Location=original.Location };
                    if (next.Native)
                    {
                        if (!Desktop.NativeEdit(next.Focus) || Desktop.Password(next.Focus)) return new SelectionCheck(SelectionIssue.UnsafeSource);
                        if (!Desktop.Range(next.Focus,out next.Start,out next.End) || next.Start != original.Start || next.End != original.End)
                            return new SelectionCheck(SelectionIssue.PositionChanged);
                        next.Writable = Desktop.NativeWritable(next.Focus);
                    }
                    var pattern = (TextPattern)owner.GetCurrentPattern(TextPattern.Pattern);
                    var ranges = pattern.GetSelection();
                    if (ranges == null || ranges.Length == 0) return new SelectionCheck(SelectionIssue.NoRange);
                    if (ranges.Length > 1) return new SelectionCheck(SelectionIssue.MultipleRanges);
                    if (ranges[0].CompareEndpoints(TextPatternRangeEndpoint.Start,ranges[0],TextPatternRangeEndpoint.End) == 0)
                        return new SelectionCheck(SelectionIssue.Collapsed);
                    now = FromRange(next,owner,pattern,ranges[0],stillCurrent,out var readIssue,rechecking:true);
                    if (now == null) return new SelectionCheck(readIssue);
                }
                var issue = original.Difference(now);
                return issue == SelectionIssue.None ? new SelectionCheck(now) : new SelectionCheck(issue);
            }, token,laneWaitMilliseconds,issue => new SelectionCheck(issue),workMilliseconds);
        }

        internal static Task<string> ConfirmInsertion(Selection original, string translation, Func<bool> current, CancellationToken token)
        {
            return Query(() => {
                if (!current() || !original.Foreground) return null;
                var focus = AutomationElement.FocusedElement;
                if (!original.MatchesFocus(focus) || !SafeSource(focus,original.Window)) return null;
                var owner = original.EditorDigest != null ? focus : original.Owner;
                if (!ReferenceEquals(owner,focus) && !SafeSource(owner,original.Window,focus)) return null;
                var pattern = (TextPattern)owner.GetCurrentPattern(TextPattern.Pattern);
                var ranges = pattern.GetSelection();
                if (ranges.Length != 1 || !SafeSource(ranges[0].GetEnclosingElement(),original.Window,owner)) return null;
                var caret = ranges[0].Clone();
                if (caret.CompareEndpoints(TextPatternRangeEndpoint.Start,caret,TextPatternRangeEndpoint.End) != 0) return null;
                string expected = translation.Replace("\r\n","\n").Replace('\r','\n');
                caret.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Character,-Passage.Count(expected));
                string inserted = caret.GetText(-1).Replace("\r\n","\n").Replace('\r','\n');
                return current() && inserted == expected ? "confirmed" : null;
            },token);
        }
    }
}
