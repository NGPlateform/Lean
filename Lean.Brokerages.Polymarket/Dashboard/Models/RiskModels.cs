using System.Collections.Generic;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Models
{
    public class RiskSettings
    {
        public decimal DailySpendingLimit { get; set; } = 1000m;
        public decimal TotalExposureLimit { get; set; } = 5000m;
        public decimal PerMarketPositionLimit { get; set; } = 500m;
        public decimal PerTradePositionLimit { get; set; } = 200m;
        public decimal DailyLossLimit { get; set; } = 500m;
        public decimal MaxDrawdownLimit { get; set; } = 2000m;
        public Dictionary<string, decimal> PerStrategyCapitalLimits { get; set; } = new();
    }

    public class RiskCheckResult
    {
        public bool Allowed { get; set; }
        public string Reason { get; set; }

        public static RiskCheckResult Allow() => new() { Allowed = true };
        public static RiskCheckResult Deny(string reason) => new() { Allowed = false, Reason = reason };
    }

    public class RiskStatus
    {
        public decimal DailySpending { get; set; }
        public decimal DailySpendingLimit { get; set; }
        public decimal TotalExposure { get; set; }
        public decimal TotalExposureLimit { get; set; }
        public decimal DailyPnl { get; set; }
        public decimal DailyLossLimit { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal MaxDrawdownLimit { get; set; }
        public decimal PerMarketPositionLimit { get; set; }
        public decimal PerTradePositionLimit { get; set; }
        public List<RiskAlert> Alerts { get; set; } = new();
    }

    public class RiskAlert
    {
        public string Level { get; set; } // "warning", "critical"
        public string Message { get; set; }
        public string Metric { get; set; }
        public decimal Current { get; set; }
        public decimal Limit { get; set; }
    }
}
