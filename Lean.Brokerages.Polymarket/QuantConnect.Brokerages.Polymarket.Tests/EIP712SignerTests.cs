/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Numerics;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Auth;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class EIP712SignerTests
    {
        // Well-known test private key (DO NOT use in production)
        private const string TestPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";

        [Test]
        public void Constructor_ValidPrivateKey_CreatesInstance()
        {
            var signer = new EIP712Signer(TestPrivateKey);
            Assert.IsNotNull(signer);
            Assert.IsNotEmpty(signer.Address);
        }

        [Test]
        public void Constructor_NullPrivateKey_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() => new EIP712Signer(null));
        }

        [Test]
        public void Constructor_EmptyPrivateKey_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() => new EIP712Signer(""));
        }

        [Test]
        public void Address_ReturnsValidEthereumAddress()
        {
            var signer = new EIP712Signer(TestPrivateKey);
            Assert.IsNotNull(signer.Address);
            Assert.That(signer.Address, Does.StartWith("0x"));
            Assert.AreEqual(42, signer.Address.Length); // 0x + 40 hex chars
        }

        [Test]
        public void SignOrder_ValidOrderData_ReturnsSignature()
        {
            var signer = new EIP712Signer(TestPrivateKey);

            var orderData = new PolymarketOrderData
            {
                Salt = new BigInteger(12345),
                Maker = signer.Address,
                Signer = signer.Address,
                Taker = "0x0000000000000000000000000000000000000000",
                TokenId = new BigInteger(100),
                MakerAmount = new BigInteger(50_000_000), // 50 USDC
                TakerAmount = new BigInteger(100_000_000), // 100 tokens
                Expiration = BigInteger.Zero,
                Nonce = BigInteger.Zero,
                FeeRateBps = BigInteger.Zero,
                Side = 0, // BUY
                SignatureType = 0 // EOA
            };

            var signature = signer.SignOrder(orderData);

            Assert.IsNotNull(signature);
            Assert.IsNotEmpty(signature);
            Assert.That(signature, Does.StartWith("0x"));
        }

        [Test]
        public void SignOrder_DifferentOrders_ProduceDifferentSignatures()
        {
            var signer = new EIP712Signer(TestPrivateKey);

            var orderData1 = new PolymarketOrderData
            {
                Salt = new BigInteger(12345),
                Maker = signer.Address,
                Signer = signer.Address,
                Taker = "0x0000000000000000000000000000000000000000",
                TokenId = new BigInteger(100),
                MakerAmount = new BigInteger(50_000_000),
                TakerAmount = new BigInteger(100_000_000),
                Expiration = BigInteger.Zero,
                Nonce = BigInteger.Zero,
                FeeRateBps = BigInteger.Zero,
                Side = 0,
                SignatureType = 0
            };

            var orderData2 = new PolymarketOrderData
            {
                Salt = new BigInteger(67890), // Different salt
                Maker = signer.Address,
                Signer = signer.Address,
                Taker = "0x0000000000000000000000000000000000000000",
                TokenId = new BigInteger(100),
                MakerAmount = new BigInteger(50_000_000),
                TakerAmount = new BigInteger(100_000_000),
                Expiration = BigInteger.Zero,
                Nonce = BigInteger.Zero,
                FeeRateBps = BigInteger.Zero,
                Side = 0,
                SignatureType = 0
            };

            var sig1 = signer.SignOrder(orderData1);
            var sig2 = signer.SignOrder(orderData2);

            Assert.AreNotEqual(sig1, sig2);
        }

        [Test]
        public void GenerateSalt_ReturnsPositiveBigInteger()
        {
            var salt = EIP712Signer.GenerateSalt();
            Assert.That(salt, Is.GreaterThan(BigInteger.Zero));
        }

        [Test]
        public void GenerateSalt_MultipleCalls_ReturnsDifferentValues()
        {
            var salt1 = EIP712Signer.GenerateSalt();
            var salt2 = EIP712Signer.GenerateSalt();
            Assert.AreNotEqual(salt1, salt2);
        }

        [Test]
        public void PolygonChainId_Is137()
        {
            Assert.AreEqual(137, EIP712Signer.PolygonChainId);
        }

        [Test]
        public void CtfExchangeAddress_IsValid()
        {
            Assert.That(EIP712Signer.CtfExchangeAddress, Does.StartWith("0x"));
            Assert.AreEqual(42, EIP712Signer.CtfExchangeAddress.Length);
        }

        [Test]
        public void NegRiskCtfExchangeAddress_IsValid()
        {
            Assert.That(EIP712Signer.NegRiskCtfExchangeAddress, Does.StartWith("0x"));
            Assert.AreEqual(42, EIP712Signer.NegRiskCtfExchangeAddress.Length);
        }
    }
}
