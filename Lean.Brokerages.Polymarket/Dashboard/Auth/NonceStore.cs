using System;
using System.Collections.Concurrent;
using System.Linq;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Auth
{
    public class NonceStore
    {
        private readonly ConcurrentDictionary<string, (string Nonce, DateTime Expiry)> _nonces = new();
        private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(5);

        public string GenerateNonce(string address)
        {
            CleanExpired();
            var nonce = Guid.NewGuid().ToString("N");
            var key = address.ToLowerInvariant();
            _nonces[key] = (nonce, DateTime.UtcNow.Add(NonceTtl));
            return nonce;
        }

        public bool ConsumeNonce(string address, string nonce)
        {
            var key = address.ToLowerInvariant();
            if (!_nonces.TryRemove(key, out var stored))
                return false;

            if (stored.Expiry < DateTime.UtcNow)
                return false;

            return string.Equals(stored.Nonce, nonce, StringComparison.Ordinal);
        }

        private void CleanExpired()
        {
            var now = DateTime.UtcNow;
            var expired = _nonces.Where(kv => kv.Value.Expiry < now).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
                _nonces.TryRemove(key, out _);
        }
    }
}
