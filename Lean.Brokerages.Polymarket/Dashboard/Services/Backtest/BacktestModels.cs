using System;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest
{
    public class HistoricalBar
    {
        public DateTime Time { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
    }

    public class BacktestResult
    {
        public string StrategyName { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new();
        public BacktestMetricsSummary Metrics { get; set; }
        public List<SimulatedTrade> Trades { get; set; } = new();
        public List<(DateTime Time, decimal Equity)> EquityCurve { get; set; } = new();
        public decimal InitialBalance { get; set; }
        public decimal FinalBalance { get; set; }
        public int TicksProcessed { get; set; }
        public int TokensProcessed { get; set; }
        public double ElapsedMs { get; set; }
    }

    public class BacktestComparisonResult
    {
        public DateTime RunDate { get; set; }
        public DateTime DataStartDate { get; set; }
        public DateTime DataEndDate { get; set; }
        public int TotalTokens { get; set; }
        public int TotalBars { get; set; }
        public List<BacktestResult> Results { get; set; } = new();
        public double TotalElapsedMs { get; set; }
    }

    public class BacktestMetricsSummary
    {
        public decimal TotalPnl { get; set; }
        public decimal TotalPnlPercent { get; set; }
        public decimal SharpeRatio { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal MaxDrawdownPercent { get; set; }
        public int TradeCount { get; set; }
        public decimal WinRate { get; set; }
        public decimal AvgTradePnl { get; set; }
        public decimal ProfitFactor { get; set; }
        public int OrderCount { get; set; }
        public int FillCount { get; set; }
        public decimal FillRate { get; set; }
    }

    public class BacktestRequest
    {
        public int Days { get; set; } = 30;
        public decimal InitialBalance { get; set; } = 10000m;
        public string Strategy { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
    }
}
