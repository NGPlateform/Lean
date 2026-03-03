# Polymarket 开发计划实施状态与待办事项

## 总体完成度

```
Phase 1  核心基础设施       ████████████████████ 100%  (4/4)
Phase 2  Brokerage 连接层   ████████████████████ 100%  (9/9)
Phase 3  回测基础设施       ████████████████████ 100%  (3/3)
Phase 4  做市策略           ████████████████████ 100%  (2/2)
Phase 5  量化策略框架       ██████████████████░░  90%  (3.5/4 — SentimentAlpha 部分由 SentimentService 替代)
Dashboard 做市模拟系统       ████████████████████ 100%  (全部完成)
Dashboard 数据下载工具       ████████████████████ 100%  (CLI + API 端点)
BTC 相关性分析 & 随动策略    ████████████████████ 100%  (BTC 数据下载 + 相关性监控 + BtcFollowMM 策略)
外部情绪集成                 ████████████████████ 100%  (Fear&Greed Index + Binance 资金费率 → SentimentService)
测试                        ████████████████████  95%  (189 Dashboard tests, 90 brokerage tests)
```

---

## 逐项完成状态

### Phase 1: 核心基础设施

| # | 计划项 | 状态 | 说明 |
|---|--------|------|------|
| 1.1 | `Market.Add("polymarket", 43)` 注册 | **DONE** | `PolymarketBrokerageFactory.cs` 静态构造函数中完成 |
| 1.2 | SecurityType 使用 Crypto | **DONE** | 全局统一使用 `SecurityType.Crypto` |
| 1.3 | PolymarketSymbolMapper | **DONE** | 实现 `ISymbolMapper`，支持 ticker ↔ token_id 双向映射 |
| 1.4 | market-hours-database.json 条目 | **DONE** | `Crypto-polymarket-[*]` 24/7 全天候已配置 |
| 1.5 | symbol-properties-database.csv 条目 | **DONE** | ETH5000MAR26YES/NO 等示例条目已添加 |
| 1.6 | PredictionMarketData 自定义数据类型 | **DONE** | 含 ConditionId、Question、EndDate、概率、Volume、Reader/GetSource |

### Phase 2: Brokerage 连接层

| # | 计划项 | 状态 | 说明 |
|---|--------|------|------|
| 2.1 | PolymarketBrokerage 主类 | **DONE** | 继承 `BaseWebsocketsBrokerage` + `IDataQueueHandler` |
| — | PlaceOrder | **DONE** | EIP-712 签名 → POST /order → 触发 OrderEvent |
| — | CancelOrder | **DONE** | DELETE /order/{id} → 触发 OrderEvent(Canceled) |
| — | GetOpenOrders | **DONE** | REST API → 转换为 LEAN Order 列表 |
| — | GetAccountHoldings | **DONE** | GET positions → 转换为 Holding 列表 |
| — | GetCashBalance | **DONE** | GET balance → 返回 USDC CashAmount |
| — | Subscribe/OnMessage | **DONE** | WebSocket 订阅 + OrderBook 增量更新 |
| 2.2 | EIP-712 订单签名 | **DONE** | Nethereum 集成，Polygon ChainId=137，CTF Exchange 地址 |
| 2.3 | BrokerageModel | **DONE** | Cash 账户、Leverage=1、限价 [0,1] 验证 |
| 2.4 | FeeModel | **DONE** | Maker 0% / Taker 0% / Settlement 2% |
| 2.5 | OrderProperties | **DONE** | PostOnly、TimeToLive、Nonce、FillOrKill |
| 2.6 | BrokerageFactory | **DONE** | MEF [Export]、config key 映射、CreateBrokerage() |
| 2.7 | config.json 配置 | **DONE** | 4 个 polymarket- 配置项已加入 Launcher/config.json |
| 2.8 | REST API 客户端 | **DONE** | PolymarketApiClient: 订单/余额/持仓/订单簿 |
| 2.9 | WebSocket 客户端 | **DONE** | PolymarketWebSocketClient: 市场行情/用户订单订阅 |

