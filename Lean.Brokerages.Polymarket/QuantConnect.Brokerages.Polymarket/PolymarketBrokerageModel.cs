/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System.Collections.Generic;
using System.Linq;
using QuantConnect.Benchmarks;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Polymarket
{
    /// <summary>
    /// Polymarket brokerage model implementing trading rules and validation
    /// for the Polymarket prediction market platform.
    /// </summary>
    public class PolymarketBrokerageModel : DefaultBrokerageModel
    {
        /// <summary>
        /// The polymarket market identifier string
        /// </summary>
        public const string PolymarketMarket = "polymarket";

        private static readonly HashSet<OrderType> SupportedOrderTypes = new()
        {
            OrderType.Limit,
            OrderType.Market
        };

        /// <summary>
        /// Gets a map of the default markets to be used for each security type
        /// </summary>
        public override IReadOnlyDictionary<SecurityType, string> DefaultMarkets { get; } = GetDefaultMarkets();

        /// <summary>
        /// Constructor for Polymarket brokerage model. Always uses Cash account type.
        /// </summary>
        public PolymarketBrokerageModel() : base(AccountType.Cash)
        {
        }

        /// <summary>
        /// Returns true if the brokerage can accept the given order
        /// </summary>
        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            if (!IsValidOrderSize(security, order.Quantity, out message))
            {
                return false;
            }

            message = null;

            if (security.Type != SecurityType.Crypto)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    Messages.DefaultBrokerageModel.UnsupportedSecurityType(this, security));
                return false;
            }

            if (security.Symbol.ID.Market != PolymarketMarket)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "WrongMarket",
                    $"Polymarket brokerage model only supports market '{PolymarketMarket}', " +
                    $"received: '{security.Symbol.ID.Market}'");
                return false;
            }

            if (!SupportedOrderTypes.Contains(order.Type))
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    Messages.DefaultBrokerageModel.UnsupportedOrderType(this, order, SupportedOrderTypes));
                return false;
            }

            // Validate limit price is within [0, 1] for prediction market tokens
            if (order.Type == OrderType.Limit)
            {
                var limitPrice = ((LimitOrder)order).LimitPrice;
                if (limitPrice < 0m || limitPrice > 1m)
                {
                    message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidPrice",
                        $"Polymarket limit price must be between 0.00 and 1.00, received: {limitPrice}");
                    return false;
                }
            }

            return base.CanSubmitOrder(security, order, out message);
        }

        /// <summary>
        /// Polymarket does not support order updates. Cancel and resubmit instead.
        /// </summary>
        public override bool CanUpdateOrder(Security security, Order order, UpdateOrderRequest request, out BrokerageMessageEvent message)
        {
            message = new BrokerageMessageEvent(BrokerageMessageType.Warning, 0,
                Messages.DefaultBrokerageModel.OrderUpdateNotSupported);
            return false;
        }

        /// <summary>
        /// Provides Polymarket fee model
        /// </summary>
        public override IFeeModel GetFeeModel(Security security)
        {
            return new PolymarketFeeModel();
        }

        /// <summary>
        /// Gets the leverage for Polymarket. Always 1 (no margin).
        /// </summary>
        public override decimal GetLeverage(Security security)
        {
            return 1m;
        }

        /// <summary>
        /// Gets the benchmark for Polymarket. Uses a fixed benchmark since there's no standard index.
        /// </summary>
        public override IBenchmark GetBenchmark(SecurityManager securities)
        {
            // Use a fixed benchmark since prediction markets don't have a standard index
            return new FuncBenchmark(dt => 0m);
        }

        private static IReadOnlyDictionary<SecurityType, string> GetDefaultMarkets()
        {
            var map = DefaultMarketMap.ToDictionary();
            map[SecurityType.Crypto] = PolymarketMarket;
            return map.ToReadOnlyDictionary();
        }
    }
}
