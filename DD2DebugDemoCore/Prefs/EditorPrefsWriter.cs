using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DD2DebugDemoCore.Prefs
{
    public sealed class EditorPrefsWriter
    {
        private readonly List<EditorPrefsEntry> _entries = new List<EditorPrefsEntry>();

        public IReadOnlyList<EditorPrefsEntry> Entries => _entries;

        public void Add(string key, string value)
        {
            _entries.Add(new EditorPrefsEntry(_entries.Count + 1, string.Empty, key, value ?? string.Empty));
        }

        public void AddCsv(string key, IEnumerable<string> values)
        {
            Add(key, string.Join(",", (values ?? Enumerable.Empty<string>()).Select(EditorPrefsDocument.CleanSlotValue).ToArray()));
        }

        public string[] ToLines()
        {
            return _entries.Select(entry => entry.Key + "-" + entry.Value).ToArray();
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            foreach (string line in ToLines())
            {
                builder.AppendLine(line);
            }

            return builder.ToString();
        }
    }
}
