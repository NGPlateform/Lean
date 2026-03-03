using Nethereum.Signer;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Auth
{
    public class SignatureVerifier
    {
        private readonly EthereumMessageSigner _signer = new();

        public string RecoverAddress(string message, string signature)
        {
            return _signer.EncodeUTF8AndEcRecover(message, signature);
        }

        public bool Verify(string message, string signature, string expectedAddress)
        {
            var recovered = RecoverAddress(message, signature);
            return string.Equals(recovered, expectedAddress, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
