using System;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public class SimulatedOrder
    {
        public string Id { get; set; }
        public string TokenId { get; set; }
        public string Side { get; set; }
        public decimal Price { get; set; }
        public decimal OriginalSize { get; set; }
        public decimal FilledSize { get; set; }
        public decimal RemainingSize => OriginalSize - FilledSize;
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Source { get; set; }
    }

    public class SimulatedTrade
    {
        public string Id { get; set; }
        public string OrderId { get; set; }
        public string TokenId { get; set; }
        public string Side { get; set; }
        public decimal Price { get; set; }
        public decimal Size { get; set; }
        public DateTime MatchTime { get; set; }
    }

    public class SimulatedPosition
    {
        public string TokenId { get; set; }
        public decimal Size { get; set; }
        public decimal AvgPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal RealizedPnl { get; set; }

        public decimal UnrealizedPnl =>
            Size != 0 ? (CurrentPrice - AvgPrice) * Size : 0;

        public decimal TotalCost { get; set; }
    }

    public class DryRunLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Source { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
    }
}
