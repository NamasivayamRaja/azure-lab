using System.Text.Json.Serialization;

namespace AzureLab.Assistant;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolErrorCode { InvalidArgument, NotFound, LimitReached }
// Infrastructure failures are deliberately NOT a code here: they abort the request (503), the model never sees them.

public sealed record ToolError(ToolErrorCode Code, string Message);

public sealed record ToolResult<T>(bool Ok, T? Data, ToolError? Error)
{
    public static ToolResult<T> Success(T data) => new(true, data, null);
    public static ToolResult<T> Fail(ToolErrorCode code, string message) => new(false, default, new(code, message));
}