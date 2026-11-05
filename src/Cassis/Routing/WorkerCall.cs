using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;

namespace Cassis.Routing;

internal static class WorkerCall
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public static async Task<string> ForwardAsync(int port, string documentPath, JsonRpcRequest request, bool modern, CancellationToken cancellationToken)
    {
        var messageJson = JsonSerializer.Serialize(request);
        var payload = "{\"documentPath\":" + JsonSerializer.Serialize(documentPath)
            + ",\"modern\":" + (modern ? "true" : "false")
            + ",\"message\":" + messageJson + "}";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync($"http://127.0.0.1:{port}/cassis/call", content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return ErrorJson(request, $"The Rhino on port {port} did not accept the call.");
        }

        if (!response.IsSuccessStatusCode && !IsJsonRpcError(body))
        {
            return ErrorJson(request, $"The Rhino on port {port} did not accept the call.");
        }

        return body;
    }

    private static bool IsJsonRpcError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonRpcMessage>(body) is JsonRpcError;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string ErrorJson(JsonRpcRequest request, string detail)
    {
        var error = new JsonRpcError
        {
            Id = request.Id,
            Error = new JsonRpcErrorDetail
            {
                Code = -32000,
                Message = detail,
            },
        };
        return JsonSerializer.Serialize(error);
    }
}