### Phase 3: 回测基础设施

| # | 计划项 | 状态 | 说明 |
|---|--------|------|------|
| 3.1 | PolymarketDataDownloader | **DONE** | 实现 DownloadTradeData/FetchTrades/AggregateToBars |
| 3.2 | 历史数据文件 | **DONE** | 24 市场 48 tokens，7 天价格历史 (2/24–3/2)，218 CSV + 48 盘口快照，6,457 bars |
| 3.3 | 自定义 FillModel | **DONE** | PolymarketFillModel: 价格钳位 [0.001, 0.999]、宽价差处理 |

### Phase 4: 做市策略

| # | 计划项 | 状态 | 说明 |
|---|--------|------|------|
| 4.1 | PolymarketMarketMaker (LEAN 算法) | **DONE** | 多层报价、库存偏移、参数化配置、OnData/OnOrderEvent |
| 4.2 | YES/NO 互补套利 | **DONE** | CheckComplementaryArbitrage(): bid sum > 1 卖双边 / ask sum < 1 买双边 |

### Phase 5: 量化策略框架

| # | 计划项 | 状态 | 说明 |
|---|--------|------|------|
| 5.1a | CrossMarketArbitrageAlpha | **DONE** | YES+NO 定价偏离检测，confidence=0.9 |
| 5.1b | ProbabilityMeanReversionAlpha | **DONE** | z-score 均值回归，lookback=60，deviation=2σ |
| 5.1c | CrossMarketCorrelationAlpha | **DONE** | 相关市场联动信号，correlation window=30 |
| 5.1d | SentimentAlpha | **MISSING** | 计划中提到的新闻/社交情绪 Alpha 模型未实现 |
| 5.2 | Kelly Portfolio Construction | **DONE** | f* = (pb-q)/b，Half-Kelly，maxPositionSize=10% |
| 5.3 | PredictionMarketRiskManagement | **DONE** | 结算风险(3天)、单市场10%、极端概率减半、总敞口50% |

### Dashboard (计划外增量)

| # | 组件 | 状态 | 说明 |
|---|------|------|------|
| D.1 | MarketMakingStrategy (DryRun 版) | **DONE** | 报价管理、库存倾斜、波动率自适应、市场评分 |
| D.2 | MeanReversionStrategy | **DONE** | 价格偏离均值时反向交易 |
| D.3 | SpreadCaptureStrategy | **DONE** | bid-ask spread 内挂单 |
| D.4 | DryRunEngine | **DONE** | 模拟撮合 + 被动成交模型 + 自动订阅 |
| D.5 | Web UI (SignalR) | **DONE** | 实时仪表盘：行情/订单/持仓/PnL |
| D.6 | LeanStubs 解耦 | **DONE** | 94 行桩代码实现 LEAN 独立编译 |
| D.7 | DataDownloadService | **DONE** | 市场发现 (Gamma API) → 价格历史 (CLOB prices-history) → 盘口快照 (CLOB book) |
| D.8 | 数据下载 API 端点 | **DONE** | `POST /api/data/download?days=N` + `GET /api/data/download/status` |
| D.9 | CLI 下载模式 | **DONE** | `dotnet run -- --download-data --days 30`，无需启动 Web 服务器 |

### BTC 相关性分析与随动策略 (计划外增量)

