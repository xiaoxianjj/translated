using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Translated.Tests
{
    internal static class Program
    {
        static int passed;
        static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual));
        static void Throws(Action work)
        {
            try { work(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected rejection");
        }
        static void Check(string name, Action work) { work(); passed++; Console.WriteLine("PASS  " + name); }
        static async Task Check(string name, Func<Task> work) { await work(); passed++; Console.WriteLine("PASS  " + name); }

        sealed class Stub : HttpMessageHandler
        {
            public Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Reply;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => Reply(r,t);
        }
        static HttpResponseMessage Response(string value, string finish = "stop")
        {
            var dto = new Translated.Response { choices = new[] { new Choice {
                message = new ChatMessage { role = "assistant", content = value }, finish_reason = finish } } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Json.Write(dto)) };
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
#if NET48
            if (args.Length == 4 && args[0] == "--shortcut-key" && int.TryParse(args[1],out int fixtureKey) &&
                long.TryParse(args[3],out long fixtureWindow))
            { ShortcutDesktopChecks.InjectKey(fixtureKey,args[2]=="1",fixtureWindow); return 0; }
            if (args.Length == 1 && args[0] == "--shortcuts") { ShortcutDesktopChecks.Run(); return 0; }
            if (args.Length == 1 && args[0] == "--shortcut-keyboard") { ShortcutDesktopChecks.RunKeyboardFixture(); return 0; }
            if (args.Length == 1 && args[0] == "--shortcut-editor") { VisualChecks.ShortcutEditorFixture(); return 0; }
            if (args.Length == 2 && args[0] == "--probe-selection" && int.TryParse(args[1],out int processId) && processId>0)
            { SelectionProbe.Run(processId); return 0; }
            if (args.Length == 2 && args[0] == "--render") { VisualChecks.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--browser") { BrowserChecks.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--browser-cold") { BrowserChecks.Run(args[1], true); return 0; }
            if (args.Length == 2 && args[0] == "--browser-cold-busy") { BrowserChecks.Run(args[1], true, true); return 0; }
            if (args.Length == 2 && args[0] == "--browser-range-owner") { BrowserChecks.Run(args[1], true, false, true); return 0; }
            if (args.Length == 2 && args[0] == "--browser-repeat") { BrowserChecks.Run(args[1], cold:true, repeat:true); return 0; }
            if (args.Length == 2 && args[0] == "--browser-multiline") { BrowserChecks.Run(args[1], cold:true, multiline:true); return 0; }
            if (args.Length == 1 && (args[0] == "--desktop" || args[0] == "--fixture"))
            { WindowsDesktopChecks.Run(args[0] == "--fixture"); return 0; }
#endif
            Run().GetAwaiter().GetResult();
            return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetBaseException().Message);
                Console.Error.WriteLine(error.GetBaseException().StackTrace);
                return 1;
            }
        }

        static async Task Run()
        {
            var local = Provider.Default(Provider.Custom); local.Model = "fixture";
            var ds = Provider.Default(Provider.DeepSeek);
            Check("selection keyboard gestures exclude typing and unrelated modifiers", () => {
                Require(SelectionPolicy.KeyboardGesture('A', false, true, false, false));
                foreach (int key in Enumerable.Range(0x21, 8))
                {
                    Require(SelectionPolicy.KeyboardGesture(key, true, false, false, false));
                    Require(SelectionPolicy.KeyboardGesture(key, true, true, false, false));
                    Require(!SelectionPolicy.KeyboardGesture(key, false, false, false, false));
                }
                Require(!SelectionPolicy.KeyboardGesture('A', true, true, false, false));
                Require(!SelectionPolicy.KeyboardGesture('C', false, true, false, false));
                Require(!SelectionPolicy.KeyboardGesture('X', false, false, false, false));
                Require(!SelectionPolicy.KeyboardGesture(0x25, true, false, true, false));
                Require(!SelectionPolicy.KeyboardGesture(0x25, true, false, false, true));
            });
            Check("short English correction does not force unrelated Latin words", () => {
                foreach (string word in new[] { "words", " Application ", "hello!" }) Equal("English", LanguageRouting.SourceHint(word));
                foreach (string word in new[] { "bonjour", "hola", "Danke", "你好", "¿qué?" }) Equal(null,LanguageRouting.SourceHint(word));
                var wordChunk = Passage.Split("words").Single();
                Require(Translator.Build(ds,true,wordChunk,sourceLanguage:"English").messages[0].content.Contains("Translate into Traditional Chinese"));
                Require(Translator.Build(ds,false,wordChunk,sourceLanguage:"French").messages[0].content.Contains("Source language: French. Translate into English"));
            });
            Check("delivery requires stable selection and unchanged clipboard", () => {
                Equal(SelectionDelivery.Copy, SelectionPolicy.Delivery(false,true,true,true,true));
                Equal(SelectionDelivery.Replace, SelectionPolicy.Delivery(true,true,true,true,true));
                Equal(SelectionDelivery.Review, SelectionPolicy.Delivery(true,false,true,true,true));
                Equal(SelectionDelivery.Review, SelectionPolicy.Delivery(true,true,false,true,true));
                foreach (bool editable in new[] { false, true })
                {
                    Equal(SelectionDelivery.Review, SelectionPolicy.Delivery(editable,true,true,false,true));
                    Equal(SelectionDelivery.Review, SelectionPolicy.Delivery(editable,true,true,true,false));
                }
            });
            Check("default Windows shortcut", () => {
                var c = Shortcut.Default(); Require(c.Valid); Equal("Ctrl + Shift + F8", c.ToString());
                Require(!new Shortcut { Modifiers = Shortcut.Ctrl, Key = 'C' }.Valid);
                Require(!new Shortcut { Modifiers = 6, Key = Shortcut.F1 + 11 }.Valid);
            });
            Check("custom shortcuts keep legacy settings and round-trip multi-key mouse chords", () => {
                var old = Json.Read<Shortcut>(Encoding.UTF8.GetBytes("{\"Modifiers\":6,\"Key\":119}"));
                Require(old.Valid && old.Native && old.Same(Shortcut.Default()));
                var chord = ShortcutKeys.FromKeys(new[] { 0xA2, 0xA0, 'K', ShortcutKeys.Mouse4 });
                Require(chord.Valid && !chord.Native && chord.Modifiers == 6);
                var saved = Json.Read<Shortcut>(Json.Write(chord));
                Require(chord.Same(saved) && saved.ToString().Contains("Mouse 4") && saved.ToString().Contains("K"));
                Require(chord.Same(new Shortcut { Modifiers = 6, Key = 'K', AdditionalKeys = new[] { 5, 'K' } }));
                Require(new Shortcut { Key = 5 }.Valid && !new Shortcut { Key = 'K' }.Valid);
            });
            Check("custom shortcut validation rejects reserved combinations and supports extended keys", () => {
                foreach (var keys in new[] { new[] { 17, 'C' }, new[] { 18, 9 }, new[] { 91, 'L' }, new[] { 17, 18, 46 },
                    new[] { 17, 123 }, new[] { 17, 'A', 'B', 'C', 'D', 'E' }, new[] { 16, 'K' } })
                    Require(!ShortcutKeys.FromKeys(keys).Valid);
                foreach (var keys in new[] { new[] { 17, 186 }, new[] { 18, 100 }, new[] { 5, 6 }, new[] { 4 },
                    new[] { 17, 16, 'K', 'J' }, new[] { 17, 38 }, new[] { 124 } })
                    Require(ShortcutKeys.FromKeys(keys).Valid);
                Equal("Num 4", Shortcut.KeyName(100)); Equal("F13", Shortcut.KeyName(124));
            });
            Check("mouse and multi-key shortcuts require the full chord and do not autorepeat", () => {
                var chord = ShortcutKeys.FromKeys(new[] { 17, 'K', 5 });
                var state = new ShortcutChordState();
                Require(!state.Update(chord, 0xA2, true));
                Require(!state.Update(chord, 'K', true));
                Require(state.Update(chord, 5, true));
                Require(!state.Update(chord, 5, true));
                Require(!state.Update(chord, 5, false));
                Require(state.Update(chord, 5, true));
                state.Reset();
                Require(!state.Update(chord, 16, true));
                Require(!state.Update(chord, 17, true));
                Require(!state.Update(chord, 'K', true));
                Require(!state.Update(chord, 5, true));
            });
            Check("left and right modifiers preserve physical ownership until both release", () => {
                var chord = new Shortcut { Modifiers = Shortcut.Ctrl, Key = 5 };
                var state = new ShortcutChordState();
                state.Update(chord, 0xA2, true); state.Update(chord, 0xA3, true);
                state.Update(chord, 0xA2, false);
                Require(state.Update(chord, 5, true));
                state.Update(chord, 5, false); state.Update(chord, 0xA3, false);
                Require(!state.Update(chord, 5, true));
            });
            Check("mouse chords reconcile keys already held before registration and discard released modifiers", () => {
                var chord = ShortcutKeys.FromKeys(new[] { 17, 'K', 5 });
                var state = new ShortcutChordState();
                state.SynchronizeKeyboard(k => k == 162 || k == 'K');
                Require(state.Update(chord,5,true));
                state.Update(chord,5,false);
                state.SynchronizeKeyboard(k => k == 'K');
                Require(!state.Update(chord,5,true));
            });
            Check("recorder latches a gesture, replaces it on the next and supports virtual plus physical selection", () => {
                var recorder = new ShortcutRecorder(Shortcut.Default());
                recorder.Input(0xA2, true); recorder.Input('K', true);
                Require(recorder.Candidate.Same(new Shortcut { Modifiers = 2, Key = 'K' }));
                recorder.Input('K', false); recorder.Input(0xA2, false);
                Require(recorder.Selected.Contains('K') && !recorder.Holding('K'));
                recorder.Input(5, true); recorder.Input(5, false);
                Require(recorder.Candidate.Same(new Shortcut { Key = 5 }));
                recorder.Clear(); recorder.Toggle(17); recorder.Toggle(16);
                recorder.Input(6, true); recorder.Input(6, false);
                Require(recorder.Candidate.Same(new Shortcut { Modifiers = 6, Key = 6 }));
                recorder.Toggle(6); Require(recorder.Candidate.Key == 0 && !recorder.Candidate.Valid);
            });
            Check("failed custom shortcut persistence releases the candidate and retains the previous binding", () => {
                var held = new HashSet<int>();
                var b = new HotkeyBinding((id,c) => held.Add(id), id => held.Remove(id));
                var old = Shortcut.Default(); b.Apply(old, () => {});
                Throws(() => b.Apply(new Shortcut { Key = 5 }, () => { throw new InvalidOperationException("fixture"); }));
                Require(b.Current.Same(old) && held.Count == 1);
                b.Apply(new Shortcut { Key = 5 }, () => {}); Require(b.Current.Key == 5 && held.Count == 1);
                b.Release(); Require(held.Count == 0);
            });
            Check("hotkey conflict keeps old registration and settings", () => {
                var held = new Dictionary<int,int>();
                var b = new HotkeyBinding((id,c) => { if (c.Key == 'A') return false; held.Add(id,c.Key); return true; }, id => held.Remove(id));
                var old = Shortcut.Default(); b.Apply(old, () => {});
                Throws(() => b.Apply(new Shortcut { Key = 'A', Modifiers = 6 }, () => { throw new Exception("persisted conflicting shortcut"); }));
                Equal(old.Key, held.Single().Value); Require(b.Current.Same(old));
            });
            Check("hotkey rollback and successful handover", () => {
                var held = new HashSet<int>();
                var b = new HotkeyBinding((id,c) => held.Add(id), id => held.Remove(id));
                var old = Shortcut.Default(); var next = new Shortcut { Key = 'B', Modifiers = 6 };
                b.Apply(old, () => { }); int first = b.Id;
                Throws(() => b.Apply(next, () => { throw new InvalidOperationException(); }));
                Require(held.SetEquals(new[] { first }) && b.Current.Same(old));
                b.Apply(next, () => Require(held.Count == 2)); Require(held.SetEquals(new[] { b.Id }) && b.Current.Same(next));
                b.Apply(next, () => {}); Require(held.Count == 1); b.Release(); Equal(0,held.Count);
            });
            Check("provider endpoint and credential scope", () => {
                var p = Provider.Default(Provider.Custom);
                p.BaseUrl = "http://127.0.0.1:12345/v1/"; var id = p.CredentialId();
                Equal("http://127.0.0.1:12345/v1/chat/completions", p.Endpoint().AbsoluteUri);
                p.BaseUrl = p.Endpoint().AbsoluteUri; Equal(id, p.CredentialId());
                p.BaseUrl = "http://127.0.0.1:12345/v2"; Require(id != p.CredentialId());
                p.BaseUrl = ds.BaseUrl; Require(p.CredentialId() != ds.CredentialId());
                p.BaseUrl = "http://[::1]:11434/v1"; p.Model = "local"; p.Validate("");
            });
            Check("unsafe endpoints, credentials and model validation", () => {
                foreach (string address in new[] { "http://remote.test/v1", "https://a:b@remote.test/v1",
                    "https://remote.test/v1?q=1", "http://localhost.bad.test/v1", "file:///model", "https://remote.test/#f" })
                { var p = Provider.Default(Provider.Custom); p.BaseUrl = address; Throws(() => p.Endpoint()); }
                Throws(() => ds.Validate("")); Throws(() => ds.Validate("x\r\ny"));
                Throws(() => Provider.Default(Provider.Custom).Validate(""));
            });
            Check("settings round-trip and atomic overwrite", () => {
                string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                string path = Path.Combine(dir, "settings.json");
                try
                {
                    var p = Preferences.Load(path); Require(p.Replace && p.Floating);
                    p.Save(path); var changed = p.Copy(); changed.Active = Provider.Custom; changed.Traditional = true;
                    changed.Profile(Provider.Custom).Model = "mock"; changed.Hotkey = new Shortcut { Modifiers=3, Key='Q' };
                    changed.Save(path); var saved = Preferences.Load(path);
                    Require(saved.Traditional && saved.Hotkey.Same(changed.Hotkey)); Equal("mock", saved.Profile(saved.Active).Model);
                    Equal(Provider.DeepSeek, p.Active); Equal(1, Directory.GetFiles(dir).Length);
                }
                finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            });
            Check("empty, nonlinguistic and oversized selections", () => {
                foreach (var s in new[] { "", " \t ", "123?!", "🍊🙂", new string('a', 50001) }) Throws(() => Passage.Split(s));
                Require(Passage.Split(new string('a',50000)).Count > 1);
            });
            Check("Unicode chunks, selected whitespace and bounded context", () => {
                string text = "  " + string.Concat(Enumerable.Repeat("𠀀 héllo e\u0301。\r\n", 1700)) + "  ";
                var chunks = Passage.Split(text); Require(chunks.Count > 1);
                Equal(text, string.Concat(chunks.Select(c => c.Prefix + c.Text + c.Suffix)));
                Equal("", chunks[0].Before); Equal("", chunks.Last().After); Require(chunks[0].After.Length > 0);
                foreach (var c in chunks)
                {
                    Require(c.Before.Length <= 1200 && c.After.Length <= 1200);
                    var raw = c.Prefix + c.Text + c.Suffix; Require(!char.IsLowSurrogate(raw[0]) && !char.IsHighSurrogate(raw[raw.Length-1]));
                }
            });
            Check("long whitespace chunks are reconstructed once", () => {
                var text = "a" + new string(' ', 19000) + "b"; var chunks = Passage.Split(text);
                Require(chunks.Any(c => c.Text.Length == 0)); Equal(text, string.Concat(chunks.Select(c=>c.Prefix+c.Text+c.Suffix)));
            });
            Check("protected code, links and paragraph separators survive long chunk boundaries", () => {
                var original="  See `id_42` at https://example.org/a?q=17\r\n\r\n" +
                    "```cs\r\n"+new string('x',13000)+"\r\n```\r\nDone.  ";
                var protectedSpans=new VerbatimSpans(original);
                var restored=new StringBuilder();
                foreach(var chunk in Passage.Split(original))
                {
                    var masked=protectedSpans.Protect(chunk,out var literals,out bool prose);
                    Require(!masked.Contains("id_42") && !masked.Contains("example.org") && !masked.Contains("xxxx"));
                    restored.Append(chunk.Prefix).Append(VerbatimSpans.Restore(masked,literals)).Append(chunk.Suffix);
                    if(literals.Count!=0) Throws(()=>VerbatimSpans.Restore("",literals));
                }
                Equal(original,restored.ToString());
            });
            await Check("code-only selections retain exact contents without a provider call",async () => {
                string code="`const url = 123;`";
                using(var client=new HttpClient(new Stub{Reply=(r,t)=>throw new Exception("Unexpected paid request")}))
                    Equal(code,await Translator.Translate(code,local,"",false,CancellationToken.None,client:client));
            });
            Check("automatic language directions and provider options", () => {
                var c = Passage.Split("words").Single();
                var deep = Translator.Build(ds,false,c); Equal("disabled",deep.thinking.type); Equal((int?)8192, deep.max_tokens);
                Require(deep.messages[0].content.Contains("English -> Simplified Chinese") && deep.messages[0].content.Contains("every other language -> English"));
                ds.Reasoning = true; Equal("low",Translator.Build(ds,false,c).reasoning_effort); ds.Reasoning = false;
                var ai = Translator.Build(Provider.Default(Provider.OpenAI),true,c);
                var wire = Encoding.UTF8.GetString(Json.Write(ai));
                Require(ai.messages[0].content.Contains("Traditional Chinese") && wire.Contains("\"store\":false"));
                Require(wire.Contains("max_completion_tokens") && !wire.Contains("\"max_tokens\""));
                Require(Translator.Build(ds,false,c,OutputLanguage.English).messages[0].content.Contains("Translate into English as explicitly requested"));
            });
            Check("refused, truncated, empty and malformed completions", () => {
                foreach (string raw in new[] { "{}", "not json", "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}",
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\" \"}}]}",
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"refusal\":\"no\",\"content\":\"text\"}}]}",
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"reasoning_content\":\"private\"}}]}" })
                    Throws(() => Translator.Decode(Encoding.UTF8.GetBytes(raw)));
            });
            await Check("request, response, progress and paragraph recovery", async () => {
                int requests = 0; string progress = null;
                var handler = new Stub { Reply = async (req,ct) => {
                    Equal(local.Endpoint(), req.RequestUri); Equal("POST",req.Method.Method);
                    Equal("test", req.Headers.Authorization.Parameter);
                    var body = Json.Read<Request>(await req.Content.ReadAsByteArrayAsync());
                    var payload = Json.Read<Payload>(Encoding.UTF8.GetBytes(body.messages[1].content));
                    requests++; return Response(payload.text == "world" ? "世界" : "你好");
                } };
                using (var client = new HttpClient(handler))
                {
                    string text = " " + new string('a',5900) + "\r\n" + new string(' ',100) + "world  ";
                    var result = await Translator.Translate(text, local, "test", false, CancellationToken.None,
                        (i,n) => progress = i + "/" + n, client);
                    Equal(" 你好\r\n" + new string(' ',100) + "世界  ",result); Equal(2,requests); Equal("2/2",progress);
                }
            });
            await Check("later chunk failure emits no partial translation", async () => {
                int requests = 0;
                using (var client = new HttpClient(new Stub { Reply=(r,t) => Task.FromResult(++requests == 1 ? Response("first") : Response("partial", "length")) }))
                {
                    try { await Translator.Translate(new string('x',6100),local,"",false,CancellationToken.None,null,client); }
                    catch (InvalidOperationException) { Equal(2,requests); return; }
                    throw new Exception("Accepted partial result");
                }
            });
            await Check("cancellation aborts an in-flight request", async () => {
                using (var stop = new CancellationTokenSource())
                using (var client = new HttpClient(new Stub { Reply=async (r,t) => {
                    stop.Cancel(); await Task.Delay(1000,t); return Response("unexpected");
                } }))
                {
                    try { await Translator.Translate("hello",local,"",false,stop.Token,null,client); }
                    catch (OperationCanceledException) { return; }
                    throw new Exception("Ignored cancellation");
                }
            });
            await Check("loopback HTTP request through the shipping client", () => Loopback.Check(false));
            await Check("shipping client refuses HTTP redirect", () => Loopback.Check(true));
            Console.WriteLine(passed + " checks passed.");
        }
    }
}
