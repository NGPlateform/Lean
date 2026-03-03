// === Authentication Module ===
// JWT-based auth with MetaMask signing

const AUTH_TOKEN_KEY = 'pm_jwt_token';
const AUTH_ADDR_KEY = 'pm_auth_address';
const AUTH_EXPIRY_KEY = 'pm_auth_expiry';

function getToken() {
    return sessionStorage.getItem(AUTH_TOKEN_KEY);
}

function getAuthAddress() {
    return sessionStorage.getItem(AUTH_ADDR_KEY);
}

function isAuthenticated() {
    const token = getToken();
    const expiry = sessionStorage.getItem(AUTH_EXPIRY_KEY);
    if (!token || !expiry) return false;
    return new Date(expiry) > new Date();
}

function saveAuth(token, address, expiresAt) {
    sessionStorage.setItem(AUTH_TOKEN_KEY, token);
    sessionStorage.setItem(AUTH_ADDR_KEY, address);
    sessionStorage.setItem(AUTH_EXPIRY_KEY, expiresAt);
}

function logout() {
    sessionStorage.removeItem(AUTH_TOKEN_KEY);
    sessionStorage.removeItem(AUTH_ADDR_KEY);
    sessionStorage.removeItem(AUTH_EXPIRY_KEY);
    showLoginScreen();
}

function authHeaders() {
    const token = getToken();
    if (!token) return {};
    return { 'Authorization': 'Bearer ' + token };
}

async function doLogin() {
    const loginBtn = document.getElementById('login-btn');
    const loginStatus = document.getElementById('login-status');
    const loginError = document.getElementById('login-error');

    loginError.style.display = 'none';
    loginBtn.disabled = true;
    loginStatus.textContent = 'Connecting to MetaMask...';

    try {
        // Step 1: Connect wallet
        const address = await connectWallet();
        loginStatus.textContent = 'Requesting nonce...';

        // Step 2: Get nonce from server
        const nonceRes = await fetch(`/api/auth/nonce?addr=${address}`);
        const nonceData = await nonceRes.json();
        if (!nonceRes.ok) throw new Error(nonceData.error || 'Failed to get nonce');

        loginStatus.textContent = 'Please sign the message in MetaMask...';

        // Step 3: Sign nonce with MetaMask
        const signature = await personalSign(nonceData.message);

        loginStatus.textContent = 'Verifying signature...';

        // Step 4: Verify signature & get JWT
        const verifyRes = await fetch('/api/auth/verify', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                address: address,
                signature: signature,
                nonce: nonceData.nonce
            })
        });
        const authData = await verifyRes.json();
        if (!verifyRes.ok) throw new Error(authData.error || 'Verification failed');

        // Step 5: Save JWT and show dashboard
        saveAuth(authData.token, authData.address, authData.expiresAt);
        loginStatus.textContent = 'Authenticated!';
        showDashboard();
    } catch (err) {
        loginError.textContent = err.message || 'Login failed';
        loginError.style.display = 'block';
        loginStatus.textContent = '';
        loginBtn.disabled = false;
    }
}

function showLoginScreen() {
    document.getElementById('login-overlay').style.display = 'flex';
    document.getElementById('dashboard-container').style.display = 'none';
}

function showDashboard() {
    document.getElementById('login-overlay').style.display = 'none';
    document.getElementById('dashboard-container').style.display = '';
    // Update header with connected address
    const addrEl = document.getElementById('auth-address');
    if (addrEl) {
        const addr = getAuthAddress();
        addrEl.textContent = addr ? addr.substring(0, 6) + '...' + addr.substring(addr.length - 4) : '';
    }
}

// Called when wallet account changes
function onAccountChanged(newAddress) {
    // If authenticated with a different address, force re-login
    const authAddr = getAuthAddress();
    if (authAddr && authAddr !== newAddress) {
        logout();
    }
}

// Called when wallet disconnects
function onWalletDisconnected() {
    logout();
}

// Called when chain changes
function onChainChanged(chainId) {
    // Informational only — don't force logout on chain change
}
