using System.Text.Json;

namespace Hilde.Core.Engines;

/// <summary>Wird im isolierten Engine-Prozess ausgeführt.</summary>
public interface IEngineHandler : IDisposable
{
    string EngineVersion { get; }
    Task LoadAsync(CancellationToken ct);
    EngineHealth Health();
    Task<object?> HandleAsync(string method, JsonElement? parameters, CancellationToken ct);
}
