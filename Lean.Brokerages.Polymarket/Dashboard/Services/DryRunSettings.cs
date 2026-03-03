using System.Collections.Generic;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public class DryRunSettings
    {
        public bool Enabled { get; set; }
        public decimal InitialBalance { get; set; } = 10000m;
        public int TickIntervalMs { get; set; } = 5000;
        public string StrategyName { get; set; } = "MeanReversion";
        public int AutoSubscribeTopN { get; set; } = 10;
        public int OrderBookStaleThresholdSeconds { get; set; } = 60;
        public Dictionary<string, string> StrategyParameters { get; set; } = new();
    }
}
