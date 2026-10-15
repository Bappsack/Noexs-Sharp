namespace Noexs.Core;

public sealed class DebuggerStatus
{
    public static readonly DebuggerStatus STOPPED = new(0, "Shutting down");
    public static readonly DebuggerStatus RUNNING = new(1, "Running");
    public static readonly DebuggerStatus PAUSED = new(2, "Paused");

    private static readonly DebuggerStatus[] Values = [STOPPED, RUNNING, PAUSED];

    private DebuggerStatus(int id, string str)
    {
        Id = id;
        Status = str;
    }

    public int Id { get; }

    public string Status { get; }

    public static DebuggerStatus? ForId(int status)
    {
        foreach (var s in Values)
        {
            if (s.Id == status)
            {
                return s;
            }
        }
        return null;
    }
}
