// === Settings Page Module ===

async function loadSettingsPage() {
    await Promise.all([loadCredentials(), loadSystemSettings(), loadRiskSettingsForm()]);
}

// === Credentials ===
async function loadCredentials() {
    const container = document.getElementById('credentials-form');
    try {
        const creds = await api('/settings/credentials');
        container.innerHTML = `
            <div class="settings-group">
                <h3>Polymarket API Credentials</h3>
                <p class="settings-hint">Credentials are encrypted at rest. Leave blank to keep current value.</p>
                <div class="form-group">
                    <label>API Key</label>
                    <input type="text" id="cred-apikey" placeholder="${esc(creds.apiKey) || 'Not set'}" class="settings-input">
                </div>
                <div class="form-group">
                    <label>API Secret</label>
                    <input type="password" id="cred-apisecret" placeholder="${esc(creds.apiSecret) || 'Not set'}" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Private Key</label>
                    <input type="password" id="cred-privatekey" placeholder="${esc(creds.privateKey) || 'Not set'}" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Passphrase</label>
                    <input type="password" id="cred-passphrase" placeholder="${esc(creds.passphrase) || 'Not set'}" class="settings-input">
                </div>
                <button class="btn btn-primary" onclick="saveCredentials()">Save Credentials</button>
            </div>`;
    } catch (e) {
        container.innerHTML = `<div class="empty-state">Failed to load credentials: ${esc(e.message)}</div>`;
    }
}

async function saveCredentials() {
    const body = {};
    const apiKey = document.getElementById('cred-apikey').value.trim();
    const apiSecret = document.getElementById('cred-apisecret').value.trim();
    const privateKey = document.getElementById('cred-privatekey').value.trim();
    const passphrase = document.getElementById('cred-passphrase').value.trim();

    if (apiKey) body.apiKey = apiKey;
    if (apiSecret) body.apiSecret = apiSecret;
    if (privateKey) body.privateKey = privateKey;
    if (passphrase) body.passphrase = passphrase;

    if (Object.keys(body).length === 0) {
        showToast('No changes to save', 'info');
        return;
    }

    try {
        await api('/settings/credentials', { method: 'PUT', body: JSON.stringify(body) });
        showToast('Credentials saved (encrypted)', 'success');
        loadCredentials();
    } catch (e) {
        showToast('Failed to save: ' + e.message, 'error');
    }
}

// === System Settings ===
async function loadSystemSettings() {
    const container = document.getElementById('system-settings-form');
    try {
        const sys = await api('/settings/system');
        container.innerHTML = `
            <div class="settings-group">
                <h3>System Configuration</h3>
                <p class="settings-hint">Some changes require a server restart to take effect.</p>
                <div class="form-group settings-row">
                    <label>Dry Run Mode</label>
                    <label class="toggle">
                        <input type="checkbox" id="sys-dryrun" ${sys.dryRunEnabled ? 'checked' : ''}>
                        <span class="toggle-slider"></span>
                    </label>
                </div>
                <div class="form-group">
                    <label>Initial Balance (USDC)</label>
                    <input type="number" id="sys-balance" value="${sys.initialBalance}" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Tick Interval (ms)</label>
                    <input type="number" id="sys-tick" value="${sys.tickIntervalMs}" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Default Strategy</label>
                    <select id="sys-strategy" class="settings-input">
                        <option value="MarketMaking" ${sys.strategyName === 'MarketMaking' ? 'selected' : ''}>MarketMaking</option>
                        <option value="MeanReversion" ${sys.strategyName === 'MeanReversion' ? 'selected' : ''}>MeanReversion</option>
                        <option value="SpreadCapture" ${sys.strategyName === 'SpreadCapture' ? 'selected' : ''}>SpreadCapture</option>
                        <option value="BtcFollowMM" ${sys.strategyName === 'BtcFollowMM' ? 'selected' : ''}>BtcFollowMM</option>
                    </select>
                </div>
                <div class="form-group">
                    <label>Auto Subscribe Top N</label>
                    <input type="number" id="sys-topn" value="${sys.autoSubscribeTopN}" class="settings-input">
                </div>
                <button class="btn btn-primary" onclick="saveSystemSettings()">Save System Settings</button>
            </div>`;
    } catch (e) {
        container.innerHTML = `<div class="empty-state">Failed to load system settings: ${esc(e.message)}</div>`;
    }
}

async function saveSystemSettings() {
    try {
        await api('/settings/system', {
            method: 'PUT',
            body: JSON.stringify({
                dryRunEnabled: document.getElementById('sys-dryrun').checked,
                initialBalance: parseFloat(document.getElementById('sys-balance').value) || 10000,
                tickIntervalMs: parseInt(document.getElementById('sys-tick').value) || 5000,
                strategyName: document.getElementById('sys-strategy').value,
                autoSubscribeTopN: parseInt(document.getElementById('sys-topn').value) || 10
            })
        });
        showToast('System settings saved', 'success');
    } catch (e) {
        showToast('Failed to save: ' + e.message, 'error');
    }
}

// === Risk Settings ===
async function loadRiskSettingsForm() {
    const container = document.getElementById('risk-settings-form');
    try {
        const risk = await api('/settings/risk');
        container.innerHTML = `
            <div class="settings-group">
                <h3>Risk Limits</h3>
                <p class="settings-hint">Server-side enforcement. Orders exceeding limits will be rejected.</p>
                <div class="form-group">
                    <label>Daily Spending Limit ($)</label>
                    <input type="number" id="risk-daily-spend" value="${risk.dailySpendingLimit}" step="100" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Total Exposure Limit ($)</label>
                    <input type="number" id="risk-total-exposure" value="${risk.totalExposureLimit}" step="100" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Per-Market Position Limit ($)</label>
                    <input type="number" id="risk-per-market" value="${risk.perMarketPositionLimit}" step="50" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Per-Trade Position Limit ($)</label>
                    <input type="number" id="risk-per-trade" value="${risk.perTradePositionLimit}" step="50" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Daily Loss Limit ($)</label>
                    <input type="number" id="risk-daily-loss" value="${risk.dailyLossLimit}" step="100" class="settings-input">
                </div>
                <div class="form-group">
                    <label>Max Drawdown Limit ($)</label>
                    <input type="number" id="risk-max-dd" value="${risk.maxDrawdownLimit}" step="100" class="settings-input">
                </div>
                <button class="btn btn-primary" onclick="saveRiskSettings()">Save Risk Limits</button>
            </div>`;
    } catch (e) {
        container.innerHTML = `<div class="empty-state">Failed to load risk settings: ${esc(e.message)}</div>`;
    }
}

async function saveRiskSettings() {
    try {
        await api('/settings/risk', {
            method: 'PUT',
            body: JSON.stringify({
                dailySpendingLimit: parseFloat(document.getElementById('risk-daily-spend').value) || 1000,
                totalExposureLimit: parseFloat(document.getElementById('risk-total-exposure').value) || 5000,
                perMarketPositionLimit: parseFloat(document.getElementById('risk-per-market').value) || 500,
                perTradePositionLimit: parseFloat(document.getElementById('risk-per-trade').value) || 200,
                dailyLossLimit: parseFloat(document.getElementById('risk-daily-loss').value) || 500,
                maxDrawdownLimit: parseFloat(document.getElementById('risk-max-dd').value) || 2000,
                perStrategyCapitalLimits: {}
            })
        });
        showToast('Risk limits saved', 'success');
        if (typeof loadRiskStatus === 'function') loadRiskStatus();
    } catch (e) {
        showToast('Failed to save: ' + e.message, 'error');
    }
}
