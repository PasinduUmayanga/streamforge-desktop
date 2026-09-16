namespace StreamForge.Core.Exceptions;

public class StreamForgeException : Exception
{
    public StreamForgeException(string message)
        : base(message)
    {
    }

    public StreamForgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
