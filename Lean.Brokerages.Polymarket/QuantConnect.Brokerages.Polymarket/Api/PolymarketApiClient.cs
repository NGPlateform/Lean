/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using QuantConnect.Orders;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Polymarket.Api
{
    /// <summary>
    /// REST API client for the Polymarket CLOB API
    /// </summary>
    public class PolymarketApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly PolymarketCredentials _credentials;
        private readonly EIP712Signer _signer;
        private readonly string _baseUrl;

        /// <summary>
        /// Creates a new Polymarket API client
        /// </summary>
        public PolymarketApiClient(PolymarketCredentials credentials, string baseUrl = "https://clob.polymarket.com")
        {
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(credentials.PrivateKey))
            {
                _signer = new EIP712Signer(credentials.PrivateKey);
            }
        }

        /// <summary>
        /// Gets all open orders
        /// </summary>
        public List<PolymarketOrder> GetOpenOrders()
        {
            var response = AuthenticatedGet("/orders?status=live&status=delayed");
            return JsonConvert.DeserializeObject<List<PolymarketOrder>>(response) ?? new List<PolymarketOrder>();
        }

        /// <summary>
        /// Gets user positions
        /// </summary>
        public List<PolymarketPosition> GetPositions()
        {
            var response = AuthenticatedGet("/positions");
            return JsonConvert.DeserializeObject<List<PolymarketPosition>>(response) ?? new List<PolymarketPosition>();
        }

        /// <summary>
        /// Gets user USDC balance
        /// </summary>
        public PolymarketBalance GetBalance()
        {
            var response = AuthenticatedGet("/balance");
            return JsonConvert.DeserializeObject<PolymarketBalance>(response) ?? new PolymarketBalance();
        }

        /// <summary>
        /// Gets the order book for a specific token
        /// </summary>
        public PolymarketOrderBook GetOrderBook(string tokenId)
        {
            var response = Get($"/book?token_id={tokenId}");
            return JsonConvert.DeserializeObject<PolymarketOrderBook>(response);
        }

        /// <summary>
        /// Gets trade history
        /// </summary>
        public List<PolymarketTrade> GetTrades(string assetId = null, int limit = 100)
        {
            var url = $"/trades?limit={limit}";
            if (!string.IsNullOrEmpty(assetId))
            {
                url += $"&asset_id={assetId}";
            }

            var response = AuthenticatedGet(url);
            return JsonConvert.DeserializeObject<List<PolymarketTrade>>(response) ?? new List<PolymarketTrade>();
        }

        /// <summary>
        /// Places an order on Polymarket
        /// </summary>
        public PolymarketOrderResponse PlaceOrder(
            string tokenId,
            decimal price,
            decimal size,
            OrderDirection direction,
            bool isNegRisk = false)
        {
            if (_signer == null)
            {
                throw new InvalidOperationException("Cannot place orders without a private key");
            }

            var side = direction == OrderDirection.Buy ? (byte)0 : (byte)1;

            // USDC has 6 decimal places
            var usdcDecimals = 1_000_000m;
            BigInteger makerAmount, takerAmount;

            if (direction == OrderDirection.Buy)
            {
                // Buying: maker pays USDC, taker delivers tokens
                makerAmount = new BigInteger(Math.Round(price * size * usdcDecimals));
                takerAmount = new BigInteger(Math.Round(size * usdcDecimals));
            }
            else
            {
                // Selling: maker delivers tokens, taker pays USDC
                makerAmount = new BigInteger(Math.Round(size * usdcDecimals));
                takerAmount = new BigInteger(Math.Round(price * size * usdcDecimals));
            }

            var orderData = new PolymarketOrderData
            {
                Salt = EIP712Signer.GenerateSalt(),
                Maker = _signer.Address,
                Signer = _signer.Address,
                Taker = "0x0000000000000000000000000000000000000000",
                TokenId = BigInteger.Parse(tokenId),
                MakerAmount = makerAmount,
                TakerAmount = takerAmount,
                Expiration = BigInteger.Zero,
                Nonce = BigInteger.Zero,
                FeeRateBps = BigInteger.Zero,
                Side = side,
                SignatureType = 0 // EOA
            };

            var signature = _signer.SignOrder(orderData, isNegRisk);

            var signedOrder = new PolymarketSignedOrder
            {
                Salt = orderData.Salt.ToString(),
                Maker = orderData.Maker,
                Signer = orderData.Signer,
                Taker = orderData.Taker,
                TokenId = tokenId,
                MakerAmount = orderData.MakerAmount.ToString(),
                TakerAmount = orderData.TakerAmount.ToString(),
                Expiration = "0",
                Nonce = "0",
                FeeRateBps = "0",
                Side = side == 0 ? "BUY" : "SELL",
                SignatureType = "0",
                Signature = signature
            };

            var request = new PolymarketPlaceOrderRequest
            {
                Order = signedOrder,
                Owner = _signer.Address,
                OrderType = "GTC"
            };

            var json = JsonConvert.SerializeObject(request);
            var response = AuthenticatedPost("/order", json);
            return JsonConvert.DeserializeObject<PolymarketOrderResponse>(response);
        }

        /// <summary>
        /// Cancels an order
        /// </summary>
        public PolymarketCancelResponse CancelOrder(string orderId)
        {
            var response = AuthenticatedDelete($"/order/{orderId}");
            return JsonConvert.DeserializeObject<PolymarketCancelResponse>(response);
        }

        /// <summary>
        /// Cancels all open orders
        /// </summary>
        public void CancelAllOrders()
        {
            AuthenticatedDelete("/orders");
        }

        private string Get(string path)
        {
            var url = $"{_baseUrl}{path}";
            var response = _httpClient.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().Result;
        }

        private string AuthenticatedGet(string path)
        {
            var url = $"{_baseUrl}{path}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(request, "GET", path);
            var response = _httpClient.SendAsync(request).Result;
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().Result;
        }

        private string AuthenticatedPost(string path, string body)
        {
            var url = $"{_baseUrl}{path}";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(request, "POST", path, body);
            var response = _httpClient.SendAsync(request).Result;
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().Result;
        }

        private string AuthenticatedDelete(string path)
        {
            var url = $"{_baseUrl}{path}";
            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            AddAuthHeaders(request, "DELETE", path);
            var response = _httpClient.SendAsync(request).Result;
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().Result;
        }

        private void AddAuthHeaders(HttpRequestMessage request, string method, string path, string body = "")
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = $"{timestamp}{method}{path}{body}";
            var signature = CreateHmacSignature(message, _credentials.ApiSecret);

            request.Headers.Add("POLY_API_KEY", _credentials.ApiKey);
            request.Headers.Add("POLY_SIGNATURE", signature);
            request.Headers.Add("POLY_TIMESTAMP", timestamp);
            request.Headers.Add("POLY_PASSPHRASE", _credentials.Passphrase);
        }

        private static string CreateHmacSignature(string message, string secret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                return string.Empty;
            }

            var secretBytes = Convert.FromBase64String(secret);
            using var hmac = new HMACSHA256(secretBytes);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Dispose the HTTP client
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
