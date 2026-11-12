using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Translated
{
    internal sealed class Chunk
    {
        public string Text, Prefix, Suffix, Before, After;
        public int Offset;
    }

    internal static class Passage
    {
        public const int Maximum = 50000;
        const int ChunkLimit = 6000, ContextLimit = 600;

        internal static int Count(string text) => new StringInfo(text ?? "").LengthInTextElements;
        internal static bool HasLetter(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (char.IsLetter(text, i)) return true;
            return false;
        }
        internal static void Validate(string text)
        {
            if (Count(text) > Maximum) throw new InvalidOperationException("每次最多翻译 50,000 个字符，请缩短选区。");
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请先选择或输入要翻译的文字。");
            if (!HasLetter(text)) throw new InvalidOperationException("请选择包含文字的内容。");
        }

        public static List<Chunk> Split(string text)
        {
            Validate(text);
            var offsets = StringInfo.ParseCombiningCharacters(text);
            int Offset(int n) => n == offsets.Length ? text.Length : offsets[n];
            string Slice(int a, int b) => text.Substring(Offset(a), Offset(b) - Offset(a));
            var chunks = new List<Chunk>();
            for (int start = 0; start < offsets.Length;)
            {
                int end = Math.Min(start + ChunkLimit, offsets.Length);
                if (end < offsets.Length)
                {
                    int space = 0, sentence = 0;
                    for (int i = end - 1; i >= start + ChunkLimit / 3; --i)
                    {
                        char c = text[offsets[i]];
                        if (c == '\n' || (c == '\r' && (Offset(i + 1) == text.Length || text[Offset(i + 1)] != '\n')))
                        { end = i + 1; sentence = space = 0; break; }
                        if (sentence == 0 && ".!?。！？".IndexOf(c) >= 0) sentence = i + 1;
                        if (space == 0 && char.IsWhiteSpace(c)) space = i + 1;
                    }
                    if (sentence != 0) end = sentence;
                    else if (space != 0) end = space;
                    if (end < offsets.Length && text[Offset(end)] == '\n' && text[Offset(end) - 1] == '\r') --end;
                }
                string raw = Slice(start, end);
                string body = raw.Trim();
                int leading = raw.Length - raw.TrimStart().Length;
                chunks.Add(new Chunk { Text = body, Offset = Offset(start) + leading, Prefix = raw.Substring(0, leading),
                    Suffix = raw.Substring(leading + body.Length), Before = Slice(Math.Max(0, start - ContextLimit), start),
                    After = Slice(end, Math.Min(offsets.Length, end + ContextLimit)) });
                start = end;
            }
            return chunks;
        }
    }
}
