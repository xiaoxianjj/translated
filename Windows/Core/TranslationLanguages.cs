using System;
using System.Linq;

namespace Translated
{
    internal enum OutputLanguage
    {
        Auto, SimplifiedChinese, TraditionalChinese, English, Japanese, Korean, French, German,
        Spanish, Portuguese, Russian, Arabic, Hindi, Italian, Thai, Vietnamese, Indonesian,
        Turkish, Dutch, Polish, Ukrainian, Malay
    }

    internal sealed class TranslationLanguage
    {
        internal readonly string Code, Label, PromptName;
        internal readonly OutputLanguage Direction;
        internal bool RightToLeft => Code == "ar";
        internal TranslationLanguage(string code, string label, string prompt, OutputLanguage direction)
        { Code = code; Label = label; PromptName = prompt; Direction = direction; }
        public override string ToString() => Label;
    }

    internal static class TranslationLanguages
    {
        internal static readonly TranslationLanguage[] All = {
            new TranslationLanguage("auto", "自动 · 英语 ↔ 中文", "Auto", OutputLanguage.Auto),
            new TranslationLanguage("zh-Hans", "简体中文", "Simplified Chinese", OutputLanguage.SimplifiedChinese),
            new TranslationLanguage("zh-Hant", "繁體中文", "Traditional Chinese", OutputLanguage.TraditionalChinese),
            new TranslationLanguage("en", "英语 · English", "English", OutputLanguage.English),
            new TranslationLanguage("ja", "日语 · 日本語", "Japanese", OutputLanguage.Japanese),
            new TranslationLanguage("ko", "韩语 · 한국어", "Korean", OutputLanguage.Korean),
            new TranslationLanguage("fr", "法语 · Français", "French", OutputLanguage.French),
            new TranslationLanguage("de", "德语 · Deutsch", "German", OutputLanguage.German),
            new TranslationLanguage("es", "西班牙语 · Español", "Spanish", OutputLanguage.Spanish),
            new TranslationLanguage("pt", "葡萄牙语 · Português", "Portuguese", OutputLanguage.Portuguese),
            new TranslationLanguage("ru", "俄语 · Русский", "Russian", OutputLanguage.Russian),
            new TranslationLanguage("ar", "阿拉伯语 · العربية", "Arabic", OutputLanguage.Arabic),
            new TranslationLanguage("hi", "印地语 · हिन्दी", "Hindi", OutputLanguage.Hindi),
            new TranslationLanguage("it", "意大利语 · Italiano", "Italian", OutputLanguage.Italian),
            new TranslationLanguage("th", "泰语 · ไทย", "Thai", OutputLanguage.Thai),
            new TranslationLanguage("vi", "越南语 · Tiếng Việt", "Vietnamese", OutputLanguage.Vietnamese),
            new TranslationLanguage("id", "印度尼西亚语 · Indonesia", "Indonesian", OutputLanguage.Indonesian),
            new TranslationLanguage("tr", "土耳其语 · Türkçe", "Turkish", OutputLanguage.Turkish),
            new TranslationLanguage("nl", "荷兰语 · Nederlands", "Dutch", OutputLanguage.Dutch),
            new TranslationLanguage("pl", "波兰语 · Polski", "Polish", OutputLanguage.Polish),
            new TranslationLanguage("uk", "乌克兰语 · Українська", "Ukrainian", OutputLanguage.Ukrainian),
            new TranslationLanguage("ms", "马来语 · Melayu", "Malay", OutputLanguage.Malay)
        };
        internal static TranslationLanguage Find(string code) => All.FirstOrDefault(l => l.Code == code) ?? All[0];
        internal static TranslationLanguage Find(OutputLanguage direction) => All.First(l => l.Direction == direction);
    }
}
