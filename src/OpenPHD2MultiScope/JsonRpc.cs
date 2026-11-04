using System.Buffers;
using System.Text.Json;

namespace TianWen.OpenPHD2MultiScope;

/// <summary>
/// One JSON-RPC request line as PHD2 receives it. PHD2 is lenient: <c>jsonrpc</c> is optional,
/// <c>params</c> may be an array (positional) or an object (named), and a request without an
/// <c>id</c> is a notification that gets no reply.
/// </summary>
/// <remarks>Owns the parsed document; dispose it once the request is handled.</remarks>
internal sealed class RpcRequest : IDisposable
{
    private readonly JsonDocument _document;

    private RpcRequest(JsonDocument document, string method, JsonElement? id, JsonElement? @params)
    {
        _document = document;
        Method = method;
        Id = id;
        Params = @params;
    }

    public string Method { get; }

    /// <summary>The request's id exactly as sent (number or string), echoed back verbatim. Null for a notification.</summary>
    public JsonElement? Id { get; }

    public JsonElement? Params { get; }

    public bool IsNotification => Id is null;

    /// <summary>
    /// Parses one line. Never throws on client input: a line that is not a JSON-RPC request
    /// yields <see langword="false"/> and an error to send back (with the id, when one could be read).
    /// </summary>
    public static bool TryParse(string line, out RpcRequest? request, out RpcError? error, out JsonElement? idForError)
    {
        request = null;
        error = null;
        idForError = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            error = RpcError.ParseError;
            return false;
        }

        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            document.Dispose();
            error = RpcError.InvalidRequest;
            return false;
        }

        JsonElement? id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? idElement.Clone()
            : null;

        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind is not JsonValueKind.String
            || methodElement.GetString() is not { Length: > 0 } method)
        {
            document.Dispose();
            error = RpcError.InvalidRequest;
            idForError = id;
            return false;
        }

        JsonElement? @params = root.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind is not JsonValueKind.Null
            ? paramsElement
            : null;

        request = new RpcRequest(document, method, id, @params);
        return true;
    }

    /// <summary>
    /// A parameter by its PHD2 name (object form) or its position (array form), whichever the client used.
    /// </summary>
    public bool TryGetParam(string name, int position, out JsonElement value)
    {
        value = default;
        switch (Params)
        {
            case { ValueKind: JsonValueKind.Object } obj:
                return obj.TryGetProperty(name, out value);
            case { ValueKind: JsonValueKind.Array } array when position < array.GetArrayLength():
                value = array[position];
                return true;
            default:
                return false;
        }
    }

    public void Dispose() => _document.Dispose();
}

internal sealed record RpcError(int Code, string Message)
{
    public static readonly RpcError ParseError = new(-32700, "Parse error");
    public static readonly RpcError InvalidRequest = new(-32600, "Invalid request");
    public static RpcError MethodNotFound(string method) => new(-32601, $"Method {method} is not supported by openphd2-multiscope");
    public static RpcError InvalidParams(string message) => new(-32602, message);

    /// <summary>PHD2's own code for a command it refused (it uses 1 for most failures).</summary>
    public static RpcError Failed(string message) => new(1, message);
}

/// <summary>Writes the response lines PHD2 sends, each terminated by CRLF.</summary>
internal static class RpcResponse
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();

    public static byte[] Result(JsonElement? id, Action<Utf8JsonWriter> writeResult) => Write(id, writer =>
    {
        writer.WritePropertyName("result");
        writeResult(writer);
    });

    public static byte[] Result(JsonElement? id, int result) => Result(id, w => w.WriteNumberValue(result));

    public static byte[] Error(JsonElement? id, RpcError error) => Write(id, writer =>
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", error.Code);
        writer.WriteString("message", error.Message);
        writer.WriteEndObject();
    });

    private static byte[] Write(JsonElement? id, Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writeBody(writer);
            writer.WritePropertyName("id");
            if (id is { } idValue)
            {
                idValue.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }
            writer.WriteEndObject();
        }
        buffer.Write(CrLf);
        return buffer.WrittenSpan.ToArray();
    }
}
