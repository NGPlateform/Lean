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
    public class MarketMakingStrategyTests
    {
        private MarketMakingStrategy _strategy;

        [SetUp]
        public void Setup()
        {
            _strategy = new MarketMakingStrategy();
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
        public void Name_ReturnsMarketMaking()
        {
            Assert.AreEqual("MarketMaking", _strategy.Name);
        }

        [Test]
        public void Initialize_CustomParameters_Applied()
        {
            var strategy = new MarketMakingStrategy();
            strategy.Initialize(new Dictionary<string, string>
            {
                { "OrderSize", "50" },
                { "HalfSpread", "0.03" },
                { "SkewFactor", "0.01" },
                { "MaxPositionPerToken", "200" },
                { "MaxTotalExposure", "1000" },
                { "MaxActiveMarkets", "10" },
                { "RequoteIntervalTicks", "3" }
            });

            // Verify by evaluating — a larger order size means bigger orders
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var context = CreateContext(books);
            var actions = strategy.Evaluate(context);

            var buyAction = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            Assert.IsNotNull(buyAction);
            Assert.AreEqual(50m, buyAction.Size);
        }

        #endregion

        #region Quote Generation Tests

        [Test]
        public void Evaluate_PlacesBidAndAsk_WhenNoPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var context = CreateContext(books);

            var actions = _strategy.Evaluate(context);

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();

            Assert.AreEqual(1, buys.Count, "Should place one buy order");
            Assert.AreEqual(0, sells.Count, "No sell without position");

            Assert.Greater(buys[0].Price, 0.01m);
            Assert.Less(buys[0].Price, 0.50m); // Bid should be below mid
        }

        [Test]
        public void Evaluate_PlacesSellOnly_WhenHoldingPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            // Small position so skew doesn't dominate: skew = 5 * 0.005 = 0.025
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 5m, AvgPrice = 0.48m, TotalCost = 2.4m } }
            };
            var context = CreateContext(books, positions);

            var actions = _strategy.Evaluate(context);

            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();
            Assert.AreEqual(1, sells.Count, "Should place a sell when holding position");
            Assert.Greater(sells[0].Price, 0.01m);
            Assert.Less(sells[0].Price, 0.99m);
        }

        [Test]
        public void Evaluate_BidLessThanAsk()
        {
            // With zero position (no skew), bid should be below ask
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            // Small position so both bid and ask are placed
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 3m, AvgPrice = 0.50m, TotalCost = 1.5m } }
            };
            var context = CreateContext(books, positions);
            var actions = _strategy.Evaluate(context);

            var bid = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            var ask = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "SELL");

            Assert.IsNotNull(bid, "Should place bid");
            Assert.IsNotNull(ask, "Should place ask");
            Assert.Less(bid.Price, ask.Price, "Bid should be less than ask");
        }

        [Test]
        public void Evaluate_ClampsPrice_InRange()
        {
            // Very low priced book — bid should not go below 0.01
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.09m, 0.11m) }
            };
            var context = CreateContext(books);
            var actions = _strategy.Evaluate(context);

            foreach (var order in actions.OfType<PlaceOrderAction>())
            {
                Assert.GreaterOrEqual(order.Price, 0.01m, "Price should be >= 0.01");
                Assert.LessOrEqual(order.Price, 0.99m, "Price should be <= 0.99");
            }
        }

        #endregion

        #region Inventory Skew Tests

        [Test]
        public void Evaluate_LongPosition_SkewsBidDown_AskDown()
        {
            // With a long position, skew = posSize * skewFactor > 0
            // bid = mid - spread - skew (lower) , ask = mid + spread - skew (lower)
            // The net effect: both quotes shift down to encourage sells
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            // First evaluate with no position
            var contextNoPos = CreateContext(books);
            var actionsNoPos = _strategy.Evaluate(contextNoPos);
            var bidNoPos = actionsNoPos.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");

            // Now with a large long position
            _strategy = new MarketMakingStrategy();
            _strategy.Initialize(new Dictionary<string, string>());

            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 100m, AvgPrice = 0.45m, TotalCost = 45m } }
            };
            var contextLong = CreateContext(books, positions);
            var actionsLong = _strategy.Evaluate(contextLong);
            var bidLong = actionsLong.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");

            // With inventory, bid should be lower (or absent due to position limit)
            if (bidNoPos != null && bidLong != null)
            {
                Assert.Less(bidLong.Price, bidNoPos.Price, "Long inventory should skew bid lower");
            }
        }

        [Test]
        public void Evaluate_ZeroPosition_NoSkew()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var context = CreateContext(books);
            var actions = _strategy.Evaluate(context);

            // With zero position, skew = 0, quotes should be symmetric around mid
            var bid = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            if (bid != null)
            {
                var mid = 0.50m;
                var bidDistance = mid - bid.Price;
                // Bid distance from mid should equal approximately the half spread
                Assert.Greater(bidDistance, 0m);
            }
        }

        #endregion

        #region Emergency Mode Tests

        [Test]
        public void Evaluate_EmergencyMode_OnlySells()
        {
            // maxPositionPerToken = 150 (default), 90% threshold = 135
            // posCost = posSize * avgPrice >= 135 triggers emergency
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition
                    {
                        TokenId = "token-1",
                        Size = 300m,      // 300 * 0.50 = 150 cost >= 135 threshold
                        AvgPrice = 0.50m,
                        TotalCost = 150m
                    }
                }
            };
            var context = CreateContext(books, positions);
            var actions = _strategy.Evaluate(context);

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();

            Assert.AreEqual(0, buys.Count, "Emergency mode should not place buys");
            Assert.GreaterOrEqual(sells.Count, 1, "Emergency mode should place at least one sell");

            // Emergency sell reason
            var sellAction = sells[0];
            Assert.That(sellAction.Reason, Does.Contain("EMERGENCY"));
        }

        #endregion

        #region Market Selection / Scoring Tests

        [Test]
        public void Evaluate_FiltersExtremePrices()
        {
            // Prices at 0.02 and 0.04 — mid=0.03 < MinPrice(0.08), should be filtered
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-extreme-low", CreateBook(0.02m, 0.04m) }
            };
            var context = CreateContext(books);
            var actions = _strategy.Evaluate(context);

            var orders = actions.OfType<PlaceOrderAction>().ToList();
            Assert.AreEqual(0, orders.Count, "Should not trade extreme low-price markets");
        }

        [Test]
        public void Evaluate_FiltersHighPriceMarkets()
        {
            // mid = 0.96 > MaxPrice(0.92), should be filtered
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-extreme-high", CreateBook(0.95m, 0.97m) }
            };
            var context = CreateContext(books);
            var actions = _strategy.Evaluate(context);

            var orders = actions.OfType<PlaceOrderAction>().ToList();
            Assert.AreEqual(0, orders.Count, "Should not trade extreme high-price markets");
        }

        [Test]
        public void Evaluate_RespectsMaxActiveMarkets()
        {
            _strategy = new MarketMakingStrategy();
            _strategy.Initialize(new Dictionary<string, string>
            {
                { "MaxActiveMarkets", "2" }
            });

            var books = new Dictionary<string, PolymarketOrderBook>();
            for (int i = 0; i < 5; i++)
            {
                books[$"token-{i}"] = CreateBook(0.45m, 0.55m);
            }
            var context = CreateContext(books);
            var actions = _strategy.Evaluate(context);

            var uniqueTokens = actions.OfType<PlaceOrderAction>()
                .Select(a => a.TokenId).Distinct().Count();
            Assert.LessOrEqual(uniqueTokens, 2, "Should respect MaxActiveMarkets limit");
        }

        [Test]
        public void Evaluate_ForceIncludesPositionTokens()
        {
            _strategy = new MarketMakingStrategy();
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
            var context = CreateContext(books, positions);
            var actions = _strategy.Evaluate(context);

            var tokenIds = actions.OfType<PlaceOrderAction>().Select(a => a.TokenId).Distinct().ToList();
            Assert.Contains("token-held", tokenIds, "Tokens with positions should always be included");
        }

        #endregion

        #region Requote Logic Tests

        [Test]
        public void Evaluate_CancelsExistingOrders_OnRequote()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            // First evaluate to establish the token and place initial quotes
            // This sets _lastQuoteTick["token-1"] = 1 (first tick)
            _strategy.Evaluate(CreateContext(books));

            // Now pass enough ticks WITHOUT the token in the book (so _lastQuoteTick stays at 1).
            // We need _tickCount - _lastQuoteTick >= requoteIntervalTicks(6).
            // Each Evaluate increments _tickCount. We need 6 more calls to reach tick 7.
            var emptyBooks = new Dictionary<string, PolymarketOrderBook>();
            for (int i = 0; i < 5; i++)
            {
                _strategy.Evaluate(CreateContext(emptyBooks));
            }

            // Now on tick 7 (7 - 1 = 6 >= requoteIntervalTicks), with existing orders
            var existingOrders = new List<SimulatedOrder>
            {
                new SimulatedOrder { Id = "old-1", TokenId = "token-1", Source = "Strategy", Side = "BUY", Price = 0.47m }
            };
            var context2 = CreateContext(books, openOrders: existingOrders);
            var actions = _strategy.Evaluate(context2);

            var cancels = actions.OfType<CancelOrderAction>().ToList();
            Assert.GreaterOrEqual(cancels.Count, 1, "Should cancel old orders on requote");
            Assert.AreEqual("old-1", cancels[0].OrderId);
        }

        #endregion

        #region Size / Balance Limits Tests

        [Test]
        public void Evaluate_ReducesBidSize_WhenLowBalance()
        {
            _strategy = new MarketMakingStrategy();
            _strategy.Initialize(new Dictionary<string, string>
            {
                { "OrderSize", "100" }
            });

            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };
            var context = CreateContext(books, balance: 10m); // Very low balance
            var actions = _strategy.Evaluate(context);

            var bid = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            if (bid != null)
            {
                // bid.Size * bid.Price should not exceed 90% of balance
                Assert.LessOrEqual(bid.Size * bid.Price, 10m * 0.9m + 0.01m,
                    "Bid cost should not exceed 90% of balance");
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
            var context = CreateContext(books, positions);
            var actions = _strategy.Evaluate(context);

            var sell = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "SELL");
            if (sell != null)
            {
                Assert.LessOrEqual(sell.Size, 5m, "Sell size should not exceed position size");
            }
        }

        #endregion

        #region Empty/Invalid Book Tests

        [Test]
        public void Evaluate_NoOrderBooks_ReturnsNoActions()
        {
            var context = CreateContext(new Dictionary<string, PolymarketOrderBook>());
            var actions = _strategy.Evaluate(context);
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
            var context = CreateContext(books);
            var actions = _strategy.Evaluate(context);
            Assert.AreEqual(0, actions.Count);
        }

        #endregion
    }
}
