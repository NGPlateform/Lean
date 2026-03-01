/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.Polymarket.Api.Models
{
    /// <summary>
    /// Represents a Polymarket CLOB order
    /// </summary>
    public class PolymarketOrder
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("owner")]
        public string Owner { get; set; }

        [JsonProperty("market")]
        public string Market { get; set; }

        [JsonProperty("asset_id")]
        public string AssetId { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("original_size")]
        public string OriginalSize { get; set; }

        [JsonProperty("size_matched")]
        public string SizeMatched { get; set; }

        [JsonProperty("price")]
        public string Price { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("expiration")]
        public string Expiration { get; set; }

        [JsonProperty("associate_trades")]
        public List<PolymarketTrade> AssociateTrades { get; set; }
    }

    /// <summary>
    /// Represents a Polymarket trade
    /// </summary>
    public class PolymarketTrade
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("taker_order_id")]
        public string TakerOrderId { get; set; }

        [JsonProperty("market")]
        public string Market { get; set; }

        [JsonProperty("asset_id")]
        public string AssetId { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("size")]
        public string Size { get; set; }

        [JsonProperty("price")]
        public string Price { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("match_time")]
        public DateTime MatchTime { get; set; }

        [JsonProperty("fee_rate_bps")]
        public string FeeRateBps { get; set; }

        [JsonProperty("owner")]
        public string Owner { get; set; }

        [JsonProperty("maker_address")]
        public string MakerAddress { get; set; }

        [JsonProperty("transaction_hash")]
        public string TransactionHash { get; set; }
    }

    /// <summary>
    /// Represents a user position on Polymarket
    /// </summary>
    public class PolymarketPosition
    {
        [JsonProperty("asset_id")]
        public string AssetId { get; set; }

        [JsonProperty("condition_id")]
        public string ConditionId { get; set; }

        [JsonProperty("size")]
        public string Size { get; set; }

        [JsonProperty("avg_price")]
        public string AvgPrice { get; set; }

        [JsonProperty("cur_price")]
        public string CurPrice { get; set; }

        [JsonProperty("realized_pnl")]
        public string RealizedPnl { get; set; }

        [JsonProperty("unrealized_pnl")]
        public string UnrealizedPnl { get; set; }
    }

    /// <summary>
    /// Represents balance information
    /// </summary>
    public class PolymarketBalance
    {
        [JsonProperty("balance")]
        public string Balance { get; set; }

        [JsonProperty("allowance")]
        public string Allowance { get; set; }
    }

    /// <summary>
    /// Order book snapshot
    /// </summary>
    public class PolymarketOrderBook
    {
        [JsonProperty("market")]
        public string Market { get; set; }

        [JsonProperty("asset_id")]
        public string AssetId { get; set; }

        [JsonProperty("bids")]
        public List<PolymarketOrderBookLevel> Bids { get; set; }

        [JsonProperty("asks")]
        public List<PolymarketOrderBookLevel> Asks { get; set; }

        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }
    }

    /// <summary>
    /// An order book price level
    /// </summary>
    public class PolymarketOrderBookLevel
    {
        [JsonProperty("price")]
        public string Price { get; set; }

        [JsonProperty("size")]
        public string Size { get; set; }
    }

    /// <summary>
    /// Response from POST /order
    /// </summary>
    public class PolymarketOrderResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("errorMsg")]
        public string ErrorMsg { get; set; }

        [JsonProperty("orderID")]
        public string OrderId { get; set; }

        [JsonProperty("transactID")]
        public string TransactId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
    }

    /// <summary>
    /// Response from DELETE /order
    /// </summary>
    public class PolymarketCancelResponse
    {
        [JsonProperty("canceled")]
        public bool Canceled { get; set; }

        [JsonProperty("not_canceled")]
        public bool NotCanceled { get; set; }

        [JsonProperty("orderID")]
        public string OrderId { get; set; }
    }

    /// <summary>
    /// WebSocket message types
    /// </summary>
    public static class PolymarketWsMessageType
    {
        public const string PriceChange = "price_change";
        public const string TradeUpdate = "trade";
        public const string OrderUpdate = "order";
        public const string BookSnapshot = "book";
    }

    /// <summary>
    /// Represents a WebSocket message from Polymarket
    /// </summary>
    public class PolymarketWsMessage
    {
        [JsonProperty("event_type")]
        public string EventType { get; set; }

        [JsonProperty("asset_id")]
        public string AssetId { get; set; }

        [JsonProperty("market")]
        public string Market { get; set; }

        [JsonProperty("price")]
        public string Price { get; set; }

        [JsonProperty("size")]
        public string Size { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        /// <summary>
        /// For order book updates
        /// </summary>
        [JsonProperty("changes")]
        public List<List<string>> Changes { get; set; }

        /// <summary>
        /// For user order status updates
        /// </summary>
        [JsonProperty("order")]
        public PolymarketOrder Order { get; set; }

        /// <summary>
        /// For trade fills
        /// </summary>
        [JsonProperty("trade")]
        public PolymarketTrade Trade { get; set; }
    }

    /// <summary>
    /// WebSocket subscription request
    /// </summary>
    public class PolymarketWsSubscription
    {
        [JsonProperty("auth")]
        public PolymarketWsAuth Auth { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("markets")]
        public List<string> Markets { get; set; }

        [JsonProperty("assets_ids")]
        public List<string> AssetIds { get; set; }
    }

    /// <summary>
    /// WebSocket authentication
    /// </summary>
    public class PolymarketWsAuth
    {
        [JsonProperty("apiKey")]
        public string ApiKey { get; set; }

        [JsonProperty("secret")]
        public string Secret { get; set; }

        [JsonProperty("passphrase")]
        public string Passphrase { get; set; }
    }

    /// <summary>
    /// Request body for placing an order
    /// </summary>
    public class PolymarketPlaceOrderRequest
    {
        [JsonProperty("order")]
        public PolymarketSignedOrder Order { get; set; }

        [JsonProperty("owner")]
        public string Owner { get; set; }

        [JsonProperty("orderType")]
        public string OrderType { get; set; }
    }

    /// <summary>
    /// A signed order ready for submission
    /// </summary>
    public class PolymarketSignedOrder
    {
        [JsonProperty("salt")]
        public string Salt { get; set; }

        [JsonProperty("maker")]
        public string Maker { get; set; }

        [JsonProperty("signer")]
        public string Signer { get; set; }

        [JsonProperty("taker")]
        public string Taker { get; set; }

        [JsonProperty("tokenId")]
        public string TokenId { get; set; }

        [JsonProperty("makerAmount")]
        public string MakerAmount { get; set; }

        [JsonProperty("takerAmount")]
        public string TakerAmount { get; set; }

        [JsonProperty("expiration")]
        public string Expiration { get; set; }

        [JsonProperty("nonce")]
        public string Nonce { get; set; }

        [JsonProperty("feeRateBps")]
        public string FeeRateBps { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("signatureType")]
        public string SignatureType { get; set; }

        [JsonProperty("signature")]
        public string Signature { get; set; }
    }
}
