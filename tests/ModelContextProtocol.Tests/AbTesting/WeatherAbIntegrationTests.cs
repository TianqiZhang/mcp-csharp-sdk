#if !NET472
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using QuickstartWeatherServer.AbTesting;
using QuickstartWeatherServer.Tools;
using System.Linq;

namespace ModelContextProtocol.Tests.AbTesting;

public sealed class WeatherAbIntegrationTests : ClientServerTestBase
{
    public WeatherAbIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureServices(ServiceCollection services, IMcpServerBuilder builder)
    {
        // Wire up the same setup as the sample program, but using a fake HttpClient.
        services.AddSingleton<HttpClient>(sp =>
        {
            var handler = new StubHttpMessageHandler();
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.weather.gov")
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("weather-tool/1.0");
            return client;
        });

        builder.WithTools<WeatherTools>();
        services.AddSingleton<ToolVariantSelector>();
        services.Configure<McpServerOptions>(options =>
        {
            options.Filters.ListToolsFilters.Add(AbTestingFilters.ListTools);
            options.Filters.CallToolFilters.Add(AbTestingFilters.CallTool);
        });
    }

    [Fact]
    public async Task CanonicalNameIsStableAndVariantIsStickyPerSession()
    {
        await using McpClient client = await CreateMcpClientForServer();

        // The list should surface a single canonical name (get_forecast) even though two variants exist.
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var forecast = Assert.Single(tools, t => t.Name == "get_forecast");
        Assert.NotNull(forecast.ProtocolTool.Meta);
        Assert.True(forecast.ProtocolTool.Meta!.ContainsKey("ab_experiment"));
        Assert.True(forecast.ProtocolTool.Meta!.ContainsKey("ab_treatment"));

        // Call the canonical name twice; the treatment should be the same for the session.
        var first = await client.CallToolAsync("get_forecast", new Dictionary<string, object?>
        {
            ["latitude"] = 47.0,
            ["longitude"] = -122.0,
        }, cancellationToken: TestContext.Current.CancellationToken);

        var second = await client.CallToolAsync("get_forecast", new Dictionary<string, object?>
        {
            ["latitude"] = 47.0,
            ["longitude"] = -122.0,
        }, cancellationToken: TestContext.Current.CancellationToken);

        string firstText = first.Content.OfType<TextContentBlock>().Single().Text!;
        string secondText = second.Content.OfType<TextContentBlock>().Single().Text!;

        Assert.Equal(IsDetailed(firstText), IsDetailed(secondText));
    }

    [Fact]
    public async Task ConcreteVariantCanBeForced()
    {
        await using McpClient client = await CreateMcpClientForServer();

        var concise = await client.CallToolAsync("get_forecast__concise", new Dictionary<string, object?>
        {
            ["latitude"] = 47.0,
            ["longitude"] = -122.0,
        }, cancellationToken: TestContext.Current.CancellationToken);

        string text = concise.Content.OfType<TextContentBlock>().Single().Text!;
        Assert.False(IsDetailed(text));
    }

    private static bool IsDetailed(string forecastText) => forecastText.Contains("Temperature:", StringComparison.Ordinal);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri)
            {
                if (uri.AbsolutePath.StartsWith("/points/", StringComparison.OrdinalIgnoreCase))
                {
                    // Return a forecast URL for the supplied point.
                    var json = """
                    {
                      "properties": {
                        "forecast": "https://api.weather.gov/gridpoints/SEW/123,45/forecast"
                      }
                    }
                    """;
                    return Task.FromResult(JsonResponse(json));
                }

                if (uri.AbsolutePath.Contains("/forecast", StringComparison.OrdinalIgnoreCase))
                {
                    // Minimal forecast payload with both detailed and short forecasts.
                    var json = """
                    {
                      "properties": {
                        "periods": [
                          {
                            "name": "Tonight",
                            "temperature": 60,
                            "windSpeed": "5 mph",
                            "windDirection": "NW",
                            "detailedForecast": "Clear with stars.",
                            "shortForecast": "Clear"
                          }
                        ]
                      }
                    }
                    """;
                    return Task.FromResult(JsonResponse(json));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
    }
}
#endif
