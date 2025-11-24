using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using QuickstartWeatherServer.Tools;
using QuickstartWeatherServer.AbTesting;
using System.Net.Http.Headers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<WeatherTools>();

// Enable the sample A/B routing filter without modifying the SDK.
builder.Services.AddSingleton<ToolVariantSelector>();
builder.Services.Configure<McpServerOptions>(options =>
{
    options.Filters.ListToolsFilters.Add(AbTestingFilters.ListTools);
    options.Filters.CallToolFilters.Add(AbTestingFilters.CallTool);
});

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

using var httpClient = new HttpClient { BaseAddress = new Uri("https://api.weather.gov") };
httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("weather-tool", "1.0"));
builder.Services.AddSingleton(httpClient);

await builder.Build().RunAsync();
