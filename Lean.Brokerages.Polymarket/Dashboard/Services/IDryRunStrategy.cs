using System;
using System.Collections.Generic;
using QuantConnect.Brokerages.Polymarket.Api.Models;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public interface IDryRunStrategy
    {
        string Name { get; }
        string Description { get; }
        void Initialize(Dictionary<string, string> parameters);
        List<StrategyAction> Evaluate(StrategyContext context);
        void OnFill(SimulatedTrade trade);

        Dictionary<string, string> GetParameters() => new();
        List<MarketScore> GetMarketScores(StrategyContext context) => new();
    }

    public class StrategyContext
    {
        public DateTime CurrentTime { get; set; }
        public List<DashboardMarket> Markets { get; set; }
        public Dictionary<string, PolymarketOrderBook> OrderBooks { get; set; }
        public decimal Balance { get; set; }
        public Dictionary<string, SimulatedPosition> Positions { get; set; }
        public List<SimulatedOrder> OpenOrders { get; set; }
        public List<SimulatedTrade> RecentTrades { get; set; }
        public decimal RealizedPnl { get; set; }
        public decimal UnrealizedPnl { get; set; }
    }

    public abstract class StrategyAction
    {
        public string Reason { get; set; }
    }

    public class PlaceOrderAction : StrategyAction
    {
        public string TokenId { get; set; }
        public decimal Price { get; set; }
        public decimal Size { get; set; }
        public string Side { get; set; }
    }

    public class CancelOrderAction : StrategyAction
    {
        public string OrderId { get; set; }
    }

    public class MarketScore
    {
        public string TokenId { get; set; }
        public string Question { get; set; }
        public decimal Score { get; set; }
        public bool IsSelected { get; set; }
        public bool HasPosition { get; set; }
        public Dictionary<string, decimal> ScoreComponents { get; set; } = new();
    }
}
