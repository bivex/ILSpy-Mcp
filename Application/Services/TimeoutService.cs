using Microsoft.Extensions.Options;

namespace ILSpy.Mcp.Application.Services;

/// <summary>
/// Service for managing timeouts and cancellation tokens.
/// </summary>
public interface ITimeoutService
{
    /// <summary>
    /// Creates a linked CancellationTokenSource that cancels after the configured timeout.
    /// Caller must dispose the returned source.
    /// </summary>
    CancellationTokenSource CreateLinkedTimeout(CancellationToken cancellationToken = default);
    TimeSpan GetDefaultTimeout();
}

public sealed class TimeoutService : ITimeoutService
{
    private readonly ILSpy.Mcp.Application.Configuration.ILSpyOptions _options;
    private readonly TimeSpan _defaultTimeout;

    public TimeoutService(IOptions<ILSpy.Mcp.Application.Configuration.ILSpyOptions> options)
    {
        _options = options.Value;
        // FIX #16: Cache TimeSpan once instead of recomputing in every catch block
        _defaultTimeout = TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds);
    }

    // FIX #8: Single CTS returned to caller for disposal — no internal linking + leak
    public CancellationTokenSource CreateLinkedTimeout(CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
            return new CancellationTokenSource(_defaultTimeout);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_defaultTimeout);
        return cts;
    }

    public TimeSpan GetDefaultTimeout() => _defaultTimeout;
}
