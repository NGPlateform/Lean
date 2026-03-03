using System;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Auth
{
    public class AuthSettings
    {
        public string JwtSecret { get; set; } = "";
        public int TokenExpiryHours { get; set; } = 24;
        public List<string> WhitelistedAddresses { get; set; } = new();
    }

    public class NonceResponse
    {
        public string Nonce { get; set; }
        public string Message { get; set; }
    }

    public class VerifyRequest
    {
        public string Address { get; set; }
        public string Signature { get; set; }
        public string Nonce { get; set; }
    }

    public class AuthResponse
    {
        public string Token { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Address { get; set; }
    }
}
