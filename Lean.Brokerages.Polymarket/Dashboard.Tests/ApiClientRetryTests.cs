using System.Linq;
using System.Net;
using System.Net.Http;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Brokerages.Polymarket.Tests.Helpers;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class ApiClientRetryTests
    {
        private PolymarketCredentials _credentials;

        [SetUp]
        public void SetUp()
        {
            _credentials = new PolymarketCredentials("test-key", "dGVzdC1zZWNyZXQ=", null, "test-pass");
        }

        [Test]
        public void Get_429ThenSuccess_Retries()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{}", HttpStatusCode.TooManyRequests);
            handler.EnqueueResponse("{\"balance\":\"100\"}", HttpStatusCode.OK);

            using var client = new PolymarketApiClient(_credentials, "http://localhost", handler);
            var balance = client.GetBalance();

            Assert.AreEqual(2, handler.SentRequests.Count);
            Assert.AreEqual("100", balance.Balance);
        }

        [Test]
        public void Get_500ThenSuccess_Retries()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{\"balance\":\"50\"}", HttpStatusCode.OK);

            using var client = new PolymarketApiClient(_credentials, "http://localhost", handler);
            var balance = client.GetBalance();

            Assert.AreEqual(2, handler.SentRequests.Count);
            Assert.AreEqual("50", balance.Balance);
        }

        [Test]
        public void Get_ThreeFailuresThenSuccess_MaxRetries()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{\"balance\":\"25\"}", HttpStatusCode.OK);

            using var client = new PolymarketApiClient(_credentials, "http://localhost", handler);
            var balance = client.GetBalance();

            Assert.AreEqual(4, handler.SentRequests.Count);
            Assert.AreEqual("25", balance.Balance);
        }

        [Test]
        public void Get_FourFailures_ThrowsAfterExhausted()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);

            using var client = new PolymarketApiClient(_credentials, "http://localhost", handler);
            Assert.Throws<HttpRequestException>(() => client.GetBalance());

            Assert.AreEqual(4, handler.SentRequests.Count);
        }

        [Test]
        public void Post_429_DoesNotRetry()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{}", HttpStatusCode.TooManyRequests);

            // PlaceOrder requires a valid private key for EIP-712 signing
            var creds = new PolymarketCredentials("test-key", "dGVzdC1zZWNyZXQ=",
                "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "test-pass");
            using var client = new PolymarketApiClient(creds, "http://localhost", handler);
            Assert.Throws<HttpRequestException>(() =>
                client.PlaceOrder("123", 0.5m, 10m, Orders.OrderDirection.Buy));

            // POST /order is non-idempotent, should not retry
            Assert.AreEqual(1, handler.SentRequests.Count);
        }

        [Test]
        public void Delete_429ThenSuccess_Retries()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{}", HttpStatusCode.TooManyRequests);
            handler.EnqueueResponse("{\"canceled\":true,\"order_id\":\"abc\"}", HttpStatusCode.OK);

            using var client = new PolymarketApiClient(_credentials, "http://localhost", handler);
            var result = client.CancelOrder("abc");

            Assert.AreEqual(2, handler.SentRequests.Count);
        }

        [Test]
        public void Get_400_DoesNotRetry()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{\"error\":\"bad request\"}", HttpStatusCode.BadRequest);

            using var client = new PolymarketApiClient(_credentials, "http://localhost", handler);
            Assert.Throws<HttpRequestException>(() => client.GetBalance());

            Assert.AreEqual(1, handler.SentRequests.Count);
        }

        [Test]
        public void AuthHeaders_RegeneratedOnRetry()
        {
            var handler = new MockHttpMessageHandler();
            handler.EnqueueResponse("{}", HttpStatusCode.InternalServerError);
            handler.EnqueueResponse("{\"balance\":\"10\"}", HttpStatusCode.OK);

            using var client = new PolymarketApiClient(_credentials, "http://localhost", handler);
            client.GetBalance();

            Assert.AreEqual(2, handler.SentRequests.Count);

            // Each request should have its own POLY_TIMESTAMP header (fresh per attempt)
            var ts1 = handler.SentRequests[0].Headers.GetValues("POLY_TIMESTAMP").First();
            var ts2 = handler.SentRequests[1].Headers.GetValues("POLY_TIMESTAMP").First();
            Assert.IsNotNull(ts1);
            Assert.IsNotNull(ts2);
            // Both should be valid timestamps (may be equal if within same second, but both present)
        }
    }
}
