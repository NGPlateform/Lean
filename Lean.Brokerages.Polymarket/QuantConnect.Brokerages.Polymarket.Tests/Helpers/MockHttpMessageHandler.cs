/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.Polymarket.Tests.Helpers
{
    /// <summary>
    /// Mock HTTP message handler for testing API clients without network calls.
    /// Matches request URLs via Contains() and records sent requests for header verification.
    /// </summary>
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<(string UrlContains, HttpResponseMessage Response)> _responses = new();
        private HttpResponseMessage _defaultResponse;

        /// <summary>
        /// All requests sent through this handler, for inspection/assertion
        /// </summary>
        public List<HttpRequestMessage> SentRequests { get; } = new();

        /// <summary>
        /// Registers a canned response for any URL containing the given substring
        /// </summary>
        public void SetResponse(string urlContains, string jsonBody, HttpStatusCode code = HttpStatusCode.OK)
        {
            _responses.Add((urlContains, new HttpResponseMessage(code)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            }));
        }

        /// <summary>
        /// Sets a fallback response when no URL pattern matches
        /// </summary>
        public void SetDefaultResponse(string jsonBody, HttpStatusCode code = HttpStatusCode.OK)
        {
            _defaultResponse = new HttpResponseMessage(code)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);

            var url = request.RequestUri?.ToString() ?? string.Empty;
            var match = _responses.FirstOrDefault(r => url.Contains(r.UrlContains));

            if (match.Response != null)
            {
                return Task.FromResult(match.Response);
            }

            if (_defaultResponse != null)
            {
                return Task.FromResult(_defaultResponse);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"no mock configured\"}", Encoding.UTF8, "application/json")
            });
        }
    }
}