| # | 组件 | 状态 | 说明 |
|---|------|------|------|
| B.1 | BTC 数据下载 (Binance) | **DONE** | `DownloadBtcKlinesAsync()`: 5min K 线分页下载，聚合为 10min bars，存储 `data/reference/btc-usd/`。`--download-btc` CLI flag + 集成入 `DownloadAllAsync()` |
| B.2 | BtcPriceService | **DONE** | `BackgroundService` 每 10s 轮询 Binance ticker，60 点滑动窗口，暴露 `CurrentPrice`/`GetReturn()`/`Momentum` (短期 vs 长期 EMA) |
| B.3 | CorrelationMonitor | **DONE** | 实时 BTC↔token 相关性计算：20 点滑动窗口 Pearson 相关系数，`GetCorrelation(tokenId)` 供策略查询 |
| B.4 | BtcFollowMMStrategy | **DONE** | 完整 MM 逻辑 + BTC 信号层：动量阈值、spread 放大、size 缩减、strike 感知 delta、相关性门控 (\|r\| < 0.3 回退普通 MM)、**TTE 动态缩放** (0.75×–1.5× 基于到期距离)、**涨跌不对称调整** (down-move × 0.5 scale) |
| B.5 | Python 相关性分析 Notebooks | **DONE** | `btc_polymarket_correlation.ipynb`: CCF/delta/rolling 相关性。`tte_correlation_analysis.ipynb`: TTE 分段 CCF、Daily vs Monthly、涨跌不对称、内部情绪指标、综合情绪指数。报告见 [`docs/BtcCorrelationAnalysis.md`](BtcCorrelationAnalysis.md) |
| B.6 | 测试 | **DONE** | 61 个新测试 (BtcPriceServiceTests 16 + BtcFollowMMStrategyTests 35 + CorrelationMonitorTests 10)，总计 138 个 Dashboard 测试全部通过 |
| B.7 | SentimentService | **DONE** | `BackgroundService` 双定时器轮询：Fear & Greed Index (每30分钟) + Binance BTCUSDT 资金费率 (每60秒)。暴露 `GetSentimentSpreadMultiplier()` [0.8,1.5] 和 `GetSentimentDirectionalBias()` [-1,1] 反向情绪信号 |
| B.8 | BtcFollowMM 情绪集成 | **DONE** | 三参数构造函数 + `EnableSentiment` 参数 + 乘法覆盖层：极端情绪/高费率→加宽 spread，反向偏移→方向性 size 调整 |
| B.9 | `/api/sentiment` 端点 | **DONE** | 返回 FGI 值/分类、资金费率/信号、复合乘数/方向偏移 |
| B.10 | 情绪测试 | **DONE** | 51 个新测试 (SentimentServiceTests 25 + BtcFollowMMStrategy 情绪集成 7 + FGI 分类 19)，总计 189 个 Dashboard 测试全部通过 |

### 测试

| # | 测试项 | 状态 | 说明 |
|---|--------|------|------|
| T.1 | PolymarketSymbolMapperTests | **DONE** | 16 个测试用例 |
| T.2 | PolymarketBrokerageModelTests | **DONE** | 14 个测试用例 (订单验证、价格范围) |
| T.3 | PolymarketFeeModelTests | **DONE** | 4 个费率计算测试 |
| T.4 | PolymarketOrderPropertiesTests | **DONE** | 5 个订单属性测试 |
| T.5 | EIP712SignerTests | **DONE** | 11 个签名生成与验证测试 |
| T.6 | PolymarketApiClientTests (REST API) | **DONE** | 12 个测试 — MockHttpMessageHandler 拦截全部 HTTP，覆盖 GetOpenOrders/Positions/Balance/OrderBook/Trades/PlaceOrder/CancelOrder/Auth headers |
| T.7 | PolymarketWebSocketTests | **DONE** | 12 个测试 — 订阅创建 (market/user)、消息解析 (book/price_change/trade/order/invalid/null/empty/unknown) |
| T.8 | PolymarketBrokerageIntegrationTests | **DONE** | 16 个测试 — PlaceOrder/CancelOrder/UpdateOrder 全链路、GetOpenOrders/Holdings/CashBalance 转换、WebSocket order update (live/matched/partial/canceled) |
| T.9 | PolymarketStrategyValidationTests | **DONE** | 37 个测试 — Kelly PCM (7: flat/low-conf/up/down/half-vs-full/max-clamp/multi)、Risk Model (3: defaults/custom/settlement)、Alpha Models (6: arb/meanrev/corr 初始化+自定义)、Market Maker (8: price-clamp/skew/levels/sizes/arb-overpriced/underpriced/normal)、Data Quality (10: dir/json/48-tokens/pairs/csv-format/ohlc/dust/dates/bars/yes-no-independence) |
| T.10 | Dashboard 测试 | **DONE** | 189 个测试 — MarketMakingStrategy (20)、MeanReversionStrategy (17)、SpreadCaptureStrategy (15)、DryRunModels (14)、ApiClientRetryTests (8)、OrderBookStalenessTests (3)、BtcPriceServiceTests (16)、BtcFollowMMStrategyTests (42, 含 TTE 7 + Asymmetry 5 + Sentiment 7)、CorrelationMonitorTests (10)、SentimentServiceTests (44, 含分类 19 + 信号 7 + 状态 4 + 乘数 6 + 偏移 5 + HTTP 4) |
| T.11 | 策略回测 P&L 验证 | **MISSING** | 需部署 .NET 10 SDK 运行完整 LEAN 引擎回测 |

