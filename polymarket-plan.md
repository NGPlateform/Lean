# 基于 LEAN 开发 Polymarket 做市与量化策略交易

## Context

Polymarket 是基于 Polygon 区块链的预测市场平台，使用 CLOB（中央限价订单簿）撮合交易。用户交易二元结果代币（YES/NO），价格范围 $0.00–$1.00（代表概率），以 USDC 结算。LEAN 是 QuantConnect 开源的量化交易引擎，支持 29+ 券商和多资产类型，具备完善的插件化架构。本方案旨在将 Polymarket 作为新的 Brokerage 集成到 LEAN 中，并在此基础上开发做市和量化策略。

---

## 架构概览

```
Lean.Brokerages.Polymarket/          ← 独立项目，引用 LEAN 核心库
├── QuantConnect.Brokerages.Polymarket/
│   ├── PolymarketBrokerage.cs              # 主 Brokerage（继承 BaseWebsocketsBrokerage）
│   ├── PolymarketBrokerageFactory.cs       # MEF 工厂
│   ├── PolymarketBrokerageModel.cs         # 交易规则/验证/费率
│   ├── PolymarketSymbolMapper.cs           # Symbol 映射（token_id ↔ LEAN Symbol）
│   ├── PolymarketFeeModel.cs               # 手续费模型
│   ├── PolymarketOrderProperties.cs        # 自定义订单属性
│   ├── Api/
│   │   ├── PolymarketApiClient.cs          # REST API 客户端
│   │   ├── PolymarketWebSocketClient.cs    # WebSocket 数据流
│   │   └── Models/                         # API 数据模型
│   ├── Auth/
│   │   ├── EIP712Signer.cs                 # EIP-712 订单签名
│   │   └── PolymarketCredentials.cs        # 凭证管理
│   └── Data/
│       ├── PredictionMarketData.cs         # 预测市场自定义数据类型
│       └── PolymarketDataDownloader.cs     # 历史数据下载器
├── QuantConnect.Brokerages.Polymarket.Tests/
└── Strategies/
    ├── PolymarketMarketMaker.cs            # 做市策略
    ├── Alphas/                             # Alpha 信号模型
    ├── Portfolio/                          # 组合构建模型
    └── Risk/                               # 风险管理模型
```

---

## Phase 1: 核心基础设施

### 1.1 注册 Polymarket 市场

在 `PolymarketBrokerageFactory` 静态初始化中动态注册，无需修改 LEAN 核心代码：

```csharp
Market.Add("polymarket", 43); // 使用下一个可用 ID
```

参考文件: `Common/Market.cs` — `Market.Add()` 方法支持运行时注册（ID 范围 0–999）

### 1.2 SecurityType 选择：使用 SecurityType.Crypto

不新增 SecurityType，原因：

- 新增枚举值需在 LEAN 核心中修改数十处 switch 语句
- Crypto 类型已支持 BaseCurrency/QuoteCurrency，与 TOKEN/USDC 结构匹配
- Kraken/Coinbase 等已证明同一 SecurityType 可通过不同 Market 标识区分

### 1.3 Symbol 映射策略

Polymarket 层级：Market Question → condition_id → token_id (YES/NO)

| 属性 | 值 |
|------|-----|
| LEAN Symbol | `ETH5000MAR26YES` |
| SecurityType | Crypto |
| Market | `polymarket` |
| QuoteCurrency | USDC |

`PolymarketSymbolMapper`（实现 `ISymbolMapper`）维护双向映射：

- LEAN ticker ↔ Polymarket token_id
- 启动时从 REST API `GET /markets` 加载市场目录并缓存
- 参考: `Brokerages/SymbolPropertiesDatabaseSymbolMapper.cs` 的 `TryRefreshMappings()` 模式

### 1.4 市场交易时间

Polymarket 7×24 运行，在 `Data/market-hours/market-hours-database.json` 添加：

```json
"Crypto-polymarket-[*]": { /* 24/7 全天候 */ }
```

### 1.5 自定义数据类型

```csharp
public class PredictionMarketData : BaseData
{
    public string ConditionId { get; set; }
    public string Question { get; set; }
    public DateTime EndDate { get; set; }        // 结算日期
    public decimal YesProbability { get; set; }
    public decimal NoProbability { get; set; }
    public decimal Volume24h { get; set; }
    public decimal Liquidity { get; set; }
    public bool IsResolved { get; set; }
    public string WinningOutcome { get; set; }
}
```

