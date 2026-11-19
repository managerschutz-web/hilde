using System.Text.Json;
using System.Text.Json.Serialization;
using Hilde.Core;
using Serilog;

namespace Hilde.Desktop;

/// <summary>
/// Nachrichtenbrücke WebView2 ⇄ .NET. Anfrage: {id, method, params}; Antwort: {id, ok, result | error}.
/// Ereignisse an die Oberfläche: {event, data}.
/// </summary>
public sealed class UiBridge
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _handlers = new(StringComparer.Ordinal);
    private Action<string>? _post;
    private readonly ILogger _log = Log.ForContext<UiBridge>();

    public void RegisterAsync(string method, Func<JsonElement, Task<object?>> handler) => _handlers[method] = handler;

    public void Register(string method, Func<JsonElement, object?> handler) =>
        _handlers[method] = p => Task.FromResult(handler(p));

    public void Attach(Action<string> post) => _post = post;

    public void Emit(string evt, object? data)
    {
        if (_post == null) return;
        _post(JsonSerializer.Serialize(new { @event = evt, data }, Json));
    }

    public async void OnMessage(string json)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            var method = root.GetProperty("method").GetString() ?? "";
            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            if (!_handlers.TryGetValue(method, out var handler))
            {
                Reply(id, false, null, "Unbekannte Methode: " + method, null);
                return;
            }
            var result = await handler(p);
            Reply(id, true, result, null, null);
        }
        catch (HildeException ex)
        {
            Reply(id, false, null, ex.Error.Message, ex.Error.Id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "UI-Anfrage fehlgeschlagen");
            Reply(id, false, null, HildeErrors.Unexpected.Message, HildeErrors.Unexpected.Id);
        }
    }

    private void Reply(long id, bool ok, object? result, string? error, string? errorId)
    {
        _post?.Invoke(JsonSerializer.Serialize(new { id, ok, result, error, errorId }, Json));
    }
}

internal static class JsonExt
{
    public static T To<T>(this JsonElement e) => e.Deserialize<T>(UiBridge.Json)!;
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool Bool(this JsonElement e, string name, bool def = false) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
    public static long Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
