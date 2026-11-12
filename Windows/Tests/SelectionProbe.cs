#if NET48
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Translated.Tests
{
    // One explicit gesture in the specified application. Inspect the same selection
    // across several reads without translating, sending input, or accessing the clipboard.
    internal static class SelectionProbe
    {
        internal static void Run(int processId)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (var listener = new InputWindow())
            using (var timeout = new System.Windows.Forms.Timer { Interval = 300000 })
            {
                var shortcut = new Shortcut { Modifiers=Shortcut.Ctrl|Shortcut.Alt, Key=Shortcut.F1+8 };
                listener.Apply(shortcut,() => {});
                bool started = false;
                listener.ShortcutPressed += async () => {
                    var window = Desktop.GetForegroundWindow();
                    if (started || Desktop.ProcessOf(window) != processId) return;
                    started = true;
                    timeout.Stop();
                    try
                    {
                        int revision = listener.Revision;
                        var focus = Desktop.Focus(window);
                        bool Current() => listener.Revision == revision && Desktop.GetForegroundWindow() == window && Desktop.Focus(window) == focus;
                        for (int i=0; i<65 && (Desktop.ModifiersDown || Desktop.Down(shortcut.Key)); i++) await Task.Delay(25);
                        if (!Current() || Desktop.ModifiersDown || Desktop.Down(shortcut.Key)) { Console.WriteLine("Interrupted before capture"); return; }
                        var pointer = Cursor.Position;
                        var clock = Stopwatch.StartNew();
                        var original = await SelectionReader.Query(() => SelectionReader.ReadShortcut(window,pointer,Current),CancellationToken.None,2500);
                        if (original == null) { Console.WriteLine("No selection at capture"); return; }
                        Console.WriteLine("Capture {0}ms, writable={1}, editor position={2}",clock.ElapsedMilliseconds,original.Writable,original.EditorDigest!=null);
                        foreach (int delay in new[]{0,300,800,1200,2000})
                        {
                            await Task.Delay(delay);
                            if (!Current()) { Console.WriteLine("Input or focus changed"); break; }
                            clock.Restart();
                            var check = await SelectionReader.CheckOnce(original,CancellationToken.None,Current);
                            Console.WriteLine("Recheck {0}ms: {1}",clock.ElapsedMilliseconds,check.Issue);
                        }
                    }
                    catch (Exception error) { Console.WriteLine("Probe stopped: "+error.GetType().Name); }
                    finally { Application.ExitThread(); }
                };
                timeout.Tick += (s,e) => { Console.WriteLine("No probe gesture received"); Application.ExitThread(); };
                timeout.Start();
                Console.WriteLine("Ready for Ctrl+Alt+F9 in process "+processId);
                Application.Run();
            }
        }
    }
}
#endif
