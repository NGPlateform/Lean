/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class PolymarketSymbolMapperTests
    {
        private PolymarketSymbolMapper _mapper;

        [SetUp]
        public void Setup()
        {
            _mapper = new PolymarketSymbolMapper();
            // Register test market
            try { Market.Add("polymarket", 43); } catch (ArgumentException) { }
        }

        [Test]
        public void AddMapping_ValidInputs_MapsCorrectly()
        {
            var tokenId = "1234567890";
            var ticker = "ETH5000MAR26YES";

            _mapper.AddMapping(ticker, tokenId, "condition_1");

            Assert.AreEqual(tokenId, _mapper.GetBrokerageSymbol(
                Symbol.Create(ticker, SecurityType.Crypto, "polymarket")));
        }

        [Test]
        public void GetLeanSymbol_ValidTokenId_ReturnsSymbol()
        {
            var tokenId = "9876543210";
            var ticker = "ETH5000MAR26NO";

            _mapper.AddMapping(ticker, tokenId, "condition_1");

            var symbol = _mapper.GetLeanSymbol(tokenId, SecurityType.Crypto, "polymarket");
            Assert.AreEqual(ticker, symbol.Value);
            Assert.AreEqual(SecurityType.Crypto, symbol.ID.SecurityType);
            Assert.AreEqual("polymarket", symbol.ID.Market);
        }

        [Test]
        public void GetBrokerageSymbol_UnknownSymbol_ThrowsException()
        {
            var symbol = Symbol.Create("UNKNOWNYES", SecurityType.Crypto, "polymarket");
            Assert.Throws<ArgumentException>(() => _mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void GetLeanSymbol_UnknownTokenId_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() =>
                _mapper.GetLeanSymbol("unknown_token_id", SecurityType.Crypto, "polymarket"));
        }

        [Test]
        public void GetBrokerageSymbol_NullSymbol_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() => _mapper.GetBrokerageSymbol(null));
        }

        [Test]
        public void GetBrokerageSymbol_WrongMarket_ThrowsException()
        {
            var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Kraken);
            Assert.Throws<ArgumentException>(() => _mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void AddMapping_EmptyTicker_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() =>
                _mapper.AddMapping("", "token123", "condition1"));
        }

        [Test]
        public void AddMapping_EmptyTokenId_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() =>
                _mapper.AddMapping("TICKER", "", "condition1"));
        }

        [Test]
        public void IsKnownTokenId_AfterMapping_ReturnsTrue()
        {
            _mapper.AddMapping("TESTYES", "token_abc", "cond_1");
            Assert.IsTrue(_mapper.IsKnownTokenId("token_abc"));
        }

        [Test]
        public void IsKnownTokenId_UnknownToken_ReturnsFalse()
        {
            Assert.IsFalse(_mapper.IsKnownTokenId("nonexistent"));
        }

        [Test]
        public void IsKnownLeanSymbol_AfterMapping_ReturnsTrue()
        {
            _mapper.AddMapping("TESTYES", "token_abc", "cond_1");
            Assert.IsTrue(_mapper.IsKnownLeanSymbol("TESTYES"));
        }

        [Test]
        public void GenerateSlug_SimpleQuestion_ReturnsUppercaseSlug()
        {
            var slug = PolymarketSymbolMapper.GenerateSlug("Will ETH reach $5000 by March?");
            Assert.AreEqual("WILLETHREACH$5000", slug);
        }

        [Test]
        public void GenerateSlug_NullQuestion_ReturnsUnknown()
        {
            var slug = PolymarketSymbolMapper.GenerateSlug(null);
            Assert.AreEqual("UNKNOWN", slug);
        }

        [Test]
        public void GenerateSlug_EmptyQuestion_ReturnsUnknown()
        {
            var slug = PolymarketSymbolMapper.GenerateSlug("  ");
            Assert.AreEqual("UNKNOWN", slug);
        }

        [Test]
        public void GetAllSymbols_AfterMappings_ReturnsAll()
        {
            _mapper.AddMapping("AYES", "token_a", "cond_1");
            _mapper.AddMapping("ANO", "token_b", "cond_1");

            var symbols = _mapper.GetAllSymbols();
            Assert.AreEqual(2, System.Linq.Enumerable.Count(symbols));
        }

        [Test]
        public void GetMarketInfo_WithInfo_ReturnsCorrectData()
        {
            var info = new PolymarketMarketInfo
            {
                ConditionId = "cond_123",
                Question = "Test question?",
                EndDate = new DateTime(2026, 3, 26),
                IsResolved = false
            };

            _mapper.AddMapping("TESTYES", "token_yes", "cond_123", info);

            var result = _mapper.GetMarketInfo("cond_123");
            Assert.IsNotNull(result);
            Assert.AreEqual("Test question?", result.Question);
            Assert.AreEqual(new DateTime(2026, 3, 26), result.EndDate);
            Assert.IsFalse(result.IsResolved);
        }
    }
}
