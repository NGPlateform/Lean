// === Wallet Module ===

async function loadWalletPage() {
    await loadWalletBalance();
}

async function loadWalletBalance() {
    const container = document.getElementById('wallet-balance-cards');
    if (!container) return;

    if (!isWalletConnected()) {
        container.innerHTML = '<div class="empty-state">Connect your wallet to view balances</div>';
        return;
    }

    container.innerHTML = '<div class="empty-state">Loading balances...</div>';
    try {
        const bal = await api(`/wallet/balance?address=${getConnectedAddress()}`);
        container.innerHTML = `
            <div class="balance-cards">
                <div class="balance-card">
                    <div class="balance-card-label">USDC Balance</div>
                    <div class="balance-card-value">${esc(bal.usdcBalance)}</div>
                    <div class="balance-card-sub">Polygon Network</div>
                </div>
                <div class="balance-card">
                    <div class="balance-card-label">MATIC Balance</div>
                    <div class="balance-card-value">${esc(bal.maticBalance)}</div>
                    <div class="balance-card-sub">Gas Token</div>
                </div>
                <div class="balance-card">
                    <div class="balance-card-label">USDC Allowance</div>
                    <div class="balance-card-value">${esc(bal.usdcAllowance)}</div>
                    <div class="balance-card-sub">CTF Exchange</div>
                </div>
            </div>`;
    } catch (e) {
        container.innerHTML = `<div class="empty-state">Failed to load balances: ${esc(e.message)}</div>`;
    }
}

async function initiateDeposit() {
    const amount = document.getElementById('deposit-amount').value.trim();
    if (!amount || parseFloat(amount) <= 0) {
        showToast('Enter a valid deposit amount', 'error');
        return;
    }

    if (!isWalletConnected()) {
        showToast('Connect your wallet first', 'error');
        return;
    }

    // Show confirmation
    showConfirmModal(
        'Confirm Deposit',
        `Deposit ${amount} USDC to CTF Exchange on Polygon?`,
        async () => {
            try {
                // Ensure on Polygon
                const onPolygon = await ensurePolygonChain();
                if (!onPolygon) {
                    showToast('Please switch to Polygon network', 'error');
                    return;
                }

                // Get tx params from server
                const txParams = await api('/wallet/deposit', {
                    method: 'POST',
                    body: JSON.stringify({ amount, fromAddress: getConnectedAddress() })
                });

                // Send via MetaMask
                showToast('Confirm transaction in MetaMask...', 'info');
                const txHash = await sendTransaction(txParams);
                showToast('Transaction submitted: ' + txHash.substring(0, 10) + '...', 'success');

                // Track status
                trackTransaction(txHash);
                document.getElementById('deposit-amount').value = '';
            } catch (e) {
                showToast('Deposit failed: ' + e.message, 'error');
            }
        }
    );
}

async function initiateWithdraw() {
    const amount = document.getElementById('withdraw-amount').value.trim();
    if (!amount || parseFloat(amount) <= 0) {
        showToast('Enter a valid withdrawal amount', 'error');
        return;
    }

    if (!isWalletConnected()) {
        showToast('Connect your wallet first', 'error');
        return;
    }

    showConfirmModal(
        'Confirm Withdrawal',
        `Withdraw ${amount} USDC to your wallet?`,
        async () => {
            try {
                const onPolygon = await ensurePolygonChain();
                if (!onPolygon) {
                    showToast('Please switch to Polygon network', 'error');
                    return;
                }

                const txParams = await api('/wallet/withdraw', {
                    method: 'POST',
                    body: JSON.stringify({ amount, fromAddress: getConnectedAddress() })
                });

                showToast('Confirm transaction in MetaMask...', 'info');
                const txHash = await sendTransaction(txParams);
                showToast('Transaction submitted: ' + txHash.substring(0, 10) + '...', 'success');

                trackTransaction(txHash);
                document.getElementById('withdraw-amount').value = '';
            } catch (e) {
                showToast('Withdrawal failed: ' + e.message, 'error');
            }
        }
    );
}

async function approveUsdc() {
    if (!isWalletConnected()) {
        showToast('Connect your wallet first', 'error');
        return;
    }

    showConfirmModal(
        'Approve USDC',
        'Approve unlimited USDC spending for CTF Exchange? This is required before depositing.',
        async () => {
            try {
                const onPolygon = await ensurePolygonChain();
                if (!onPolygon) {
                    showToast('Please switch to Polygon network', 'error');
                    return;
                }

                const txParams = await api('/wallet/approve', {
                    method: 'POST',
                    body: JSON.stringify({ amount: 'max' })
                });

                showToast('Confirm approval in MetaMask...', 'info');
                const txHash = await sendTransaction(txParams);
                showToast('Approval submitted: ' + txHash.substring(0, 10) + '...', 'success');
                trackTransaction(txHash);
            } catch (e) {
                showToast('Approval failed: ' + e.message, 'error');
            }
        }
    );
}

function trackTransaction(txHash) {
    const txList = document.getElementById('tx-history-body');
    if (!txList) return;

    const row = document.createElement('tr');
    row.id = `tx-${txHash}`;
    row.innerHTML = `
        <td title="${esc(txHash)}">${esc(txHash.substring(0, 10))}...</td>
        <td><span class="tx-status pending">Pending</span></td>
        <td>${new Date().toLocaleTimeString()}</td>`;
    txList.prepend(row);

    pollTxStatus(txHash);
}

async function pollTxStatus(txHash) {
    let attempts = 0;
    const maxAttempts = 60;

    const check = async () => {
        attempts++;
        try {
            const status = await api(`/wallet/tx/${txHash}`);
            const row = document.getElementById(`tx-${txHash}`);
            if (row) {
                const statusEl = row.querySelector('.tx-status');
                statusEl.textContent = status.status;
                statusEl.className = `tx-status ${status.status}`;
            }
            if (status.status === 'confirmed' || status.status === 'failed') {
                if (status.status === 'confirmed') {
                    showToast('Transaction confirmed!', 'success');
                    loadWalletBalance();
                } else {
                    showToast('Transaction failed', 'error');
                }
                return;
            }
        } catch { /* retry */ }
        if (attempts < maxAttempts) {
            setTimeout(check, 5000);
        }
    };
    setTimeout(check, 3000);
}

// === Confirmation Modal ===
function showConfirmModal(title, message, onConfirm) {
    const modal = document.getElementById('confirm-modal');
    document.getElementById('confirm-modal-title').textContent = title;
    document.getElementById('confirm-modal-message').textContent = message;
    modal.style.display = 'flex';

    const confirmBtn = document.getElementById('confirm-modal-yes');
    const cancelBtn = document.getElementById('confirm-modal-no');

    const cleanup = () => {
        modal.style.display = 'none';
        confirmBtn.replaceWith(confirmBtn.cloneNode(true));
        cancelBtn.replaceWith(cancelBtn.cloneNode(true));
    };

    document.getElementById('confirm-modal-yes').addEventListener('click', () => {
        cleanup();
        onConfirm();
    });
    document.getElementById('confirm-modal-no').addEventListener('click', cleanup);
}
