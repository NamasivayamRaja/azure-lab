using Microsoft.Extensions.AI;

namespace AzureLabs.Api.Assistant;

public sealed record ScriptedCall(string Name, IDictionary<string, object?> Args);

/// <summary>One model turn: tool calls, final text, a delay (hang), a thrown exception (model down / 429), or truncated text.</summary>
public sealed record ScriptedTurn(
    IReadOnlyList<ScriptedCall>? Calls = null, string? Text = null, TimeSpan? Delay = null,
    Exception? Throw = null, bool Truncated = false);

/// <summary>DEV ONLY: a scripted model. Once the script runs out, the last turn repeats (endless loops).</summary>
public sealed class ScriptedChatClient(IReadOnlyList<ScriptedTurn> script) : IChatClient
{
    private int _turn;
    public int Turns => _turn;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var turn = script[Math.Min(_turn, script.Count - 1)];
        var n = ++_turn;

        if (turn.Delay is { } delay) await Task.Delay(delay, cancellationToken);
        if (turn.Throw is not null) throw turn.Throw;

        if (turn.Calls is { Count: > 0 } calls)
        {
            var contents = calls
                .Select((c, i) => (AIContent)new FunctionCallContent($"call_{n}_{i}", c.Name, c.Args))
                .ToList();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)) { FinishReason = ChatFinishReason.ToolCalls };
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, turn.Text ?? ""))
        {
            FinishReason = turn.Truncated ? ChatFinishReason.Length : ChatFinishReason.Stop
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
