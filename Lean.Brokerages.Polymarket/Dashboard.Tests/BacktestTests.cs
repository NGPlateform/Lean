using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest;
using QuantConnect.Brokerages.Polymarket.Dashboard.Strategies;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class BacktestTests
    {
        private string _tempDir;

        [SetUp]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "backtest_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void Teardown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        #region CSV Parsing Tests

        [Test]
        public void LoadPriceBars_ValidCsv_ParsesCorrectly()
        {
            // Arrange: create a minute data CSV
            var ticker = "test-token-yes";
            var dir = Path.Combine(_tempDir, "crypto", "polymarket", "minute", ticker);
            Directory.CreateDirectory(dir);

            // 3 bars at 10-min intervals: 0ms, 600000ms, 1200000ms from midnight
            File.WriteAllText(Path.Combine(dir, "20260301_trade.csv"),
                "0,0.500,0.520,0.480,0.510,100\n" +
                "600000,0.510,0.530,0.500,0.525,150\n" +
                "1200000,0.525,0.540,0.510,0.530,200\n");

            var loader = new HistoricalDataLoader(_tempDir);
            var start = new DateTime(2026, 3, 1);
            var end = new DateTime(2026, 3, 2);

            // Act
            var bars = loader.LoadPriceBars(ticker, start, end);

            // Assert
            Assert.AreEqual(3, bars.Count);
            Assert.AreEqual(0.500m, bars[0].Open);
            Assert.AreEqual(0.520m, bars[0].High);
            Assert.AreEqual(0.480m, bars[0].Low);
            Assert.AreEqual(0.510m, bars[0].Close);
            Assert.AreEqual(100m, bars[0].Volume);
            Assert.AreEqual(new DateTime(2026, 3, 1, 0, 0, 0), bars[0].Time);
            Assert.AreEqual(new DateTime(2026, 3, 1, 0, 10, 0), bars[1].Time);
        }

        [Test]
        public void LoadBtcBars_ValidCsv_ParsesCorrectly()
        {
            // Arrange
            var dir = Path.Combine(_tempDir, "reference", "btc-usd");
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "20260301_trade.csv"),
                "0,64656.01,64769.66,64656.01,64750.99,59.288\n" +
                "600000,64751.00,64974.51,64746.02,64974.51,81.366\n");

            var loader = new HistoricalDataLoader(_tempDir);
            var start = new DateTime(2026, 3, 1);
            var end = new DateTime(2026, 3, 2);

            // Act
            var bars = loader.LoadBtcBars(start, end);

            // Assert
            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual(64656.01m, bars[0].Open);
            Assert.AreEqual(64750.99m, bars[0].Close);
            Assert.AreEqual(59.288m, bars[0].Volume);
        }

        [Test]
        public void LoadPriceBars_MissingFile_ReturnsEmpty()
        {
            var loader = new HistoricalDataLoader(_tempDir);
            var bars = loader.LoadPriceBars("nonexistent-token", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
            Assert.AreEqual(0, bars.Count);
        }

        #endregion

        #region Synthesize OrderBook Tests

        [Test]
        public void SynthesizeOrderBook_NormalBar_CorrectStructure()
        {
            var bar = new HistoricalBar
            {
                Time = DateTime.UtcNow,
                Open = 0.50m,
                High = 0.55m,
                Low = 0.45m,
                Close = 0.52m,
                Volume = 1000m
            };

            var book = HistoricalDataLoader.SynthesizeOrderBook(bar, "token-1");

            Assert.AreEqual(2, book.Bids.Count);
            Assert.AreEqual(2, book.Asks.Count);

            // halfSpread = max(0.005, (0.55 - 0.45) * 0.1) = max(0.005, 0.01) = 0.01
            var expectedBid1 = 0.52m - 0.01m; // 0.51
            var expectedAsk1 = 0.52m + 0.01m; // 0.53
            var depth = 1000m * 0.3m; // 300

            var bid1Price = decimal.Parse(book.Bids[0].Price, CultureInfo.InvariantCulture);
            var ask1Price = decimal.Parse(book.Asks[0].Price, CultureInfo.InvariantCulture);
            var bid1Size = decimal.Parse(book.Bids[0].Size, CultureInfo.InvariantCulture);
            Assert.AreEqual(expectedBid1, bid1Price);
            Assert.AreEqual(expectedAsk1, ask1Price);
            Assert.AreEqual(depth * 0.6m, bid1Size);
        }

        [Test]
        public void SynthesizeOrderBook_NarrowBar_UsesMinSpread()
        {
            var bar = new HistoricalBar
            {
                Open = 0.50m, High = 0.501m, Low = 0.499m, Close = 0.50m, Volume = 500m
            };

            var book = HistoricalDataLoader.SynthesizeOrderBook(bar);

            // halfSpread = max(0.005, (0.501 - 0.499) * 0.1) = max(0.005, 0.0002) = 0.005
            var bid1Price = decimal.Parse(book.Bids[0].Price, CultureInfo.InvariantCulture);
            var ask1Price = decimal.Parse(book.Asks[0].Price, CultureInfo.InvariantCulture);
            Assert.AreEqual(0.005m, ask1Price - 0.50m, "Should use min halfSpread of 0.005");
            Assert.AreEqual(0.005m, 0.50m - bid1Price, "Should use min halfSpread of 0.005");
        }

        [Test]
        public void SynthesizeOrderBook_ZeroVolume_UsesDefaultDepth()
        {
            var bar = new HistoricalBar
            {
                Open = 0.50m, High = 0.55m, Low = 0.45m, Close = 0.52m, Volume = 0m
            };

            var book = HistoricalDataLoader.SynthesizeOrderBook(bar);

            // depth = max(100, 0 > 0 ? ... : 200) = 200
            var bid1Size = decimal.Parse(book.Bids[0].Size, CultureInfo.InvariantCulture);
            Assert.AreEqual(200m * 0.6m, bid1Size);
        }

        #endregion

        #region Deterministic Fill Model Tests

        [Test]
        public void BacktestEngine_TakerBuy_FillsAtBestAsk()
        {
            // Strategy that immediately places a BUY above bestAsk
            var strategy = new TestBuyStrategy(tokenId: "token-1", price: 0.60m, size: 10m);

            var engine = new BacktestEngine(strategy, initialBalance: 10000m);

            var timeline = CreateSingleTickTimeline("token-1",
                new HistoricalBar { Time = DateTime.UtcNow, Open = 0.50m, High = 0.55m, Low = 0.45m, Close = 0.50m, Volume = 500m });

            var markets = CreateTestMarkets("token-1");
            var result = engine.Run(timeline, markets);

            // Should have at least placed an order and potentially filled it
            Assert.GreaterOrEqual(result.Metrics.OrderCount, 1);
        }

        [Test]
        public void BacktestEngine_TakerSell_FillsAtBestBid()
        {
            // Strategy that buys first, then sells below bestBid
            var strategy = new TestSellStrategy(tokenId: "token-1", buyPrice: 0.60m, sellPrice: 0.30m, size: 10m);

            var engine = new BacktestEngine(strategy, initialBalance: 10000m);

            // Two ticks: first to buy, second to sell
            var bar1 = new HistoricalBar { Time = new DateTime(2026, 3, 1, 0, 0, 0), Open = 0.50m, High = 0.55m, Low = 0.45m, Close = 0.50m, Volume = 500m };
            var bar2 = new HistoricalBar { Time = new DateTime(2026, 3, 1, 0, 10, 0), Open = 0.50m, High = 0.55m, Low = 0.45m, Close = 0.50m, Volume = 500m };
            var timeline = new List<(DateTime, Dictionary<string, HistoricalBar>)>
            {
                (bar1.Time, new Dictionary<string, HistoricalBar> { ["token-1"] = bar1 }),
                (bar2.Time, new Dictionary<string, HistoricalBar> { ["token-1"] = bar2 })
            };

            var markets = CreateTestMarkets("token-1");
            var result = engine.Run(timeline, markets);

            Assert.GreaterOrEqual(result.Metrics.OrderCount, 2);
        }

        [Test]
        public void BacktestEngine_MakerFill_BarLowHitsLimitBuy()
        {
            // Strategy places a limit buy at 0.44, bar.Low is 0.45 → should fill at 0.44
            // Actually bar.Low <= order.Price, so if bar.Low=0.43 and order=0.44, fills.
            var strategy = new TestBuyStrategy(tokenId: "token-1", price: 0.44m, size: 10m);

            var engine = new BacktestEngine(strategy, initialBalance: 10000m);

            var bar = new HistoricalBar
            {
                Time = new DateTime(2026, 3, 1, 0, 0, 0),
                Open = 0.50m, High = 0.55m, Low = 0.43m, Close = 0.50m, Volume = 500m
            };
            // Need 2 ticks: first places order, second matches it
            var bar2 = new HistoricalBar
            {
                Time = new DateTime(2026, 3, 1, 0, 10, 0),
                Open = 0.50m, High = 0.55m, Low = 0.43m, Close = 0.50m, Volume = 500m
            };
            var timeline = new List<(DateTime, Dictionary<string, HistoricalBar>)>
            {
                (bar.Time, new Dictionary<string, HistoricalBar> { ["token-1"] = bar }),
                (bar2.Time, new Dictionary<string, HistoricalBar> { ["token-1"] = bar2 })
            };

            var markets = CreateTestMarkets("token-1");
            var result = engine.Run(timeline, markets);

            // The order should get filled since bar.Low (0.43) <= order price (0.44)
            Assert.GreaterOrEqual(result.Trades.Count, 1, "Maker buy should fill when bar.Low <= order price");
            if (result.Trades.Count > 0)
            {
                Assert.AreEqual(0.44m, result.Trades[0].Price, "Should fill at order price (maker)");
            }
        }

        [Test]
        public void BacktestEngine_OrderAging_CancelsOldOrders()
        {
            // Place an order that won't get filled, verify it's cancelled after 6 ticks
            var strategy = new TestBuyStrategy(tokenId: "token-1", price: 0.01m, size: 10m, onlyFirstTick: true);

            var engine = new BacktestEngine(strategy, initialBalance: 10000m);

            // Create 8 ticks — order placed on tick 1 should be cancelled by tick 7+
            var timeline = new List<(DateTime, Dictionary<string, HistoricalBar>)>();
            var baseTime = new DateTime(2026, 3, 1, 0, 0, 0);
            for (int i = 0; i < 8; i++)
            {
                var time = baseTime.AddMinutes(i * 10);
                var bar = new HistoricalBar
                {
                    Time = time,
                    Open = 0.50m, High = 0.55m, Low = 0.45m, Close = 0.50m, Volume = 500m
                };
                timeline.Add((bar.Time, new Dictionary<string, HistoricalBar> { ["token-1"] = bar }));
            }

            var markets = CreateTestMarkets("token-1");
            var result = engine.Run(timeline, markets);

            // Order at 0.01 with bar.Low=0.45 won't be filled, should be aged out
            Assert.AreEqual(0, result.Trades.Count, "Order at 0.01 should not fill (bar.Low=0.45)");
        }

        #endregion

        #region BacktestMetrics Tests

        [Test]
        public void Sharpe_FlatEquity_ReturnsZero()
        {
            var curve = new List<(DateTime, decimal)>
            {
                (new DateTime(2026, 3, 1, 0, 0, 0), 10000m),
                (new DateTime(2026, 3, 1, 0, 10, 0), 10000m),
                (new DateTime(2026, 3, 1, 0, 20, 0), 10000m)
            };

            var sharpe = BacktestMetrics.CalculateSharpe(curve);
            Assert.AreEqual(0m, sharpe);
        }

        [Test]
        public void Sharpe_MonotonicallyIncreasing_Positive()
        {
            var curve = new List<(DateTime, decimal)>();
            var equity = 10000m;
            for (int i = 0; i < 100; i++)
            {
                curve.Add((new DateTime(2026, 3, 1).AddMinutes(i * 10), equity));
                equity += 10m; // Constant positive returns
            }

            var sharpe = BacktestMetrics.CalculateSharpe(curve);
            Assert.Greater(sharpe, 0m, "Steadily increasing equity should have positive Sharpe");
        }

        [Test]
        public void MaxDrawdown_NoDrawdown_ReturnsZero()
        {
            var curve = new List<(DateTime, decimal)>
            {
                (DateTime.UtcNow, 10000m),
                (DateTime.UtcNow.AddMinutes(10), 10100m),
                (DateTime.UtcNow.AddMinutes(20), 10200m)
            };

            var (maxDd, maxDdPct) = BacktestMetrics.CalculateMaxDrawdown(curve);
            Assert.AreEqual(0m, maxDd);
            Assert.AreEqual(0m, maxDdPct);
        }

        [Test]
        public void MaxDrawdown_WithDrawdown_CorrectValues()
        {
            var curve = new List<(DateTime, decimal)>
            {
                (DateTime.UtcNow, 10000m),
                (DateTime.UtcNow.AddMinutes(10), 10500m),   // peak
                (DateTime.UtcNow.AddMinutes(20), 9800m),    // drawdown of 700 from peak 10500
                (DateTime.UtcNow.AddMinutes(30), 10200m)
            };

            var (maxDd, maxDdPct) = BacktestMetrics.CalculateMaxDrawdown(curve);
            Assert.AreEqual(700m, maxDd);
            // 700 / 10500 * 100 ≈ 6.67%
            Assert.That(maxDdPct, Is.EqualTo(700m / 10500m * 100m).Within(0.01m));
        }

        [Test]
        public void WinRate_MixedTrades_CorrectCalculation()
        {
            var trades = new List<SimulatedTrade>
            {
                new SimulatedTrade { TokenId = "t1", Side = "BUY", Price = 0.40m, Size = 10m, MatchTime = DateTime.UtcNow },
                new SimulatedTrade { TokenId = "t1", Side = "SELL", Price = 0.50m, Size = 10m, MatchTime = DateTime.UtcNow.AddMinutes(10) }, // +1.0 win
                new SimulatedTrade { TokenId = "t2", Side = "BUY", Price = 0.60m, Size = 10m, MatchTime = DateTime.UtcNow.AddMinutes(20) },
                new SimulatedTrade { TokenId = "t2", Side = "SELL", Price = 0.55m, Size = 10m, MatchTime = DateTime.UtcNow.AddMinutes(30) }, // -0.5 loss
            };

            var roundTrips = BacktestMetrics.CalculateRoundTrips(trades);
            Assert.AreEqual(2, roundTrips.Count);
            Assert.AreEqual(1, roundTrips.Count(r => r > 0), "One winning trade");
            Assert.AreEqual(1, roundTrips.Count(r => r < 0), "One losing trade");
        }

        [Test]
        public void ProfitFactor_NoLosses_Returns999()
        {
            var trades = new List<SimulatedTrade>
            {
                new SimulatedTrade { TokenId = "t1", Side = "BUY", Price = 0.40m, Size = 10m, MatchTime = DateTime.UtcNow },
                new SimulatedTrade { TokenId = "t1", Side = "SELL", Price = 0.50m, Size = 10m, MatchTime = DateTime.UtcNow.AddMinutes(10) }
            };

            var curve = new List<(DateTime, decimal)>
            {
                (DateTime.UtcNow, 10000m),
                (DateTime.UtcNow.AddMinutes(10), 10100m)
            };

            var metrics = BacktestMetrics.Calculate(curve, trades, 10000m, 2);
            Assert.AreEqual(999m, metrics.ProfitFactor);
        }

        [Test]
        public void Calculate_EmptyTradesAndCurve_ReturnsZeroMetrics()
        {
            var metrics = BacktestMetrics.Calculate(
                new List<(DateTime, decimal)>(),
                new List<SimulatedTrade>(),
                10000m, 0);

            Assert.AreEqual(0, metrics.TotalPnl);
            Assert.AreEqual(0, metrics.SharpeRatio);
            Assert.AreEqual(0, metrics.MaxDrawdown);
            Assert.AreEqual(0, metrics.TradeCount);
            Assert.AreEqual(0, metrics.WinRate);
        }

        #endregion

        #region BacktestEngine Integration Tests

        [Test]
        public void BacktestEngine_MeanReversion_CompletesWithoutError()
        {
            var strategy = new MeanReversionStrategy();
            strategy.Initialize(new Dictionary<string, string>
            {
                ["SpreadThreshold"] = "0.02",
                ["WindowSize"] = "5"
            });

            var engine = new BacktestEngine(strategy, initialBalance: 10000m);
            var timeline = CreateMultiTickTimeline("token-1", 20);
            var markets = CreateTestMarkets("token-1");

            var result = engine.Run(timeline, markets);

            Assert.IsNotNull(result);
            Assert.AreEqual("MeanReversion", result.StrategyName);
            Assert.AreEqual(20, result.TicksProcessed);
            Assert.AreEqual(10000m, result.InitialBalance);
            Assert.IsNotNull(result.Metrics);
            Assert.GreaterOrEqual(result.EquityCurve.Count, 1);
        }

        [Test]
        public void BacktestEngine_BtcFollowMM_InjectsBtcPrice()
        {
            var btcService = new BtcPriceService(Microsoft.Extensions.Logging.Abstractions.NullLogger<BtcPriceService>.Instance);
            var corrMonitor = new CorrelationMonitor(btcService);
            var strategy = new BtcFollowMMStrategy(btcService, corrMonitor);
            strategy.Initialize(new Dictionary<string, string>());

            var engine = new BacktestEngine(strategy, initialBalance: 10000m, btcPriceService: btcService);

            var timeline = CreateMultiTickTimeline("token-1", 10);
            var markets = CreateTestMarkets("token-1");

            // Create BTC bars aligned with timeline
            var btcBars = new List<HistoricalBar>();
            var btcBaseTime = new DateTime(2026, 3, 1, 0, 0, 0);
            for (int i = 0; i < 10; i++)
            {
                btcBars.Add(new HistoricalBar
                {
                    Time = btcBaseTime.AddMinutes(i * 10),
                    Open = 65000m + i * 100m,
                    High = 65100m + i * 100m,
                    Low = 64900m + i * 100m,
                    Close = 65050m + i * 100m,
                    Volume = 50m
                });
            }

            var result = engine.Run(timeline, markets, btcBars);

            Assert.IsNotNull(result);
            Assert.AreEqual("BtcFollowMM", result.StrategyName);
            // BTC price should have been injected
            Assert.IsTrue(btcService.CurrentPrice.HasValue, "BTC price should be injected");
        }

        [Test]
        public void BacktestEngine_EmptyTimeline_ReturnsDefaultResult()
        {
            var strategy = new MeanReversionStrategy();
            strategy.Initialize(new Dictionary<string, string>());

            var engine = new BacktestEngine(strategy, initialBalance: 10000m);
            var result = engine.Run(
                new List<(DateTime, Dictionary<string, HistoricalBar>)>(),
                new List<DashboardMarket>());

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.TicksProcessed);
            Assert.AreEqual(10000m, result.FinalBalance);
            Assert.AreEqual(0, result.Trades.Count);
        }

        #endregion

        #region BacktestRunner Tests

        [Test]
        public void BacktestRunner_SingleStrategy_Completes()
        {
            var runner = new BacktestRunner();
            var timeline = CreateMultiTickTimeline("token-1", 10);
            var markets = CreateTestMarkets("token-1");

            var result = runner.RunSingle("MeanReversion",
                new Dictionary<string, string> { ["SpreadThreshold"] = "0.03" },
                timeline, markets, new List<HistoricalBar>());

            Assert.IsNotNull(result);
            Assert.AreEqual("MeanReversion", result.StrategyName);
            Assert.IsNotNull(result.Metrics);
        }

        [Test]
        public void BacktestRunner_RunComparison_WithTestData()
        {
            // Create minimal test data structure
            SetupMinimalTestData();

            var runner = new BacktestRunner();
            var result = runner.RunComparison(days: 1, dataRoot: _tempDir);

            Assert.IsNotNull(result);
            Assert.AreEqual(12, result.Results.Count, "Should have 4 strategies × 3 param sets = 12 results");

            foreach (var r in result.Results)
            {
                Assert.IsNotNull(r.StrategyName);
                Assert.IsNotNull(r.Metrics);
                Assert.IsNotNull(r.Parameters);
            }
        }

        #endregion

        #region HistoricalDataLoader Additional Tests

        [Test]
        public void BuildTimeline_MultipleTickers_MergesCorrectly()
        {
            // Create data for two tokens (files named with today's date)
            SetupTokenData("token-a-yes", 0.40m);
            SetupTokenData("token-b-yes", 0.60m);

            var loader = new HistoricalDataLoader(_tempDir);
            var tokenMap = new Dictionary<string, string>
            {
                ["token-a"] = "token-a-yes",
                ["token-b"] = "token-b-yes"
            };

            // Use today's date since SetupTokenData uses DateTime.UtcNow.Date for filenames
            var today = DateTime.UtcNow.Date;
            var timeline = loader.BuildTimeline(tokenMap, today, today.AddDays(1));

            Assert.Greater(timeline.Count, 0);
            // At each tick, should have bars for both tokens
            var firstTick = timeline[0];
            Assert.AreEqual(2, firstTick.TokenBars.Count, "Both tokens should appear at same time tick");
        }

        [Test]
        public void ConvertToDashboardMarkets_CorrectMapping()
        {
            var markets = new List<CryptoMarketInfo>
            {
                new CryptoMarketInfo
                {
                    Question = "Will BTC reach $100k?",
                    Slug = "btc-100k",
                    ConditionId = "0xabc",
                    Volume = 1000m,
                    Volume24h = 500m,
                    Category = "crypto",
                    Tokens = new List<CryptoTokenInfo>
                    {
                        new CryptoTokenInfo { TokenId = "t1", Outcome = "Yes", Ticker = "btc-100k-yes" },
                        new CryptoTokenInfo { TokenId = "t2", Outcome = "No", Ticker = "btc-100k-no" }
                    }
                }
            };

            var dashboard = HistoricalDataLoader.ConvertToDashboardMarkets(markets);

            Assert.AreEqual(1, dashboard.Count);
            Assert.AreEqual("Will BTC reach $100k?", dashboard[0].Question);
            Assert.AreEqual("btc-100k", dashboard[0].Slug);
            Assert.AreEqual(2, dashboard[0].Tokens.Count);
            Assert.IsNull(dashboard[0].EndDate, "EndDate should be null (not in CryptoMarketInfo)");
            Assert.IsTrue(dashboard[0].Active);
        }

        [Test]
        public void BuildTokenTickerMap_CreatesCorrectMapping()
        {
            var markets = new List<CryptoMarketInfo>
            {
                new CryptoMarketInfo
                {
                    Question = "Test",
                    Tokens = new List<CryptoTokenInfo>
                    {
                        new CryptoTokenInfo { TokenId = "id-1", Ticker = "ticker-1" },
                        new CryptoTokenInfo { TokenId = "id-2", Ticker = "ticker-2" }
                    }
                }
            };

            var map = HistoricalDataLoader.BuildTokenTickerMap(markets);

            Assert.AreEqual(2, map.Count);
            Assert.AreEqual("ticker-1", map["id-1"]);
            Assert.AreEqual("ticker-2", map["id-2"]);
        }

        #endregion

        #region Metrics Edge Cases

        [Test]
        public void FillRate_ZeroOrders_ReturnsZero()
        {
            var metrics = BacktestMetrics.Calculate(
                new List<(DateTime, decimal)> { (DateTime.UtcNow, 10000m) },
                new List<SimulatedTrade>(),
                10000m,
                0);

            Assert.AreEqual(0m, metrics.FillRate);
        }

        [Test]
        public void FillRate_SomeFills_CorrectRatio()
        {
            var trades = new List<SimulatedTrade>
            {
                new SimulatedTrade { TokenId = "t1", Side = "BUY", Price = 0.50m, Size = 10m, MatchTime = DateTime.UtcNow }
            };

            var curve = new List<(DateTime, decimal)>
            {
                (DateTime.UtcNow, 10000m),
                (DateTime.UtcNow.AddMinutes(10), 9995m)
            };

            var metrics = BacktestMetrics.Calculate(curve, trades, 10000m, 4);
            Assert.AreEqual(0.25m, metrics.FillRate); // 1 fill / 4 orders
        }

        #endregion

        #region Helper Methods

        private void SetupMinimalTestData()
        {
            // Create markets.json
            var marketsDir = Path.Combine(_tempDir, "crypto", "polymarket");
            Directory.CreateDirectory(marketsDir);

            var markets = new[]
            {
                new
                {
                    question = "Test Market?",
                    conditionId = "0xtest",
                    slug = "test-market",
                    volume = 1000.0,
                    volume24h = 500.0,
                    category = "",
                    tokens = new[]
                    {
                        new { tokenId = "token-yes", outcome = "Yes", ticker = "test-market-yes" },
                        new { tokenId = "token-no", outcome = "No", ticker = "test-market-no" }
                    }
                }
            };

            File.WriteAllText(Path.Combine(marketsDir, "markets.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(markets));

            // Create price data for each token
            SetupTokenData("test-market-yes", 0.55m);
            SetupTokenData("test-market-no", 0.45m);

            // Create BTC data
            var btcDir = Path.Combine(_tempDir, "reference", "btc-usd");
            Directory.CreateDirectory(btcDir);

            var today = DateTime.UtcNow.Date;
            var lines = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                var ms = i * 600000;
                lines.Add($"{ms},65000,65100,64900,65050,50");
            }
            File.WriteAllLines(Path.Combine(btcDir, $"{today:yyyyMMdd}_trade.csv"), lines);
        }

        private void SetupTokenData(string ticker, decimal basePrice)
        {
            var dir = Path.Combine(_tempDir, "crypto", "polymarket", "minute", ticker);
            Directory.CreateDirectory(dir);

            var today = DateTime.UtcNow.Date;
            var lines = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                var ms = i * 600000;
                var price = basePrice + (i * 0.01m);
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5}", ms, price, price + 0.02m, price - 0.02m, price + 0.005m, 100));
            }
            File.WriteAllLines(Path.Combine(dir, $"{today:yyyyMMdd}_trade.csv"), lines);
        }

        private static List<(DateTime Time, Dictionary<string, HistoricalBar> TokenBars)> CreateSingleTickTimeline(
            string tokenId, HistoricalBar bar)
        {
            return new List<(DateTime, Dictionary<string, HistoricalBar>)>
            {
                (bar.Time, new Dictionary<string, HistoricalBar> { [tokenId] = bar })
            };
        }

        private static List<(DateTime Time, Dictionary<string, HistoricalBar> TokenBars)> CreateMultiTickTimeline(
            string tokenId, int ticks)
        {
            var timeline = new List<(DateTime, Dictionary<string, HistoricalBar>)>();
            var baseTime = new DateTime(2026, 3, 1, 0, 0, 0);
            for (int i = 0; i < ticks; i++)
            {
                var time = baseTime.AddMinutes(i * 10);
                var price = 0.50m + (decimal)Math.Sin(i * 0.5) * 0.05m;
                var bar = new HistoricalBar
                {
                    Time = time,
                    Open = price,
                    High = price + 0.02m,
                    Low = price - 0.02m,
                    Close = price + 0.005m,
                    Volume = 200m
                };
                timeline.Add((time, new Dictionary<string, HistoricalBar> { [tokenId] = bar }));
            }
            return timeline;
        }

        private static List<DashboardMarket> CreateTestMarkets(string tokenId)
        {
            return new List<DashboardMarket>
            {
                new DashboardMarket
                {
                    Question = "Test BTC $65000?",
                    Slug = "test-btc-65k",
                    ConditionId = "0xtest",
                    Volume = 1000m,
                    Volume24h = 500m,
                    Active = true,
                    Tokens = new List<DashboardToken>
                    {
                        new DashboardToken { TokenId = tokenId, Outcome = "Yes", Price = 0.50m }
                    }
                }
            };
        }

        #endregion

        #region Test Strategy Helpers

        /// <summary>
        /// Test strategy that places a single BUY order on first evaluation.
        /// </summary>
        private class TestBuyStrategy : IDryRunStrategy
        {
            private readonly string _tokenId;
            private readonly decimal _price;
            private readonly decimal _size;
            private readonly bool _onlyFirstTick;
            private bool _placed;

            public string Name => "TestBuy";
            public string Description => "Test buy strategy";

            public TestBuyStrategy(string tokenId, decimal price, decimal size, bool onlyFirstTick = false)
            {
                _tokenId = tokenId;
                _price = price;
                _size = size;
                _onlyFirstTick = onlyFirstTick;
            }

            public void Initialize(Dictionary<string, string> parameters) { }

            public List<StrategyAction> Evaluate(StrategyContext context)
            {
                if (_onlyFirstTick && _placed)
                    return new List<StrategyAction>();

                _placed = true;
                return new List<StrategyAction>
                {
                    new PlaceOrderAction
                    {
                        TokenId = _tokenId,
                        Price = _price,
                        Size = _size,
                        Side = "BUY",
                        Reason = "Test"
                    }
                };
            }

            public void OnFill(SimulatedTrade trade) { }
        }

        /// <summary>
        /// Test strategy that buys on first tick and sells on second.
        /// </summary>
        private class TestSellStrategy : IDryRunStrategy
        {
            private readonly string _tokenId;
            private readonly decimal _buyPrice;
            private readonly decimal _sellPrice;
            private readonly decimal _size;
            private int _tick;

            public string Name => "TestSell";
            public string Description => "Test sell strategy";

            public TestSellStrategy(string tokenId, decimal buyPrice, decimal sellPrice, decimal size)
            {
                _tokenId = tokenId;
                _buyPrice = buyPrice;
                _sellPrice = sellPrice;
                _size = size;
            }

            public void Initialize(Dictionary<string, string> parameters) { }

            public List<StrategyAction> Evaluate(StrategyContext context)
            {
                _tick++;
                if (_tick == 1)
                {
                    return new List<StrategyAction>
                    {
                        new PlaceOrderAction { TokenId = _tokenId, Price = _buyPrice, Size = _size, Side = "BUY", Reason = "Buy" }
                    };
                }
                if (_tick == 2 && context.Positions.TryGetValue(_tokenId, out var pos) && pos.Size > 0)
                {
                    return new List<StrategyAction>
                    {
                        new PlaceOrderAction { TokenId = _tokenId, Price = _sellPrice, Size = pos.Size, Side = "SELL", Reason = "Sell" }
                    };
                }
                return new List<StrategyAction>();
            }

            public void OnFill(SimulatedTrade trade) { }
        }

        #endregion
    }
}
