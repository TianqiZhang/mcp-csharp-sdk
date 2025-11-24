using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace QuickstartWeatherServer.AbTesting;

/// <summary>
/// Provides list/call filters that turn multiple tool treatments into a single canonical tool for clients.
/// </summary>
public static class AbTestingFilters
{
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListTools =>
        next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);

            var selector = ResolveSelector(request.Services, request.Server.Services);
            if (selector is null || selector.IsEmpty)
            {
                return result;
            }

            var seenCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Tool> rewritten = new(result.Tools.Count);

            foreach (var canonicalName in selector.CanonicalNames)
            {
                if (!selector.TrySelectVariant(request, canonicalName, out var selection))
                {
                    continue;
                }

                seenCanonical.Add(canonicalName);
                rewritten.Add(CloneAsCanonical(selection));
            }

            // Keep any tools that are not part of an A/B group untouched.
            foreach (var tool in result.Tools)
            {
                if (selector.IsVariantName(tool.Name))
                {
                    continue;
                }

                rewritten.Add(tool);
            }

            result.Tools = rewritten;
            return result;
        };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool =>
        next => async (request, cancellationToken) =>
        {
            var selector = ResolveSelector(request.Services, request.Server.Services);
            if (selector is null || selector.IsEmpty || request.Params?.Name is not { } incomingName)
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            if (selector.TryResolveConcrete(incomingName, out var directVariant))
            {
                var context = selector.CreateContext(request, directVariant);
                request.SetAbContext(context);
                request.MatchedPrimitive = directVariant.Tool;
                request.Params.Name = directVariant.Tool.ProtocolTool.Name;
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            if (!selector.TrySelectVariant(request, incomingName, out var selection))
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            request.SetAbContext(selection.Context);
            request.MatchedPrimitive = selection.Variant.Tool;
            request.Params.Name = selection.Variant.Tool.ProtocolTool.Name;

            return await next(request, cancellationToken).ConfigureAwait(false);
        };

    private static Tool CloneAsCanonical(ToolSelection selection)
    {
        var source = selection.Variant.Tool.ProtocolTool;

        // Shallow clone; safe for demo and keeps schema/description aligned with the concrete tool.
        var meta = source.Meta is JsonObject metaObj ? (JsonObject)metaObj.DeepClone() : new JsonObject();
        meta["ab_experiment"] = selection.Context.Experiment;
        meta["ab_treatment"] = selection.Context.Treatment;
        meta["ab_concrete"] = selection.Context.ConcreteName;

        return new Tool
        {
            Name = selection.Context.CanonicalName,
            Description = source.Description,
            Title = source.Title,
            InputSchema = source.InputSchema,
            OutputSchema = source.OutputSchema,
            Annotations = source.Annotations,
            Icons = source.Icons,
            Meta = meta
        };
    }

    private static ToolVariantSelector? ResolveSelector(IServiceProvider? requestServices, IServiceProvider? serverServices) =>
        requestServices?.GetService<ToolVariantSelector>() ??
        serverServices?.GetService<ToolVariantSelector>();
}

public sealed class ToolVariantSelector
{
    private static readonly ConditionalWeakTable<McpServer, BucketKeyHolder> _serverBucketKeys = new();
    private static readonly ConditionalWeakTable<ITransport, BucketKeyHolder> _transportBucketKeys = new();
    private static readonly ConditionalWeakTable<IServiceProvider, BucketKeyHolder> _servicesBucketKeys = new();

    private readonly Dictionary<string, List<ToolVariant>> _byCanonical;
    private readonly Dictionary<string, ToolVariant> _byConcrete;