---

## 待完成工作清单 (按优先级排序)

### P0 — 阻塞性缺失 (影响回测和生产验证)

#### ~~1. 下载历史数据~~ ✅ 已完成

**完成情况**: `Dashboard/Services/DataDownloadService.cs` 实现独立数据下载工具。
- 使用 CLOB `prices-history` API 获取每 token 价格快照（~10 分钟间隔）
- 使用 CLOB `book` API 获取 top-5 盘口快照
- Gamma API 市场发现 + crypto 关键词过滤 + 体育排除规则
- 支持 CLI 模式 (`--download-data --days N`) 和 API 端点 (`POST /api/data/download`)
- **数据覆盖**: 24 crypto 市场、48 tokens、218 trade CSV、48 orderbook CSV、6,457 bars
- **日期范围**: 2026-02-24 ~ 2026-03-02 (7 天)
- **数据质量**: YES/NO 价格独立、无 dust trades、无体育类误匹配

---

#### ~~2. Brokerage 集成测试~~ ✅ 已完成

**完成情况**: 新增 3 个集成测试文件 (40 个测试)，使用 MockHttpMessageHandler 拦截全部 HTTP。
- `PolymarketApiClientTests.cs` — 12 个 REST API 测试 (订单/余额/持仓/订单簿/交易/下单/撤单/认证头)
- `PolymarketWebSocketTests.cs` — 12 个 WebSocket 测试 (订阅创建/消息解析/边界条件)
- `PolymarketBrokerageIntegrationTests.cs` — 16 个全链路测试 (PlaceOrder/CancelOrder/UpdateOrder/GetOpenOrders/Holdings/CashBalance/WS order updates)
- `Helpers/MockHttpMessageHandler.cs` — 可复用 mock HTTP handler
- `PolymarketApiClient.cs` 新增 `HttpMessageHandler handler = null` 可选参数 (向后兼容)
- **总测试数**: 90 个 (原 50 + 集成 40)，零网络调用

---

### P1 — 重要缺失 (影响策略验证完整性)

#### ~~3. 策略回测验证~~ ✅ 部分完成 (组件级验证)

**完成情况**: `PolymarketStrategyValidationTests.cs` — 37 个测试覆盖策略逻辑组件验证。
- **Kelly PCM** (7 tests): flat/low-confidence 零目标、Up/Down 方向正确、Half-Kelly < Full-Kelly、MaxPositionSize 钳位、多 Insight 独立计算
- **Risk Management** (3 tests): 默认/自定义参数、SetSettlementDate 设置
- **Alpha Models** (6 tests): CrossMarketArbitrageAlpha/ProbabilityMeanReversionAlpha/CrossMarketCorrelationAlpha 初始化和自定义参数
- **Market Maker** (8 tests): ClampPrice [0.01,0.99]、零/正/负库存 skew、多层报价间距、外层 size 递增、互补套利过高/过低/正常检测
- **Data Quality** (10 tests): 目录/JSON 存在、48 token 文件夹、YES/NO 配对、CSV 6 列 OHLCV 格式、OHLC 关系验证、dust price < 1%、7 天日期覆盖、6000+ bars、YES/NO 价格独立性
- **未完成**: 完整 LEAN 引擎回测需 .NET 10 SDK (当前环境仅 .NET 7)

