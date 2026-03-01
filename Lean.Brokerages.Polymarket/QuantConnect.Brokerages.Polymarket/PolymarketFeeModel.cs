/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Polymarket
{
    /// <summary>
    /// Provides an implementation of <see cref="FeeModel"/> that models Polymarket order fees.
    /// Polymarket currently charges 0% maker/taker fees on trades.
    /// A ~2% fee is charged on winning positions at settlement.
    /// </summary>
    public class PolymarketFeeModel : FeeModel
    {
        /// <summary>
        /// Maker fee rate (currently 0%)
        /// </summary>
        public const decimal MakerFee = 0.0m;

        /// <summary>
        /// Taker fee rate (currently 0%)
        /// </summary>
        public const decimal TakerFee = 0.0m;

        /// <summary>
        /// Settlement fee on winning positions (~2%)
        /// </summary>
        public const decimal SettlementFee = 0.02m;

        /// <summary>
        /// Gets the order fee for a Polymarket trade.
        /// Trading fees are currently 0%. Settlement fees are handled separately.
        /// </summary>
        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            var order = parameters.Order;
            var fee = order.Type == OrderType.Limit && !order.IsMarketable ? MakerFee : TakerFee;

            if (fee == 0m)
            {
                return OrderFee.Zero;
            }

            var security = parameters.Security;
            var unitPrice = order.Direction == OrderDirection.Buy ? security.AskPrice : security.BidPrice;

            if (order.Type == OrderType.Limit)
            {
                unitPrice = ((LimitOrder)order).LimitPrice;
            }

            return new OrderFee(new CashAmount(
                unitPrice * order.AbsoluteQuantity * fee,
                "USDC"));
        }
    }
}