    public ToolVariantSelector(IEnumerable<McpServerTool> tools)
    {
        _byCanonical = new(StringComparer.OrdinalIgnoreCase);
        _byConcrete = new(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
        {
            var treatment = tool.Metadata.OfType<AbToolTreatmentAttribute>().FirstOrDefault();
            if (treatment is null)
            {
                continue;
            }

            string canonicalName = treatment.CanonicalName ?? tool.ProtocolTool.Name;
            string experiment = string.IsNullOrWhiteSpace(treatment.Experiment) ? canonicalName : treatment.Experiment;

            var variant = new ToolVariant(
                canonicalName,
                experiment,
                treatment.Treatment,
                treatment.Weight,
                tool);

            _byConcrete[tool.ProtocolTool.Name] = variant;

            if (!_byCanonical.TryGetValue(canonicalName, out var list))
            {
                list = [];
                _byCanonical[canonicalName] = list;
            }

            list.Add(variant);
        }
    }

    public bool IsEmpty => _byCanonical.Count == 0;

    public IEnumerable<string> CanonicalNames => _byCanonical.Keys;

    public bool IsVariantName(string name) => _byConcrete.ContainsKey(name);

    public bool TryResolveConcrete(string name, out ToolVariant variant) => _byConcrete.TryGetValue(name, out variant!);

    public bool TrySelectVariant<TParams>(RequestContext<TParams> request, string canonicalName, out ToolSelection selection)
    {
        if (!_byCanonical.TryGetValue(canonicalName, out var variants) || variants.Count == 0)
        {
            selection = default!;
            return false;
        }

        string bucketKey = ComputeBucketKey(request);
        var winner = PickVariant(variants, bucketKey);
        selection = new ToolSelection(winner, CreateContext(request, winner, canonicalName, bucketKey));
        return true;
    }

    public ToolInvocationContext CreateContext<TParams>(RequestContext<TParams> request, ToolVariant variant)
    {
        string bucketKey = ComputeBucketKey(request);
        return CreateContext(request, variant, variant.CanonicalName, bucketKey);
    }

    private static ToolInvocationContext CreateContext<TParams>(RequestContext<TParams> request, ToolVariant variant, string canonicalName, string bucketKey)
    {
        return new ToolInvocationContext
        {
            CanonicalName = canonicalName,
            Experiment = variant.Experiment,
            Treatment = variant.Treatment,
            ConcreteName = variant.Tool.ProtocolTool.Name,
            BucketKey = bucketKey
        };
    }

    private static ToolVariant PickVariant(IReadOnlyList<ToolVariant> variants, string bucketKey)
    {
        double totalWeight = variants.Sum(v => v.Weight);
        double target = ScaleHashToDouble(bucketKey, variants[0].Experiment) % totalWeight;
        double cumulative = 0;

        foreach (var variant in variants)
        {
            cumulative += variant.Weight;
            if (target <= cumulative)
            {
                return variant;
            }
        }

        return variants[^1];
    }

    private static double ScaleHashToDouble(string bucketKey, string experiment)
    {
        string input = $"{experiment}:{bucketKey}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        ulong value = BitConverter.ToUInt64(hash, 0);
        return value / (double)ulong.MaxValue * 1000.0;
    }

    private static string ComputeBucketKey<TParams>(RequestContext<TParams> request)
    {
        if (request.User is ClaimsPrincipal user)
        {
            string? sub = user.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(sub))
            {
                return sub;
            }

            if (!string.IsNullOrEmpty(user.Identity?.Name))
            {
                return user.Identity.Name!;
            }
        }

        if (!string.IsNullOrEmpty(request.Server.SessionId))
        {
            return request.Server.SessionId!;
        }

        if (request.JsonRpcRequest.Context?.RelatedTransport is { } transport)
        {
            return _transportBucketKeys.GetValue(transport, _ => new BucketKeyHolder()).Key;
        }

        if (request.Server.Services is { } services)
        {
            return _servicesBucketKeys.GetValue(services, _ => new BucketKeyHolder()).Key;
        }

        return _serverBucketKeys.GetValue(request.Server, _ => new BucketKeyHolder()).Key;
    }

    private sealed class BucketKeyHolder
    {
        public string Key { get; } = Guid.NewGuid().ToString("N");
    }
}

public readonly record struct ToolVariant(
    string CanonicalName,
    string Experiment,
    string Treatment,
    double Weight,
    McpServerTool Tool);

public readonly record struct ToolSelection(ToolVariant Variant, ToolInvocationContext Context);