---

#### ~~4. SentimentAlpha 模型~~ ✅ 部分完成 (SentimentService 替代方案)

**完成情况**: `SentimentService` 实现了外部情绪数据集成，虽非 LEAN `AlphaModel`，但直接服务于 Dashboard 做市策略。
- **Fear & Greed Index**: `api.alternative.me` 每 30 分钟轮询，0-100 指数 + 5 级分类
- **Binance 资金费率**: `fapi.binance.com` 每 60 秒轮询，BTCUSDT 8 小时费率 + 归一化信号
- **策略接口**: `GetSentimentSpreadMultiplier()` [0.8,1.5] + `GetSentimentDirectionalBias()` [-1,1]
- **BtcFollowMM 集成**: 乘法覆盖层，极端情绪/高费率→防御性加宽 spread，反向情绪→方向性 size 偏移
- **API 端点**: `GET /api/sentiment` 暴露全部指标
- **测试**: 51 个新测试全部通过

**未完成**: LEAN `AlphaModel` 接口的 `SentimentAlpha` 仍未实现 (需要不同的数据管道)

---

### P2 — 质量提升 (非阻塞但影响生产安全)

#### 5. 小资金实盘验证

**现状**: 计划"验证方案"第 5 步 — 少量 USDC 实盘验证全链路。从未执行。

**工作内容**:
- 配置真实 Polymarket API 凭证
- 存入小额 USDC (建议 50-100 USDC)
- 验证全链路: EIP-712 签名 → REST 下单 → WebSocket 推送 → 成交回报 → 持仓更新
- 验证撤单、部分成交、余额查询
- 记录延迟和错误率

**依赖**: 真实 API 凭证 + 测试资金

**预估工作量**: 1 天

---

#### ~~6. Dashboard 自动化测试~~ ✅ 已完成

**完成情况**: 新增 `Dashboard.Tests` 项目 (net7.0) — 66 个测试，可构建运行 (与 Dashboard 同目标框架)。
- `MarketMakingStrategyTests.cs` — 20 个测试: 初始化/自定义参数、报价生成 (bid/ask)、库存倾斜 (长仓/零仓)、紧急模式 (止买+激进卖)、价格过滤 (极端高/低价)、MaxActiveMarkets/ForceInclude、requote 取消旧单、size/balance 限制、空book
- `MeanReversionStrategyTests.cs` — 17 个测试: 初始化/自定义参数、窗口历史不足/刚好足够、买入信号 (低于均值)、卖出信号 (高于均值且持仓)、无仓不卖、小偏差无操作、仓位上限、卖出 size 封顶、已有订单跳过、空book/极端价格/OnFill
- `SpreadCaptureStrategyTests.cs` — 15 个测试: 初始化/自定义参数、宽 spread 买入/卖出、无仓不卖、价格在 spread 内验证、窄 spread 过滤、精确 minSpread 边界、exposure 限制、订单去重、sell size 封顶、余额检查、多 token、空book/OnFill
- `DryRunModelsTests.cs` — 14 个测试: SimulatedOrder RemainingSize、SimulatedPosition UnrealizedPnl (正/负/零/同价)、SimulatedTrade 属性、DryRunLogEntry 属性、DryRunSettings 默认值/自定义、PlaceOrderAction/CancelOrderAction 属性与继承、StrategyContext
- **总测试数**: 167 个 (原 90 + Dashboard 77)，Dashboard 测试可实际构建运行 (`dotnet test` all pass)

---

#### ~~7. 错误处理与重连健壮性~~ ✅ 已完成

