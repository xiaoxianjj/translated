using System;
using System.Collections.Generic;
using System.Linq;

namespace Translated
{
    internal static class ShortcutKeys
    {
        internal const int Middle = 4, Mouse4 = 5, Mouse5 = 6;
        internal static bool Mouse(int key) => key >= Middle && key <= Mouse5;
        internal static int Normalize(int key) => key == 0xA0 || key == 0xA1 ? 0x10 :
            key == 0xA2 || key == 0xA3 ? 0x11 : key == 0xA4 || key == 0xA5 ? 0x12 : key == 0x5C ? 0x5B : key;
        internal static int Modifier(int key)
        {
            switch (Normalize(key))
            {
                case 0x10: return Shortcut.Shift;
                case 0x11: return Shortcut.Ctrl;
                case 0x12: return Shortcut.Alt;
                case 0x5B: return Shortcut.Win;
                default: return 0;
            }
        }
        internal static bool Supported(int key) => Mouse(key) ||
            (key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9') ||
            (key >= 0x60 && key <= 0x6F && key != 0x6C) ||
            (key >= Shortcut.F1 && key <= Shortcut.F1 + 23 && key != Shortcut.F1 + 11) ||
            (key >= 0x21 && key <= 0x28) ||
            new[] { 8, 9, 13, 19, 20, 32, 45, 46, 144, 145, 186, 187, 188, 189, 190, 191, 192, 219, 220, 221, 222, 226 }.Contains(key);
        internal static string Name(int key)
        {
            if (key >= 'A' && key <= 'Z' || key >= '0' && key <= '9') return ((char)key).ToString();
            if (key >= Shortcut.F1 && key <= Shortcut.F1 + 23) return "F" + (key - Shortcut.F1 + 1);
            if (key >= 0x60 && key <= 0x69) return "Num " + (key - 0x60);
            switch (Normalize(key))
            {
                case 4: return "Mouse Middle"; case 5: return "Mouse 4"; case 6: return "Mouse 5";
                case 8: return "Backspace"; case 9: return "Tab"; case 13: return "Enter";
                case 16: return "Shift"; case 17: return "Ctrl"; case 18: return "Alt"; case 91: return "Win";
                case 19: return "Pause"; case 20: return "Caps Lock"; case 27: return "Esc";
                case 32: return "Space"; case 33: return "Page Up"; case 34: return "Page Down";
                case 35: return "End"; case 36: return "Home"; case 37: return "Left"; case 38: return "Up";
                case 39: return "Right"; case 40: return "Down"; case 45: return "Insert"; case 46: return "Delete";
                case 106: return "Num *"; case 107: return "Num +"; case 109: return "Num -";
                case 110: return "Num ."; case 111: return "Num /"; case 144: return "Num Lock"; case 145: return "Scroll Lock";
                case 186: return ";"; case 187: return "="; case 188: return ","; case 189: return "-";
                case 190: return "."; case 191: return "/"; case 192: return "\u0060"; case 219: return "[";
                case 220: return "\\"; case 221: return "]"; case 222: return "'"; case 226: return "ISO \\";
                default: return "Key " + key;
            }
        }
        internal static string Validate(Shortcut shortcut)
        {
            var keys = shortcut.Keys;
            if (shortcut.Modifiers < 0 || shortcut.Modifiers > 15 || keys.Any(k => !Supported(k)))
                return "请选择键盘按键、鼠标中键或侧键；Esc、F12 和 Fn 不可用。";
            if (keys.Length > 4) return "最多组合 4 个普通按键，另可搭配 Ctrl、Alt、Shift、Win。";
            if (shortcut.Modifiers == 0 && !keys.All(Mouse) &&
                !(keys.Length == 1 && keys[0] >= Shortcut.F1 && keys[0] <= Shortcut.F1 + 23))
                return "键盘组合请至少搭配 Ctrl、Alt 或 Win，避免打字时误触发。鼠标侧键可以单独使用。";
            if (shortcut.Modifiers == Shortcut.Shift && !keys.All(Mouse))
                return "键盘组合请搭配 Ctrl、Alt 或 Win，不要只使用 Shift。";
            if (shortcut.Modifiers == Shortcut.Ctrl && keys.Any(k => "ACVXYZ ".IndexOf((char)k) >= 0) ||
                shortcut.Modifiers == Shortcut.Alt && keys.Any(k => k == Shortcut.F1 + 3 || k == 32 || k == 9) ||
                (shortcut.Modifiers & Shortcut.Win) != 0 && keys.Any(k => "LREDIUXP".IndexOf((char)k) >= 0 || k == 9) ||
                (shortcut.Modifiers & (Shortcut.Ctrl | Shortcut.Alt)) == (Shortcut.Ctrl | Shortcut.Alt) && keys.Contains(46))
                return "这个组合包含常用编辑或系统快捷键，请换一个组合。";
            return null;
        }
        internal static Shortcut FromKeys(IEnumerable<int> pressed)
        {
            var all = pressed.Select(Normalize).Distinct().ToArray();
            var keys = all.Where(k => Modifier(k) == 0).OrderBy(k => k).ToArray();
            return new Shortcut { Modifiers = all.Aggregate(0, (mods, k) => mods | Modifier(k)),
                Key = keys.FirstOrDefault(), AdditionalKeys = keys.Length <= 1 ? null : keys.Skip(1).ToArray() };
        }
        internal static bool Matches(Shortcut shortcut, IEnumerable<int> pressed)
        {
            var current = FromKeys(pressed);
            return shortcut.Same(current);
        }
    }

