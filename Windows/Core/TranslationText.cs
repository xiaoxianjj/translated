using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Translated
{
    internal static class LanguageRouting
    {
        // Unambiguous common English words also seen as Dutch/French by short-input detectors.
        // Other Latin words still go through multilingual detection, including accented words.
        static readonly HashSet<string> ShortEnglish = new HashSet<string>(
            ("a an the is are was were will would should could and or not yes no i you he she we they " +
             "this these that those my your words word hello hi world application applications apply " +
             "open close save copy paste test text select translate translated translating settings " +
             "apple orange read write file files good morning thanks thank please language windows " +
             "with without from into about use work works working go run home ready right left").Split(' '),
            StringComparer.OrdinalIgnoreCase);

        internal static string SourceHint(string text)
        {
            string word = text.Trim().TrimEnd('.', '!', '?', ',', ';', ':');
            return word.Length <= 16 && ShortEnglish.Contains(word) ? "English" : null;
        }
    }

    // Verbatim spans are located in the original passage, so a chunk boundary inside a code
    // fence or a URL cannot expose part of it to the model or insert a joining space into it.
    internal sealed class VerbatimSpans
    {
        static readonly Regex Pattern = new Regex(
            @"(?ms)^(?:[ \t]{0,3})(?<fence>`{3,}|~{3,})[^\r\n]*(?:\r\n|\n|\r).*?(?:^[ \t]{0,3}\k<fence>[ \t]*(?=\r?$)|\z)|(?<tick>`+) [^\r\n]*? \k<tick>|https?://[^\s<>""`]+|\r?\n[ \t]*(?:\r?\n)+",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        readonly Match[] spans;
        readonly string tokenPrefix = "⟦T" + Guid.NewGuid().ToString("N").Substring(0,8) + "-";
        internal VerbatimSpans(string original) { spans = Pattern.Matches(original).Cast<Match>().ToArray(); }
        internal bool Inside(int offset) => spans.Any(m => m.Index < offset && offset < m.Index + m.Length);

        internal string Protect(Chunk chunk, out Dictionary<string,string> literals, out bool needsTranslation)
        {
            literals = new Dictionary<string,string>();
            var encoded = new StringBuilder();
            int cursor = 0;
            needsTranslation = false;
            foreach (var m in spans)
            {
                int begin = Math.Max(0,m.Index - chunk.Offset);
                int end = Math.Min(chunk.Text.Length,m.Index + m.Length - chunk.Offset);
                if (begin >= end) continue;
                string prose = chunk.Text.Substring(cursor,begin-cursor);
                needsTranslation |= Passage.HasLetter(prose);
                encoded.Append(prose);
                string marker = tokenPrefix + literals.Count + "⟧";
                literals.Add(marker,chunk.Text.Substring(begin,end-begin));
                encoded.Append(marker);
                cursor = end;
            }
            string tail = chunk.Text.Substring(cursor);
            needsTranslation |= Passage.HasLetter(tail);
            return encoded.Append(tail).ToString();
        }

        internal static string Restore(string text, Dictionary<string,string> literals)
        {
            foreach (var pair in literals)
            {
                int at = text.IndexOf(pair.Key,StringComparison.Ordinal);
                if (at < 0 || text.IndexOf(pair.Key,at+pair.Key.Length,StringComparison.Ordinal) >= 0)
                    throw new InvalidOperationException("服务更改了受保护的代码、链接或段落，未交付不完整的译文。");
                text = text.Replace(pair.Key,pair.Value);
            }
            return text;
        }
    }
}
