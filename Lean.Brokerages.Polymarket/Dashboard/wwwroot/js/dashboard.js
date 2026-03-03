// === State ===
const state = {
    markets: [],
    events: [],
    selectedTokenId: null,
    selectedQuestion: null,
    orderSide: 'BUY',
    connection: null,
    hasCredentials: false,
    dryRunMode: false,
    view: 'trending', // 'trending' | 'events'
    // Chart state
    equityChart: null,
    equityData: [],
    backtestCurves: null,
    chartMode: 'live' // 'live' | 'backtest'
};

// === API ===
async function api(path, options = {}) {
    const headers = { 'Content-Type': 'application/json', ...authHeaders() };
    const res = await fetch(`/api${path}`, {
        headers,
        ...options,
        headers: { ...headers, ...(options.headers || {}) }
    });
    if (res.status === 401) {
        logout();
        throw new Error('Session expired. Please login again.');
    }
    if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error || 'Request failed');
    }
    return res.json();
}

// === Toast ===
function showToast(msg, type = 'info') {
    const c = document.getElementById('toast-container');
    const t = document.createElement('div');
    t.className = `toast ${type}`;
    t.textContent = msg;
    c.appendChild(t);
    setTimeout(() => t.remove(), 5000);
}

// === Formatting Helpers ===
function fmtVol(v) {
    if (!v) return '';
    if (v >= 1e6) return `$${(v/1e6).toFixed(1)}M`;
    if (v >= 1e3) return `$${(v/1e3).toFixed(0)}K`;
    return `$${v.toFixed(0)}`;
}
function fmtPct(p) { return `${(p * 100).toFixed(1)}%`; }
function fmtDate(d) {
    if (!d) return '';
    try { return new Date(d).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' }); }
    catch { return d; }
}
function shortId(id) {
    if (!id || id.length < 16) return id || '';
    return id.substring(0, 8) + '...' + id.substring(id.length - 6);
}
function esc(s) {
    return (s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// === Status ===
async function checkStatus() {
    try {
        const s = await api('/status');
        state.hasCredentials = s.hasCredentials;
        state.dryRunMode = s.dryRunMode || false;

        if (state.dryRunMode) {
            document.getElementById('dry-run-badge').style.display = 'inline-block';
            document.getElementById('pnl-display').style.display = 'flex';
            document.getElementById('tab-btn-logs').style.display = '';
            document.getElementById('tab-btn-params').style.display = '';
            document.getElementById('tab-btn-scores').style.display = '';
            document.getElementById('chart-panel').style.display = '';
            document.getElementById('strategy-selector').style.display = 'flex';
            showToast(s.message, 'info');
        } else if (!s.hasCredentials) {
            const el = document.getElementById('balance-value');
            el.textContent = 'No API Key';
            el.style.color = 'var(--yellow)';
            showToast(s.message, 'info');
        }
    } catch { showToast('Cannot reach server', 'error'); }
}

// === Markets ===
async function loadMarkets() {
    const list = document.getElementById('market-list');
    list.innerHTML = '<div class="empty-state">Loading markets from Polymarket...</div>';
    try {
        state.markets = await api('/markets');
        if (!state.markets.length) {
            list.innerHTML = '<div class="empty-state">No markets found</div>';
            return;
        }
        renderMarkets();
        showToast(`Loaded ${state.markets.length} markets`, 'success');
    } catch (e) {
        list.innerHTML = `<div class="empty-state">Failed to load<br><small>${esc(e.message)}</small></div>`;
        showToast('Failed: ' + e.message, 'error');
    }
}

async function loadEvents() {
    try {
        state.events = await api('/events');
    } catch { state.events = []; }
}

function renderMarkets(filter = '') {
    const list = document.getElementById('market-list');
    const lf = filter.toLowerCase();

    if (state.view === 'events') {
        renderEventsView(list, lf);
        return;
    }

    const filtered = lf
        ? state.markets.filter(m => m.question.toLowerCase().includes(lf) ||
            (m.category || '').toLowerCase().includes(lf))
        : state.markets;

    if (!filtered.length) {
        list.innerHTML = '<div class="empty-state">No markets match</div>';
        return;
    }

    list.innerHTML = filtered.map(m => marketCard(m)).join('');
    bindMarketClicks(list);
}

function renderEventsView(list, filter) {
    const filtered = filter
        ? state.events.filter(e => e.title.toLowerCase().includes(filter) ||
            e.markets.some(m => m.question.toLowerCase().includes(filter)))
        : state.events;

    if (!filtered.length) {
        list.innerHTML = '<div class="empty-state">No events found</div>';
        return;
    }

    let html = '';
    for (const evt of filtered) {
        html += `<div class="event-group">
            <div class="event-title">${esc(evt.title)} <span class="event-count">${evt.markets.length} markets</span></div>
            ${evt.markets.map(m => marketCard(m)).join('')}
        </div>`;
    }
    list.innerHTML = html;
    bindMarketClicks(list);
}

function marketCard(m) {
    const primaryToken = m.tokens?.find(t => t.tokenId) || m.tokens?.[0];
    const isSelected = state.selectedTokenId && m.tokens?.some(t => t.tokenId === state.selectedTokenId);
    const hasTradeable = m.tokens?.some(t => t.tokenId);

    return `<div class="market-item ${isSelected ? 'selected' : ''} ${!hasTradeable ? 'no-clob' : ''}"
                  data-token-id="${primaryToken?.tokenId || ''}"
                  data-question="${esc(m.question)}">
        <div class="market-header-row">
            <div class="ticker">${esc(m.question)}</div>
        </div>
        <div class="market-tokens">
            ${(m.tokens || []).map(t => {
                const cls = (t.outcome === 'Yes' || t.outcome === 'YES') ? 'outcome-yes' : 'outcome-no';
                return `<span class="${cls}" data-token-id="${t.tokenId || ''}">${esc(t.outcome)} ${fmtPct(t.price)}</span>`;
            }).join(' / ')}
        </div>
        <div class="market-meta">
            ${m.volume24h ? `<span class="meta-tag vol">Vol ${fmtVol(m.volume24h)}</span>` : ''}
            ${m.liquidity ? `<span class="meta-tag liq">Liq ${fmtVol(m.liquidity)}</span>` : ''}
            ${m.endDate ? `<span class="meta-tag date">Ends ${fmtDate(m.endDate)}</span>` : ''}
            ${m.category ? `<span class="meta-tag cat">${esc(m.category)}</span>` : ''}
        </div>
    </div>`;
}

function bindMarketClicks(list) {
    list.querySelectorAll('.market-item').forEach(item => {
        item.addEventListener('click', () => {
            const tid = item.dataset.tokenId;
            if (tid) selectMarket(tid, item.dataset.question);
            else showToast('No CLOB data for this market', 'info');
        });
    });
    list.querySelectorAll('.market-tokens span[data-token-id]').forEach(span => {
        span.addEventListener('click', (e) => {
            e.stopPropagation();
            const tid = span.dataset.tokenId;
            if (!tid) return;
            selectMarket(tid, span.closest('.market-item').dataset.question);
        });
    });
}

// === Market Selection & Order Book ===
async function selectMarket(tokenId, question) {
    state.selectedTokenId = tokenId;
    state.selectedQuestion = question;

    document.getElementById('orderbook-token').textContent = question?.substring(0, 40) || shortId(tokenId);
    // In dryRunMode, order form is always enabled
    document.getElementById('btn-submit-order').disabled = !(state.hasCredentials || state.dryRunMode);

    document.querySelectorAll('.market-item').forEach(el => el.classList.remove('selected'));
    const parent = document.querySelector(`.market-item[data-token-id="${tokenId}"]`)
        || document.querySelector(`.market-item:has(span[data-token-id="${tokenId}"])`);
    if (parent) parent.classList.add('selected');

    document.getElementById('asks-list').innerHTML = '<div class="empty-state">Loading...</div>';
    document.getElementById('bids-list').innerHTML = '';
    document.getElementById('spread-display').textContent = '';

    await loadOrderBook(tokenId);

    if (state.connection?.state === 'Connected') {
        state.connection.invoke('SubscribeToTokens', [tokenId]).catch(() => {});
    }
}

async function loadOrderBook(tokenId) {
    try {
        const book = await api(`/orderbook/${tokenId}`);
        renderOrderBook(book);
    } catch (e) {
        document.getElementById('asks-list').innerHTML =
            `<div class="empty-state">Failed to load<br><small>${esc(e.message)}</small></div>`;
        document.getElementById('bids-list').innerHTML = '';
    }
}

function renderOrderBook(book) {
    if (!book) return;

    const asks = (book.asks || [])
        .map(l => ({ price: parseFloat(l.price), size: parseFloat(l.size) }))
        .filter(l => l.size > 0)
        .sort((a, b) => a.price - b.price)
        .slice(0, 12);
    const bids = (book.bids || [])
        .map(l => ({ price: parseFloat(l.price), size: parseFloat(l.size) }))
        .filter(l => l.size > 0)
        .sort((a, b) => b.price - a.price)
        .slice(0, 12);

    if (!asks.length && !bids.length) {
        document.getElementById('asks-list').innerHTML = '<div class="empty-state">Empty order book</div>';
        document.getElementById('bids-list').innerHTML = '';
        document.getElementById('spread-display').textContent = '';
        return;
    }

    const maxSize = Math.max(...asks.map(l => l.size), ...bids.map(l => l.size), 1);

    document.getElementById('asks-list').innerHTML = [...asks].reverse().map(l => {
        const pct = (l.size / maxSize * 100).toFixed(1);
        return `<div class="ob-level ask" data-price="${l.price}">
            <span class="ob-price">${l.price.toFixed(4)}</span>
            <span class="ob-bar"><div class="ob-bar-fill" style="width:${pct}%"></div></span>
            <span class="ob-size">${l.size.toFixed(2)}</span>
        </div>`;
    }).join('');

    document.getElementById('bids-list').innerHTML = bids.map(l => {
        const pct = (l.size / maxSize * 100).toFixed(1);
        return `<div class="ob-level bid" data-price="${l.price}">
            <span class="ob-price">${l.price.toFixed(4)}</span>
            <span class="ob-bar"><div class="ob-bar-fill" style="width:${pct}%"></div></span>
            <span class="ob-size">${l.size.toFixed(2)}</span>
        </div>`;
    }).join('');

    const bestAsk = asks[0]?.price, bestBid = bids[0]?.price;
    document.getElementById('spread-display').textContent =
        (bestAsk != null && bestBid != null) ? `Spread: ${(bestAsk - bestBid).toFixed(4)}` : '';

    document.querySelectorAll('.ob-level').forEach(el => {
        el.addEventListener('click', () => {
            document.getElementById('order-price').value = el.dataset.price;
            updateOrderSummary();
        });
    });
}

// === Balance ===
async function loadBalance() {
    if (!state.hasCredentials && !state.dryRunMode) return;
    // In dryRunMode, balance is pushed via SignalR — only fetch once at init
    if (state.dryRunMode) {
        try {
            const bal = await api('/balance');
            updateBalanceDisplay(bal.balance);
        } catch {}
        return;
    }
    try {
        const bal = await api('/balance');
        updateBalanceDisplay(bal.balance);
    } catch {}
}

function updateBalanceDisplay(balStr) {
    const el = document.getElementById('balance-value');
    el.textContent = '$' + parseFloat(balStr || '0').toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    el.style.color = '';
}

// === Bottom Tabs ===
async function loadPositions() {
    const tbody = document.querySelector('#positions-table tbody');
    try {
        const pos = await api('/positions');
        if (!pos?.length) {
            tbody.innerHTML = `<tr><td colspan="5" class="empty-state">${state.hasCredentials || state.dryRunMode ? 'No positions' : 'Configure API credentials'}</td></tr>`;
            return;
        }
        tbody.innerHTML = pos.map(p => {
            const pnl = parseFloat(p.unrealized_pnl || p.unrealizedPnl || '0');
            const cls = pnl >= 0 ? 'pnl-positive' : 'pnl-negative';
            return `<tr>
                <td title="${p.asset_id || p.assetId}">${shortId(p.asset_id || p.assetId)}</td>
                <td>${p.size}</td>
                <td>${parseFloat(p.avg_price || p.avgPrice || '0').toFixed(4)}</td>
                <td>${parseFloat(p.cur_price || p.curPrice || '0').toFixed(4)}</td>
                <td class="${cls}">${pnl >= 0 ? '+' : ''}${pnl.toFixed(4)}</td>
            </tr>`;
        }).join('');
    } catch { tbody.innerHTML = '<tr><td colspan="5" class="empty-state">Failed</td></tr>'; }
}

async function loadOrders() {
    const tbody = document.querySelector('#orders-table tbody');
    try {
        const orders = await api('/orders');
        if (!orders?.length) {
            tbody.innerHTML = `<tr><td colspan="7" class="empty-state">${state.hasCredentials || state.dryRunMode ? 'No active orders' : 'Configure API credentials'}</td></tr>`;
            return;
        }
        tbody.innerHTML = orders.map(o => {
            const side = (o.side || '').toUpperCase();
            return `<tr>
                <td title="${o.id}">${shortId(o.id)}</td>
                <td class="${side === 'BUY' ? 'side-buy' : 'side-sell'}">${side}</td>
                <td>${o.price}</td>
                <td>${o.original_size || o.originalSize}</td>
                <td>${o.size_matched || o.sizeMatched || '0'}</td>
                <td>${o.status}</td>
                <td><button class="btn btn-danger" onclick="cancelOrder('${o.id}')">Cancel</button></td>
            </tr>`;
        }).join('');
    } catch { tbody.innerHTML = '<tr><td colspan="7" class="empty-state">Failed</td></tr>'; }
}

async function loadTrades() {
    const tbody = document.querySelector('#trades-table tbody');
    try {
        const trades = await api('/trades?limit=50');
        if (!trades?.length) {
            tbody.innerHTML = `<tr><td colspan="5" class="empty-state">${state.hasCredentials || state.dryRunMode ? 'No trades' : 'Configure API credentials'}</td></tr>`;
            return;
        }
        tbody.innerHTML = trades.map(t => {
            const side = (t.side || '').toUpperCase();
            const time = t.match_time || t.matchTime;
            return `<tr>
                <td>${time ? new Date(time).toLocaleString() : '--'}</td>
                <td class="${side === 'BUY' ? 'side-buy' : 'side-sell'}">${side}</td>
                <td>${t.price}</td>
                <td>${t.size}</td>
                <td>${t.status}</td>
            </tr>`;
        }).join('');
    } catch { tbody.innerHTML = '<tr><td colspan="5" class="empty-state">Failed</td></tr>'; }
}

// === Logs (DryRun) ===
async function loadLogs() {
    if (!state.dryRunMode) return;
    const container = document.getElementById('logs-container');
    try {
        const logs = await api('/logs?limit=200');
        if (!logs?.length) {
            container.innerHTML = '<div class="empty-state">No logs yet</div>';
            return;
        }
        container.innerHTML = logs.map(l => logEntryHtml(l)).join('');
        container.scrollTop = container.scrollHeight;
    } catch { container.innerHTML = '<div class="empty-state">Failed to load logs</div>'; }
}

function logEntryHtml(l) {
    const time = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : '--';
    const levelCls = (l.level || '').toLowerCase() === 'error' ? 'log-error'
        : (l.level || '').toLowerCase() === 'warning' ? 'log-warning' : 'log-info';
    return `<div class="log-entry ${levelCls}">
        <span class="log-time">${time}</span>
        <span class="log-source">[${esc(l.source || '')}]</span>
        <span class="log-msg">${esc(l.message || '')}</span>
    </div>`;
}

function appendLog(l) {
    const container = document.getElementById('logs-container');
    if (!container) return;
    // Remove empty state if present
    const empty = container.querySelector('.empty-state');
    if (empty) empty.remove();

    container.insertAdjacentHTML('beforeend', logEntryHtml(l));
    // Keep max 500 entries in DOM
    while (container.children.length > 500) container.removeChild(container.firstChild);
    // Auto-scroll if near bottom
    if (container.scrollHeight - container.scrollTop - container.clientHeight < 100) {
        container.scrollTop = container.scrollHeight;
    }
}

// === Strategy Parameters (DryRun) ===
async function loadParameters() {
    if (!state.dryRunMode) return;
    const container = document.getElementById('params-container');
    try {
        const params = await api('/strategy/parameters');
        if (!params || Object.keys(params).length === 0) {
            container.innerHTML = '<div class="empty-state">No parameters available</div>';
            return;
        }
        let html = '<div class="params-form">';
        for (const [key, value] of Object.entries(params)) {
            html += `<div class="param-row">
                <label class="param-label">${esc(key)}</label>
                <input class="param-input" type="text" data-key="${esc(key)}" value="${esc(value)}">
            </div>`;
        }
        html += '<button id="btn-apply-params" class="btn btn-primary">Apply</button></div>';
        container.innerHTML = html;
        document.getElementById('btn-apply-params').addEventListener('click', applyParameters);
    } catch { container.innerHTML = '<div class="empty-state">Failed to load parameters</div>'; }
}

async function applyParameters() {
    const inputs = document.querySelectorAll('.param-input');
    const params = {};
    inputs.forEach(input => { params[input.dataset.key] = input.value; });
    try {
        await api('/strategy/parameters', { method: 'PUT', body: JSON.stringify(params) });
        showToast('Parameters updated', 'success');
    } catch (e) { showToast('Failed: ' + e.message, 'error'); }
}

// === Market Scores (DryRun) ===
async function loadMarketScores() {
    if (!state.dryRunMode) return;
    const container = document.getElementById('scores-container');
    try {
        const scores = await api('/strategy/market-scores');
        if (!scores?.length) {
            container.innerHTML = '<div class="empty-state">No market scores available</div>';
            return;
        }
        const maxScore = Math.max(...scores.map(s => s.score), 0.01);
        container.innerHTML = scores.map(s => {
            const pct = (s.score / maxScore * 100).toFixed(1);
            const selectedCls = s.isSelected ? 'score-selected' : '';
            const posIcon = s.hasPosition ? '<span class="pos-dot" title="Has position"></span>' : '';
            const question = s.question ? esc(s.question).substring(0, 50) : shortId(s.tokenId);
            return `<div class="score-row ${selectedCls}">
                <div class="score-info">
                    ${posIcon}
                    <span class="score-question" title="${esc(s.tokenId)}">${question}</span>
                </div>
                <div class="score-bar-wrap">
                    <div class="score-bar" style="width:${pct}%"></div>
                    <span class="score-value">${s.score.toFixed(3)}</span>
                </div>
            </div>`;
        }).join('');
    } catch { container.innerHTML = '<div class="empty-state">Failed to load scores</div>'; }
}

// === Equity Chart ===
function initEquityChart() {
    const ctx = document.getElementById('equity-chart');
    if (!ctx || typeof Chart === 'undefined') return;
    state.equityChart = new Chart(ctx, {
        type: 'line',
        data: {
            datasets: [{
                label: 'Equity',
                data: [],
                borderColor: '#58a6ff',
                backgroundColor: 'rgba(88, 166, 255, 0.1)',
                fill: true,
                tension: 0.3,
                pointRadius: 0,
                borderWidth: 2
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { intersect: false, mode: 'index' },
            plugins: {
                legend: { display: false },
                tooltip: {
                    callbacks: {
                        label: ctx => `$${ctx.parsed.y.toFixed(2)}`
                    }
                }
            },
            scales: {
                x: {
                    type: 'time',
                    time: { unit: 'minute', displayFormats: { minute: 'HH:mm' } },
                    grid: { color: 'rgba(48,54,61,0.5)' },
                    ticks: { color: '#8b949e', maxTicksLimit: 8 }
                },
                y: {
                    grid: { color: 'rgba(48,54,61,0.5)' },
                    ticks: { color: '#8b949e', callback: v => '$' + v.toFixed(0) }
                }
            }
        }
    });
}

function updateEquityChart(point) {
    if (!state.equityChart || state.chartMode !== 'live') return;
    const ds = state.equityChart.data.datasets[0];
    ds.data.push({ x: new Date(point.time || Date.now()), y: point.equity });
    // Cap at 500 points
    if (ds.data.length > 500) ds.data.shift();
    state.equityChart.update('none');
}

async function loadEquityCurve() {
    if (!state.dryRunMode) return;
    try {
        const data = await api('/equity-curve');
        if (!data?.length || !state.equityChart) return;
        const ds = state.equityChart.data.datasets[0];
        ds.data = data.map(p => ({ x: new Date(p.time), y: p.equity }));
        // Cap at 500
        if (ds.data.length > 500) ds.data = ds.data.slice(-500);
        state.equityChart.update();
    } catch {}
}

async function loadBacktestCurves() {
    try {
        const result = await api('/backtest/equity-curves');
        if (result.status !== 'completed' || !result.curves) {
            showToast('No backtest data available. Run a backtest first.', 'info');
            return;
        }
        state.backtestCurves = result.curves;
        showBacktestCurves();
    } catch { showToast('Failed to load backtest curves', 'error'); }
}

const chartColors = ['#58a6ff', '#3fb950', '#f85149', '#d29922', '#bc8cff', '#f778ba'];

function showBacktestCurves() {
    if (!state.equityChart || !state.backtestCurves) return;
    state.equityChart.data.datasets = state.backtestCurves.map((c, i) => ({
        label: c.strategy,
        data: (c.points || []).map(p => ({ x: new Date(p.time), y: p.equity })),
        borderColor: chartColors[i % chartColors.length],
        backgroundColor: 'transparent',
        fill: false,
        tension: 0.3,
        pointRadius: 0,
        borderWidth: 2
    }));
    state.equityChart.options.plugins.legend.display = true;
    state.equityChart.update();
}

function showLiveCurve() {
    if (!state.equityChart) return;
    state.equityChart.data.datasets = [{
        label: 'Equity',
        data: [],
        borderColor: '#58a6ff',
        backgroundColor: 'rgba(88, 166, 255, 0.1)',
        fill: true,
        tension: 0.3,
        pointRadius: 0,
        borderWidth: 2
    }];
    state.equityChart.options.plugins.legend.display = false;
    state.equityChart.update();
    loadEquityCurve();
}

function setupChartToggle() {
    document.getElementById('chart-live')?.addEventListener('click', () => {
        state.chartMode = 'live';
        document.getElementById('chart-live').classList.add('active');
        document.getElementById('chart-backtest').classList.remove('active');
        showLiveCurve();
    });
    document.getElementById('chart-backtest')?.addEventListener('click', () => {
        state.chartMode = 'backtest';
        document.getElementById('chart-backtest').classList.add('active');
        document.getElementById('chart-live').classList.remove('active');
        loadBacktestCurves();
    });
}

// === Strategy Switching ===
async function loadStrategy() {
    if (!state.dryRunMode) return;
    try {
        const data = await api('/strategy');
        const dropdown = document.getElementById('strategy-dropdown');
        if (!dropdown) return;
        dropdown.innerHTML = (data.available || []).map(s =>
            `<option value="${s}" ${s === data.current ? 'selected' : ''}>${s}</option>`
        ).join('');
    } catch {}
}

async function switchStrategy(name, resetState) {
    try {
        const result = await api('/strategy', {
            method: 'PUT',
            body: JSON.stringify({ strategy: name, resetState })
        });
        if (result.success) {
            showToast(`Switched to ${result.strategy}` + (resetState ? ' (reset)' : ''), 'success');
            if (resetState && state.equityChart && state.chartMode === 'live') {
                state.equityChart.data.datasets[0].data = [];
                state.equityChart.update();
            }
            loadParameters();
        }
    } catch (e) { showToast('Switch failed: ' + e.message, 'error'); }
}

function setupStrategySelector() {
    const dropdown = document.getElementById('strategy-dropdown');
    if (!dropdown) return;
    dropdown.addEventListener('change', () => {
        const reset = document.getElementById('strategy-reset')?.checked ?? true;
        switchStrategy(dropdown.value, reset);
    });
}

// === Order Placement ===
function updateOrderSummary() {
    const price = parseFloat(document.getElementById('order-price').value);
    const size = parseFloat(document.getElementById('order-size').value);
    const summary = document.getElementById('order-summary');
    if (!state.selectedTokenId) { summary.textContent = 'Select a market first'; return; }
    if (!state.hasCredentials && !state.dryRunMode) { summary.textContent = 'API credentials required to trade'; return; }
    if (!price || !size) { summary.textContent = 'Enter price and size'; return; }
    const modeTag = state.dryRunMode ? ' [SIMULATED]' : '';
    summary.textContent = `${state.orderSide} ${size} tokens @ ${price} = $${(price * size).toFixed(2)} USDC${modeTag}`;
}

async function submitOrder() {
    if (!state.hasCredentials && !state.dryRunMode) { showToast('API credentials required', 'error'); return; }
    const tokenId = state.selectedTokenId;
    const price = parseFloat(document.getElementById('order-price').value);
    const size = parseFloat(document.getElementById('order-size').value);
    if (!tokenId || !price || !size) { showToast('Fill all fields', 'error'); return; }
    if (price < 0.01 || price > 0.99) { showToast('Price must be 0.01-0.99', 'error'); return; }

    try {
        document.getElementById('btn-submit-order').disabled = true;
        const r = await api('/orders', { method: 'POST', body: JSON.stringify({ tokenId, price, size, side: state.orderSide }) });
        if (r.success) { showToast('Order placed: ' + (r.orderId || r.orderID || 'OK'), 'success'); document.getElementById('order-price').value = ''; document.getElementById('order-size').value = ''; updateOrderSummary(); loadOrders(); }
        else showToast('Failed: ' + (r.errorMsg || 'Unknown'), 'error');
    } catch (e) { showToast('Error: ' + e.message, 'error'); }
    finally { document.getElementById('btn-submit-order').disabled = !(state.hasCredentials || state.dryRunMode) || !state.selectedTokenId; }
}

async function cancelOrder(orderId) {
    try { await api(`/orders/${orderId}`, { method: 'DELETE' }); showToast('Cancelled', 'success'); loadOrders(); }
    catch (e) { showToast('Failed: ' + e.message, 'error'); }
}

// === SignalR ===
async function connectSignalR() {
    try {
        const conn = new signalR.HubConnectionBuilder()
            .withUrl('/hub/trading', {
                accessTokenFactory: () => getToken()
            })
            .withAutomaticReconnect()
            .build();
        conn.on('OrderBookUpdate', d => { if (d.tokenId === state.selectedTokenId && d.book) renderOrderBook(d.book); });
        conn.on('TradeUpdate', t => { if (t?.price) showToast(`Trade: ${t.side} ${t.size} @ ${t.price}`, 'info'); });

        // DryRun SignalR events
        conn.on('DryRunUpdate', d => {
            if (!state.dryRunMode) return;
            updateBalanceDisplay(String(d.balance));
            const realEl = document.getElementById('pnl-realized');
            const unrealEl = document.getElementById('pnl-unrealized');
            const tickEl = document.getElementById('tick-count');
            if (realEl) {
                const rPnl = d.realizedPnl || 0;
                realEl.textContent = `$${rPnl.toFixed(2)}`;
                realEl.className = 'pnl-value ' + (rPnl >= 0 ? 'pnl-positive' : 'pnl-negative');
            }
            if (unrealEl) {
                const uPnl = d.unrealizedPnl || 0;
                unrealEl.textContent = `$${uPnl.toFixed(2)}`;
                unrealEl.className = 'pnl-value ' + (uPnl >= 0 ? 'pnl-positive' : 'pnl-negative');
            }
            if (tickEl) tickEl.textContent = `#${d.tickCount || 0}`;

            // Update equity chart with live data
            if (d.totalEquity != null) {
                updateEquityChart({ time: new Date(), equity: d.totalEquity });
            }
        });

        conn.on('DryRunLog', l => {
            if (!state.dryRunMode) return;
            appendLog(l);
        });

        conn.onreconnecting(() => showToast('Reconnecting...', 'info'));
        conn.onreconnected(() => showToast('Reconnected', 'success'));
        await conn.start();
        state.connection = conn;
    } catch (e) { console.warn('SignalR:', e.message); }
}

// === Tab / View Switching ===
function setupTabs() {
    document.querySelectorAll('.tab').forEach(tab => {
        tab.addEventListener('click', () => {
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
            tab.classList.add('active');
            document.getElementById('tab-' + tab.dataset.tab).classList.add('active');
            switch (tab.dataset.tab) {
                case 'positions': loadPositions(); break;
                case 'orders': loadOrders(); break;
                case 'trades': loadTrades(); break;
                case 'logs': loadLogs(); break;
                case 'params': loadParameters(); break;
                case 'scores': loadMarketScores(); break;
            }
        });
    });
}

function setupEvents() {
    document.querySelectorAll('.side-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.side-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            state.orderSide = btn.dataset.side;
            updateOrderSummary();
        });
    });
    document.getElementById('order-price').addEventListener('input', updateOrderSummary);
    document.getElementById('order-size').addEventListener('input', updateOrderSummary);
    document.getElementById('btn-submit-order').addEventListener('click', submitOrder);
    document.getElementById('market-search').addEventListener('input', e => renderMarkets(e.target.value));
    document.getElementById('refresh-markets').addEventListener('click', async () => {
        try { await api('/markets/refresh', { method: 'POST' }); await loadMarkets(); await loadEvents(); }
        catch (e) { showToast('Refresh failed: ' + e.message, 'error'); }
    });

    // View toggle: Trending / Events
    document.getElementById('view-trending')?.addEventListener('click', () => {
        state.view = 'trending';
        document.getElementById('view-trending').classList.add('active');
        document.getElementById('view-events').classList.remove('active');
        renderMarkets(document.getElementById('market-search').value);
    });
    document.getElementById('view-events')?.addEventListener('click', () => {
        state.view = 'events';
        document.getElementById('view-events').classList.add('active');
        document.getElementById('view-trending').classList.remove('active');
        renderMarkets(document.getElementById('market-search').value);
    });
}

// === Main Navigation / View Switching ===
function showView(viewName) {
    document.querySelectorAll('.view-panel').forEach(el => el.style.display = 'none');
    document.querySelectorAll('.nav-btn').forEach(btn => btn.classList.remove('active'));

    const panel = document.getElementById('view-' + viewName);
    if (panel) panel.style.display = '';

    const btn = document.querySelector(`.nav-btn[data-view="${viewName}"]`);
    if (btn) btn.classList.add('active');

    // Load data for the view
    if (viewName === 'settings') loadSettingsPage();
    if (viewName === 'wallet') loadWalletPage();
}

// === Risk Status Panel ===
async function loadRiskStatus() {
    const container = document.getElementById('risk-bars');
    if (!container) return;
    try {
        const status = await api('/risk/status');
        const bars = [
            { label: 'Daily Spending', current: status.dailySpending, limit: status.dailySpendingLimit },
            { label: 'Total Exposure', current: status.totalExposure, limit: status.totalExposureLimit },
            { label: 'Daily P&L Loss', current: -status.dailyPnl, limit: status.dailyLossLimit },
            { label: 'Drawdown', current: status.maxDrawdown, limit: status.maxDrawdownLimit }
        ];
        container.innerHTML = bars.map(b => {
            const pct = b.limit > 0 ? Math.min((b.current / b.limit) * 100, 100) : 0;
            const cls = pct >= 90 ? 'risk-critical' : pct >= 70 ? 'risk-warning' : 'risk-ok';
            return `<div class="risk-bar-row">
                <div class="risk-bar-label">${b.label}</div>
                <div class="risk-bar-track">
                    <div class="risk-bar-fill ${cls}" style="width:${pct.toFixed(1)}%"></div>
                </div>
                <div class="risk-bar-text">$${b.current.toFixed(0)} / $${b.limit.toFixed(0)}</div>
            </div>`;
        }).join('');

        // Show alerts
        if (status.alerts && status.alerts.length > 0) {
            container.innerHTML += status.alerts.map(a =>
                `<div class="risk-alert risk-alert-${a.level}">${esc(a.message)}</div>`
            ).join('');
        }
    } catch { container.innerHTML = '<div class="empty-state" style="padding:8px">--</div>'; }
}

// === Init ===
async function init() {
    // Auth gate
    setupWeb3Listeners();
    if (!isAuthenticated()) {
        showLoginScreen();
        return;
    }
    showDashboard();

    setupTabs();
    setupEvents();
    setupChartToggle();
    setupStrategySelector();
    await checkStatus();
    updateOrderSummary();

    if (state.dryRunMode) {
        initEquityChart();
    }

    await Promise.all([loadMarkets(), loadEvents(), loadBalance(), loadPositions(), connectSignalR(), loadRiskStatus()]);

    if (state.dryRunMode) {
        loadStrategy();
        loadEquityCurve();
    }

    // Polling: balance and risk status
    if (state.hasCredentials && !state.dryRunMode) setInterval(loadBalance, 30000);
    setInterval(loadRiskStatus, 15000);
}

document.addEventListener('DOMContentLoaded', init);