---

## Phase 2: Brokerage 连接层

### 2.1 PolymarketBrokerage

继承 `BaseWebsocketsBrokerage`（`Brokerages/BaseWebsocketsBrokerage.cs`），同时实现 `IDataQueueHandler` 提供实时数据。

关键方法实现：

| 方法 | 说明 |
|------|------|
| PlaceOrder | 构建订单 → EIP-712 签名 → POST /order → 触发 OrderEvent |
| CancelOrder | DELETE /order/{id} → 触发 OrderEvent(Canceled) |
| GetOpenOrders | GET /orders?status=open → 转换为 LEAN Order 列表 |
| GetAccountHoldings | GET /user/positions → 转换为 Holding 列表 |
| GetCashBalance | GET /user/balance → 返回 USDC CashAmount |
| GetHistory | GET /trades → 转换为 TradeBar 序列 |
| Subscribe | WebSocket 订阅 market/trade 频道 → 更新 OrderBook |
| OnMessage | 解析 WebSocket 消息，分发到 OrderBook 更新 / 用户订单状态 / 成交回报 |

### 2.2 EIP-712 订单签名

Polymarket 要求使用以太坊 EIP-712 类型化数据签名提交订单，这是技术上最复杂的组件：

- 使用 Nethereum NuGet 包处理以太坊加密操作
- 构建 EIP-712 TypedData（Domain: Polymarket CTF Exchange, ChainId: 137）
- 用用户以太坊私钥签名订单哈希

### 2.3 BrokerageModel

参考 `Common/Brokerages/KrakenBrokerageModel.cs` 模式：

- 仅支持 SecurityType.Crypto
- 支持订单类型：Limit、Market
- 价格验证：限价单价格必须在 [0, 1] 范围内
- 杠杆 = 1（纯现金账户，使用 CashBuyingPowerModel）
- 自定义 PolymarketFeeModel：当前 Maker 0% / Taker 0%，结算时收取约 2% 赢利费

### 2.4 配置项

在 `config.json` 中添加：

```json
{
  "polymarket-api-key": "",
  "polymarket-api-secret": "",
  "polymarket-private-key": "",
  "polymarket-passphrase": ""
}
```

说明：`polymarket-private-key` 为以太坊私钥，用于 EIP-712 签名。

---

## Phase 3: 回测基础设施

### 3.1 历史数据下载器

`PolymarketDataDownloader` 工具：

- 从 Polymarket CLOB API 拉取历史成交数据
- 转换为 LEAN TradeBar/QuoteBar 格式
- 存储至 `Data/crypto/polymarket/{symbol}/` 目录

### 3.2 数据格式

遵循 LEAN 的 Crypto 数据存储约定：

```
Data/crypto/polymarket/minute/eth5000mar26yes/
  20260101_trade.zip
  20260101_quote.zip
```

### 3.3 自定义 Fill Model

预测市场流动性特征不同于传统加密市场，需要自定义填充模型考虑：

- 价格 [0,1] 的硬边界约束
- 通常更宽的买卖价差
- 接近结算时的流动性变化

---

## Phase 4: 做市策略

### 4.1 核心做市算法 PolymarketMarketMaker

继承 `QCAlgorithm`，核心逻辑：

**Initialize**

- 设置 USDC 账户 → 订阅目标市场 YES/NO 代币 → 初始化参数

**RefreshQuotes（每 30 秒循环）**

1. 撤销现有挂单
2. 获取最新 mid price = (best_bid + best_ask) / 2
3. 计算库存偏移 skew = -(inventory / max_inventory) × skew_factor × spread
4. 多层报价（3–5 层，每层间距 1 cent，外层挂单量更大）
5. bid_price = mid - half_spread - level_offset + skew  
   ask_price = mid + half_spread + level_offset + skew
6. 价格钳位到 [0.01, 0.99]
7. 下限价单

**关键参数**

| 参数 | 说明 |
|------|------|
| targetSpread | 目标价差（如 2 cents） |
| maxInventory | 单边最大持仓量 |
| inventorySkew | 库存偏移强度 |
| orderLevels | 报价层数 |
| refreshInterval | 报价刷新频率 |

**预测市场做市特殊考量**

