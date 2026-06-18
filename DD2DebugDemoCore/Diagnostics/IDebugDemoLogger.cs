namespace DD2DebugDemoCore.Diagnostics
{
    public interface IDebugDemoLogger
    {
        void Info(string message);
        void Warning(string message);
    }
}
