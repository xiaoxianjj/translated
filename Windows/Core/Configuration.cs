using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace Translated
{
    internal static class Json
    {
        public static byte[] Write<T>(T value)
        {
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(buffer, value);
                return buffer.ToArray();
            }
        }

        public static T Read<T>(byte[] bytes)
        {
            using (var buffer = new MemoryStream(bytes))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(buffer);
        }
    }

    [DataContract]
    public sealed class Provider
    {
        public const string DeepSeek = "DeepSeek", OpenAI = "OpenAI", Custom = "自定义 / 本地";
        public static readonly string[] Choices = { DeepSeek, OpenAI, Custom };
        [DataMember] public string Name;
        [DataMember] public string BaseUrl;
        [DataMember] public string Model;
        [DataMember] public bool Reasoning;

        public static Provider Default(string name)
        {
            switch (name)
            {
                case DeepSeek: return new Provider { Name = name, BaseUrl = "https://api.deepseek.com", Model = "deepseek-flash" };
                case OpenAI: return new Provider { Name = name, BaseUrl = "https://api.openai.com/v1", Model = "gpt-6-luna" };
                case Custom: return new Provider { Name = name, BaseUrl = "http://localhost:11434/v1", Model = "" };
                default: throw new InvalidOperationException("请选择翻译服务。");
            }
        }

        public Uri Endpoint()
        {
            string address = Name == Custom ? BaseUrl : Default(Name).BaseUrl;
            Uri uri;
            if (!Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out uri) || uri.Host.Length == 0 ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !(uri.Scheme == "https" || (uri.Scheme == "http" && IsLocal(uri))))
                throw new InvalidOperationException("请输入 HTTPS 服务地址。本机服务也可以使用 HTTP。");
            var path = uri.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith("/chat/completions", StringComparison.Ordinal)) path += "/chat/completions";
            return new Uri(uri.GetLeftPart(UriPartial.Authority) + path);
        }

        public static bool IsLocal(Uri uri)
        {
            IPAddress ip;
            return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(uri.Host.Trim('[', ']'), out ip) && IPAddress.IsLoopback(ip));
        }

        public string CredentialId()
        {
            byte[] scope = Encoding.UTF8.GetBytes(Name + "|" + Endpoint().AbsoluteUri);
            using (var sha = SHA256.Create())
                return "Translated/Windows/" + BitConverter.ToString(sha.ComputeHash(scope)).Replace("-", "");
        }

        public void Validate(string key)
        {
            var endpoint = Endpoint();
            if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("请填写模型名称。");
            if (!IsLocal(endpoint) && string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("请在设置中为这个服务地址保存 API 密钥。");
            if (key != null && key.Any(char.IsControl)) throw new InvalidOperationException("API 密钥包含无效字符。");
        }
    }

    [DataContract]
    public sealed class Shortcut
    {
        public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8, F1 = 112;
        [DataMember] public int Modifiers;
        [DataMember] public int Key;
        // Null for older settings; Key remains the primary key for backwards compatibility.
        [DataMember(EmitDefaultValue = false)] public int[] AdditionalKeys;
        public static Shortcut Default() => new Shortcut { Modifiers = Ctrl | Shift, Key = F1 + 7 };
        public static string KeyName(int key) => ShortcutKeys.Name(key);
        public int[] Keys => new[] { Key }.Concat(AdditionalKeys ?? new int[0]).Distinct().OrderBy(k => k).ToArray();
        public string ValidationMessage => ShortcutKeys.Validate(this);
        public bool Valid => ValidationMessage == null;
        public bool Native => Keys.Length == 1 && !ShortcutKeys.Mouse(Key);
        public bool Same(Shortcut other) => other != null && Modifiers == other.Modifiers && Keys.SequenceEqual(other.Keys);
        public override string ToString()
        {
            var names = new List<string>();
            if ((Modifiers & Ctrl) != 0) names.Add("Ctrl");
            if ((Modifiers & Alt) != 0) names.Add("Alt");
            if ((Modifiers & Shift) != 0) names.Add("Shift");
            if ((Modifiers & Win) != 0) names.Add("Win");
            names.AddRange(Keys.OrderBy(k => ShortcutKeys.Mouse(k) ? 1 : 0).ThenBy(k => k).Select(KeyName));
            return string.Join(" + ", names);
        }
    }

    [DataContract]
    public sealed class Preferences
    {
        [DataMember] public string Active = Provider.DeepSeek;
        [DataMember] public Provider[] Profiles = Provider.Choices.Select(Provider.Default).ToArray();
        [DataMember] public Shortcut Hotkey = Shortcut.Default();
        [DataMember] public bool Traditional;
        [DataMember] public bool Floating = true;
        [DataMember] public bool Replace = true;
        [DataMember] public bool Configured;
        [DataMember] public bool WeChatCopy;
        [DataMember] public string SelectionMode = "card";
        [DataMember] public string TargetLanguage = "auto";

        public Provider Profile(string name) => Profiles.FirstOrDefault(p => p != null && p.Name == name) ?? Provider.Default(name);
        public Preferences Copy() => Json.Read<Preferences>(Json.Write(this));

        public static Preferences Load(string path)
        {
            if (!File.Exists(path)) return new Preferences();
            var prefs = Json.Read<Preferences>(File.ReadAllBytes(path));
            if (prefs == null) throw new InvalidDataException();
            prefs.Profiles = prefs.Profiles ?? new Provider[0];
            if (!Provider.Choices.Contains(prefs.Active)) prefs.Active = Provider.DeepSeek;
            if (prefs.Hotkey == null || !prefs.Hotkey.Valid) prefs.Hotkey = Shortcut.Default();
            if (prefs.SelectionMode != "automatic") prefs.SelectionMode = "card";
            prefs.TargetLanguage = TranslationLanguages.Find(prefs.TargetLanguage).Code;
            return prefs;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, Json.Write(this));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class HotkeyBinding
    {
        readonly Func<int, Shortcut, bool> register;
        readonly Action<int> unregister;
        int id;
        public Shortcut Current { get; private set; }
        public int Id => id;

        public HotkeyBinding(Func<int, Shortcut, bool> register, Action<int> unregister)
        { this.register = register; this.unregister = unregister; }

        public void Apply(Shortcut candidate, Action persist)
        {
            if (!candidate.Valid) throw new InvalidOperationException(candidate.ValidationMessage);
            if (candidate.Same(Current)) { persist(); return; }
            int next = id == 1 ? 2 : 1;
            if (!register(next, candidate)) throw new InvalidOperationException("快捷键已被占用，请换一个组合。");
            try { persist(); }
            catch { unregister(next); throw; }
            if (Current != null) unregister(id);
            Current = candidate;
            id = next;
        }

        public void Release() { if (Current != null) unregister(id); Current = null; }
    }
}
