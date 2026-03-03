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
using QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest;
using QuantConnect.Brokerages.Polymarket.Dashboard.Strategies;

// Check for CLI modes
var downloadMode = args.Contains("--download-data");
var downloadBtcMode = args.Contains("--download-btc");
var backtestMode = args.Contains("--backtest");
var days = 30;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--days" && int.TryParse(args[i + 1], out var d))
        days = d;
}

if (downloadBtcMode && !downloadMode)
{
    // BTC-only download mode
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<DataDownloadService>();

    Console.WriteLine($"=== BTC/USDT Reference Data Downloader (Binance) ===");
    Console.WriteLine($"Downloading {days} days of 5-min klines...");
    Console.WriteLine();

    using var downloader = new DataDownloadService(logger);
    var btcBars = await downloader.DownloadBtcKlinesAsync(days);

    Console.WriteLine();
    Console.WriteLine($"=== Download Complete ===");
    Console.WriteLine($"BTC 10-min bars:    {btcBars}");

    return;
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
    Console.WriteLine($"BTC 10-min bars:    {result.BtcBars}");
    Console.WriteLine($"Order book snaps:   {result.OrderBookSnapshots}");
    Console.WriteLine($"Errors:             {result.Errors}");
    Console.WriteLine($"Elapsed:            {result.ElapsedSeconds}s");

    if (!string.IsNullOrEmpty(result.Error))
        Console.WriteLine($"Error:              {result.Error}");

    return;
}

if (backtestMode)
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<BacktestRunner>();

    Console.WriteLine($"=== Backtest Comparison ({days} days) ===");
    Console.WriteLine();

    var runner = new BacktestRunner(logger);
    var result = runner.RunComparison(days);

    Console.WriteLine();
    PrintBacktestResults(result);
    return;
}

static void PrintBacktestResults(BacktestComparisonResult result)
{
    Console.WriteLine($"=== Backtest Results ===");
    Console.WriteLine($"Data: {result.DataStartDate:yyyy-MM-dd} to {result.DataEndDate:yyyy-MM-dd} | " +
        $"Tokens: {result.TotalTokens} | Bars: {result.TotalBars} | " +
        $"Elapsed: {result.TotalElapsedMs / 1000:F1}s");
    Console.WriteLine();

    // Header
    Console.WriteLine("{0,-16}| {1,-22}| {2,10} | {3,7} | {4,10} | {5,8} | {6,6}",
        "Strategy", "Params", "PnL", "Sharpe", "MaxDD", "WinRate", "Fills");
    Console.WriteLine(new string('-', 90));

    foreach (var r in result.Results)
    {
        var paramStr = string.Join(" ", r.Parameters.Select(p =>
        {
            var key = p.Key.Length > 2 ? p.Key.Substring(0, 2).ToUpper() : p.Key.ToUpper();
            return $"{key}={p.Value}";
        }));
        if (paramStr.Length > 20) paramStr = paramStr.Substring(0, 20);

        var m = r.Metrics;
        Console.WriteLine("{0,-16}| {1,-22}| {2,10} | {3,7:F2} | {4,10} | {5,7:F1}% | {6,6}",
            r.StrategyName,
            paramStr,
            m.TotalPnl >= 0 ? $"+${m.TotalPnl:F2}" : $"-${Math.Abs(m.TotalPnl):F2}",
            m.SharpeRatio,
            $"-${m.MaxDrawdown:F2}",
            m.WinRate,
            m.FillCount);
    }

    Console.WriteLine(new string('-', 90));
    Console.WriteLine($"Total elapsed: {result.TotalElapsedMs / 1000:F1}s");
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

// Register BTC price service and correlation monitor
builder.Services.AddSingleton<BtcPriceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BtcPriceService>());
builder.Services.AddSingleton<CorrelationMonitor>();

// Register sentiment service (Fear & Greed + Binance funding rate)
builder.Services.AddSingleton<SentimentService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SentimentService>());

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
    // Note: BtcFollowMM needs DI services, resolved after build
    var strategyName = dryRunSettings.StrategyName?.ToLower();
    if (strategyName == "btcfollowmm" || strategyName == "btcmm")
    {
        builder.Services.AddSingleton<IDryRunStrategy>(sp =>
        {
            var strategy = new BtcFollowMMStrategy(
                sp.GetRequiredService<BtcPriceService>(),
                sp.GetRequiredService<CorrelationMonitor>(),
                sp.GetRequiredService<SentimentService>());
            strategy.Initialize(dryRunSettings.StrategyParameters);
            return strategy;
        });
    }
    else
    {
        IDryRunStrategy strategy = strategyName switch
        {
            "spreadcapture" => new SpreadCaptureStrategy(),
            "marketmaking" or "mm" => new MarketMakingStrategy(),
            _ => new MeanReversionStrategy()
        };
        builder.Services.AddSingleton(strategy);
    }

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
