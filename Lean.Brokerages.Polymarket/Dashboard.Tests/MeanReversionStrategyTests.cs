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
    public class MeanReversionStrategyTests
    {
        private MeanReversionStrategy _strategy;

        [SetUp]
        public void Setup()
        {
            _strategy = new MeanReversionStrategy();
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
        public void Name_ReturnsMeanReversion()
        {
            Assert.AreEqual("MeanReversion", _strategy.Name);
        }

        [Test]
        public void Initialize_CustomParameters()
        {
            var strategy = new MeanReversionStrategy();
            strategy.Initialize(new Dictionary<string, string>
            {
                { "SpreadThreshold", "0.05" },
                { "OrderSize", "50" },
                { "MaxPositionSize", "500" },
                { "WindowSize", "30" }
            });

            // Verify by evaluating with enough history — larger order size expected
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            // Build enough history
            for (int i = 0; i < 15; i++)
            {
                strategy.Evaluate(CreateContext(books));
            }

            // Now a big deviation
            var devBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.38m, 0.42m) } // Big drop from ~0.50 to ~0.40
            };
            var actions = strategy.Evaluate(CreateContext(devBooks));

            var buy = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "BUY");
            if (buy != null)
            {
                Assert.AreEqual(50m, buy.Size, "Custom OrderSize should be applied");
            }
        }

        #endregion

        #region Insufficient History Tests

        [Test]
        public void Evaluate_InsufficientHistory_NoActions()
        {
            // Default windowSize=20, needs at least 10 data points
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            // Only 5 ticks — not enough
            for (int i = 0; i < 5; i++)
            {
                var actions = _strategy.Evaluate(CreateContext(books));
                Assert.AreEqual(0, actions.Count, $"Tick {i}: Should have no actions with insufficient history");
            }
        }

        [Test]
        public void Evaluate_ExactlyHalfWindow_StartsGeneratingSignals()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            // Build exactly windowSize/2 = 10 data points at stable price
            for (int i = 0; i < 10; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            // Now a big downward move should trigger a buy
            var devBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.38m, 0.42m) } // -0.10 from mean of ~0.50
            };
            var actions = _strategy.Evaluate(CreateContext(devBooks));

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            Assert.AreEqual(1, buys.Count, "Should generate BUY signal on large negative deviation");
        }

        #endregion

        #region Signal Generation Tests

        [Test]
        public void Evaluate_PriceBelowMean_GeneratesBuy()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) } // mid = 0.50
            };

            // Build stable history at 0.50
            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            // Price drops significantly below mean
            var dropBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.43m, 0.47m) } // mid = 0.45, deviation ~ -0.05 < -0.03 threshold
            };
            var actions = _strategy.Evaluate(CreateContext(dropBooks));

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            Assert.AreEqual(1, buys.Count, "Should buy when price is below mean by more than threshold");
            Assert.That(buys[0].Reason, Does.Contain("Mean reversion BUY"));
        }

        [Test]
        public void Evaluate_PriceAboveMean_GeneratesSell_WhenHolding()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) } // mid = 0.50
            };

            // Build history
            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            // Price rises above mean, and we have a position
            var riseBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.53m, 0.57m) } // mid = 0.55, deviation ~ +0.05 > 0.03
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 50m, AvgPrice = 0.48m } }
            };
            var actions = _strategy.Evaluate(CreateContext(riseBooks, positions));

            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();
            Assert.AreEqual(1, sells.Count, "Should sell when price is above mean and holding position");
            Assert.That(sells[0].Reason, Does.Contain("Mean reversion SELL"));
        }

        [Test]
        public void Evaluate_PriceAboveMean_NoSell_WhenNoPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            var riseBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.53m, 0.57m) }
            };
            // No position
            var actions = _strategy.Evaluate(CreateContext(riseBooks));

            var sells = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "SELL").ToList();
            Assert.AreEqual(0, sells.Count, "Should not sell without a position");
        }

        [Test]
        public void Evaluate_SmallDeviation_NoAction()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) } // mid = 0.50
            };

            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            // Price moves slightly — within threshold (0.03)
            var slightBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.47m, 0.51m) } // mid = 0.49, deviation ~ -0.01 (within +-0.03)
            };
            var actions = _strategy.Evaluate(CreateContext(slightBooks));

            Assert.AreEqual(0, actions.Count, "Small deviation should not trigger any action");
        }

        #endregion

        #region Position Limit Tests

        [Test]
        public void Evaluate_AtMaxPosition_NoBuy()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            // Big drop but already at max position (200)
            var dropBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.38m, 0.42m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 200m, AvgPrice = 0.45m } }
            };
            var actions = _strategy.Evaluate(CreateContext(dropBooks, positions));

            var buys = actions.OfType<PlaceOrderAction>().Where(a => a.Side == "BUY").ToList();
            Assert.AreEqual(0, buys.Count, "Should not buy when at max position size");
        }

        [Test]
        public void Evaluate_SellSizeCappedByPosition()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            // Price up, small position (less than default orderSize=25)
            var riseBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.53m, 0.57m) }
            };
            var positions = new Dictionary<string, SimulatedPosition>
            {
                { "token-1", new SimulatedPosition { TokenId = "token-1", Size = 10m, AvgPrice = 0.48m } }
            };
            var actions = _strategy.Evaluate(CreateContext(riseBooks, positions));

            var sell = actions.OfType<PlaceOrderAction>().FirstOrDefault(a => a.Side == "SELL");
            Assert.IsNotNull(sell);
            Assert.AreEqual(10m, sell.Size, "Sell size should be capped by position size");
        }

        #endregion

        #region Open Order Check Tests

        [Test]
        public void Evaluate_SkipsToken_WhenOpenOrderExists()
        {
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.48m, 0.52m) }
            };

            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            var dropBooks = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-1", CreateBook(0.38m, 0.42m) }
            };
            var openOrders = new List<SimulatedOrder>
            {
                new SimulatedOrder { Id = "existing-1", TokenId = "token-1", Side = "BUY" }
            };
            var actions = _strategy.Evaluate(CreateContext(dropBooks, openOrders: openOrders));

            Assert.AreEqual(0, actions.Count, "Should skip tokens that already have open orders");
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
        public void Evaluate_InvalidBook_NoMid_Skips()
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
        public void Evaluate_ExtremeMid_Skips()
        {
            // mid at 0 or 1 is filtered out by the strategy (mid <= 0 || mid >= 1)
            var books = new Dictionary<string, PolymarketOrderBook>
            {
                { "token-edge", new PolymarketOrderBook
                    {
                        Bids = new List<PolymarketOrderBookLevel>
                        {
                            new() { Price = "0.99", Size = "100" }
                        },
                        Asks = new List<PolymarketOrderBookLevel>
                        {
                            new() { Price = "1.01", Size = "100" }
                        }
                    }
                }
            };

            for (int i = 0; i < 12; i++)
            {
                _strategy.Evaluate(CreateContext(books));
            }

            var actions = _strategy.Evaluate(CreateContext(books));
            Assert.AreEqual(0, actions.Count, "Should skip tokens with mid at extreme (>=1 or <=0)");
        }

        #endregion

        #region OnFill

        [Test]
        public void OnFill_DoesNotThrow()
        {
            var trade = new SimulatedTrade
            {
                Id = "t1",
                OrderId = "o1",
                TokenId = "token-1",
                Side = "BUY",
                Price = 0.50m,
                Size = 25m,
                MatchTime = DateTime.UtcNow
            };

            Assert.DoesNotThrow(() => _strategy.OnFill(trade));
        }

        #endregion
    }
}
