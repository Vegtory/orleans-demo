namespace App.Api.Observability;

/// <summary>
/// Ambient flag used to stop the tracing system from tracing its own calls. The
/// reporter wraps its <c>IClusterCallRecorderGrain.Append</c> call in
/// <see cref="Suppress"/> so that call is not itself recorded. Registered as a
/// singleton; the flag flows with the async context via <see cref="AsyncLocal{T}"/>.
/// </summary>
public sealed class CallTraceSuppression
{
    private readonly AsyncLocal<bool> _suppressed = new();

    public bool IsSuppressed => _suppressed.Value;

    public IDisposable Suppress()
    {
        var previous = _suppressed.Value;
        _suppressed.Value = true;
        return new Reset(() => _suppressed.Value = previous);
    }

    private sealed class Reset(Action reset) : IDisposable
    {
        public void Dispose() => reset();
    }
}
