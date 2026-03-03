namespace QuantConnect.Brokerages.Polymarket.Dashboard.Models
{
    public class WalletBalanceResponse
    {
        public string Address { get; set; }
        public string UsdcBalance { get; set; }
        public string MaticBalance { get; set; }
        public string UsdcAllowance { get; set; }
    }

    public class TransferRequest
    {
        public string Amount { get; set; }
        public string FromAddress { get; set; }
    }

    public class TransactionParams
    {
        public string To { get; set; }
        public string Data { get; set; }
        public string Value { get; set; }
        public string ChainId { get; set; }
    }

    public class TransactionStatusResponse
    {
        public string TxHash { get; set; }
        public string Status { get; set; } // "pending", "confirmed", "failed"
        public int? BlockNumber { get; set; }
    }
}