- **价格有界**：钳位在 [0, 1]，与传统资产不同
- **互补约束**：YES + NO ≈ 1.00，偏离时可套利
- **结算风险**：代币最终结算为 0 或 1，临近结算时持仓风险极大
- **时间衰减**：随着结算日接近，不确定性降低，应收窄价差

### 4.2 YES/NO 互补套利

监控 YES_bid + NO_bid 和 YES_ask + NO_ask：

- 若 YES_bid + NO_bid > 1.00 + 成本 → 同时卖出两侧，锁定利润
- 若 YES_ask + NO_ask < 1.00 - 成本 → 同时买入两侧，等待结算获利

---

## Phase 5: 量化策略框架

### 5.1 Alpha 模型

| Alpha 模型 | 信号来源 | 说明 |
|------------|----------|------|
| CrossMarketArbitrageAlpha | YES+NO 定价偏离 | 当 sum ≠ 1.00 时生成套利信号 |
| ProbabilityMeanReversionAlpha | 价格均值回归 | 价格快速波动后向均值回复 |
| SentimentAlpha | 新闻/社交情绪 | 外部情绪数据驱动概率估计 |
| CrossMarketCorrelationAlpha | 相关市场联动 | 检测关联预测市场间的领先/滞后关系 |

### 5.2 Portfolio 构建

使用 Kelly 准则适配二元结果：

```
f* = (p × b - q) / b
```

其中 p = 估计概率，q = 1-p，b = 赔率 = (1/price - 1)。实际使用 Half-Kelly（f*/2）以降低风险。

### 5.3 风险管理

`PredictionMarketRiskManagementModel`：

- **结算风险**：结算日前 N 天强制平仓
- **持仓上限**：单市场不超过组合 10%
- **极端概率保护**：价格 < 0.03 或 > 0.97 时减半仓位
- **总敞口上限**：总投资不超过组合 50%

---

## 需要对 LEAN 核心做的最小修改

| 文件 | 修改 | 说明 |
|------|------|------|
| Data/market-hours/market-hours-database.json | 添加条目 | Crypto-polymarket-[*] 24/7 交易时间 |
| Data/symbol-properties/symbol-properties-database.csv | 添加条目 | Polymarket 代币属性（QuoteCurrency=USDC 等） |

其余全部在独立项目 `Lean.Brokerages.Polymarket` 中实现，通过 `Market.Add()` 动态注册。

---

## 关键参考文件

| 文件 | 用途 |
|------|------|
| Brokerages/BaseWebsocketsBrokerage.cs | PolymarketBrokerage 基类 |
| Common/Brokerages/KrakenBrokerageModel.cs | BrokerageModel 参考模板 |
| Brokerages/BrokerageFactory.cs | Factory 抽象基类 |
| Brokerages/ISymbolMapper.cs | Symbol 映射接口 |
| Brokerages/SymbolPropertiesDatabaseSymbolMapper.cs | 映射实现参考 |
| Common/Market.cs | 市场注册机制 |
| Common/Orders/Fees/KrakenFeeModel.cs | 费率模型参考 |
| Algorithm.Framework/Alphas/ | Alpha 模型基类和示例 |
| Algorithm.Framework/Portfolio/ | 组合构建模型参考 |
| Algorithm.Framework/Risk/ | 风险管理模型参考 |

---

## 实施优先级

1. **Phase 1** — 核心基础设施（Market 注册、Symbol 映射、数据类型）
2. **Phase 2** — Brokerage 连接层（REST/WebSocket/EIP-712 签名/订单管理）
3. **Phase 3** — 回测基础设施（历史数据下载、数据格式转换）
4. **Phase 4** — 做市策略（库存管理报价、互补套利）
5. **Phase 5** — 量化策略框架（Alpha 模型、组合构建、风险管理）

---

## 验证方案

1. **单元测试**：Symbol 映射、Fee 模型、EIP-712 签名、订单属性
2. **集成测试**：REST API 调用、WebSocket 连接与数据解析
3. **回测验证**：使用下载的 Polymarket 历史数据运行做市策略，检查 P&L 和成交逻辑
4. **模拟交易**：接入真实行情但模拟执行（Paper Trading），验证信号质量和风控逻辑
5. **小资金实盘**：少量 USDC 实盘验证全链路（签名 → 下单 → 成交 → 结算）
