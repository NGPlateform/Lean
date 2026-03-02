# Polymarket 做市策略 (Market Making Strategy) 技术文档

## 目录

1. [概述](#1-概述)
2. [系统架构](#2-系统架构)
3. [策略核心算法](#3-策略核心算法)
4. [DryRun 引擎增强](#4-dryrun-引擎增强)
5. [配置参考](#5-配置参考)
6. [文件清单与变更](#6-文件清单与变更)
7. [运行日志解读](#7-运行日志解读)
8. [调优指南](#8-调优指南)

---

## 1. 概述

### 1.1 什么是做市策略

做市 (Market Making) 是一种通过同时在买卖两侧提供流动性来赚取价差利润的量化策略。做市商在 mid price 两侧持续挂出限价单 (bid/ask)，当买卖双方分别成交时，做市商赚取中间的 spread 差价。

### 1.2 Polymarket 特殊性

Polymarket 是预测市场 (prediction market)，其标的为二元事件的概率 token。价格范围恒定在 `[0.01, 0.99]`，这对做市策略提出以下特殊约束：

- **价格边界**：报价不能低于 0.01 或高于 0.99
- **极端概率**：接近 0 或 1 的 token 流动性差，做市风险高
- **二元对称**：一个 market 包含 YES/NO 两个 token，价格理论互补 (YES + NO ≈ 1.00)
- **到期结算**：市场最终以 0 或 1 结算，持仓面临方向性风险

### 1.3 策略亮点

| 特性 | 描述 |
|------|------|
| 库存倾斜 (Inventory Skew) | 根据持仓方向动态偏移报价，自然回归中性 |
| 波动率自适应价差 | 基于近期价格标准差自动调整 spread 宽度 |
| 市场选择评分 | 多维度评分筛选最优做市标的 |
| 紧急风控模式 | 极端持仓时停止买入、激进卖出 |
| 多层资金管控 | 单 token 上限 + 总敞口上限 + 余额安全边际 |
| 自动行情订阅 | 引擎层自动订阅 top 市场，无需 UI 手动点击 |

---

## 2. 系统架构

### 2.1 组件拓扑

```
┌─────────────────────────────────────────────────────────┐
│                    appsettings.json                       │
│              (StrategyName / Parameters)                  │
└────────────────────────┬────────────────────────────────┘
                         │ 配置注入
                         ▼
┌────────────┐    ┌──────────────┐    ┌───────────────────┐
│ Program.cs │───▶│ DryRunEngine │───▶│MarketMakingStrategy│
│ (DI 注册)   │    │ (BackgroundService)│    │ (IDryRunStrategy)  │
└────────────┘    └──────┬───────┘    └───────────────────┘
                         │
              ┌──────────┼──────────┐
              ▼          ▼          ▼
     ┌──────────┐ ┌──────────┐ ┌────────┐
     │MarketData│ │ Trading  │ │SignalR  │
     │ Service  │ │ Service  │ │Hub     │
     │(WebSocket)│ │(REST API)│ │(前端推送)│
     └──────────┘ └──────────┘ └────────┘
```

### 2.2 Tick 循环

每 5 秒执行一次 `Tick()`，完整流程：

```
Tick()
  ├── [0] 周期性刷新自动订阅 (每 60 tick ≈ 5 分钟)
  ├── [1] 从 MarketDataService 收集全部缓存订单簿
  ├── [2] 撮合引擎：匹配 open orders vs real order books
  ├── [3] 更新持仓的当前价格 (mark-to-market)
  ├── [4] 构建 StrategyContext → 调用 strategy.Evaluate()
  ├── [5] 执行返回的 PlaceOrderAction / CancelOrderAction
  └── [6] 通过 SignalR 广播状态到前端
```

### 2.3 数据流

```
Gamma API  ──(REST)──▶  TradingService._markets   ──▶  DryRunEngine.GetAllCachedOrderBooks()
                              │
                              │ top N by volume24h
                              ▼
CLOB API   ──(REST)──▶  SeedOrderBook() (首次预取)
                              │
                              ▼
Polymarket ──(WSS)───▶  MarketDataService._orderBooks  ──▶  strategy.Evaluate(context)
WebSocket       (实时增量更新)                                         │
                                                                      ▼
                                                            List<StrategyAction>
                                                              ├── PlaceOrderAction
                                                              └── CancelOrderAction
```

---

## 3. 策略核心算法

### 3.1 主循环 (`Evaluate`)

```
Evaluate(context)
  ├── UpdatePriceHistory()          // 更新所有 token 的中间价历史
  ├── SelectMarkets() (每 12 tick)  // 市场选择评分
  ├── 计算总敞口
  └── foreach selectedToken:
       └── ProcessToken()           // 报价管理
```

**源码位置**: `MarketMakingStrategy.cs:60-96`

### 3.2 报价管理 (Quote Cycle)

每个被选中的 token 按固定间隔执行报价刷新：

```
ProcessToken(tokenId, book, midPrice, context, totalExposure)
  ├── 检查是否需要 requote (每 RequoteIntervalTicks=6 tick = 30秒)
  │    └── 无需 requote 且有活跃订单 → 跳过
  ├── 取消该 token 所有现有 Strategy 订单
  ├── 获取当前持仓 (posSize, posCost)
  ├── 计算库存倾斜: skew = posSize × SkewFactor
  ├── 计算自适应半价差: adjustedHalfSpread
  ├── 检查紧急模式 (posCost ≥ 90% × MaxPositionPerToken)
  │    └── 是 → 仅下 EMERGENCY SELL 单，停止买入
  ├── 计算报价:
  │    bid = mid - adjustedHalfSpread - skew
  │    ask = mid + adjustedHalfSpread - skew
  ├── 价格钳位到 [0.01, 0.99]
  ├── 确保 bid < ask
  ├── 买单尺寸调整:
  │    ├── 单 token 持仓上限检查
  │    ├── 总敞口上限检查
  │    └── 余额安全检查 (≤ 90% 可用余额)
  ├── 卖单尺寸 = min(orderSize, 持仓量)
  ├── 下 BID 单 (size ≥ 1)
  └── 下 ASK 单 (size ≥ 1)
```

**源码位置**: `MarketMakingStrategy.cs:100-227`

**关键公式**:

```
bidPrice = midPrice - adjustedHalfSpread - skew
askPrice = midPrice + adjustedHalfSpread - skew
```

注意 skew 对 bid 和 ask 做**同向偏移**（而非对称扩展）。当 `skew > 0`（持多头），两个价格同时下移：
- bid 更低 → 降低买入意愿
- ask 更低 → 增加卖出吸引力

### 3.3 库存倾斜 (Inventory Skew)

**核心公式**:

```
skew = positionSize × SkewFactor
```

| 持仓状态 | skew | bid 偏移 | ask 偏移 | 效果 |
|----------|------|---------|---------|------|
| 0 (无持仓) | 0 | 无 | 无 | 对称报价 |
| +1 | +0.005 | 下移 0.005 | 下移 0.005 | 轻微抑制买入 |
| +10 | +0.05 | 下移 0.05 | 下移 0.05 | 显著倾斜卖出 |
| +100 | +0.50 | 下移 0.50 | 下移 0.50 | 极端倾斜 (触发紧急模式) |

**实际观测** (来自测试日志):

```
skew=0.0000  →  BID @ 0.3550, ASK: 无 (无持仓无法卖)
skew=0.0050  →  BID @ 0.3500, ASK @ 0.3900        (持仓 1 单位)
skew=0.0100  →  BID @ 0.5950, ASK @ 0.6350        (持仓 2 单位)
skew=0.0150  →  BID @ 0.5900, ASK @ 0.6300        (持仓 3 单位)
```

**源码位置**: `MarketMakingStrategy.cs:128` (计算) / `155-156` (应用)

### 3.4 波动率自适应价差

**核心算法**:

1. 维护每个 token 最近 20 tick 的中间价滑动窗口
2. 计算窗口内标准差 σ
3. 自适应半价差 = 基础半价差 + 2σ
4. 结果钳位在 `[MinHalfSpread, MaxHalfSpread]` 区间

```
adjustedHalfSpread = Clamp(HalfSpread + 2 × stdDev, 0.005, 0.10)
```

| 波动状态 | σ (估算) | 调整后半价差 | 含义 |
|----------|---------|------------|------|
| 极低波动 | ≈ 0 | 0.020 (基础值) | 正常 spread |
| 低波动 | ≈ 0.005 | 0.030 | 稍微加宽 |
| 中波动 | ≈ 0.015 | 0.050 | 适度保护 |
| 高波动 | ≈ 0.040 | 0.100 (上限) | 最大保护 |

**设计意图**: 高波动时加宽 spread，减少逆向选择风险；低波动时收窄 spread，增加成交概率。

**源码位置**: `MarketMakingStrategy.cs:229-242`

### 3.5 市场选择评分

每 12 tick (~60秒) 从所有可用 token 中选出最适合做市的 top N 个。

**评分模型** (3 维度加权):

```
score = spreadQuality × 0.4 + liquidityScore × 0.4 + centrality × 0.2
```

| 维度 | 权重 | 计算方式 | 含义 |
|------|------|---------|------|
| 价差质量 | 40% | `1 - min(1, spread / 0.20)` | 价差越窄 → 分越高 → 越适合做市 |
| 流动性 | 40% | `min(1, totalDepth / 1000)` | 双边深度越厚 → 分越高 → 成交越容易 |
| 价格居中度 | 20% | `1 - |mid - 0.5| × 2` | 越接近 0.50 → 分越高 → 双边机会均衡 |

**前置过滤条件** (不满足任一条件则 score = 0):

| 过滤器 | 条件 | 理由 |
|--------|------|------|
| 极端价格 | `mid < 0.08` 或 `mid > 0.92` | 近似确定的事件，做市方向性风险过大 |
| 过窄价差 | `spread < 0.002` | 纯算法主导，利润空间不足 |
| 过宽价差 | `spread > 0.20` | 无真实流动性，对手方缺失 |

**强制包含规则**: 已有持仓的 token 始终入选，确保可以对冲平仓。

**源码位置**: `MarketMakingStrategy.cs:265-330`

### 3.6 风险控制矩阵

```
                    ┌────────────────────────────────┐
                    │       风险控制层级图             │
                    └────────────────────────────────┘

Layer 1: 市场过滤
  ├── 极端价格过滤: mid ∉ [0.08, 0.92] → 不参与
  └── 价差过滤: spread ∉ [0.002, 0.20] → 不参与

Layer 2: 单 token 限仓
  ├── posCost ≥ MaxPositionPerToken (150 USDC) → 停止买入
  └── posCost ≥ 90% × MaxPositionPerToken → EMERGENCY 模式

Layer 3: 总敞口控制
  └── totalExposure + 新单成本 > MaxTotalExposure (500 USDC) → 缩减买单

Layer 4: 余额安全
  └── bidSize × bidPrice > Balance × 90% → 缩减买单

Layer 5: 卖单约束
  └── askSize = min(OrderSize, 实际持仓量) → 不能裸卖

Layer 6: 紧急模式
  └── posCost ≥ 135 USDC (90%×150):
      ├── 停止所有买入
      └── 以 mid - halfSpread×0.5 激进卖出
```

**紧急模式报价**:

```csharp
emergencySellPrice = max(0.01, midPrice - adjustedHalfSpread × 0.5)
emergencySize = min(OrderSize, positionSize)
```

相比正常 ask 价 (`mid + halfSpread - skew`)，紧急卖出价直接放在 `mid - halfSpread/2`，即**低于**中间价抛售，最大化成交概率。

**源码位置**: `MarketMakingStrategy.cs:133-152` (紧急模式) / `170-197` (尺寸调整)

---

## 4. DryRun 引擎增强

### 4.1 自动行情订阅

**问题**: 原引擎仅当用户在 UI 点击某个 market 时才通过 WebSocket 订阅其行情。策略无 UI 交互，看不到任何订单簿数据。

**解决方案**: 引擎启动时自动订阅 top N 市场的全部 token。

```
AutoSubscribeTopMarkets()
  ├── 从 TradingService 获取全部 markets
  ├── 按 volume24h 降序排列，取 top N (默认 10)
  ├── 提取所有 tokenId (每个 market 通常 2 个 token → ~20 tokens)
  ├── 调用 MarketDataService.SubscribeAsync() 发送 WebSocket 订阅
  └── 逐个 token 通过 REST API 预取订单簿，调用 SeedOrderBook() 写入缓存
```

**预取 (Seed) 的必要性**: WebSocket 订阅后，只有当该 token 产生价格变动时才会推送数据。对于低频交易的市场，可能要等很久才收到第一条消息。预取通过 REST 同步获取当前完整订单簿快照，确保策略首个 tick 就有数据可用。

**刷新频率**: 每 60 tick (~5 分钟) 重新检查 top 市场列表，订阅新增的 token。

**源码位置**: `DryRunEngine.cs:516-571`

### 4.2 被动成交模拟 (Passive Fill Model)

**问题**: 原撮合引擎仅在订单价格"穿越" (cross) 对手方最优价时成交。做市策略的限价单挂在 spread 内，永远不会穿越对手方价格，导致零成交。

**解决方案**: 新增概率化被动成交模型，模拟做市商限价单被市场订单冲击的过程。

**模型逻辑**:

```
对于 BUY 订单 (bid):
  distance = bestAsk - orderPrice          // 距离对手方最优价的距离
  maxDistance = max(spread × 5, 0.03)      // 最大考虑范围
  if distance > 0 且 distance < maxDistance:
    fillProb = (1 - distance/maxDistance) × 0.12   // 线性衰减概率，最高 12%/tick
    if random() < fillProb:
      fill at orderPrice (maker 价格)

对于 SELL 订单 (ask) 同理，方向相反
```

**概率分布示意**:

```
Fill Probability
     12% │ ██
         │ ████
         │ ██████
         │ ████████
         │ ██████████
      0% │─────────────▶ distance from best opposite price
         0           maxDistance
```

**设计考量**:

- **Maker 成交价**: 被动单以自己的挂单价成交（而非对手方价格），反映做市商的实际收益
- **距离衰减**: 离 best ask/bid 越远，被市场 taker 冲击的概率越低
- **上限 12%/tick**: 避免过度乐观的成交频率，5 秒一个 tick 意味着平均约 8 个 tick (~40秒) 成交一次较近的挂单
- **前置条件**: BUY 订单需 `price ≥ bestBid × 0.95`，SELL 订单需 `price ≤ bestAsk × 1.05`，排除明显离谱的挂单

**源码位置**: `DryRunEngine.cs:178-213`

### 4.3 撮合引擎完整流程

```
MatchOrders(orderBooks)
  foreach openOrder:
    ├── 获取该 token 的 bestBid, bestAsk
    │
    ├── [阶段1: 主动成交 (Taker)]
    │   ├── BUY 且 orderPrice ≥ bestAsk → 立即成交 @ bestAsk
    │   └── SELL 且 orderPrice ≤ bestBid → 立即成交 @ bestBid
    │
    ├── [阶段2: 被动成交 (Maker)] (仅当阶段1未触发)
    │   ├── 计算距离对手方最优价的 distance
    │   ├── 计算概率 fillProb = (1 - distance/maxDistance) × 0.12
    │   └── 随机数 < fillProb → 成交 @ orderPrice
    │
    └── [成交处理]
        ├── 计算成交量 = min(剩余量, 随机深度)
        ├── 执行 ExecuteFill() → 更新余额/持仓/PnL
        └── 完全成交 → 移除订单
```

---

## 5. 配置参考

### 5.1 appsettings.json 完整配置

```json
{
  "DryRun": {
    "Enabled": true,
    "InitialBalance": 10000,
    "TickIntervalMs": 5000,
    "StrategyName": "MarketMaking",
    "AutoSubscribeTopN": 10,
    "StrategyParameters": {
      "OrderSize": "25",
      "HalfSpread": "0.02",
      "SkewFactor": "0.005",
      "MaxPositionPerToken": "150",
      "MaxTotalExposure": "500",
      "MaxActiveMarkets": "5",
      "RequoteIntervalTicks": "6"
    }
  }
}
```

### 5.2 引擎层参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `true` | 是否启用 DryRun 引擎 |
| `InitialBalance` | decimal | `10000` | 初始模拟 USDC 余额 |
| `TickIntervalMs` | int | `5000` | Tick 间隔 (毫秒)，5000 = 每 5 秒 |
| `StrategyName` | string | `"MarketMaking"` | 策略名，可选: `MarketMaking` / `MM` / `MeanReversion` / `SpreadCapture` |
| `AutoSubscribeTopN` | int | `10` | 自动订阅 top N 市场 (按 24h volume 排序)，0 = 禁用 |

### 5.3 策略参数详解

| 参数 | 类型 | 默认值 | 范围建议 | 说明 |
|------|------|--------|---------|------|
| `OrderSize` | decimal | `25` | 5 - 100 | 每笔限价单的基础数量 (token 单位) |
| `HalfSpread` | decimal | `0.02` | 0.005 - 0.05 | 基础半价差，mid 两侧各偏移的距离 |
| `SkewFactor` | decimal | `0.005` | 0.001 - 0.02 | 库存倾斜系数，每单位持仓偏移量 |
| `MaxPositionPerToken` | decimal | `150` | 50 - 500 | 单 token 最大持仓价值 (USDC) |
| `MaxTotalExposure` | decimal | `500` | 200 - 2000 | 所有 token 合计最大敞口 (USDC) |
| `MaxActiveMarkets` | int | `5` | 3 - 10 | 同时做市的最大 token 数 |
| `RequoteIntervalTicks` | int | `6` | 3 - 20 | 重新报价间隔 (tick 数)，6×5s = 30s |

### 5.4 硬编码常量

| 常量 | 值 | 位置 | 说明 |
|------|----|------|------|
| `MinHalfSpread` | 0.005 | 策略 | 自适应价差下限 |
| `MaxHalfSpread` | 0.10 | 策略 | 自适应价差上限 |
| `MinPrice` | 0.08 | 策略 | 做市价格下限 (过滤极端概率) |
| `MaxPrice` | 0.92 | 策略 | 做市价格上限 |
| `MinMarketSpread` | 0.002 | 策略 | 市场价差下限 (过滤纯算法市场) |
| `MaxMarketSpread` | 0.20 | 策略 | 市场价差上限 (过滤无流动性市场) |
| `VolatilityWindow` | 20 | 策略 | 波动率计算窗口 (tick 数) |
| `MarketSelectionIntervalTicks` | 12 | 策略 | 市场选择刷新间隔 (tick 数) |
| `AutoSubscribeRefreshIntervalTicks` | 60 | 引擎 | 自动订阅刷新间隔 (tick 数) |
| Maker fill max probability | 12%/tick | 引擎 | 被动成交最大概率 |

---

## 6. 文件清单与变更

### 6.1 新增文件

| 文件 | 行数 | 说明 |
|------|------|------|
| `Dashboard/Strategies/MarketMakingStrategy.cs` | 374 | 完整 MM 策略实现 |

### 6.2 修改文件

| 文件 | 变更 | 说明 |
|------|------|------|
| `Dashboard/Services/DryRunSettings.cs` | +1 行 | 新增 `AutoSubscribeTopN` 属性 |
| `Dashboard/Services/DryRunEngine.cs` | +93 行 | 自动订阅 + 被动成交模型 |
| `Dashboard/Services/MarketDataService.cs` | +9 行 | 新增 `SeedOrderBook()` 方法 |
| `Dashboard/Program.cs` | +2 行 | 注册 `"marketmaking"` / `"mm"` + 解析 `AutoSubscribeTopN` |
| `Dashboard/appsettings.json` | 重写 | 切换为 MM 策略配置 |

### 6.3 关键接口

**IDryRunStrategy** — 策略插件接口:

```csharp
public interface IDryRunStrategy
{
    string Name { get; }
    string Description { get; }
    void Initialize(Dictionary<string, string> parameters);
    List<StrategyAction> Evaluate(StrategyContext context);
    void OnFill(SimulatedTrade trade);
}
```

**StrategyContext** — 策略每 tick 接收的完整市场快照:

```csharp
public class StrategyContext
{
    DateTime CurrentTime;
    List<DashboardMarket> Markets;                          // 所有市场信息
    Dictionary<string, PolymarketOrderBook> OrderBooks;     // token → 订单簿
    decimal Balance;                                        // 可用余额
    Dictionary<string, SimulatedPosition> Positions;        // token → 持仓
    List<SimulatedOrder> OpenOrders;                        // 当前活跃订单
    List<SimulatedTrade> RecentTrades;                      // 最近 50 笔成交
    decimal RealizedPnl;                                    // 已实现盈亏
    decimal UnrealizedPnl;                                  // 未实现盈亏
}
```

**StrategyAction** — 策略返回的操作指令:

```csharp
// 下单
public class PlaceOrderAction : StrategyAction
{
    string TokenId;     // 目标 token
    decimal Price;      // 限价
    decimal Size;       // 数量
    string Side;        // "BUY" 或 "SELL"
}

// 撤单
public class CancelOrderAction : StrategyAction
{
    string OrderId;     // 目标订单 ID
}
```

---

## 7. 运行日志解读

### 7.1 启动日志

```
DryRun engine started. Strategy: MarketMaking, Balance: $10000.00
Auto-subscribed to 20 tokens from top 10 markets (seeded 20 order books)
```

- `20 tokens from top 10 markets`: 每个 market 有 YES/NO 两个 token
- `seeded 20 order books`: 通过 REST API 预取了全部订单簿

### 7.2 报价日志

```
ORDER: BUY 25.00 of 112592...6564 @ 0.3550 | MM BID | mid=0.3750 spread=0.0200 skew=0.0000
ORDER: SELL 1.00 of 112592...6564 @ 0.3900 | MM ASK | mid=0.3750 spread=0.0200 skew=0.0050
```

格式: `ORDER: {方向} {数量} of {tokenId} @ {价格} | {类型} | mid={中间价} spread={半价差} skew={倾斜}`

- `MM BID`: 做市买单
- `MM ASK`: 做市卖单
- `skew=0.0050`: 有 1 单位持仓 (1 × 0.005)

### 7.3 成交日志

```
FILL: BUY 1.00 of 112592...6564 @ 0.3550 | Balance: $9999.65
FILL: SELL 1.00 of 112592...6564 @ 0.3900 | Balance: $10000.04
```

这一买一卖完成一个完整的做市 round-trip:
- 买入成本: 1 × 0.3550 = 0.3550 USDC
- 卖出收入: 1 × 0.3900 = 0.3900 USDC
- **单笔利润: 0.0350 USDC** (即 spread 收入)

### 7.4 撤单日志

```
CANCEL: DRY-000005 | Requote
```

Requote 表示定期重新报价，非异常操作。

### 7.5 紧急模式日志

```
EMERGENCY SELL | pos=150.0 mid=0.3750
```

当持仓成本达到 `MaxPositionPerToken × 90%` 时触发。

---

## 8. 调优指南

### 8.1 利润 vs 成交频率

```
HalfSpread ↑  →  单笔利润 ↑，成交频率 ↓
HalfSpread ↓  →  单笔利润 ↓，成交频率 ↑

推荐: 从 0.02 开始，观察成交率。若长时间无成交，降至 0.015 或 0.01。
```

### 8.2 库存控制激进度

```
SkewFactor ↑  →  更快回归中性，但可能放弃利润
SkewFactor ↓  →  更多方向性敞口，利润空间大但风险高

推荐:
  保守型: 0.01 (持仓 10 单位偏移 0.10)
  平衡型: 0.005 (默认)
  激进型: 0.002 (持仓 10 单位仅偏移 0.02)
```

### 8.3 市场覆盖范围

```
AutoSubscribeTopN ↑ + MaxActiveMarkets ↑  →  更多机会，但精力分散
AutoSubscribeTopN ↓ + MaxActiveMarkets ↓  →  聚焦少数市场，深度做市

推荐:
  AutoSubscribeTopN: 10-20 (提供足够的候选池)
  MaxActiveMarkets: 3-5 (资金集中效果好)
```

### 8.4 风险预算

```
保守配置:
  MaxPositionPerToken: 50
  MaxTotalExposure: 150
  OrderSize: 10

标准配置 (默认):
  MaxPositionPerToken: 150
  MaxTotalExposure: 500
  OrderSize: 25

激进配置:
  MaxPositionPerToken: 500
  MaxTotalExposure: 2000
  OrderSize: 100
```

### 8.5 Tick 频率

```
TickIntervalMs = 5000 (默认): 标准频率
TickIntervalMs = 2000: 更敏捷的报价更新，但增加系统负载
TickIntervalMs = 10000: 低频，适合慢速市场

注: RequoteIntervalTicks 不变时，调整 TickIntervalMs 会同步影响报价刷新实际时间。
     RequoteIntervalTicks=6 × 5000ms = 30秒 报价刷新
     RequoteIntervalTicks=6 × 2000ms = 12秒 报价刷新
```

### 8.6 常见问题排查

| 现象 | 可能原因 | 解决方案 |
|------|---------|---------|
| 无订单生成 | 所有市场被过滤 | 增大 `AutoSubscribeTopN`，或放宽 `MinPrice`/`MaxPrice` |
| 只有 BID 无 ASK | 无持仓 (正常) | 等待 BID 成交后自然出现 ASK |
| 成交极少 | `HalfSpread` 过大 | 降低 `HalfSpread` 或增大 `OrderSize` |
| 持仓单边累积 | `SkewFactor` 过小 | 增大 `SkewFactor` 加速回归 |
| 余额快速下降 | `MaxTotalExposure` 过大 | 降低敞口上限 |
| 日志显示 EMERGENCY | 单 token 持仓过重 | 降低 `MaxPositionPerToken` 或增大 `SkewFactor` |
