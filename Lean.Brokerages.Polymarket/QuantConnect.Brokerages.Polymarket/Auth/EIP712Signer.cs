/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Nethereum.Signer;
using Nethereum.Util;

namespace QuantConnect.Brokerages.Polymarket.Auth
{
    /// <summary>
    /// Handles EIP-712 typed data signing for Polymarket CLOB orders.
    /// Polymarket uses the CTF Exchange contract on Polygon (chainId: 137).
    /// Implements EIP-712 hashing manually using Keccak256.
    /// </summary>
    public class EIP712Signer
    {
        /// <summary>
        /// Polygon chain ID
        /// </summary>
        public const int PolygonChainId = 137;

        /// <summary>
        /// Polymarket CTF Exchange contract address on Polygon
        /// </summary>
        public const string CtfExchangeAddress = "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E";

        /// <summary>
        /// Polymarket Neg Risk CTF Exchange contract address on Polygon
        /// </summary>
        public const string NegRiskCtfExchangeAddress = "0xC5d563A36AE78145C45a50134d48A1215220f80a";

        // EIP-712 type hashes (pre-computed Keccak256 of type strings)
        private static readonly byte[] DomainTypeHash = Keccak256(Encoding.UTF8.GetBytes(
            "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));

        private static readonly byte[] OrderTypeHash = Keccak256(Encoding.UTF8.GetBytes(
            "Order(uint256 salt,address maker,address signer,address taker,uint256 tokenId," +
            "uint256 makerAmount,uint256 takerAmount,uint256 expiration,uint256 nonce," +
            "uint256 feeRateBps,uint8 side,uint8 signatureType)"));

        private readonly EthECKey _signingKey;

        /// <summary>
        /// Creates a new EIP-712 signer with the given private key
        /// </summary>
        /// <param name="privateKey">Ethereum private key (hex string, with or without 0x prefix)</param>
        public EIP712Signer(string privateKey)
        {
            if (string.IsNullOrWhiteSpace(privateKey))
            {
                throw new ArgumentException("Private key must not be empty", nameof(privateKey));
            }

            _signingKey = new EthECKey(privateKey);
        }

        /// <summary>
        /// Gets the Ethereum address associated with this signer
        /// </summary>
        public string Address => _signingKey.GetPublicAddress();

        /// <summary>
        /// Signs a Polymarket CLOB order using EIP-712 typed data
        /// </summary>
        /// <param name="order">The order parameters to sign</param>
        /// <param name="isNegRisk">Whether to use the NegRisk exchange address</param>
        /// <returns>The EIP-712 signature as a hex string</returns>
        public string SignOrder(PolymarketOrderData order, bool isNegRisk = false)
        {
            var exchangeAddress = isNegRisk ? NegRiskCtfExchangeAddress : CtfExchangeAddress;

            // 1. Compute domain separator
            var domainSeparator = ComputeDomainSeparator(
                "Polymarket CTF Exchange", "1", PolygonChainId, exchangeAddress);

            // 2. Compute struct hash of the order
            var structHash = ComputeOrderStructHash(order);

            // 3. Compute final EIP-712 digest: keccak256("\x19\x01" || domainSeparator || structHash)
            var digest = ComputeEip712Digest(domainSeparator, structHash);

            // 4. Sign the digest with the private key
            var signature = _signingKey.SignAndCalculateV(digest);
            var r = signature.R;
            var s = signature.S;
            var v = signature.V;

            // Combine into a single signature bytes (r + s + v)
            var sigBytes = new byte[65];
            var rBytes = PadLeft(r, 32);
            var sBytes = PadLeft(s, 32);
            Array.Copy(rBytes, 0, sigBytes, 0, 32);
            Array.Copy(sBytes, 0, sigBytes, 32, 32);
            sigBytes[64] = v[0];

            return "0x" + BitConverter.ToString(sigBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Generates a random salt for order signing
        /// </summary>
        public static BigInteger GenerateSalt()
        {
            var random = new Random();
            var bytes = new byte[32];
            random.NextBytes(bytes);
            // Ensure positive
            bytes[31] &= 0x7F;
            return new BigInteger(bytes, isUnsigned: true);
        }

        private static byte[] ComputeDomainSeparator(string name, string version, int chainId, string verifyingContract)
        {
            var nameHash = Keccak256(Encoding.UTF8.GetBytes(name));
            var versionHash = Keccak256(Encoding.UTF8.GetBytes(version));
            var chainIdBytes = PadBigInteger(new BigInteger(chainId));
            var contractBytes = PadAddress(verifyingContract);

            var encoded = Concat(DomainTypeHash, nameHash, versionHash, chainIdBytes, contractBytes);
            return Keccak256(encoded);
        }

        private static byte[] ComputeOrderStructHash(PolymarketOrderData order)
        {
            var encoded = Concat(
                OrderTypeHash,
                PadBigInteger(order.Salt),
                PadAddress(order.Maker),
                PadAddress(order.Signer),
                PadAddress(order.Taker),
                PadBigInteger(order.TokenId),
                PadBigInteger(order.MakerAmount),
                PadBigInteger(order.TakerAmount),
                PadBigInteger(order.Expiration),
                PadBigInteger(order.Nonce),
                PadBigInteger(order.FeeRateBps),
                PadBigInteger(new BigInteger(order.Side)),
                PadBigInteger(new BigInteger(order.SignatureType)));

            return Keccak256(encoded);
        }

        private static byte[] ComputeEip712Digest(byte[] domainSeparator, byte[] structHash)
        {
            // "\x19\x01" prefix per EIP-712
            var prefix = new byte[] { 0x19, 0x01 };
            var data = Concat(prefix, domainSeparator, structHash);
            return Keccak256(data);
        }

        private static byte[] Keccak256(byte[] input)
        {
            var keccak = new Sha3Keccack();
            return keccak.CalculateHash(input);
        }

        /// <summary>
        /// Pads a BigInteger to 32 bytes (uint256), big-endian
        /// </summary>
        private static byte[] PadBigInteger(BigInteger value)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (bytes.Length >= 32)
            {
                return bytes.Take(32).ToArray();
            }

            var padded = new byte[32];
            Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
            return padded;
        }

        /// <summary>
        /// Pads an Ethereum address (20 bytes) to 32 bytes (left-padded with zeros)
        /// </summary>
        private static byte[] PadAddress(string address)
        {
            var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? address.Substring(2)
                : address;

            var bytes = HexToBytes(hex);
            var padded = new byte[32];
            Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
            return padded;
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            var totalLength = arrays.Sum(a => a.Length);
            var result = new byte[totalLength];
            var offset = 0;
            foreach (var array in arrays)
            {
                Array.Copy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }
            return result;
        }

        /// <summary>
        /// Pads a byte array to the left to reach the target length
        /// </summary>
        private static byte[] PadLeft(byte[] bytes, int targetLength)
        {
            if (bytes.Length >= targetLength) return bytes;
            var result = new byte[targetLength];
            Array.Copy(bytes, 0, result, targetLength - bytes.Length, bytes.Length);
            return result;
        }
    }

    /// <summary>
    /// Order data to be signed via EIP-712
    /// </summary>
    public class PolymarketOrderData
    {
        /// <summary>
        /// Random salt for uniqueness
        /// </summary>
        public BigInteger Salt { get; set; }

        /// <summary>
        /// Maker address (the user's Polygon address)
        /// </summary>
        public string Maker { get; set; }

        /// <summary>
        /// Signer address (usually same as maker for EOA)
        /// </summary>
        public string Signer { get; set; }

        /// <summary>
        /// Taker address (0x0 for open order)
        /// </summary>
        public string Taker { get; set; } = "0x0000000000000000000000000000000000000000";

        /// <summary>
        /// The CTF token ID being traded
        /// </summary>
        public BigInteger TokenId { get; set; }

        /// <summary>
        /// Maker amount in raw units (USDC has 6 decimals)
        /// </summary>
        public BigInteger MakerAmount { get; set; }

        /// <summary>
        /// Taker amount in raw units
        /// </summary>
        public BigInteger TakerAmount { get; set; }

        /// <summary>
        /// Order expiration timestamp (0 for no expiration)
        /// </summary>
        public BigInteger Expiration { get; set; }

        /// <summary>
        /// Nonce for replay protection
        /// </summary>
        public BigInteger Nonce { get; set; }

        /// <summary>
        /// Fee rate in basis points
        /// </summary>
        public BigInteger FeeRateBps { get; set; }

        /// <summary>
        /// Order side: 0 = BUY, 1 = SELL
        /// </summary>
        public byte Side { get; set; }

        /// <summary>
        /// Signature type: 0 = EOA, 1 = POLY_PROXY, 2 = POLY_GNOSIS_SAFE
        /// </summary>
        public byte SignatureType { get; set; }
    }
}
