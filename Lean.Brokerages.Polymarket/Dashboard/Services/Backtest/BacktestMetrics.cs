using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest
{
    public static class BacktestMetrics
    {
        /// <summary>
        /// Computes all metrics from an equity curve and trade list.
        /// </summary>
        public static BacktestMetricsSummary Calculate(
            List<(DateTime Time, decimal Equity)> equityCurve,
            List<SimulatedTrade> trades,
            decimal initialBalance,
            int orderCount)
        {
            var summary = new BacktestMetricsSummary();

            if (equityCurve == null || equityCurve.Count == 0)
            {
                summary.TotalPnl = 0;
                summary.TotalPnlPercent = 0;
                summary.SharpeRatio = 0;
                summary.MaxDrawdown = 0;
                summary.MaxDrawdownPercent = 0;
                summary.TradeCount = 0;
                summary.WinRate = 0;
                summary.AvgTradePnl = 0;
                summary.ProfitFactor = 0;
                summary.OrderCount = orderCount;
                summary.FillCount = trades?.Count ?? 0;
                summary.FillRate = orderCount > 0 ? (decimal)(trades?.Count ?? 0) / orderCount : 0;
                return summary;
            }

            var finalEquity = equityCurve.Last().Equity;
            summary.TotalPnl = finalEquity - initialBalance;
            summary.TotalPnlPercent = initialBalance > 0 ? summary.TotalPnl / initialBalance * 100m : 0;

            // Sharpe ratio: annualized from 10-minute return intervals
            summary.SharpeRatio = CalculateSharpe(equityCurve);

            // Max drawdown
            var (maxDd, maxDdPct) = CalculateMaxDrawdown(equityCurve);
            summary.MaxDrawdown = maxDd;
            summary.MaxDrawdownPercent = maxDdPct;

            // Trade metrics
            summary.TradeCount = trades?.Count ?? 0;
            summary.OrderCount = orderCount;
            summary.FillCount = trades?.Count ?? 0;
            summary.FillRate = orderCount > 0 ? (decimal)summary.FillCount / orderCount : 0;

            if (trades != null && trades.Count > 0)
            {
                var roundTrips = CalculateRoundTrips(trades);
                if (roundTrips.Count > 0)
                {
                    var wins = roundTrips.Count(r => r > 0);
                    summary.WinRate = (decimal)wins / roundTrips.Count * 100m;
                    summary.AvgTradePnl = roundTrips.Average();

                    var totalProfit = roundTrips.Where(r => r > 0).Sum();
                    var totalLoss = Math.Abs(roundTrips.Where(r => r < 0).Sum());
                    summary.ProfitFactor = totalLoss > 0 ? totalProfit / totalLoss : totalProfit > 0 ? 999m : 0;
                }
            }

            return summary;
        }

        /// <summary>
        /// Annualized Sharpe ratio from 10-minute equity points.
        /// Periods per year = 365.25 * 24 * 6 = 52,596
        /// </summary>
        internal static decimal CalculateSharpe(List<(DateTime Time, decimal Equity)> equityCurve)
        {
            if (equityCurve.Count < 2) return 0;

            var returns = new List<decimal>();
            for (int i = 1; i < equityCurve.Count; i++)
            {
                var prev = equityCurve[i - 1].Equity;
                if (prev <= 0) continue;
                returns.Add((equityCurve[i].Equity - prev) / prev);
            }

            if (returns.Count < 2) return 0;

            var mean = returns.Average();
            var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
            var stdDev = (decimal)Math.Sqrt((double)variance);

            if (stdDev == 0) return 0;

            // Annualize: sqrt(periods_per_year) where period = 10 minutes
            var annualizationFactor = (decimal)Math.Sqrt(365.25 * 24 * 6);
            return mean / stdDev * annualizationFactor;
        }

        /// <summary>
        /// Maximum drawdown in absolute and percentage terms.
        /// </summary>
        internal static (decimal MaxDrawdown, decimal MaxDrawdownPercent) CalculateMaxDrawdown(
            List<(DateTime Time, decimal Equity)> equityCurve)
        {
            if (equityCurve.Count == 0) return (0, 0);

            decimal peak = equityCurve[0].Equity;
            decimal maxDd = 0;
            decimal maxDdPct = 0;

            foreach (var point in equityCurve)
            {
                if (point.Equity > peak)
                    peak = point.Equity;

                var dd = peak - point.Equity;
                if (dd > maxDd)
                {
                    maxDd = dd;
                    maxDdPct = peak > 0 ? dd / peak * 100m : 0;
                }
            }

            return (maxDd, maxDdPct);
        }

        /// <summary>
        /// Pairs BUY/SELL trades per tokenId to compute round-trip PnL.
        /// Returns a list of round-trip PnL values.
        /// </summary>
        internal static List<decimal> CalculateRoundTrips(List<SimulatedTrade> trades)
        {
            var roundTrips = new List<decimal>();
            // Track open cost per token: FIFO list of (price, remainingSize) for buys
            var openBuys = new Dictionary<string, List<(decimal Price, decimal Size)>>();

            foreach (var trade in trades.OrderBy(t => t.MatchTime))
            {
                if (trade.Side == "BUY")
                {
                    if (!openBuys.TryGetValue(trade.TokenId, out var list))
                    {
                        list = new List<(decimal, decimal)>();
                        openBuys[trade.TokenId] = list;
                    }
                    list.Add((trade.Price, trade.Size));
                }
                else // SELL
                {
                    if (!openBuys.TryGetValue(trade.TokenId, out var list) || list.Count == 0)
                        continue;

                    var remaining = trade.Size;
                    while (remaining > 0 && list.Count > 0)
                    {
                        var buy = list[0];
                        var matchSize = Math.Min(remaining, buy.Size);
                        var pnl = (trade.Price - buy.Price) * matchSize;
                        roundTrips.Add(pnl);

                        remaining -= matchSize;
                        if (matchSize >= buy.Size)
                        {
                            list.RemoveAt(0);
                        }
                        else
                        {
                            list[0] = (buy.Price, buy.Size - matchSize);
                        }
                    }
                }
            }

            return roundTrips;
        }
    }
}
