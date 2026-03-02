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
    view: 'trending' // 'trending' | 'events'
};

// === API ===
async function api(path, options = {}) {
    const res = await fetch(`/api${path}`, {
        headers: { 'Content-Type': 'application/json' },
        ...options
    });
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
        const conn = new signalR.HubConnectionBuilder().withUrl('/hub/trading').withAutomaticReconnect().build();
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

// === Init ===
async function init() {
    setupTabs();
    setupEvents();
    await checkStatus();
    updateOrderSummary();
    await Promise.all([loadMarkets(), loadEvents(), loadBalance(), loadPositions(), connectSignalR()]);
    // Polling: only poll balance in non-dryrun mode
    if (state.hasCredentials && !state.dryRunMode) setInterval(loadBalance, 30000);
}

document.addEventListener('DOMContentLoaded', init);
