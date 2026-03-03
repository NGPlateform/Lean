using System;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class DryRunModelsTests
    {
        #region SimulatedOrder Tests

        [Test]
        public void SimulatedOrder_RemainingSize_Computed()
        {
            var order = new SimulatedOrder
            {
                Id = "DRY-000001",
                TokenId = "token-1",
                Side = "BUY",
                Price = 0.50m,
                OriginalSize = 100m,
                FilledSize = 30m,
                Status = "LIVE"
            };

            Assert.AreEqual(70m, order.RemainingSize);
        }

        [Test]
        public void SimulatedOrder_FullyFilled_RemainingZero()
        {
            var order = new SimulatedOrder
            {
                OriginalSize = 50m,
                FilledSize = 50m
            };

            Assert.AreEqual(0m, order.RemainingSize);
        }

        [Test]
        public void SimulatedOrder_NoFills_RemainingEqualsOriginal()
        {
            var order = new SimulatedOrder
            {
                OriginalSize = 100m,
                FilledSize = 0m
            };

            Assert.AreEqual(100m, order.RemainingSize);
        }

        #endregion

        #region SimulatedPosition Tests

        [Test]
        public void SimulatedPosition_UnrealizedPnl_Positive()
        {
            var pos = new SimulatedPosition
            {
                TokenId = "token-1",
                Size = 100m,
                AvgPrice = 0.40m,
                CurrentPrice = 0.55m
            };

            // UnrealizedPnl = (0.55 - 0.40) * 100 = 15
            Assert.AreEqual(15m, pos.UnrealizedPnl);
        }

        [Test]
        public void SimulatedPosition_UnrealizedPnl_Negative()
        {
            var pos = new SimulatedPosition
            {
                TokenId = "token-1",
                Size = 100m,
                AvgPrice = 0.60m,
                CurrentPrice = 0.45m
            };

            // UnrealizedPnl = (0.45 - 0.60) * 100 = -15
            Assert.AreEqual(-15m, pos.UnrealizedPnl);
        }

        [Test]
        public void SimulatedPosition_UnrealizedPnl_ZeroSize()
        {
            var pos = new SimulatedPosition
            {
                TokenId = "token-1",
                Size = 0m,
                AvgPrice = 0.50m,
                CurrentPrice = 0.60m
            };

            Assert.AreEqual(0m, pos.UnrealizedPnl);
        }

        [Test]
        public void SimulatedPosition_UnrealizedPnl_SamePrice()
        {
            var pos = new SimulatedPosition
            {
                TokenId = "token-1",
                Size = 100m,
                AvgPrice = 0.50m,
                CurrentPrice = 0.50m
            };

            Assert.AreEqual(0m, pos.UnrealizedPnl);
        }

        #endregion

        #region SimulatedTrade Tests

        [Test]
        public void SimulatedTrade_Properties()
        {
            var now = DateTime.UtcNow;
            var trade = new SimulatedTrade
            {
                Id = "trade-001",
                OrderId = "DRY-000001",
                TokenId = "token-abc",
                Side = "BUY",
                Price = 0.65m,
                Size = 25m,
                MatchTime = now
            };

            Assert.AreEqual("trade-001", trade.Id);
            Assert.AreEqual("DRY-000001", trade.OrderId);
            Assert.AreEqual("token-abc", trade.TokenId);
            Assert.AreEqual("BUY", trade.Side);
            Assert.AreEqual(0.65m, trade.Price);
            Assert.AreEqual(25m, trade.Size);
            Assert.AreEqual(now, trade.MatchTime);
        }

        #endregion

        #region DryRunLogEntry Tests

        [Test]
        public void DryRunLogEntry_Properties()
        {
            var now = DateTime.UtcNow;
            var entry = new DryRunLogEntry
            {
                Timestamp = now,
                Source = "Engine",
                Level = "Info",
                Message = "Test message"
            };

            Assert.AreEqual(now, entry.Timestamp);
            Assert.AreEqual("Engine", entry.Source);
            Assert.AreEqual("Info", entry.Level);
            Assert.AreEqual("Test message", entry.Message);
        }

        #endregion

        #region DryRunSettings Tests

        [Test]
        public void DryRunSettings_Defaults()
        {
            var settings = new DryRunSettings();

            Assert.IsFalse(settings.Enabled);
            Assert.AreEqual(10000m, settings.InitialBalance);
            Assert.AreEqual(5000, settings.TickIntervalMs);
            Assert.AreEqual("MeanReversion", settings.StrategyName);
            Assert.AreEqual(10, settings.AutoSubscribeTopN);
            Assert.IsNotNull(settings.StrategyParameters);
            Assert.AreEqual(0, settings.StrategyParameters.Count);
        }

        [Test]
        public void DryRunSettings_CustomValues()
        {
            var settings = new DryRunSettings
            {
                Enabled = true,
                InitialBalance = 5000m,
                TickIntervalMs = 2000,
                StrategyName = "MarketMaking",
                AutoSubscribeTopN = 5,
                StrategyParameters = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "OrderSize", "50" }
                }
            };

            Assert.IsTrue(settings.Enabled);
            Assert.AreEqual(5000m, settings.InitialBalance);
            Assert.AreEqual(2000, settings.TickIntervalMs);
            Assert.AreEqual("MarketMaking", settings.StrategyName);
            Assert.AreEqual(5, settings.AutoSubscribeTopN);
            Assert.AreEqual(1, settings.StrategyParameters.Count);
        }

        #endregion

        #region StrategyAction Tests

        [Test]
        public void PlaceOrderAction_Properties()
        {
            var action = new PlaceOrderAction
            {
                TokenId = "token-abc",
                Price = 0.50m,
                Size = 25m,
                Side = "BUY",
                Reason = "Test buy"
            };

            Assert.AreEqual("token-abc", action.TokenId);
            Assert.AreEqual(0.50m, action.Price);
            Assert.AreEqual(25m, action.Size);
            Assert.AreEqual("BUY", action.Side);
            Assert.AreEqual("Test buy", action.Reason);
        }

        [Test]
        public void CancelOrderAction_Properties()
        {
            var action = new CancelOrderAction
            {
                OrderId = "DRY-000001",
                Reason = "Requote"
            };

            Assert.AreEqual("DRY-000001", action.OrderId);
            Assert.AreEqual("Requote", action.Reason);
        }

        [Test]
        public void PlaceOrderAction_IsStrategyAction()
        {
            StrategyAction action = new PlaceOrderAction();
            Assert.IsInstanceOf<StrategyAction>(action);
        }

        [Test]
        public void CancelOrderAction_IsStrategyAction()
        {
            StrategyAction action = new CancelOrderAction();
            Assert.IsInstanceOf<StrategyAction>(action);
        }

        #endregion

        #region StrategyContext Tests

        [Test]
        public void StrategyContext_AllProperties()
        {
            var ctx = new StrategyContext
            {
                CurrentTime = DateTime.UtcNow,
                Markets = new System.Collections.Generic.List<DashboardMarket>(),
                OrderBooks = new System.Collections.Generic.Dictionary<string, QuantConnect.Brokerages.Polymarket.Api.Models.PolymarketOrderBook>(),
                Balance = 10000m,
                Positions = new System.Collections.Generic.Dictionary<string, SimulatedPosition>(),
                OpenOrders = new System.Collections.Generic.List<SimulatedOrder>(),
                RecentTrades = new System.Collections.Generic.List<SimulatedTrade>(),
                RealizedPnl = 100m,
                UnrealizedPnl = -50m
            };

            Assert.AreEqual(10000m, ctx.Balance);
            Assert.AreEqual(100m, ctx.RealizedPnl);
            Assert.AreEqual(-50m, ctx.UnrealizedPnl);
            Assert.IsNotNull(ctx.Markets);
            Assert.IsNotNull(ctx.OrderBooks);
            Assert.IsNotNull(ctx.Positions);
            Assert.IsNotNull(ctx.OpenOrders);
            Assert.IsNotNull(ctx.RecentTrades);
        }

        #endregion
    }
}
