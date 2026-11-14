using System.Text;

namespace AzureLabs.Api.Assistant;

/// <summary>DEV ONLY: records the raw request/response bodies exchanged with Ollama.</summary>
public sealed class BodyTapHandler : DelegatingHandler
{
    public string? LastRequest { get; private set; }
    public string? LastResponse { get; private set; }

    public BodyTapHandler() : base(new SocketsHttpHandler()) { }
    public string? LastRequestHeaders { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        LastRequestHeaders = $"{request.Method} {request.RequestUri} HTTP/{request.Version}\n{request.Headers}{request.Content?.Headers}";
        var response = await base.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        LastResponse = body;
        response.Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson");
        return response;
    }
}