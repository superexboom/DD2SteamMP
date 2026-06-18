namespace DD2DebugDemoCore.Diagnostics
{
    public sealed class NullDebugDemoLogger : IDebugDemoLogger
    {
        public static readonly NullDebugDemoLogger Instance = new NullDebugDemoLogger();

        private NullDebugDemoLogger()
        {
        }

        public void Info(string message)
        {
        }

        public void Warning(string message)
        {
        }
    }
}
