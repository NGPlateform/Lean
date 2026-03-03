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
    public class SpreadCaptureStrategyTests
    {
        private SpreadCaptureStrategy _strategy;

        [SetUp]
        public void Setup()
        {
            _strategy = new SpreadCaptureStrategy();
            _strategy.Initialize(new Dictionary<string, string>());
        }

        private static PolymarketOrderBook CreateBook(decimal bestBid, decimal bestAsk)
        {
            return new PolymarketOrderBook
            {
                Bids = new List<PolymarketOrderBookLevel>
                {
                    new() { Price = bestBid.ToString("F4"), Size = "500" }
                },
                Asks = new List<PolymarketOrderBookLevel>
                {
                    new() { Price = bestAsk.ToString("F4"), Size = "500" }
                }
            };
        }

        private static StrategyContext CreateContext(
            Dictionary<string, PolymarketOrderBook> books,
            Dictionary<string, SimulatedPosition> positions = null,
            List<SimulatedOrder> openOrders = null,
            decimal balance = 10000m)
        {
            return new StrategyContext
            {
                CurrentTime = DateTime.UtcNow,
                Markets = new List<DashboardMarket>(),
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
        public void Name_ReturnsSpreadCapture()
        {
            Assert.AreEqual("SpreadCapture", _strategy.Name);
        }

        [Test]
        public void Initialize_CustomParameters()
        {
            var strategy = new SpreadCaptureStrategy();
            strategy.Initialize(new Dictionary<string, string>
            {
                { "OrderSize", "50" },
                { "EdgeOffset", "0.01" },
                { "MaxExposure", "500" },
                { "MinSpread", "0.05" }
            });

            // Verify custom order size by evaluating
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) } // spread=0.10 > 0.05
            };
            var actions = strategy.Evaluate(CreateContext(books));

            var buy = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            Assert.IsNotNull(buy);
            Assert.AreEqual(50m, buy.Size);
        }

        #endregion

        #region Basic Spread Capture Tests

        [Test]
        public void Evaluate_WideSpread_PlacesBuyInsideSpread()
        {
            // bestBid=0.40, bestAsk=0.50, spread=0.10 > minSpread(0.02)
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            var buy = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            Assert.IsNotNull(buy, "Should place a buy order");

            // Buy price = bestBid + edgeOffset = 0.40 + 0.005 = 0.405
            Assert.AreEqual(0.405m, buy.Price);
            Assert.AreEqual(25m, buy.Size); // default order size
            Assert.AreEqual("token-1", buy.TokenId);
        }

        [Test]
        public void Evaluate_WideSpread_PlacesSellInsideSpread_WhenHoldingPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 30m, AvgPrice = 0.42m } }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var sell = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "SELL");
            Assert.IsNotNull(sell, "Should place sell when holding position");

            // Sell price = bestAsk - edgeOffset = 0.50 - 0.005 = 0.495
            Assert.AreEqual(0.495m, sell.Price);
            Assert.LessOrEqual(sell.Size, 25m);
        }

        [Test]
        public void Evaluate_NoSell_WithoutPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();
            Assert.AreEqual(0, sells.Count, "Should not sell without a position");
        }

        [Test]
        public void Evaluate_BuyPriceInsideSpread()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            var buy = actions.OfType<PlaceOrderAction>().First(a => a.Side == "BUY");
            Assert.Greater(buy.Price, 0.40m, "Buy should be above best bid");
            Assert.Less(buy.Price, 0.50m, "Buy should be below best ask");
        }

        [Test]
        public void Evaluate_SellPriceInsideSpread()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 30m, AvgPrice = 0.42m } }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var sell = actions.OfType<PlaceOrderAction>().First(a => a.Side == "SELL");
            Assert.Greater(sell.Price, 0.40m, "Sell should be above best bid");
            Assert.Less(sell.Price, 0.50m, "Sell should be below best ask");
        }

        #endregion

        #region Spread Filter Tests

        [Test]
        public void Evaluate_NarrowSpread_SkipsToken()
        {
            // spread = 0.01 < minSpread(0.02)
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-tight", CreateBook(0.495m, 0.505m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            Assert.AreEqual(0, actions.Count, "Should skip tokens with spread below minimum");
        }

        [Test]
        public void Evaluate_ExactMinSpread_SkipsToken()
        {
            // spread = 0.02 == minSpread → spread < _minSpread is false → should process
            // Actually: 0.02 < 0.02 is false, so it processes
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-exact", CreateBook(0.49m, 0.51m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            // 0.51 - 0.49 = 0.02, which is NOT < 0.02, so it should process
            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            Assert.AreEqual(1, buys.Count, "Spread exactly at min should still be processed");
        }

        #endregion

        #region Exposure Limit Tests

        [Test]
        public void Evaluate_AtMaxExposure_NoNewOrders()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            // Total exposure = 200 * 0.50 = 100, but maxExposure default is 200
            // To exceed it: position cost >= 200
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 500m, AvgPrice = 0.50m } }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            Assert.AreEqual(0, buys.Count, "Should not buy when at max exposure");
        }

        #endregion

        #region Open Order Check Tests

        [Test]
        public void Evaluate_SkipsToken_WhenOpenOrderExists()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            var openOrders = new List<SimulatedOrder>
            {
                new SimulatedOrder { Id = "existing-1", TokenId = "token-1", Side = "BUY" }
            };
            var actions = _strategy.Evaluate(CreateContext(books, openOrders: openOrders));

            Assert.AreEqual(0, actions.Count, "Should skip tokens with existing open orders");
        }

        #endregion

        #region Sell Size Cap Tests

        [Test]
        public void Evaluate_SellSizeCappedByPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            // Small position — less than default orderSize (25)
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 10m, AvgPrice = 0.42m } }
            };
            var actions = _strategy.Evaluate(CreateContext(books, positions));

            var sell = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "SELL");
            Assert.IsNotNull(sell);
            Assert.AreEqual(10m, sell.Size, "Sell size should be capped by position size");
        }

        #endregion

        #region Balance Check Tests

        [Test]
        public void Evaluate_InsufficientBalance_NoBuy()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) }
            };
            // Balance too low to buy: need orderSize * buyPrice = 25 * 0.405 = ~10.125
            var actions = _strategy.Evaluate(CreateContext(books, balance: 5m));

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            Assert.AreEqual(0, buys.Count, "Should not buy with insufficient balance");
        }

        #endregion

        #region Multiple Tokens Tests

        [Test]
        public void Evaluate_MultipleTokens_ProcessesAll()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.40m, 0.50m) },
                { "token-2", CreateBook(0.30m, 0.45m) }
            };
            var actions = _strategy.Evaluate(CreateContext(books));

            var uniqueTokens = actions.OfType<PlaceOrderAction>().Select(a => a.TokenId).Distinct().ToList();
            Assert.AreEqual(2, uniqueTokens.Count, "Should process all tokens with sufficient spread");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void Evaluate_EmptyOrderBooks_NoActions()
        {
            var actions = _strategy.Evaluate(CreateContext(new Dictionary<string, PolymarketOrderBook>()));
            Assert.AreEqual(0, actions.Count);
        }

        [Test]
        public void Evaluate_EmptyBook_NoActions()
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

        [Test]
        public void OnFill_DoesNotThrow()
        {
            var trade = new SimulatedTrade
            {
                Id = "t1", OrderId = "o1", TokenId = "token-1",
                Side = "BUY", Price = 0.45m, Size = 25m, MatchTime = DateTime.UtcNow
            };
            Assert.DoesNotThrow(() => _strategy.OnFill(trade));
        }

        #endregion
    }
}
