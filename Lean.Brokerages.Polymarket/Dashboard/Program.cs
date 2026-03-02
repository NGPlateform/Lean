using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Brokerages.Polymarket;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Brokerages.Polymarket.Dashboard.Hubs;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;
using QuantConnect.Brokerages.Polymarket.Dashboard.Strategies;

// Check for --download-data CLI mode
var downloadMode = args.Contains("--download-data");
var days = 30;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--days" && int.TryParse(args[i + 1], out var d))
        days = d;
}

if (downloadMode)
{
    // CLI download mode — no web server, just download and exit
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<DataDownloadService>();

    Console.WriteLine($"=== Polymarket Crypto Data Downloader ===");
    Console.WriteLine($"Downloading {days} days of historical data...");
    Console.WriteLine();

    using var downloader = new DataDownloadService(logger);
    var result = await downloader.DownloadAllAsync(days);

    Console.WriteLine();
    Console.WriteLine($"=== Download Complete ===");
    Console.WriteLine($"Markets found:      {result.MarketsFound}");
    Console.WriteLine($"Tokens processed:   {result.TokensProcessed}");
    Console.WriteLine($"Tokens with data:   {result.TokensWithData}");
    Console.WriteLine($"Total minute bars:  {result.TotalBars}");
    Console.WriteLine($"Order book snaps:   {result.OrderBookSnapshots}");
    Console.WriteLine($"Errors:             {result.Errors}");
    Console.WriteLine($"Elapsed:            {result.ElapsedSeconds}s");

    if (!string.IsNullOrEmpty(result.Error))
        Console.WriteLine($"Error:              {result.Error}");

    return;
}

// Normal web server mode
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddNewtonsoftJson();
builder.Services.AddSignalR()
    .AddNewtonsoftJsonProtocol();

// Load Polymarket credentials from configuration
var config = builder.Configuration;
var credentials = new PolymarketCredentials(
    apiKey: config["Polymarket:ApiKey"] ?? "",
    apiSecret: config["Polymarket:ApiSecret"] ?? "",
    privateKey: config["Polymarket:PrivateKey"] ?? "",
    passphrase: config["Polymarket:Passphrase"] ?? "");

builder.Services.AddSingleton(credentials);

var symbolMapper = new PolymarketSymbolMapper();
builder.Services.AddSingleton(symbolMapper);

builder.Services.AddSingleton<TradingService>();
builder.Services.AddSingleton<DataDownloadService>();

// Register MarketDataService as both singleton and hosted service so it can be injected
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketDataService>());

// DryRun configuration
var dryRunSettings = new DryRunSettings
{
    Enabled = string.Equals(config["DryRun:Enabled"], "true", System.StringComparison.OrdinalIgnoreCase),
    InitialBalance = decimal.TryParse(config["DryRun:InitialBalance"], out var ib) ? ib : 10000m,
    TickIntervalMs = int.TryParse(config["DryRun:TickIntervalMs"], out var ti) ? ti : 5000,
    StrategyName = config["DryRun:StrategyName"] ?? "MeanReversion",
    AutoSubscribeTopN = int.TryParse(config["DryRun:AutoSubscribeTopN"], out var astn) ? astn : 10
};
foreach (var child in config.GetSection("DryRun:StrategyParameters").GetChildren())
{
    dryRunSettings.StrategyParameters[child.Key] = child.Value ?? "";
}
builder.Services.AddSingleton(dryRunSettings);

if (dryRunSettings.Enabled)
{
    // Register strategy based on config
    IDryRunStrategy strategy = dryRunSettings.StrategyName?.ToLower() switch
    {
        "spreadcapture" => new SpreadCaptureStrategy(),
        "marketmaking" or "mm" => new MarketMakingStrategy(),
        _ => new MeanReversionStrategy()
    };
    builder.Services.AddSingleton(strategy);

    // Register DryRunEngine as both singleton and hosted service
    builder.Services.AddSingleton<DryRunEngine>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DryRunEngine>());
}

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<TradingHub>("/hub/trading");

app.Urls.Add("http://localhost:5010");

app.Run();
