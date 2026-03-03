using System.Collections.Generic;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Models
{
    public class CredentialsUpdateRequest
    {
        public string ApiKey { get; set; }
        public string ApiSecret { get; set; }
        public string PrivateKey { get; set; }
        public string Passphrase { get; set; }
    }

    public class CredentialsResponse
    {
        public string ApiKey { get; set; }
        public string ApiSecret { get; set; }
        public string PrivateKey { get; set; }
        public string Passphrase { get; set; }
        public bool HasCredentials { get; set; }
    }

    public class SystemSettingsRequest
    {
        public bool? DryRunEnabled { get; set; }
        public decimal? InitialBalance { get; set; }
        public int? TickIntervalMs { get; set; }
        public string StrategyName { get; set; }
        public int? AutoSubscribeTopN { get; set; }
    }

    public class SystemSettingsResponse
    {
        public bool DryRunEnabled { get; set; }
        public decimal InitialBalance { get; set; }
        public int TickIntervalMs { get; set; }
        public string StrategyName { get; set; }
        public int AutoSubscribeTopN { get; set; }
    }
}
