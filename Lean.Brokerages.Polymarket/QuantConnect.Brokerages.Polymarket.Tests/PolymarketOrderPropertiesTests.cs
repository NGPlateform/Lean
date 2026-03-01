/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using NUnit.Framework;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class PolymarketOrderPropertiesTests
    {
        [Test]
        public void DefaultProperties_AreCorrect()
        {
            var props = new PolymarketOrderProperties();

            Assert.IsFalse(props.PostOnly);
            Assert.AreEqual(0, props.TimeToLiveSecs);
            Assert.AreEqual(0, props.Nonce);
            Assert.IsFalse(props.FillOrKill);
        }

        [Test]
        public void PostOnly_CanBeSet()
        {
            var props = new PolymarketOrderProperties { PostOnly = true };
            Assert.IsTrue(props.PostOnly);
        }

        [Test]
        public void TimeToLiveSecs_CanBeSet()
        {
            var props = new PolymarketOrderProperties { TimeToLiveSecs = 300 };
            Assert.AreEqual(300, props.TimeToLiveSecs);
        }

        [Test]
        public void FillOrKill_CanBeSet()
        {
            var props = new PolymarketOrderProperties { FillOrKill = true };
            Assert.IsTrue(props.FillOrKill);
        }

        [Test]
        public void Nonce_CanBeSet()
        {
            var props = new PolymarketOrderProperties { Nonce = 12345678 };
            Assert.AreEqual(12345678, props.Nonce);
        }
    }
}