**完成情况**: 全面提升 REST API、WebSocket、Dashboard 的错误处理与重连健壮性。
- **REST API 重试** (`PolymarketApiClient.cs`): `ExecuteWithRetry()` 指数退避 (1s→2s→4s)，最多 3 次重试，支持 429 (尊重 Retry-After) 和 5xx；POST `/order` 标记 `isIdempotent: false` 不重试；`HttpClient.Timeout = 30s`
- **WebSocket 重连同步** (`PolymarketBrokerage.cs`): `OnWebSocketReconnected()` 钩入 `webSocket.Open` 事件，清空订单簿缓存，GET `/orders?status=live` 对账，缺失订单发射 `OrderStatus.Canceled`
- **WebSocket 心跳** (`MarketDataService.cs`): 后台 watchdog 每 5s 检查，30s 无消息 → `Abort()` 触发重连
- **订单簿过期检测** (`MarketDataService.cs` + `DryRunEngine.cs`): `_orderBookLastUpdated` 时间戳跟踪，`GetAllCachedOrderBooks()` 过滤超过阈值 (默认 60s) 的 stale 订单簿，节流日志警告
- **异常处理审计**: `MarketDataService.HandleOrderBookUpdate` / `DryRunEngine.AutoSubscribeTopMarkets` / `ExecuteFill` 裸 `catch {}` 替换为带日志记录
- **测试**: 11 个新测试 (8 个 ApiClientRetryTests + 3 个 OrderBookStalenessTests)，总计 77 个 Dashboard 测试全部通过

---

### P3 — 增强功能 (可选)

#### 8. 多策略回测对比框架

**工作内容**:
- 搭建批量回测脚本，并行运行 MM/MeanReversion/SpreadCapture
- 统一输出 P&L、Sharpe、MaxDrawdown、Fill Rate 等指标
- 参数扫描: HalfSpread、SkewFactor、OrderSize 的网格搜索

---

#### 9. Dashboard 增强

**工作内容**:
- PnL 曲线图 (时间序列)
- 市场选择评分可视化
- 策略参数热调整 (无需重启)
- 多策略切换 UI

---

#### 10. symbol-properties-database 批量注册

**现状**: 仅有少量示例条目 (ETH5000、BTCHALVING)。

**工作内容**:
- 开发脚本从 Gamma API 批量拉取活跃市场
- 自动生成 symbol-properties-database.csv 条目
- 定期更新机制

---

## 工作量汇总

| 优先级 | 工作项 | 预估 | 状态 |
|--------|--------|------|------|
| ~~**P0**~~ | ~~下载历史数据~~ | ~~0.5 天~~ | ✅ 已完成 |
| ~~**P0**~~ | ~~Brokerage 集成测试~~ | ~~2 天~~ | ✅ 已完成 (40 tests, mock HTTP) |
| ~~**P1**~~ | ~~策略回测验证~~ | ~~2 天~~ | ✅ 部分完成 (37 tests, 组件级验证; 完整引擎回测需 .NET 10) |
| ~~**P1**~~ | ~~SentimentAlpha~~ | ~~3 天~~ | ✅ 部分完成 (SentimentService 替代方案，51 tests) |
| **P2** | 小资金实盘验证 | 1 天 | 待做 |
| ~~**P2**~~ | ~~Dashboard 自动化测试~~ | ~~2 天~~ | ✅ 已完成 (66 tests, net7.0, all pass) |
| ~~**P2**~~ | ~~错误处理与重连~~ | ~~1.5 天~~ | ✅ 已完成 (REST重试+WS心跳+订单簿过期+异常审计+11 tests) |
| **P3** | 回测对比框架 | 2 天 | 待做 |
| **P3** | Dashboard 增强 | 3 天 | 待做 |
| **P3** | 批量 Symbol 注册 | 1 天 | 待做 |
| | **剩余合计** | **~10 天** | |
| | P1 (核心剩余) | **~3 天** | |
