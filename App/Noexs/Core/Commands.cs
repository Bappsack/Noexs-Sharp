namespace Noexs.Core;

public static class Commands
{
    public const int COMMAND_STATUS = 0x01;

    public const int COMMAND_POKE8 = 0x02;
    public const int COMMAND_POKE16 = 0x03;
    public const int COMMAND_POKE32 = 0x04;
    public const int COMMAND_POKE64 = 0x05;

    public const int COMMAND_READ = 0x06;
    public const int COMMAND_WRITE = 0x07;
    public const int COMMAND_CONTINUE = 0x08;
    public const int COMMAND_PAUSE = 0x09;
    public const int COMMAND_ATTACH = 0x0A;
    public const int COMMAND_DETATCH = 0x0B;
    public const int COMMAND_QUERY_MEMORY = 0x0C;
    public const int COMMAND_QUERY_MEMORY_MULTI = 0x0D;
    public const int COMMAND_CURRENT_PID = 0x0E;
    public const int COMMAND_GET_ATTACHED_PID = 0x0F;
    public const int COMMAND_GET_PIDS = 0x10;
    public const int COMMAND_GET_TITLEID = 0x11;
    public const int COMMAND_DISCONNECT = 0x12;
    public const int COMMAND_READ_MULTI = 0x13;
    public const int COMMAND_SET_BREAKPOINT = 0x14;

    /// <summary>The sysmodule's recent log.</summary>
    public const int COMMAND_GET_LOG = 0x15;

    /// <summary>Name of an installed game from the console.</summary>
    public const int COMMAND_GET_TITLE_NAME = 0x16;
}
