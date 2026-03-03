# Polymarket 开发计划实施状态与待办事项

## 总体完成度

```
Phase 1  核心基础设施       ████████████████████ 100%  (4/4)
Phase 2  Brokerage 连接层   ████████████████████ 100%  (9/9)
Phase 3  回测基础设施       ████████████████████ 100%  (3/3)
Phase 4  做市策略           ████████████████████ 100%  (2/2)
Phase 5  量化策略框架       ██████████████████░░  90%  (3.5/4 — 缺 SentimentAlpha)
Dashboard 做市模拟系统       ████████████████████ 100%  (全部完成)
Dashboard 数据下载工具       ████████████████████ 100%  (CLI + API 端点)
测试                        ██████████████████░░  90%  (单元+集成+策略验证完成, 缺Dashboard测试)
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
| T.10 | Dashboard 测试 | **MISSING** | 无 Dashboard 项目的自动化测试 |
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
- **总测试数**: 90 个 (原 50 + 新 40)，零网络调用

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

#### 4. SentimentAlpha 模型

**现状**: 计划 5.1 中提到的 `SentimentAlpha`（新闻/社交情绪驱动）未实现。其他 3 个 Alpha 模型已完成。

**工作内容**:
- 确定情绪数据源 (Twitter/X API、新闻 API、或 Polymarket 自身的 comments)
- 实现 `SentimentAlpha : AlphaModel`
- 情绪评分 → 概率偏差 → Insight 信号
- 与 KellyPortfolioConstruction 集成测试

**依赖**: 外部数据源接入

**预估工作量**: 3 天

**备注**: 此项优先级可降低 — 当前 3 个 Alpha 模型已覆盖主要信号来源，SentimentAlpha 属于增强型功能。

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

#### 6. Dashboard 自动化测试

**现状**: Dashboard 无任何自动化测试。DryRunEngine 的被动成交模型、策略评估逻辑均未覆盖。

**工作内容**:
- DryRunEngine 单元测试: 撮合逻辑、被动成交概率、持仓计算
- MarketMakingStrategy 单元测试: 报价生成、库存倾斜、紧急模式
- 市场评分算法测试: ScoreMarket 各维度权重验证
- TradingController API 测试: HTTP 端点正确性

**依赖**: 无

**预估工作量**: 2 天

---

#### 7. 错误处理与重连健壮性

**现状**: WebSocket 断连重连逻辑存在但简单 (5 秒后重试)。REST API 无重试机制。

**工作内容**:
- REST API: 添加指数退避重试 (429 Too Many Requests / 5xx 错误)
- WebSocket: 实现心跳检测、订阅状态恢复、断连时订单状态同步
- DryRunEngine: 订单簿缓存过期检测 (长时间无更新的 token 标记为 stale)
- 全局异常处理审计

**依赖**: 无

**预估工作量**: 1.5 天

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
| **P1** | SentimentAlpha | 3 天 (可降级) | 待做 |
| **P2** | 小资金实盘验证 | 1 天 | 待做 |
| **P2** | Dashboard 自动化测试 | 2 天 | 待做 |
| **P2** | 错误处理与重连 | 1.5 天 | 待做 |
| **P3** | 回测对比框架 | 2 天 | 待做 |
| **P3** | Dashboard 增强 | 3 天 | 待做 |
| **P3** | 批量 Symbol 注册 | 1 天 | 待做 |
| | **剩余合计** | **~13.5 天** | |
| | P1 (核心剩余) | **~3 天** | |
