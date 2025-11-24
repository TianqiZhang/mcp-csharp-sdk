using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace QuickstartWeatherServer.AbTesting;

/// <summary>
/// Captures the resolved treatment for a specific tool invocation.
/// </summary>
public sealed class ToolInvocationContext
{
    public required string CanonicalName { get; init; }

    public required string Experiment { get; init; }

    public required string Treatment { get; init; }

    public required string ConcreteName { get; init; }

    public required string BucketKey { get; init; }
}

public static class RequestContextAbExtensions
{
    private const string ItemsKey = "ab.tool.invocation";

    public static void SetAbContext<TParams>(this RequestContext<TParams> request, ToolInvocationContext context)
    {
        request.Items[ItemsKey] = context;
    }

    public static ToolInvocationContext? GetAbContext<TParams>(this RequestContext<TParams> request)
    {
        return request.Items.TryGetValue(ItemsKey, out var value) ? value as ToolInvocationContext : null;
    }
}
