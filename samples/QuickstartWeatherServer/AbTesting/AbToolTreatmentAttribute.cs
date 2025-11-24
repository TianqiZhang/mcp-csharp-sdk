using System.Diagnostics.CodeAnalysis;

namespace QuickstartWeatherServer.AbTesting;

/// <summary>
/// Marks an MCP tool method as a specific treatment within an experiment.
/// </summary>
/// <param name="experiment">Logical experiment name; defaults to the canonical name when omitted.</param>
/// <param name="treatment">The treatment/variant identifier (e.g., control, new-ui).</param>
/// <param name="weight">Relative traffic weight for selection. Default is 1.0.</param>
/// <param name="canonicalName">
/// Stable name that clients should call. If omitted, falls back to the tool's own name.
/// Multiple treatments with the same canonical name are considered part of the same experiment.
/// </param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AbToolTreatmentAttribute(
    string experiment,
    string treatment,
    double weight = 1.0,
    string? canonicalName = null) : Attribute
{
    public string Experiment { get; } = experiment;

    public string Treatment { get; } = treatment;

    public double Weight { get; } = weight > 0 ? weight : 1.0;

    public string? CanonicalName { get; } = canonicalName;
}
