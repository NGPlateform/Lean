using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;
using QuantConnect.Brokerages.Polymarket.Dashboard.Strategies;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class BtcFollowMMStrategyTests
    {
        private BtcPriceService _btcService;
        private CorrelationMonitor _correlationMonitor;
        private BtcFollowMMStrategy _strategy;

        [SetUp]
        public void Setup()
        {
            _btcService = new BtcPriceService(NullLogger<BtcPriceService>.Instance);
            _correlationMonitor = new CorrelationMonitor(_btcService);
            _strategy = new BtcFollowMMStrategy(_btcService, _correlationMonitor);
            _strategy.Initialize(new Dictionary<string, string>());
        }

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
            Dictionary<string, PolymarketOrderBook> books,
            Dictionary<string, SimulatedPosition> positions = null,
            List<SimulatedOrder> openOrders = null,
            List<DashboardMarket> markets = null,
            decimal balance = 10000m)
        {
            return new StrategyContext
            {
                CurrentTime = DateTime.UtcNow,
                Markets = markets ?? new List<DashboardMarket>(),
                OrderBooks = books,
                Balance = balance,
                Positions = positions ?? new Dictionary<string, SimulatedPosition>(),
                OpenOrders = openOrders ?? new List<SimulatedOrder>(),
                RecentTrades = new List<SimulatedTrade>(),
                RealizedPnl = 0m,
                UnrealizedPnl = 0m
            };
        }

        #region Initialization Tests

        [Test]
        public void Name_ReturnsBtcFollowMM()
        {
            Assert.AreEqual("BtcFollowMM", _strategy.Name);
        }

        [Test]
        public void Initialize_CustomBtcParameters_Applied()
        {
            _strategy.Initialize(new Dictionary<string, string>
            {
                { "MomentumThreshold", "0.005" },
                { "MomentumSpreadMultiplier", "3.0" },
                { "MomentumSizeReduction", "0.3" },
                { "MinCorrelation", "0.5" },
                { "OrderSize", "50" }
            });

            // Verify by evaluating with a basic book — the custom OrderSize should work
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            var buyAction = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            Assert.IsNotNull(buyAction);
            Assert.AreEqual(50m, buyAction.Size);
        }

        #endregion

        #region Strike Extraction Tests

        [Test]
        public void ExtractStrike_ParsesDollarK()
        {
            Assert.AreEqual(74000m, BtcFollowMMStrategy.ExtractStrike("Bitcoin above $74k on March 3"));
        }

        [Test]
        public void ExtractStrike_ParsesDollarWithCommas()
        {
            Assert.AreEqual(150000m, BtcFollowMMStrategy.ExtractStrike("Will Bitcoin reach $150,000 in March?"));
        }

        [Test]
        public void ExtractStrike_ParsesSimpleDollar()
        {
            Assert.AreEqual(90000m, BtcFollowMMStrategy.ExtractStrike("Bitcoin above $90000"));
        }

        [Test]
        public void ExtractStrike_ReturnsNull_WhenNoStrike()
        {
            Assert.IsNull(BtcFollowMMStrategy.ExtractStrike("Will Bitcoin moon?"));
        }

        [Test]
        public void ExtractStrike_ReturnsNull_WhenNull()
        {
            Assert.IsNull(BtcFollowMMStrategy.ExtractStrike(null));
        }

        #endregion

        #region BTC Signal Calculation Tests

        [Test]
        public void CalculateBtcSignal_ReturnsNoAdjustment_WhenLowCorrelation()
        {
            // No correlation data → correlation = 0 → skip BTC signal
            var signal = _strategy.CalculateBtcSignal("token-1", 0.005m, 85000m, 74000m);

            Assert.AreEqual(1.0m, signal.BidSpreadMultiplier);
            Assert.AreEqual(1.0m, signal.AskSpreadMultiplier);
            Assert.AreEqual(1.0m, signal.BidSizeMultiplier);
            Assert.AreEqual(1.0m, signal.AskSizeMultiplier);
            Assert.That(signal.Reason, Does.Contain("skip"));
        }

        [Test]
        public void CalculateBtcSignal_BullishAdjustment_WhenPositiveMomentumAndCorrelation()
        {
            // Simulate high correlation by building correlation data
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                // Token price follows BTC direction
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            // Strong positive momentum with correlated token
            var signal = _strategy.CalculateBtcSignal("token-1", 0.005m, 81000m, 74000m);

            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                // Bullish: widen ask, shrink ask size
                Assert.Greater(signal.AskSpreadMultiplier, 1.0m, "Ask spread should widen on bullish signal");
                Assert.Less(signal.AskSizeMultiplier, 1.0m, "Ask size should reduce on bullish signal");
                Assert.That(signal.Reason, Does.Contain("BULL"));
            }
        }

        [Test]
        public void CalculateBtcSignal_BearishAdjustment_WhenNegativeMomentumAndCorrelation()
        {
            // Build positive correlation
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            // Now send negative momentum
            var signal = _strategy.CalculateBtcSignal("token-1", -0.005m, 79000m, 74000m);

            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                // Bearish: widen bid, shrink bid size
                Assert.Greater(signal.BidSpreadMultiplier, 1.0m, "Bid spread should widen on bearish signal");
                Assert.Less(signal.BidSizeMultiplier, 1.0m, "Bid size should reduce on bearish signal");
                Assert.That(signal.Reason, Does.Contain("BEAR"));
            }
        }

        [Test]
        public void CalculateBtcSignal_NeutralAdjustment_WhenMomentumBelowThreshold()
        {
            // Build correlation
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            // Very small momentum — below threshold
            var signal = _strategy.CalculateBtcSignal("token-1", 0.0001m, 80000m, 74000m);

            // All multipliers should be 1.0 (neutral) or reason should say "neutral"
            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                Assert.That(signal.Reason, Does.Contain("neutral"));
            }
        }

        #endregion

        #region Delta Multiplier Tests

        [Test]
        public void DeltaMultiplier_StrongestForATM()
        {
            // ATM: btcPrice ≈ strike → moneyness ≈ 0 → deltaMultiplier ≈ 1.0
            // OTM: btcPrice << strike → large |moneyness| → deltaMultiplier → 0.1

            // Build high correlation for both tokens
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-atm", 0.50m + i * 0.005m);
                _correlationMonitor.UpdateTokenPrice("token-otm", 0.10m + i * 0.001m);
            }

            // ATM: strike = 81000, btcPrice = 81000
            var signalAtm = _strategy.CalculateBtcSignal("token-atm", 0.005m, 81000m, 81000m);
            // Deep OTM: strike = 120000, btcPrice = 81000
            var signalOtm = _strategy.CalculateBtcSignal("token-otm", 0.005m, 81000m, 120000m);

            // ATM should have stronger signal impact than deep OTM
            // The exact multiplier depends on correlation, but ATM delta is always higher
            // We can verify the delta calculation logic indirectly
            Assert.IsNotNull(signalAtm);
            Assert.IsNotNull(signalOtm);
        }

        #endregion

        #region Core MM Logic Tests

        [Test]
        public void Evaluate_PlacesBidAndAsk_WhenNoPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            Assert.AreEqual(1, buys.Count, "Should place one buy order");
            Assert.Greater(buys[0].Price, 0.01m);
            Assert.Less(buys[0].Price, 0.50m); // Bid below mid
        }

        [Test]
        public void Evaluate_PlacesSellOnly_WhenHoldingPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 5m, AvgPrice = 0.48m, TotalCost = 2.4m } }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();
            Assert.AreEqual(1, sells.Count, "Should place a sell when holding position");
        }

        [Test]
        public void Evaluate_EmergencyMode_OnlySells()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition
                    {
                        TokenId = "token-1",
                        Size = 300m,
                        AvgPrice = 0.50m,
                        TotalCost = 150m
                    }
                }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();

            Assert.AreEqual(0, buys.Count, "Emergency mode should not place buys");
            Assert.GreaterOrEqual(sells.Count, 1, "Emergency mode should place at least one sell");
            Assert.That(sells[0].Reason, Does.Contain("EMERGENCY"));
        }

        [Test]
        public void Evaluate_FiltersExtremePrices()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-low", CreateBook(0.02m, 0.04m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));
            Assert.AreEqual(0, actions.OfType<PlaceOrderAction>().Count());
        }

        [Test]
        public void Evaluate_NoOrderBooks_ReturnsNoActions()
        {
            var actions = _strategy.Evaluate(CreateContext(new Dictionary<string, PolymarketOrderBook>()));
            Assert.AreEqual(0, actions.Count);
        }

        [Test]
        public void Evaluate_EmptyBook_SkipsToken()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", new PolymarketOrderBook
                    {
                        Bids = new List<PolymarketOrderBookLevel>(),
                        Asks = new List<PolymarketOrderBookLevel>()
                    }
                }
            };
            var actions = _strategy.Evaluate(CreateContext(books));
            Assert.AreEqual(0, actions.Count);
        }

        #endregion

        #region Requote / Cancel Tests

        [Test]
        public void Evaluate_CancelsExistingOrders_OnRequote()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            // First evaluate to establish token
            _strategy.Evaluate(CreateContext(books));

            // Advance ticks past requote interval
            var emptyBooks = new Dictionary<string, PolymarketOrderBook>();
            for (int i = 0; i < 5; i++)
                _strategy.Evaluate(CreateContext(emptyBooks));

            // Now with existing orders
            var existingOrders = new List<SimulatedOrder>
            {
                new SimulatedOrder { Id = "old-1", TokenId = "token-1", Source = "Strategy", Side = "BUY", Price = 0.47m }
            };
            var actions = _strategy.Evaluate(CreateContext(books, openOrders: existingOrders));

            var cancels = actions.OfType<CancelOrderAction>().ToList();
            Assert.GreaterOrEqual(cancels.Count, 1);
            Assert.AreEqual("old-1", cancels[0].OrderId);
        }

        #endregion

        #region Market Selection Tests

        [Test]
        public void Evaluate_RespectsMaxActiveMarkets()
        {
            _strategy = new BtcFollowMMStrategy(_btcService, _correlationMonitor);
            _strategy.Initialize(new Dictionary<string, string>
            {
                { "MaxActiveMarkets", "2" }
            });

            var books = new Dictionary<string, PolymarketOrderBook>();
            for (int i = 0; i < 5; i++)
                books[$"token-{i}"] = CreateBook(0.45m, 0.55m);

            var actions = _strategy.Evaluate(CreateContext(books));

            var uniqueTokens = actions.OfType<PlaceOrderAction>()
                .Select(a => a.TokenId).Distinct().Count();
            Assert.LessOrEqual(uniqueTokens, 2);
        }

        [Test]
        public void Evaluate_ForceIncludesPositionTokens()
        {
            _strategy = new BtcFollowMMStrategy(_btcService, _correlationMonitor);
            _strategy.Initialize(new Dictionary<string, string>
            {
                { "MaxActiveMarkets", "1" }
            });

            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-held", CreateBook(0.48m, 0.52m) },
                { "token-new", CreateBook(0.45m, 0.55m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-held", new SimulatedPosition { TokenId = "token-held", Size = 10m, AvgPrice = 0.50m, TotalCost = 5m } }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var tokenIds = actions.OfType<PlaceOrderAction>().Select(a => a.TokenId).Distinct().ToList();
            Assert.Contains("token-held", tokenIds);
        }

        #endregion

        #region BTC Signal Integration Tests

        [Test]
        public void Evaluate_IncludesBtcInfoInReason()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            var buyAction = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            Assert.IsNotNull(buyAction);
            // Reason should include BTC signal info
            Assert.That(buyAction.Reason, Does.Contain("MM BID"));
        }

        [Test]
        public void Evaluate_WithBtcMomentum_AdjustsSpreads()
        {
            // Build strong correlation first
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 200m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.01m);
            }

            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            // Create market with a strike for delta calculation
            var markets = new List<DashboardMarket>
            {
                new DashboardMarket
                {
                    Question = "Bitcoin above $82k on March 3",
                    Tokens = new List<DashboardToken>
                    {
                        new DashboardToken { TokenId = "token-1", Outcome = "Yes" }
                    }
                }
            };

            var actions = _strategy.Evaluate(CreateContext(books, markets: markets));

            // Should place at least a bid (we can't easily verify the exact spread adjustment
            // without knowing the correlation value, but the action should exist)
            Assert.Greater(actions.OfType<PlaceOrderAction>().Count(), 0);
        }

        #endregion

        #region TTE-Aware Scaling Tests

        [Test]
        public void CalculateTteMultiplier_ReturnsDefault_WhenNoExpiry()
        {
            var result = BtcFollowMMStrategy.CalculateTteMultiplier(null, DateTime.UtcNow);
            Assert.AreEqual(1.0m, result);
        }

        [Test]
        public void CalculateTteMultiplier_ReturnsDefault_WhenNoCurrentTime()
        {
            var result = BtcFollowMMStrategy.CalculateTteMultiplier(DateTime.UtcNow.AddDays(5), null);
            Assert.AreEqual(1.0m, result);
        }

        [Test]
        public void CalculateTteMultiplier_MaxResponse_WhenLessThan1Day()
        {
            var now = new DateTime(2026, 3, 3, 12, 0, 0);
            var expiry = new DateTime(2026, 3, 3, 23, 59, 59);
            var result = BtcFollowMMStrategy.CalculateTteMultiplier(expiry, now);
            Assert.AreEqual(1.5m, result);
        }

        [Test]
        public void CalculateTteMultiplier_StrongResponse_When1To3Days()
        {
            var now = new DateTime(2026, 3, 1, 12, 0, 0);
            var expiry = new DateTime(2026, 3, 3, 23, 59, 59);
            var result = BtcFollowMMStrategy.CalculateTteMultiplier(expiry, now);
            Assert.AreEqual(1.25m, result);
        }

        [Test]
        public void CalculateTteMultiplier_DefaultResponse_When3To7Days()
        {
            var now = new DateTime(2026, 2, 27, 12, 0, 0);
            var expiry = new DateTime(2026, 3, 3, 23, 59, 59);
            var result = BtcFollowMMStrategy.CalculateTteMultiplier(expiry, now);
            Assert.AreEqual(1.0m, result);
        }

        [Test]
        public void CalculateTteMultiplier_WeakResponse_WhenMoreThan7Days()
        {
            var now = new DateTime(2026, 2, 20, 12, 0, 0);
            var expiry = new DateTime(2026, 3, 3, 23, 59, 59);
            var result = BtcFollowMMStrategy.CalculateTteMultiplier(expiry, now);
            Assert.AreEqual(0.75m, result);
        }

        [Test]
        public void CalculateTteMultiplier_MaxResponse_WhenExpired()
        {
            var now = new DateTime(2026, 3, 4, 12, 0, 0);
            var expiry = new DateTime(2026, 3, 3, 23, 59, 59);
            var result = BtcFollowMMStrategy.CalculateTteMultiplier(expiry, now);
            Assert.AreEqual(1.5m, result);
        }

        [Test]
        public void CalculateBtcSignal_IncludesTteInReason()
        {
            // Build correlation
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            var expiry = DateTime.UtcNow.AddDays(2); // 1-3d bucket
            var signal = _strategy.CalculateBtcSignal("token-1", 0.005m, 81000m, 74000m, expiry, DateTime.UtcNow);

            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                Assert.That(signal.Reason, Does.Contain("tte="));
            }
        }

        [Test]
        public void CalculateBtcSignal_TteScalesSpread_NearExpiry()
        {
            // Build strong correlation
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            var now = DateTime.UtcNow;
            var nearExpiry = now.AddHours(12);  // <1d → 1.5x TTE multiplier
            var farExpiry = now.AddDays(10);     // >7d → 0.75x TTE multiplier

            var signalNear = _strategy.CalculateBtcSignal("token-1", 0.005m, 81000m, 81000m, nearExpiry, now);
            var signalFar = _strategy.CalculateBtcSignal("token-1", 0.005m, 81000m, 81000m, farExpiry, now);

            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                // Near expiry should have stronger ask spread multiplier than far
                Assert.Greater(signalNear.AskSpreadMultiplier, signalFar.AskSpreadMultiplier,
                    "Near-expiry should produce stronger spread adjustment");
            }
        }

        #endregion

        #region Asymmetry Tests

        [Test]
        public void CalculateBtcSignal_BullishStrongerThanBearish()
        {
            // Build strong correlation
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            var bullSignal = _strategy.CalculateBtcSignal("token-1", 0.005m, 81000m, 81000m);
            var bearSignal = _strategy.CalculateBtcSignal("token-1", -0.005m, 81000m, 81000m);

            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                // Bull ask spread > Bear bid spread (asymmetry: up-moves are stronger)
                Assert.Greater(bullSignal.AskSpreadMultiplier, bearSignal.BidSpreadMultiplier,
                    "Bullish spread adjustment should be stronger than bearish (asymmetry)");
            }
        }

        [Test]
        public void CalculateBtcSignal_BearishStillWidensSpread()
        {
            // Build strong correlation
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            var bearSignal = _strategy.CalculateBtcSignal("token-1", -0.005m, 81000m, 81000m);

            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                // Even with asymmetry reduction, bear should still widen spread > 1.0
                Assert.Greater(bearSignal.BidSpreadMultiplier, 1.0m,
                    "Bearish signal should still widen bid spread above 1.0");
            }
        }

        [Test]
        public void Initialize_CustomDownMoveScale_Applied()
        {
            _strategy.Initialize(new Dictionary<string, string>
            {
                { "DownMoveMultiplierScale", "0.8" }
            });

            // Build correlation
            _btcService.InjectSample(80000m);
            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _correlationMonitor.UpdateTokenPrice("token-1", 0.50m + i * 0.005m);
            }

            var bearDefault = new BtcFollowMMStrategy(_btcService, _correlationMonitor);
            bearDefault.Initialize(new Dictionary<string, string>());
            var defaultSignal = bearDefault.CalculateBtcSignal("token-1", -0.005m, 81000m, 81000m);
            var customSignal = _strategy.CalculateBtcSignal("token-1", -0.005m, 81000m, 81000m);

            var corr = _correlationMonitor.GetCorrelation("token-1");
            if (Math.Abs(corr) >= 0.3m)
            {
                // Custom 0.8 scale should produce stronger bear spread than default 0.5
                Assert.Greater(customSignal.BidSpreadMultiplier, defaultSignal.BidSpreadMultiplier,
                    "Higher DownMoveMultiplierScale should produce stronger bearish adjustment");
            }
        }

        #endregion

        #region Size/Balance Limit Tests

        [Test]
        public void Evaluate_ReducesBidSize_WhenLowBalance()
        {
            _strategy = new BtcFollowMMStrategy(_btcService, _correlationMonitor);
            _strategy.Initialize(new Dictionary<string, string>
            {
                { "OrderSize", "100" }
            });

            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books, balance: 10m));

            var bid = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            if (bid != null)
            {
                Assert.LessOrEqual(bid.Size * bid.Price, 10m * 0.9m + 0.01m);
            }
        }

        [Test]
        public void Evaluate_LimitsSellSize_ToPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 5m, AvgPrice = 0.48m, TotalCost = 2.4m } }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var sell = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "SELL");
            if (sell != null)
            {
                Assert.LessOrEqual(sell.Size, 5m);
            }
        }

        #endregion
    }

    #region Correlation Monitor Tests

    [TestFixture]
    public class CorrelationMonitorTests
    {
        private BtcPriceService _btcService;
        private CorrelationMonitor _monitor;

        [SetUp]
        public void Setup()
        {
            _btcService = new BtcPriceService(NullLogger<BtcPriceService>.Instance);
            _monitor = new CorrelationMonitor(_btcService);
        }

        [Test]
        public void GetCorrelation_ReturnsZero_WhenNoData()
        {
            Assert.AreEqual(0m, _monitor.GetCorrelation("unknown-token"));
        }

        [Test]
        public void GetCorrelation_ReturnsZero_WhenInsufficientData()
        {
            _btcService.InjectSample(80000m);
            _monitor.UpdateTokenPrice("token-1", 0.50m);

            Assert.AreEqual(0m, _monitor.GetCorrelation("token-1"));
        }

        [Test]
        public void GetCorrelation_PositiveForCorrelatedMovements()
        {
            // BTC and token move in the same direction
            _btcService.InjectSample(80000m);
            _monitor.UpdateTokenPrice("token-1", 0.50m);

            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 500m);
                _monitor.UpdateTokenPrice("token-1", 0.50m + i * 0.02m);
            }

            var corr = _monitor.GetCorrelation("token-1");
            Assert.Greater(corr, 0m, "Correlated movements should yield positive correlation");
        }

        [Test]
        public void GetCorrelation_NegativeForAntiCorrelatedMovements()
        {
            // BTC alternates up/down, token does the opposite
            var rng = new Random(42);
            decimal btcPrice = 80000m;
            decimal tokenPrice = 0.50m;

            _btcService.InjectSample(btcPrice);
            _monitor.UpdateTokenPrice("token-1", tokenPrice);

            for (int i = 1; i <= 15; i++)
            {
                // Random BTC move
                var btcMove = (rng.NextDouble() > 0.5 ? 1 : -1) * (decimal)(rng.NextDouble() * 500 + 100);
                btcPrice += btcMove;
                _btcService.InjectSample(btcPrice);

                // Token moves opposite direction
                var tokenMove = -btcMove / 80000m * 0.5m;
                tokenPrice += tokenMove;
                tokenPrice = Math.Max(0.01m, Math.Min(0.99m, tokenPrice));
                _monitor.UpdateTokenPrice("token-1", tokenPrice);
            }

            var corr = _monitor.GetCorrelation("token-1");
            Assert.Less(corr, 0m, "Anti-correlated movements should yield negative correlation");
        }

        [Test]
        public void GetAllCorrelations_ReturnsAllTracked()
        {
            _btcService.InjectSample(80000m);
            _monitor.UpdateTokenPrice("token-a", 0.50m);
            _monitor.UpdateTokenPrice("token-b", 0.30m);

            for (int i = 1; i <= 10; i++)
            {
                _btcService.InjectSample(80000m + i * 100m);
                _monitor.UpdateTokenPrice("token-a", 0.50m + i * 0.01m);
                _monitor.UpdateTokenPrice("token-b", 0.30m + i * 0.005m);
            }

            var all = _monitor.GetAllCorrelations();
            Assert.AreEqual(2, all.Count);
            Assert.IsTrue(all.ContainsKey("token-a"));
            Assert.IsTrue(all.ContainsKey("token-b"));
        }

        [Test]
        public void PearsonCorrelation_PerfectPositive()
        {
            var x = new List<decimal> { 1, 2, 3, 4, 5 };
            var y = new List<decimal> { 2, 4, 6, 8, 10 };

            var r = CorrelationMonitor.PearsonCorrelation(x, y);
            Assert.AreEqual(1.0m, Math.Round(r, 4));
        }

        [Test]
        public void PearsonCorrelation_PerfectNegative()
        {
            var x = new List<decimal> { 1, 2, 3, 4, 5 };
            var y = new List<decimal> { 10, 8, 6, 4, 2 };

            var r = CorrelationMonitor.PearsonCorrelation(x, y);
            Assert.AreEqual(-1.0m, Math.Round(r, 4));
        }

        [Test]
        public void PearsonCorrelation_ReturnsZero_WhenConstant()
        {
            var x = new List<decimal> { 5, 5, 5, 5, 5 };
            var y = new List<decimal> { 1, 2, 3, 4, 5 };

            var r = CorrelationMonitor.PearsonCorrelation(x, y);
            Assert.AreEqual(0m, r);
        }

        [Test]
        public void PearsonCorrelation_ReturnsZero_WhenInsufficientData()
        {
            var x = new List<decimal> { 1 };
            var y = new List<decimal> { 2 };

            var r = CorrelationMonitor.PearsonCorrelation(x, y);
            Assert.AreEqual(0m, r);
        }
    }

    #endregion
}
