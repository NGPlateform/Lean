using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;
using QuantConnect.Brokerages.Polymarket.Dashboard.Strategies;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class DashboardEnhancementTests
    {
        private static PolymarketOrderBook CreateBook(decimal bestBid, decimal bestAsk,
            decimal bidSize = 500m, decimal askSize = 500m)
        {
            return new PolymarketOrderBook
            {
                Bids = new List<PolymarketOrderBookLevel>
                {
                    new() { Price = bestBid.ToString("F4"), Size = bidSize.ToString("F2") }
                },
                Asks = new List<PolymarketOrderBookLevel>
                {
                    new() { Price = bestAsk.ToString("F4"), Size = askSize.ToString("F2") }
                }
            };
        }

        private static StrategyContext CreateContext(
            Dictionary<string, PolymarketOrderBook> books = null,
            Dictionary<string, SimulatedPosition> positions = null,
            decimal balance = 10000m)
        {
            return new StrategyContext
            {
                CurrentTime = DateTime.UtcNow,
                Markets = new List<DashboardMarket>
                {
                    new DashboardMarket
                    {
                        Question = "Will BTC reach $100k?",
                        Tokens = new List<DashboardToken>
                        {
                            new DashboardToken { TokenId = "token1", Outcome = "Yes", Price = 0.5m }
                        }
                    }
                },
                OrderBooks = books ?? new Dictionary<string, PolymarketOrderBook>
                {
                    ["token1"] = CreateBook(0.48m, 0.52m)
                },
                Balance = balance,
                Positions = positions ?? new Dictionary<string, SimulatedPosition>(),
                OpenOrders = new List<SimulatedOrder>(),
                RecentTrades = new List<SimulatedTrade>(),
                RealizedPnl = 0m,
                UnrealizedPnl = 0m
            };
        }

        #region GetParameters Tests

        [Test]
        public void MarketMakingStrategy_GetParameters_ReturnsExpectedKeys()
        {
            var strategy = new MarketMakingStrategy();
            strategy.Initialize(new Dictionary<string, string>());

            var p = strategy.GetParameters();

            Assert.That(p.ContainsKey("OrderSize"));
            Assert.That(p.ContainsKey("HalfSpread"));
            Assert.That(p.ContainsKey("SkewFactor"));
            Assert.That(p.ContainsKey("MaxPositionPerToken"));
            Assert.That(p.ContainsKey("MaxTotalExposure"));
            Assert.That(p.ContainsKey("MaxActiveMarkets"));
            Assert.That(p.ContainsKey("RequoteIntervalTicks"));
            Assert.AreEqual(7, p.Count);
        }

        [Test]
        public void MarketMakingStrategy_GetParameters_RoundTrip()
        {
            var strategy = new MarketMakingStrategy();
            var custom = new Dictionary<string, string>
            {
                ["OrderSize"] = "50",
                ["HalfSpread"] = "0.03"
            };
            strategy.Initialize(custom);

            var p = strategy.GetParameters();

            Assert.AreEqual("50", p["OrderSize"]);
            Assert.AreEqual("0.03", p["HalfSpread"]);
        }

        [Test]
        public void MeanReversionStrategy_GetParameters_ReturnsExpectedKeys()
        {
            var strategy = new MeanReversionStrategy();
            strategy.Initialize(new Dictionary<string, string>());

            var p = strategy.GetParameters();

            Assert.That(p.ContainsKey("SpreadThreshold"));
            Assert.That(p.ContainsKey("OrderSize"));
            Assert.That(p.ContainsKey("MaxPositionSize"));
            Assert.That(p.ContainsKey("WindowSize"));
            Assert.AreEqual(4, p.Count);
        }

        [Test]
        public void MeanReversionStrategy_GetParameters_RoundTrip()
        {
            var strategy = new MeanReversionStrategy();
            var custom = new Dictionary<string, string>
            {
                ["SpreadThreshold"] = "0.05",
                ["OrderSize"] = "30"
            };
            strategy.Initialize(custom);

            var p = strategy.GetParameters();

            Assert.AreEqual("0.05", p["SpreadThreshold"]);
            Assert.AreEqual("30", p["OrderSize"]);
        }

        [Test]
        public void SpreadCaptureStrategy_GetParameters_ReturnsExpectedKeys()
        {
            var strategy = new SpreadCaptureStrategy();
            strategy.Initialize(new Dictionary<string, string>());

            var p = strategy.GetParameters();

            Assert.That(p.ContainsKey("OrderSize"));
            Assert.That(p.ContainsKey("EdgeOffset"));
            Assert.That(p.ContainsKey("MaxExposure"));
            Assert.That(p.ContainsKey("MinSpread"));
            Assert.AreEqual(4, p.Count);
        }

        [Test]
        public void SpreadCaptureStrategy_GetParameters_RoundTrip()
        {
            var strategy = new SpreadCaptureStrategy();
            var custom = new Dictionary<string, string>
            {
                ["OrderSize"] = "50",
                ["MinSpread"] = "0.03"
            };
            strategy.Initialize(custom);

            var p = strategy.GetParameters();

            Assert.AreEqual("50", p["OrderSize"]);
            Assert.AreEqual("0.03", p["MinSpread"]);
        }

        [Test]
        public void BtcFollowMMStrategy_GetParameters_ReturnsExpectedKeys()
        {
            var btcService = new BtcPriceService(null);
            var corrMonitor = new CorrelationMonitor(btcService);
            var strategy = new BtcFollowMMStrategy(btcService, corrMonitor);
            strategy.Initialize(new Dictionary<string, string>());

            var p = strategy.GetParameters();

            Assert.That(p.ContainsKey("OrderSize"));
            Assert.That(p.ContainsKey("HalfSpread"));
            Assert.That(p.ContainsKey("MomentumThreshold"));
            Assert.That(p.ContainsKey("MomentumSpreadMultiplier"));
            Assert.That(p.ContainsKey("MinCorrelation"));
            Assert.That(p.ContainsKey("DownMoveMultiplierScale"));
            Assert.That(p.ContainsKey("EnableSentiment"));
            Assert.AreEqual(13, p.Count);
        }

        [Test]
        public void BtcFollowMMStrategy_GetParameters_RoundTrip()
        {
            var btcService = new BtcPriceService(null);
            var corrMonitor = new CorrelationMonitor(btcService);
            var strategy = new BtcFollowMMStrategy(btcService, corrMonitor);
            var custom = new Dictionary<string, string>
            {
                ["MomentumThreshold"] = "0.005",
                ["OrderSize"] = "40"
            };
            strategy.Initialize(custom);

            var p = strategy.GetParameters();

            Assert.AreEqual("0.005", p["MomentumThreshold"]);
            Assert.AreEqual("40", p["OrderSize"]);
        }

        #endregion

        #region GetMarketScores Tests

        [Test]
        public void MarketMakingStrategy_GetMarketScores_ReturnsScoresForAllTokens()
        {
            var strategy = new MarketMakingStrategy();
            strategy.Initialize(new Dictionary<string, string>());
            var context = CreateContext();

            // Need to call Evaluate first to populate internal state
            strategy.Evaluate(context);
            var scores = strategy.GetMarketScores(context);

            Assert.IsNotNull(scores);
            Assert.AreEqual(1, scores.Count);
            Assert.AreEqual("token1", scores[0].TokenId);
            Assert.Greater(scores[0].Score, 0);
        }

        [Test]
        public void MarketMakingStrategy_GetMarketScores_IncludesScoreComponents()
        {
            var strategy = new MarketMakingStrategy();
            strategy.Initialize(new Dictionary<string, string>());
            var context = CreateContext();
            strategy.Evaluate(context);

            var scores = strategy.GetMarketScores(context);

            Assert.IsNotNull(scores[0].ScoreComponents);
            Assert.That(scores[0].ScoreComponents.ContainsKey("SpreadQuality"));
            Assert.That(scores[0].ScoreComponents.ContainsKey("Liquidity"));
            Assert.That(scores[0].ScoreComponents.ContainsKey("Centrality"));
        }

        [Test]
        public void MeanReversionStrategy_GetMarketScores_AllScoresOne()
        {
            var strategy = new MeanReversionStrategy();
            strategy.Initialize(new Dictionary<string, string>());
            var context = CreateContext();

            var scores = strategy.GetMarketScores(context);

            Assert.AreEqual(1, scores.Count);
            Assert.AreEqual(1.0m, scores[0].Score);
            Assert.IsTrue(scores[0].IsSelected);
        }

        [Test]
        public void SpreadCaptureStrategy_GetMarketScores_AllScoresOne()
        {
            var strategy = new SpreadCaptureStrategy();
            strategy.Initialize(new Dictionary<string, string>());
            var context = CreateContext();

            var scores = strategy.GetMarketScores(context);

            Assert.AreEqual(1, scores.Count);
            Assert.AreEqual(1.0m, scores[0].Score);
            Assert.IsTrue(scores[0].IsSelected);
        }

        [Test]
        public void MarketMakingStrategy_GetMarketScores_HasPositionFlag()
        {
            var strategy = new MarketMakingStrategy();
            strategy.Initialize(new Dictionary<string, string>());
            var positions = new Dictionary<string, SimulatedPosition>
            {
                ["token1"] = new SimulatedPosition { TokenId = "token1", Size = 10, AvgPrice = 0.50m }
            };
            var context = CreateContext(positions: positions);
            strategy.Evaluate(context);

            var scores = strategy.GetMarketScores(context);

            Assert.IsTrue(scores[0].HasPosition);
        }

        [Test]
        public void BtcFollowMMStrategy_GetMarketScores_ReturnsScores()
        {
            var btcService = new BtcPriceService(null);
            var corrMonitor = new CorrelationMonitor(btcService);
            var strategy = new BtcFollowMMStrategy(btcService, corrMonitor);
            strategy.Initialize(new Dictionary<string, string>());
            var context = CreateContext();
            strategy.Evaluate(context);

            var scores = strategy.GetMarketScores(context);

            Assert.IsNotNull(scores);
            Assert.AreEqual(1, scores.Count);
            Assert.AreEqual("token1", scores[0].TokenId);
        }

        [Test]
        public void BtcFollowMMStrategy_GetMarketScores_IncludesCorrelation()
        {
            var btcService = new BtcPriceService(null);
            var corrMonitor = new CorrelationMonitor(btcService);
            var strategy = new BtcFollowMMStrategy(btcService, corrMonitor);
            strategy.Initialize(new Dictionary<string, string>());
            var context = CreateContext();
            strategy.Evaluate(context);

            var scores = strategy.GetMarketScores(context);

            Assert.That(scores[0].ScoreComponents.ContainsKey("BtcCorrelation"));
        }

        #endregion

        #region MarketScore Model Tests

        [Test]
        public void MarketScore_DefaultValues()
        {
            var score = new MarketScore();

            Assert.IsNull(score.TokenId);
            Assert.IsNull(score.Question);
            Assert.AreEqual(0m, score.Score);
            Assert.IsFalse(score.IsSelected);
            Assert.IsFalse(score.HasPosition);
            Assert.IsNotNull(score.ScoreComponents);
            Assert.AreEqual(0, score.ScoreComponents.Count);
        }

        [Test]
        public void MarketScore_SetProperties()
        {
            var score = new MarketScore
            {
                TokenId = "t1",
                Question = "Test?",
                Score = 0.85m,
                IsSelected = true,
                HasPosition = true,
                ScoreComponents = new Dictionary<string, decimal> { ["A"] = 0.5m }
            };

            Assert.AreEqual("t1", score.TokenId);
            Assert.AreEqual("Test?", score.Question);
            Assert.AreEqual(0.85m, score.Score);
            Assert.IsTrue(score.IsSelected);
            Assert.IsTrue(score.HasPosition);
            Assert.AreEqual(0.5m, score.ScoreComponents["A"]);
        }

        #endregion

        #region EquityPoint Model Tests

        [Test]
        public void EquityPoint_SetProperties()
        {
            var now = DateTime.UtcNow;
            var point = new EquityPoint
            {
                Time = now,
                Equity = 10500.50m
            };

            Assert.AreEqual(now, point.Time);
            Assert.AreEqual(10500.50m, point.Equity);
        }

        #endregion

        #region AvailableStrategies Tests

        [Test]
        public void DryRunEngine_AvailableStrategies_ContainsAllFour()
        {
            var strategies = DryRunEngine.AvailableStrategies;

            Assert.AreEqual(4, strategies.Length);
            Assert.Contains("MarketMaking", strategies);
            Assert.Contains("MeanReversion", strategies);
            Assert.Contains("SpreadCapture", strategies);
            Assert.Contains("BtcFollowMM", strategies);
        }

        #endregion

        #region GetMarketScores with Question Lookup

        [Test]
        public void MarketMakingStrategy_GetMarketScores_ResolvesQuestion()
        {
            var strategy = new MarketMakingStrategy();
            strategy.Initialize(new Dictionary<string, string>());
            var context = CreateContext();
            strategy.Evaluate(context);

            var scores = strategy.GetMarketScores(context);

            Assert.AreEqual("Will BTC reach $100k?", scores[0].Question);
        }

        #endregion
    }
}
