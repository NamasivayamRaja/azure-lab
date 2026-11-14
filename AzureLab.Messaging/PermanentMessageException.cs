public class PermanentMessageException(string reason, string description) : Exception(description)
{
    public string Reason { get; } = reason;
}