/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

namespace QuantConnect.Brokerages.Polymarket.Auth
{
    /// <summary>
    /// Holds Polymarket authentication credentials
    /// </summary>
    public class PolymarketCredentials
    {
        /// <summary>
        /// Polymarket CLOB API key
        /// </summary>
        public string ApiKey { get; }

        /// <summary>
        /// Polymarket CLOB API secret
        /// </summary>
        public string ApiSecret { get; }

        /// <summary>
        /// Ethereum private key for EIP-712 order signing
        /// </summary>
        public string PrivateKey { get; }

        /// <summary>
        /// Polymarket CLOB API passphrase
        /// </summary>
        public string Passphrase { get; }

        /// <summary>
        /// The Ethereum address derived from the private key
        /// </summary>
        public string Address { get; }

        /// <summary>
        /// Creates a new instance of <see cref="PolymarketCredentials"/>
        /// </summary>
        public PolymarketCredentials(string apiKey, string apiSecret, string privateKey, string passphrase)
        {
            ApiKey = apiKey;
            ApiSecret = apiSecret;
            PrivateKey = privateKey;
            Passphrase = passphrase;

            if (!string.IsNullOrWhiteSpace(privateKey))
            {
                var key = new Nethereum.Signer.EthECKey(privateKey);
                Address = key.GetPublicAddress();
            }
        }
    }
}
