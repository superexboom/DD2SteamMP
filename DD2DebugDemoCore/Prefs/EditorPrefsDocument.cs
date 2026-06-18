using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DD2DebugDemoCore.Prefs
{
    public sealed class EditorPrefsDocument
    {
        public const string NoneValue = "none";

        private readonly List<EditorPrefsEntry> _entries;
        private readonly List<string> _warnings;

        private EditorPrefsDocument(List<EditorPrefsEntry> entries, List<string> warnings)
        {
            _entries = entries;
            _warnings = warnings;
        }

        public IReadOnlyList<EditorPrefsEntry> Entries => _entries;
        public IReadOnlyList<string> Warnings => _warnings;

        public static EditorPrefsDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Editor prefs path is empty.", nameof(path));
            }

            return Parse(File.ReadAllLines(path));
        }

        public static EditorPrefsDocument Parse(IEnumerable<string> lines)
        {
            List<EditorPrefsEntry> entries = new List<EditorPrefsEntry>();
            List<string> warnings = new List<string>();
            if (lines == null)
            {
                return new EditorPrefsDocument(entries, warnings);
            }

            int lineNumber = 0;
            foreach (string rawLine in lines)
            {
                lineNumber++;
                string withoutComment = RemoveComment(rawLine).Trim();
                if (withoutComment.Length == 0)
                {
                    continue;
                }

                int separatorIndex = withoutComment.IndexOf('-');
                if (separatorIndex < 0)
                {
                    warnings.Add("Line " + lineNumber + " has no key/value separator: " + rawLine);
                    continue;
                }

                string key = RemoveWhitespace(withoutComment.Substring(0, separatorIndex));
                if (key.Length == 0)
                {
                    warnings.Add("Line " + lineNumber + " has an empty key: " + rawLine);
                    continue;
                }

                string value = withoutComment.Substring(separatorIndex + 1).Trim();
                entries.Add(new EditorPrefsEntry(lineNumber, rawLine ?? string.Empty, key, value));
            }

            foreach (IGrouping<string, EditorPrefsEntry> duplicate in entries
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                warnings.Add("Key \"" + duplicate.Key + "\" appears on lines " +
                    string.Join(", ", duplicate.Select(entry => entry.LineNumber.ToString()).ToArray()) +
                    "; the last value is used for legacy compatibility.");
            }

            return new EditorPrefsDocument(entries, warnings);
        }

        public bool TryGetLastValue(string key, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                EditorPrefsEntry entry = _entries[i];
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }

            return false;
        }

        public string GetLastValueOrDefault(string key, string fallback = "")
        {
            return TryGetLastValue(key, out string value) ? value : fallback;
        }

        public string[] GetLastCsvValues(string key)
        {
            return TryGetLastValue(key, out string value) ? SplitCsv(value) : Array.Empty<string>();
        }

        public IReadOnlyList<EditorPrefsEntry> GetEntries(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return Array.Empty<EditorPrefsEntry>();
            }

            return _entries
                .Where(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public Dictionary<string, string> ToLastValueDictionary(StringComparer comparer = null)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(comparer ?? StringComparer.OrdinalIgnoreCase);
            foreach (EditorPrefsEntry entry in _entries)
            {
                result[entry.Key] = entry.Value;
            }

            return result;
        }

        public static string[] SplitCsv(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value.Split(',').Select(CleanSlotValue).ToArray();
        }

        public static string CleanSlotValue(string value)
        {
            string cleaned = string.IsNullOrWhiteSpace(value) ? NoneValue : value.Trim();
            return string.Equals(cleaned, NoneValue, StringComparison.OrdinalIgnoreCase) ? NoneValue : cleaned;
        }

        public static string RemoveComment(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return string.Empty;
            }

            int hashIndex = line.IndexOf('#');
            int slashIndex = line.IndexOf("//", StringComparison.Ordinal);
            int cutIndex = -1;
            if (hashIndex >= 0)
            {
                cutIndex = hashIndex;
            }

            if (slashIndex >= 0 && (cutIndex < 0 || slashIndex < cutIndex))
            {
                cutIndex = slashIndex;
            }

            return cutIndex >= 0 ? line.Substring(0, cutIndex) : line;
        }

        private static string RemoveWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            char[] buffer = new char[value.Length];
            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                {
                    buffer[count++] = value[i];
                }
            }

            return new string(buffer, 0, count);
        }
    }
}
