using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantConnect.Brokerages.Polymarket.Dashboard.Strategies;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest
{
    public class BacktestRunner
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Built-in parameter grids: each strategy has 3 parameter sets.
        /// </summary>
        private static readonly Dictionary<string, List<Dictionary<string, string>>> ParameterGrids = new()
        {
            ["MarketMaking"] = new List<Dictionary<string, string>>
            {
                new() { ["HalfSpread"] = "0.015", ["OrderSize"] = "25" },
                new() { ["HalfSpread"] = "0.025", ["OrderSize"] = "25" },
                new() { ["HalfSpread"] = "0.035", ["OrderSize"] = "50" }
            },
            ["MeanReversion"] = new List<Dictionary<string, string>>
            {
                new() { ["SpreadThreshold"] = "0.02", ["WindowSize"] = "15" },
                new() { ["SpreadThreshold"] = "0.03", ["WindowSize"] = "20" },
                new() { ["SpreadThreshold"] = "0.05", ["WindowSize"] = "30" }
            },
            ["SpreadCapture"] = new List<Dictionary<string, string>>
            {
                new() { ["EdgeOffset"] = "0.003", ["MinSpread"] = "0.015" },
                new() { ["EdgeOffset"] = "0.005", ["MinSpread"] = "0.02" },
                new() { ["EdgeOffset"] = "0.008", ["MinSpread"] = "0.03" }
            },
            ["BtcFollowMM"] = new List<Dictionary<string, string>>
            {
                new() { ["MomentumThreshold"] = "0.001", ["MinCorrelation"] = "0.2" },
                new() { ["MomentumThreshold"] = "0.002", ["MinCorrelation"] = "0.3" },
                new() { ["MomentumThreshold"] = "0.003", ["MinCorrelation"] = "0.4" }
            }
        };

        public BacktestRunner(ILogger logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Runs all 4 strategies × 3 parameter sets = 12 backtests.
        /// </summary>
        public BacktestComparisonResult RunComparison(int days = 30, decimal initialBalance = 10000m,
            string dataRoot = null)
        {
            var totalSw = Stopwatch.StartNew();
            var loader = new HistoricalDataLoader(dataRoot);

            // Load data once
            var end = DateTime.UtcNow;
            var start = end.AddDays(-days);

            _logger.LogInformation("Loading market metadata...");
            var markets = loader.LoadMarketMetadata();
            if (markets.Count == 0)
            {
                _logger.LogWarning("No market data found. Run --download-data first.");
                return new BacktestComparisonResult
                {
                    RunDate = DateTime.UtcNow,
                    DataStartDate = start,
                    DataEndDate = end,
                    Results = new List<BacktestResult>()
                };
            }

            var tokenTickerMap = HistoricalDataLoader.BuildTokenTickerMap(markets);
            var dashboardMarkets = HistoricalDataLoader.ConvertToDashboardMarkets(markets);

            _logger.LogInformation("Building timeline for {Count} tokens over {Days} days...",
                tokenTickerMap.Count, days);
            var timeline = loader.BuildTimeline(tokenTickerMap, start, end);

            _logger.LogInformation("Loading BTC reference data...");
            var btcBars = loader.LoadBtcBars(start, end);

            // Determine actual data date range from timeline
            var dataStart = timeline.Count > 0 ? timeline.First().Time : start;
            var dataEnd = timeline.Count > 0 ? timeline.Last().Time : end;
            var totalBars = timeline.Sum(t => t.TokenBars.Count);

            _logger.LogInformation("Timeline: {Ticks} ticks, {Bars} total bars, BTC: {BtcBars} bars",
                timeline.Count, totalBars, btcBars.Count);

            var results = new List<BacktestResult>();

            // Run each strategy × parameter combination
            foreach (var strategyName in ParameterGrids.Keys)
            {
                var paramSets = ParameterGrids[strategyName];
                foreach (var paramSet in paramSets)
                {
                    var paramStr = string.Join(" ", paramSet.Select(p => $"{p.Key}={p.Value}"));
                    _logger.LogInformation("Running {Strategy} [{Params}]...", strategyName, paramStr);

                    try
                    {
                        var result = RunSingle(strategyName, paramSet, timeline, dashboardMarkets,
                            btcBars, initialBalance);
                        result.Parameters = paramSet;
                        results.Add(result);

                        _logger.LogInformation("  -> PnL: {Pnl:F2}, Sharpe: {Sharpe:F2}, Trades: {Trades}, Elapsed: {Ms:F0}ms",
                            result.Metrics.TotalPnl, result.Metrics.SharpeRatio,
                            result.Metrics.TradeCount, result.ElapsedMs);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to run {Strategy} [{Params}]", strategyName, paramStr);
                    }
                }
            }

            totalSw.Stop();

            return new BacktestComparisonResult
            {
                RunDate = DateTime.UtcNow,
                DataStartDate = dataStart,
                DataEndDate = dataEnd,
                TotalTokens = tokenTickerMap.Count,
                TotalBars = totalBars,
                Results = results,
                TotalElapsedMs = totalSw.Elapsed.TotalMilliseconds
            };
        }

        /// <summary>
        /// Runs a single strategy with given parameters.
        /// </summary>
        public BacktestResult RunSingle(
            string strategyName,
            Dictionary<string, string> parameters,
            List<(DateTime Time, Dictionary<string, HistoricalBar> TokenBars)> timeline,
            List<DashboardMarket> markets,
            List<HistoricalBar> btcBars,
            decimal initialBalance = 10000m)
        {
            var strategy = CreateStrategy(strategyName, out var btcPriceService);
            strategy.Initialize(parameters ?? new Dictionary<string, string>());

            // Deep-copy markets to avoid cross-contamination between runs
            var marketsCopy = CloneMarkets(markets);

            var engine = new BacktestEngine(strategy, initialBalance, btcPriceService);
            return engine.Run(timeline, marketsCopy, btcBars);
        }

        private IDryRunStrategy CreateStrategy(string name, out BtcPriceService btcPriceService)
        {
            btcPriceService = null;

            switch (name)
            {
                case "MarketMaking":
                    return new MarketMakingStrategy();

                case "MeanReversion":
                    return new MeanReversionStrategy();

                case "SpreadCapture":
                    return new SpreadCaptureStrategy();

                case "BtcFollowMM":
                    btcPriceService = new BtcPriceService(NullLogger<BtcPriceService>.Instance);
                    var correlationMonitor = new CorrelationMonitor(btcPriceService);
                    // Create SentimentService with neutral defaults (no HTTP polling)
                    var sentimentService = new SentimentService(NullLogger<SentimentService>.Instance);
                    sentimentService.InjectFearGreed(50);
                    sentimentService.InjectFundingRate(0.0001m);
                    return new BtcFollowMMStrategy(btcPriceService, correlationMonitor, sentimentService);

                default:
                    return new MeanReversionStrategy();
            }
        }

        private static List<DashboardMarket> CloneMarkets(List<DashboardMarket> markets)
        {
            return markets.Select(m => new DashboardMarket
            {
                Question = m.Question,
                Slug = m.Slug,
                ConditionId = m.ConditionId,
                Volume = m.Volume,
                Volume24h = m.Volume24h,
                Liquidity = m.Liquidity,
                EndDate = m.EndDate,
                Category = m.Category,
                Active = m.Active,
                Closed = m.Closed,
                Resolved = m.Resolved,
                Outcome = m.Outcome,
                Tokens = m.Tokens?.Select(t => new DashboardToken
                {
                    TokenId = t.TokenId,
                    Outcome = t.Outcome,
                    Price = t.Price
                }).ToList() ?? new List<DashboardToken>()
            }).ToList();
        }
    }
}
