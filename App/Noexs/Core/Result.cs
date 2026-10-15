namespace Noexs.Core;

public sealed class Result
{
    public Result(int mod, int desc)
    {
        Module = mod;
        Desc = desc;
    }

    public int Module { get; }

    public int Desc { get; }

    public bool Failed => Module != 0 || Desc != 0;

    public bool Succeeded => Module == 0 && Desc == 0;

    public string Message()
    {
        return "Module:" + Module + " Code:" + Desc;
    }

    public override string ToString()
    {
        return "Result{" +
               "mod=" + Module +
               ", desc=" + Desc +
               '}';
    }

    public static Result ValueOf(int rc)
    {
        return new Result(ModuleOf(rc), DescriptionOf(rc));
    }

    public static bool IsFailure(int rc)
    {
        return rc != 0;
    }

    public static bool IsSuccess(int rc)
    {
        return rc == 0;
    }

    public static int ModuleOf(int rc)
    {
        return rc & 0x1FF;
    }

    public static int DescriptionOf(int rc)
    {
        return (rc >> 9) & 0x1FFF;
    }
}
