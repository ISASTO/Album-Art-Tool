using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AlbumArtTool.Management
{
    internal static class TitleCleanup
    {
        private const string Separator = @"(?:\s*[.:：\-–—]\s*|\s+)";
        private static readonly Regex credit = new Regex(@"(?<![\p{L}\p{N}])(?:featuring|feat|ft)" + Separator + @"(?=\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex bracketCredit = new Regex(@"^(?:featuring|feat|ft)" + Separator + @"(?=\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex version = new Regex(@"\b(?:remix|mix|live|remaster(?:ed)?|acoustic|instrumental|edit|version|demo|mono|stereo|extended|bonus|radio|re-record(?:ed|ing)?|sped|slowed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string result = CleanPart(value, false, 0, out bool changed);
            if (!changed) return value;
            return string.IsNullOrWhiteSpace(result) ? value : result;
        }
        private static string CleanPart(string value, bool bracketed, int depth, out bool changed)
        {
            changed = false;
            if (depth > 20) return value;
            if (bracketed && bracketCredit.IsMatch(value.TrimStart())) { changed = true; return ""; }
            var text = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char close = Closing(value[i]);
                if (close == '\0') { text.Append(value[i]); continue; }
                int end = EndGroup(value, i);
                if (end < 0) { text.Append(value.Substring(i)); break; }
                string inner = CleanPart(value.Substring(i + 1, end - i - 1), true, depth + 1, out bool innerChanged);
                changed |= innerChanged;
                if (!string.IsNullOrWhiteSpace(inner)) text.Append(value[i]).Append(inner).Append(close);
                i = end;
            }
            string result = text.ToString(); int level = 0;
            foreach (Match match in credit.Matches(result))
            {
                level = 0;
                for (int i = 0; i < match.Index; i++) { if (Closing(result[i]) != '\0') level++; else if (IsClosing(result[i])) level = Math.Max(0, level - 1); }
                if (level != 0 || string.IsNullOrWhiteSpace(result.Substring(0, match.Index).Trim(' ', '-', '–', '—', ',', ':'))) continue;
                int suffix = VersionSuffix(result, match.Index + match.Length);
                changed = true;
                result = result.Substring(0, match.Index).TrimEnd(' ', '-', '–', '—', ',', ':') + (suffix < result.Length ? " " + result.Substring(suffix).Trim() : "");
                break;
            }
            if (!changed) return value;
            result = Regex.Replace(result, @"\s+", " ").Trim();
            return result.TrimEnd(' ', '-', '–', '—', ',', ':');
        }
        private static int VersionSuffix(string value, int start)
        {
            int suffix = value.Length, end = value.Length;
            while (end > start)
            {
                while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
                int found = -1;
                for (int i = start; i < end; i++) if (Closing(value[i]) != '\0' && EndGroup(value, i) == end - 1) { found = i; break; }
                if (found < 0 || !version.IsMatch(value.Substring(found + 1, end - found - 2))) break;
                suffix = found; end = found;
            }
            var tail = Regex.Match(value.Substring(start), @"\s+[-–—]\s+.+$");
            if (tail.Success && version.IsMatch(tail.Value)) suffix = Math.Min(suffix, start + tail.Index + 1);
            return suffix;
        }
        private static int EndGroup(string value, int start)
        {
            var stack = new System.Collections.Generic.Stack<char>(); stack.Push(Closing(value[start]));
            for (int i = start + 1; i < value.Length; i++)
            {
                char close = Closing(value[i]);
                if (close != '\0') stack.Push(close);
                else if (stack.Count > 0 && value[i] == stack.Peek()) { stack.Pop(); if (stack.Count == 0) return i; }
            }
            return -1;
        }
        private static char Closing(char value)
        {
            switch (value) { case '(': return ')'; case '[': return ']'; case '{': return '}'; case '（': return '）'; case '【': return '】'; default: return '\0'; }
        }
        private static bool IsClosing(char value) => value == ')' || value == ']' || value == '}' || value == '）' || value == '】';
    }
}
