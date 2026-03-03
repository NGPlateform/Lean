// === Web3 / MetaMask Integration ===
// Raw window.ethereum calls — no ethers.js dependency

const POLYGON_CHAIN_ID = '0x89';
const POLYGON_CHAIN_CONFIG = {
    chainId: POLYGON_CHAIN_ID,
    chainName: 'Polygon Mainnet',
    nativeCurrency: { name: 'MATIC', symbol: 'MATIC', decimals: 18 },
    rpcUrls: ['https://polygon-rpc.com'],
    blockExplorerUrls: ['https://polygonscan.com']
};

const web3State = {
    connected: false,
    address: null,
    chainId: null
};

function hasMetaMask() {
    return typeof window.ethereum !== 'undefined';
}

async function connectWallet() {
    if (!hasMetaMask()) throw new Error('MetaMask not detected. Please install MetaMask.');
    const accounts = await window.ethereum.request({ method: 'eth_requestAccounts' });
    if (!accounts || accounts.length === 0) throw new Error('No accounts returned');
    web3State.address = accounts[0].toLowerCase();
    web3State.chainId = await window.ethereum.request({ method: 'eth_chainId' });
    web3State.connected = true;
    return web3State.address;
}

async function getAccounts() {
    if (!hasMetaMask()) return [];
    const accounts = await window.ethereum.request({ method: 'eth_accounts' });
    return accounts || [];
}

async function personalSign(message) {
    if (!web3State.address) throw new Error('Wallet not connected');
    return await window.ethereum.request({
        method: 'personal_sign',
        params: [message, web3State.address]
    });
}

async function sendTransaction(txParams) {
    if (!web3State.address) throw new Error('Wallet not connected');
    return await window.ethereum.request({
        method: 'eth_sendTransaction',
        params: [{
            from: web3State.address,
            to: txParams.to,
            data: txParams.data,
            value: txParams.value || '0x0',
            chainId: txParams.chainId
        }]
    });
}

async function ensurePolygonChain() {
    const chainId = await window.ethereum.request({ method: 'eth_chainId' });
    if (chainId === POLYGON_CHAIN_ID) {
        web3State.chainId = chainId;
        return true;
    }
    try {
        await window.ethereum.request({
            method: 'wallet_switchEthereumChain',
            params: [{ chainId: POLYGON_CHAIN_ID }]
        });
        web3State.chainId = POLYGON_CHAIN_ID;
        return true;
    } catch (switchError) {
        // Chain not added — try adding it
        if (switchError.code === 4902) {
            try {
                await window.ethereum.request({
                    method: 'wallet_addEthereumChain',
                    params: [POLYGON_CHAIN_CONFIG]
                });
                web3State.chainId = POLYGON_CHAIN_ID;
                return true;
            } catch { /* user rejected */ }
        }
        return false;
    }
}

function setupWeb3Listeners() {
    if (!hasMetaMask()) return;

    window.ethereum.on('accountsChanged', (accounts) => {
        if (accounts.length === 0) {
            web3State.connected = false;
            web3State.address = null;
            if (typeof onWalletDisconnected === 'function') onWalletDisconnected();
        } else {
            web3State.address = accounts[0].toLowerCase();
            if (typeof onAccountChanged === 'function') onAccountChanged(web3State.address);
        }
    });

    window.ethereum.on('chainChanged', (chainId) => {
        web3State.chainId = chainId;
        if (typeof onChainChanged === 'function') onChainChanged(chainId);
    });
}

function getConnectedAddress() {
    return web3State.address;
}

function isWalletConnected() {
    return web3State.connected && web3State.address != null;
}
