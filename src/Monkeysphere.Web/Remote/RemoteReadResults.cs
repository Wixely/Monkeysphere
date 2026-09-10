using System.Data.Common;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// Shared error mapping for read tools, matching the write, export and query_records adapters.
/// Without it an unresolvable domain or a foreign identifier escapes as an unhandled exception and
/// the MCP SDK reports a generic "An error occurred invoking '&lt;tool&gt;'": the call still fails
/// closed and discloses nothing, but a client cannot tell a bad selector from a server fault.
/// </summary>
public static class RemoteReadResults
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<CallToolResult> RunAsync<T>(IHttpContextAccessor accessor, Func<Task<T>> read)
    {
        try
        {
            return Success(await read().ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or
            RecordCommandNotFoundException or DbException or IOException or OperationCanceledException)
        {
            return Failure(accessor, exception);
        }
    }

    public static CallToolResult Run<T>(IHttpContextAccessor accessor, Func<T> read)
    {
        try
        {
            return Success(read());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or
            RecordCommandNotFoundException or DbException or IOException or OperationCanceledException)
        {
            return Failure(accessor, exception);
        }
    }

    private static CallToolResult Success<T>(T value)
    {
        // A null result means "not found", which get_record and get_record_type use for a record
        // that does not exist in the selected domain. The SDK represented that as an empty content
        // list, and cross-domain isolation tests assert exactly that, so keep it byte-for-byte
        // rather than serializing a "null" content block.
        if (value is null) return new() { Content = [] };

        // Serialized exactly as the SDK previously produced it, so the success envelope is unchanged.
        JsonElement json = JsonSerializer.SerializeToElement(value, JsonOptions);
        return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }

    private static CallToolResult Failure(IHttpContextAccessor accessor, Exception exception)
    {
        string code = exception switch
        {
            UnauthorizedAccessException => "permission_denied",
            RecordCommandNotFoundException => "not_found",
            DomainValidationException => "validation_failed",
            _ => "temporarily_unavailable",
        };
        string message = code == "temporarily_unavailable"
            ? "The read could not complete. Retry the request."
            : exception.Message[..Math.Min(exception.Message.Length, 1024)];
        string correlation = accessor.HttpContext?.TraceIdentifier ?? "";
        JsonElement json = JsonSerializer.SerializeToElement(
            new { error = new RemoteWriteError(code, message, correlation[..Math.Min(correlation.Length, 128)]) }, JsonOptions);
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
