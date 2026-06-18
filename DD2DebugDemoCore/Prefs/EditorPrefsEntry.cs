namespace DD2DebugDemoCore.Prefs
{
    public sealed class EditorPrefsEntry
    {
        public EditorPrefsEntry(int lineNumber, string rawLine, string key, string value)
        {
            LineNumber = lineNumber;
            RawLine = rawLine ?? string.Empty;
            Key = key ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public int LineNumber { get; }
        public string RawLine { get; }
        public string Key { get; }
        public string Value { get; }
    }
}
