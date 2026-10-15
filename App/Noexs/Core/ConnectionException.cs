namespace Noexs.Core;

public class ConnectionException : Exception
{
    public ConnectionException(Exception e) : base(e.Message, e)
    {
    }

    public ConnectionException(string s) : base(s)
    {
    }

    public ConnectionException(string s, Result rc) : base(s) //TODO parse the rc module and desc.
    {
    }

    public ConnectionException(Result rc) : base(rc.ToString()) //TODO parse the rc module and desc
    {
    }
}
