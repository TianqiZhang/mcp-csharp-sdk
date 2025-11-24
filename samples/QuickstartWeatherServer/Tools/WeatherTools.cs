using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using QuickstartWeatherServer.AbTesting;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace QuickstartWeatherServer.Tools;

[McpServerToolType]
public sealed class WeatherTools
{
    [McpServerTool, Description("Get weather alerts for a US state.")]
    public static async Task<string> GetAlerts(
        HttpClient client,
        [Description("The US state to get alerts for. Use the 2 letter abbreviation for the state (e.g. NY).")] string state)
    {
        using var jsonDocument = await client.ReadJsonDocumentAsync($"/alerts/active/area/{state}");
        var jsonElement = jsonDocument.RootElement;
        var alerts = jsonElement.GetProperty("features").EnumerateArray();

        if (!alerts.Any())
        {
            return "No active alerts for this state.";
        }

        return string.Join("\n--\n", alerts.Select(alert =>
        {
            JsonElement properties = alert.GetProperty("properties");
            return $"""
                    Event: {properties.GetProperty("event").GetString()}
                    Area: {properties.GetProperty("areaDesc").GetString()}
                    Severity: {properties.GetProperty("severity").GetString()}
                    Description: {properties.GetProperty("description").GetString()}
                    Instruction: {properties.GetProperty("instruction").GetString()}
                    """;
        }));
    }

    [McpServerTool(Name = "get_forecast__detailed"), Description("Get a detailed weather forecast for a location.")]
    [AbToolTreatment("forecast_exp", "detailed", canonicalName: "get_forecast", weight: 0.5)]
    public static async Task<string> GetForecastDetailed(
        HttpClient client,
        RequestContext<CallToolRequestParams> request,
        [Description("Latitude of the location.")] double latitude,
        [Description("Longitude of the location.")] double longitude)
    {
        var pointUrl = string.Create(CultureInfo.InvariantCulture, $"/points/{latitude},{longitude}");
        using var locationDocument = await client.ReadJsonDocumentAsync(pointUrl);
        var forecastUrl = locationDocument.RootElement.GetProperty("properties").GetProperty("forecast").GetString()
            ?? throw new McpException($"No forecast URL provided by {client.BaseAddress}points/{latitude},{longitude}");

        using var forecastDocument = await client.ReadJsonDocumentAsync(forecastUrl);
        var periods = forecastDocument.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray();

        LogTreatment(request);

        return string.Join("\n---\n", periods.Select(period => $"""
            {period.GetProperty("name").GetString()}
            Temperature: {period.GetProperty("temperature").GetInt32()}°F
            Wind: {period.GetProperty("windSpeed").GetString()} {period.GetProperty("windDirection").GetString()}
            Forecast: {period.GetProperty("detailedForecast").GetString()}
            """));
    }

    [McpServerTool(Name = "get_forecast__concise"), Description("Get a concise weather forecast for a location.")]
    [AbToolTreatment("forecast_exp", "concise", canonicalName: "get_forecast", weight: 0.5)]
    public static async Task<string> GetForecastConcise(
        HttpClient client,
        RequestContext<CallToolRequestParams> request,
        [Description("Latitude of the location.")] double latitude,
        [Description("Longitude of the location.")] double longitude)
    {
        var pointUrl = string.Create(CultureInfo.InvariantCulture, $"/points/{latitude},{longitude}");
        using var locationDocument = await client.ReadJsonDocumentAsync(pointUrl);
        var forecastUrl = locationDocument.RootElement.GetProperty("properties").GetProperty("forecast").GetString()
            ?? throw new McpException($"No forecast URL provided by {client.BaseAddress}points/{latitude},{longitude}");

        using var forecastDocument = await client.ReadJsonDocumentAsync(forecastUrl);
        var periods = forecastDocument.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray();

        LogTreatment(request);

        return string.Join("\n---\n", periods.Select(period => $"""
            {period.GetProperty("name").GetString()}
            Temp: {period.GetProperty("temperature").GetInt32()}°F
            Forecast: {period.GetProperty("shortForecast").GetString()}
            """));
    }

    private static void LogTreatment(RequestContext<CallToolRequestParams> request)
    {
        var ab = request.GetAbContext();
        if (ab is null)
        {
            return;
        }

        // Visible in console logging; shows which variant was selected and the bucket key used.
        Console.WriteLine($"[AB] canonical={ab.CanonicalName} experiment={ab.Experiment} treatment={ab.Treatment} concrete={ab.ConcreteName} bucket={ab.BucketKey}");
    }
}