    // Keep physical identities until release, so releasing left Ctrl does not release held right Ctrl.
    internal sealed class ShortcutChordState
    {
        readonly HashSet<int> down = new HashSet<int>();
        bool latched;
        internal bool Active => latched;
        internal void SynchronizeKeyboard(Func<int,bool> isDown)
        {
            for (int key = 8; key < 255; key++)
            {
                // Generic modifier aliases would remain held after their physical side releases.
                if (key == 16 || key == 17 || key == 18) continue;
                if (!ShortcutKeys.Supported(key) && ShortcutKeys.Modifier(key) == 0) continue;
                if (isDown(key)) down.Add(key); else down.Remove(key);
            }
        }
        internal void Reset() { down.Clear(); latched = false; }
        internal bool Update(Shortcut shortcut, int key, bool pressed)
        {
            bool fresh = pressed ? down.Add(key) : down.Remove(key);
            bool matched = ShortcutKeys.Matches(shortcut, down);
            if (!matched) latched = false;
            if (!pressed || !fresh || !matched || latched) return false;
            latched = true;
            return true;
        }
    }

    internal sealed class ShortcutRecorder
    {
        readonly HashSet<int> selected = new HashSet<int>(), held = new HashSet<int>();
        bool preserveSelection;
        internal ShortcutRecorder(Shortcut initial)
        {
            if (initial == null) return;
            foreach (int key in initial.Keys) selected.Add(key);
            foreach (int key in new[] { 16, 17, 18, 91 })
                if ((initial.Modifiers & ShortcutKeys.Modifier(key)) != 0) selected.Add(key);
        }
        internal int[] Selected => selected.OrderBy(k => k).ToArray();
        internal bool Holding(int key) => held.Any(k => ShortcutKeys.Normalize(k) == key);
        internal Shortcut Candidate => ShortcutKeys.FromKeys(selected);
        internal void Clear() { selected.Clear(); preserveSelection = false; }
        internal void Toggle(int key)
        {
            key = ShortcutKeys.Normalize(key);
            if (!selected.Remove(key)) selected.Add(key);
            preserveSelection = true;
        }
        internal void Input(int key, bool pressed)
        {
            if (!pressed) { held.Remove(key); return; }
            if (held.Contains(key)) return;
            if (held.Count == 0 && !preserveSelection) selected.Clear();
            preserveSelection = false;
            held.Add(key);
            selected.Add(ShortcutKeys.Normalize(key));
        }
        internal void Release() => held.Clear();
    }
}
