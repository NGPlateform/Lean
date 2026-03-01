/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Polymarket
{
    /// <summary>
    /// Factory for creating Polymarket Brokerage instances.
    /// Uses MEF export for automatic discovery.
    /// </summary>
    [Export(typeof(IBrokerageFactory))]
    public class PolymarketBrokerageFactory : BrokerageFactory
    {
        /// <summary>
        /// Static constructor to register the Polymarket market identifier
        /// </summary>
        static PolymarketBrokerageFactory()
        {
            try
            {
                Market.Add("polymarket", 43);
            }
            catch (ArgumentException)
            {
                // Already registered
            }
        }

        /// <summary>
        /// Creates a new instance of the <see cref="PolymarketBrokerageFactory"/>
        /// </summary>
        public PolymarketBrokerageFactory() : base(typeof(PolymarketBrokerage))
        {
        }

        /// <summary>
        /// Gets the brokerage data required from configuration
        /// </summary>
        public override Dictionary<string, string> BrokerageData => new()
        {
            { "polymarket-api-key", Config.Get("polymarket-api-key") },
            { "polymarket-api-secret", Config.Get("polymarket-api-secret") },
            { "polymarket-private-key", Config.Get("polymarket-private-key") },
            { "polymarket-passphrase", Config.Get("polymarket-passphrase") }
        };

        /// <summary>
        /// Gets the brokerage model for Polymarket
        /// </summary>
        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider)
        {
            return new PolymarketBrokerageModel();
        }

        /// <summary>
        /// Creates a new Polymarket brokerage instance
        /// </summary>
        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var errors = new List<string>();

            var apiKey = Read<string>(job.BrokerageData, "polymarket-api-key", errors);
            var apiSecret = Read<string>(job.BrokerageData, "polymarket-api-secret", errors);
            var privateKey = Read<string>(job.BrokerageData, "polymarket-private-key", errors);
            var passphrase = Read<string>(job.BrokerageData, "polymarket-passphrase", errors);

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"PolymarketBrokerageFactory.CreateBrokerage(): Missing configuration: {string.Join(", ", errors)}");
            }

            return new PolymarketBrokerage(apiKey, apiSecret, privateKey, passphrase, algorithm);
        }

        /// <summary>
        /// Disposes the factory
        /// </summary>
        public override void Dispose()
        {
        }
    }
}
