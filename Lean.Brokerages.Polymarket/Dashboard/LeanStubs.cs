// Minimal stubs for LEAN framework types used by Polymarket source files.
// This allows the Dashboard to compile standalone without the full LEAN engine.

using System;
using System.Collections.Concurrent;

namespace QuantConnect
{
    public enum SecurityType
    {
        Equity, Forex, Cfd, Crypto, Option, Future, FutureOption, Index, CryptoFuture
    }

    public enum OptionRight
    {
        Call, Put
    }

    public class Symbol
    {
        public static readonly Symbol Empty = new Symbol();

        public string Value { get; set; } = "";
        public SymbolId ID { get; set; } = new SymbolId();

        public static Symbol Create(string ticker, SecurityType securityType, string market)
        {
            return new Symbol
            {
                Value = ticker,
                ID = new SymbolId { Market = market, SecurityType = securityType }
            };
        }

        public override string ToString() => Value;
        public override int GetHashCode() => Value?.GetHashCode() ?? 0;
        public override bool Equals(object obj) => obj is Symbol s && s.Value == Value;
        public static bool operator ==(Symbol a, Symbol b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            return a.Value == b.Value;
        }
        public static bool operator !=(Symbol a, Symbol b) => !(a == b);
    }

    public class SymbolId
    {
        public string Market { get; set; } = "";
        public SecurityType SecurityType { get; set; }
    }

    public interface ISymbolMapper
    {
        string GetBrokerageSymbol(Symbol symbol);
        Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market,
            DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = 0);
    }

    public static class Market
    {
        private static readonly ConcurrentDictionary<string, int> _markets = new();

        public static void Add(string market, int identifier)
        {
            _markets[market] = identifier;
        }
    }
}

namespace QuantConnect.Orders
{
    public enum OrderDirection
    {
        Buy,
        Sell,
        Hold
    }
}

namespace QuantConnect.Logging
{
    public static class Log
    {
        public static void Trace(string message) =>
            Console.WriteLine($"[TRACE] {message}");

        public static void Error(string message) =>
            Console.Error.WriteLine($"[ERROR] {message}");

        public static void Error(Exception exception, string message = "") =>
            Console.Error.WriteLine($"[ERROR] {message} {exception.Message}");
    }
}
