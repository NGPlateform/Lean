# Polymarket MM 与 LEAN 项目依赖关系文档

## 目录

1. [项目总览](#1-项目总览)
2. [三层架构拆解](#2-三层架构拆解)
3. [Dashboard 层：完全新开发](#3-dashboard-层完全新开发)
4. [Brokerage 层：LEAN 深度集成](#4-brokerage-层lean-深度集成)
5. [Strategies 层：双轨并行](#5-strategies-层双轨并行)
6. [LeanStubs 解耦机制](#6-leanstubs-解耦机制)
7. [源码共享机制](#7-源码共享机制)
8. [LEAN 类型使用全景表](#8-lean-类型使用全景表)
9. [外部依赖清单](#9-外部依赖清单)
10. [架构决策与权衡](#10-架构决策与权衡)

---

## 1. 项目总览

Polymarket 做市系统由三个独立 C# 项目组成，它们与 LEAN 引擎的依赖深度截然不同：

```
Lean.Brokerages.Polymarket/
│
├── Dashboard/          ← 完全新开发 | 独立于 LEAN | .NET 7.0 ASP.NET Core
├── QuantConnect.Brokerages.Polymarket/  ← 基于 LEAN 框架 | 深度耦合 | .NET 10.0
└── Strategies/         ← 双轨：LEAN 算法 + Dashboard 策略 | .NET 10.0
```

**核心结论**: Dashboard（含 MM 做市策略、DryRun 引擎、Web UI）是**完全新开发**的独立系统，通过自定义的 `LeanStubs.cs` 实现与 LEAN 框架的**编译期解耦**，仅复用 Polymarket API 客户端的源码文件。

---

## 2. 三层架构拆解

### 依赖方向图

```
                    ┌─────────────────────────┐
                    │   LEAN Engine Core       │
                    │ ┌─────────────────────┐  │
                    │ │ Common/QuantConnect  │  │    QuantConnect.csproj
                    │ │ (Symbol, Order,      │  │    QuantConnect.Brokerages.csproj
                    │ │  Security, Logging)  │  │    QuantConnect.Algorithm.csproj
                    │ └─────────┬───────────┘  │    QuantConnect.Algorithm.Framework.csproj
                    └───────────┼──────────────┘
                                │
                    ┌───────────┼──────────────┐
                    │  ProjectReference (深度耦合)│
                    │           │               │
                    │  ┌────────▼────────┐      │
                    │  │  Brokerage 层    │      │    QuantConnect.Brokerages.Polymarket.csproj
                    │  │ PolymarketBrokerage │   │    .NET 10.0
                    │  │ PolymarketApiClient │   │
                    │  │ PolymarketModels    │   │
                    │  └────────┬────────┘      │
                    │           │               │
                    │  ┌────────▼────────┐      │
                    │  │  Strategies 层   │      │    QuantConnect.Brokerages.Polymarket.Strategies.csproj
                    │  │ (LEAN 算法)       │      │    .NET 10.0
                    │  └─────────────────┘      │
                    └───────────────────────────┘

                    ┌───────────────────────────┐
                    │  Dashboard 层 (独立)       │    QuantConnect.Brokerages.Polymarket.Dashboard.csproj
                    │  ┌─────────────────────┐  │    .NET 7.0 ASP.NET Core
                    │  │ MM策略 / DryRun引擎  │  │
                    │  │ Web UI / SignalR     │  │
                    │  └─────────┬───────────┘  │
                    │            │               │
                    │  ┌─────────▼───────────┐  │
                    │  │ <Compile Include>    │  │    源码文件引用 (非 ProjectReference)
                    │  │ Polymarket API 源码  │  │
                    │  └─────────┬───────────┘  │
                    │            │               │
                    │  ┌─────────▼───────────┐  │
                    │  │ LeanStubs.cs         │  │    自定义 LEAN 类型桩
                    │  │ (Symbol, Log 等 stub)│  │    让 API 源码编译通过
                    │  └─────────────────────┘  │
                    └───────────────────────────┘
```

### 关键区别

| 维度 | Dashboard 层 | Brokerage 层 | Strategies 层 |
|------|-------------|-------------|--------------|
| **与 LEAN 的关系** | 完全独立 | 深度耦合 | 深度耦合 |
| **开发性质** | 全部新开发 | 新开发 + LEAN 集成 | LEAN 算法 + 新策略 |
| **Target Framework** | .NET 7.0 | .NET 10.0 | .NET 10.0 |
| **引用 LEAN csproj** | 否 | 是 (3 个) | 是 (4 个) |
| **可脱离 LEAN 运行** | 是 | 否 | 否 |
| **Web 框架** | ASP.NET Core | 无 | 无 |

---

## 3. Dashboard 层：完全新开发

Dashboard 是为 Polymarket 做市场景**从零开发**的独立 Web 应用，不依赖任何 LEAN 框架组件。

### 3.1 新开发组件清单

| 组件 | 文件 | 说明 |
|------|------|------|
| **做市策略** | `Strategies/MarketMakingStrategy.cs` | MM 策略核心：报价管理、库存倾斜、波动率自适应、市场评分 |
| **均值回归策略** | `Strategies/MeanReversionStrategy.cs` | 基础策略：价格偏离均值时反向交易 |
| **价差捕获策略** | `Strategies/SpreadCaptureStrategy.cs` | 基础策略：在 bid-ask spread 内下单 |
| **策略接口** | `Services/IDryRunStrategy.cs` | `IDryRunStrategy` 接口 + `StrategyContext` + `StrategyAction` |
| **模拟引擎** | `Services/DryRunEngine.cs` | 完整交易模拟：撮合、持仓、PnL、被动成交模型、自动订阅 |
| **模拟数据模型** | `Services/DryRunModels.cs` | `SimulatedOrder`、`SimulatedTrade`、`SimulatedPosition`、`DryRunLogEntry` |
| **引擎配置** | `Services/DryRunSettings.cs` | DryRun 配置类 |
| **交易服务** | `Services/TradingService.cs` | Gamma API + CLOB API 封装，市场发现、订单簿获取 |
| **行情服务** | `Services/MarketDataService.cs` | WebSocket 实时行情、订单簿缓存、增量更新 |
| **API 控制器** | `Controllers/TradingController.cs` | REST API：市场/订单/持仓/交易/日志 |
| **实时推送** | `Hubs/TradingHub.cs` | SignalR Hub：实时状态广播 |
| **前端 UI** | `wwwroot/index.html` | 自定义单页面应用 |
| **前端样式** | `wwwroot/css/dashboard.css` | 自定义 CSS |
| **前端逻辑** | `wwwroot/js/dashboard.js` | SignalR 客户端 + 交互逻辑 |
| **LEAN 桩** | `LeanStubs.cs` | LEAN 类型最小化桩定义 |
| **启动入口** | `Program.cs` | ASP.NET Core DI 注册和启动 |
| **配置文件** | `appsettings.json` | 运行时配置 |

**总计**: 17 个新开发文件，约 2500+ 行代码。

### 3.2 Dashboard 不使用 LEAN 的哪些能力

| LEAN 能力 | Dashboard 的替代方案 |
|-----------|---------------------|
| LEAN Algorithm 基类 (`QCAlgorithm`) | 自定义 `IDryRunStrategy` 接口 |
| LEAN 回测/实盘引擎 | 自定义 `DryRunEngine` (BackgroundService) |
| LEAN Order 管理 (`OrderManager`) | 自定义 `SimulatedOrder` + `_openOrders` 字典 |
| LEAN Position 追踪 (`SecurityHolding`) | 自定义 `SimulatedPosition` |
| LEAN 数据订阅 (`SubscriptionManager`) | 自定义 `AutoSubscribeTopMarkets()` |
| LEAN SymbolMapper (接口) | `LeanStubs.cs` 中的桩实现 |
| LEAN Logging (`Log.Trace/Error`) | `LeanStubs.cs` 中的 Console 输出桩 |
| LEAN Web UI (Research/Terminal) | 自定义 ASP.NET Core + SignalR |

### 3.3 Dashboard 复用 Polymarket Brokerage 的哪些源码

通过 csproj 的 `<Compile Include>` 直接引用 6 个源码文件（非项目引用，不引入 LEAN 依赖链）：

| 源码文件 | 原始位置 | Dashboard 用途 |
|---------|---------|---------------|
| `PolymarketApiClient.cs` | `Api/` | REST API 调用 (下单、查询订单簿、查询余额) |
| `PolymarketWebSocketClient.cs` | `Api/` | WebSocket 连接和消息解析 |
| `PolymarketModels.cs` | `Api/Models/` | 数据模型 (`PolymarketOrderBook`, `PolymarketOrder`, etc.) |
| `PolymarketCredentials.cs` | `Auth/` | API 凭证管理和以太坊地址推导 |
| `EIP712Signer.cs` | `Auth/` | EIP-712 标准订单签名 |
| `PolymarketSymbolMapper.cs` | 根目录 | Ticker ↔ Token ID 映射 |

这些文件在 Brokerage 项目中使用了 LEAN 核心类型（`OrderDirection`、`Symbol`、`Log` 等），Dashboard 通过 `LeanStubs.cs` 提供兼容的桩定义使其编译通过。

---

## 4. Brokerage 层：LEAN 深度集成

Polymarket Brokerage 是标准的 LEAN 券商插件，深度依赖 LEAN 核心框架。

### 4.1 LEAN 项目引用

```xml
<!-- QuantConnect.Brokerages.Polymarket.csproj -->
<ProjectReference Include="..\..\Common\QuantConnect.csproj" />
<ProjectReference Include="..\..\Brokerages\QuantConnect.Brokerages.csproj" />
<ProjectReference Include="..\..\Algorithm\QuantConnect.Algorithm.csproj" />
```

### 4.2 继承与实现的 LEAN 基类/接口

| 文件 | 继承/实现 | LEAN 类型 |
|------|---------|----------|
| `PolymarketBrokerage.cs` | 继承 | `QuantConnect.Brokerages.BaseWebsocketsBrokerage` |
| `PolymarketBrokerage.cs` | 实现 | `QuantConnect.Interfaces.IDataQueueHandler` |
| `PolymarketBrokerageFactory.cs` | 继承 | `QuantConnect.Brokerages.BrokerageFactory` |
| `PolymarketBrokerageModel.cs` | 继承 | `QuantConnect.Brokerages.DefaultBrokerageModel` |
| `PolymarketFeeModel.cs` | 实现 | `QuantConnect.Orders.Fees.IFeeModel` |
| `PolymarketFillModel.cs` | 继承 | `QuantConnect.Orders.Fills.FillModel` |
| `PolymarketSymbolMapper.cs` | 实现 | `QuantConnect.ISymbolMapper` |
| `PolymarketDataDownloader.cs` | 实现 | `QuantConnect.Data.IDataDownloader` |

### 4.3 使用的 LEAN 核心类型

```csharp
// PolymarketBrokerage.cs 中的 LEAN 依赖
using QuantConnect.Data;               // BaseData
using QuantConnect.Data.Market;        // Tick, DefaultOrderBook
using QuantConnect.Interfaces;         // IAlgorithm, IDataQueueHandler
using QuantConnect.Logging;            // Log
using QuantConnect.Orders;             // Order, OrderDirection, OrderStatus
using QuantConnect.Orders.Fees;        // OrderFee
using QuantConnect.Packets;            // LiveNodePacket
using QuantConnect.Securities;         // Symbol, Security, SecurityType
```

### 4.4 Brokerage 层新开发内容

虽然深度依赖 LEAN 框架，但以下组件是为 Polymarket 全新开发的：

| 组件 | 说明 |
|------|------|
| `PolymarketApiClient` | CLOB REST API 客户端 (订单、余额、持仓) |
| `PolymarketWebSocketClient` | 实时行情 WebSocket 客户端 |
| `PolymarketModels` | 全部 Polymarket 数据模型 (15+ 个类) |
| `EIP712Signer` | 以太坊 EIP-712 订单签名 |
| `PolymarketCredentials` | 凭证管理 + Nethereum 地址推导 |
| `PredictionMarketData` | 预测市场自定义数据类型 |
| `PolymarketBrokerage` 核心逻辑 | 订单转换、数据订阅、WebSocket 处理 |

---

## 5. Strategies 层：双轨并行

### 5.1 LEAN 算法策略（Strategies 项目）

位于 `Strategies/` 目录，直接引用完整 LEAN 框架：

```xml
<ProjectReference Include="..\QuantConnect.Brokerages.Polymarket\QuantConnect.Brokerages.Polymarket.csproj" />
<ProjectReference Include="..\..\Common\QuantConnect.csproj" />
<ProjectReference Include="..\..\Algorithm\QuantConnect.Algorithm.csproj" />
<ProjectReference Include="..\..\Algorithm.Framework\QuantConnect.Algorithm.Framework.csproj" />
```

这些策略使用 LEAN 的 `QCAlgorithm` 基类和 Algorithm Framework：

| 文件 | 继承 | 说明 |
|------|------|------|
| `PolymarketTestAlgorithm.cs` | `QCAlgorithm` | 测试用算法 |
| `PolymarketMarketMaker.cs` | `QCAlgorithm` | 基于 LEAN 框架的做市算法 |
| `Alphas/CrossMarketArbitrageAlpha.cs` | `AlphaModel` | 信号模型 |
| `Portfolio/KellyPortfolioConstructionModel.cs` | `PortfolioConstructionModel` | 组合构建模型 |
| `Risk/PredictionMarketRiskManagementModel.cs` | `RiskManagementModel` | 风控模型 |

### 5.2 Dashboard 策略（Dashboard 项目）

位于 `Dashboard/Strategies/` 目录，实现自定义的 `IDryRunStrategy` 接口：

| 文件 | 实现 | 说明 |
|------|------|------|
| `MarketMakingStrategy.cs` | `IDryRunStrategy` | 本次新开发的 MM 做市策略 |
| `MeanReversionStrategy.cs` | `IDryRunStrategy` | 均值回归策略 |
| `SpreadCaptureStrategy.cs` | `IDryRunStrategy` | 价差捕获策略 |

### 5.3 两套策略体系对比

| 维度 | LEAN 算法策略 | Dashboard 策略 |
|------|-------------|---------------|
| **基类** | `QCAlgorithm` | `IDryRunStrategy` |
| **运行环境** | LEAN 引擎 (回测/实盘) | DryRunEngine (Web 模拟) |
| **数据来源** | LEAN `SubscriptionManager` | `StrategyContext.OrderBooks` |
| **下单方式** | `algorithm.MarketOrder()` 等 | 返回 `PlaceOrderAction` |
| **持仓查询** | `algorithm.Portfolio` | `StrategyContext.Positions` |
| **部署方式** | LEAN CLI / QuantConnect Cloud | `dotnet run` + 浏览器 |
| **适用场景** | 生产交易、历史回测 | 快速原型验证、实时模拟 |

---

## 6. LeanStubs 解耦机制

### 6.1 问题

Dashboard 通过 `<Compile Include>` 直接编译 Polymarket API 源码。这些源码中使用了 LEAN 核心类型：

```csharp
// PolymarketApiClient.cs 中:
using QuantConnect.Orders;    // OrderDirection.Buy / Sell
using QuantConnect.Logging;   // Log.Trace() / Log.Error()

// PolymarketSymbolMapper.cs 中:
using QuantConnect;           // Symbol, SecurityType, ISymbolMapper, Market
```

如果直接引用 LEAN 的 `QuantConnect.csproj`，将引入数百个传递依赖（算法框架、数据层、券商基类…），完全违背 Dashboard 独立运行的设计目标。

### 6.2 解决方案

`LeanStubs.cs` 定义了 API 源码所需的**最小 LEAN 类型子集**：

```csharp
// LeanStubs.cs — 94 行代码替代整个 LEAN Common 项目

namespace QuantConnect
{
    enum SecurityType { Equity, Forex, Cfd, Crypto, ... }
    enum OptionRight { Call, Put }
    class Symbol { ... }           // 简化实现，仅 Value + ID
    class SymbolId { ... }
    interface ISymbolMapper { ... }
    static class Market { ... }    // 空壳注册
}

namespace QuantConnect.Orders
{
    enum OrderDirection { Buy, Sell, Hold }
}

namespace QuantConnect.Logging
{
    static class Log                // Console 输出替代
    {
        Trace(msg) → Console.WriteLine("[TRACE] " + msg)
        Error(msg) → Console.Error.WriteLine("[ERROR] " + msg)
    }
}
```

### 6.3 桩类型 vs 真实类型对比

| LEAN 真实类型 | LeanStubs 桩 | 差异 |
|--------------|-------------|------|
| `Symbol` (不可变，包含 SecurityIdentifier) | 简单 POCO，仅 Value + SymbolId | 无 SecurityIdentifier 系统 |
| `ISymbolMapper` (完整的双向映射) | 相同接口签名 | 功能一致 |
| `OrderDirection` (3 值枚举) | 完全一致 | 无差异 |
| `Log` (NLog 后端，多级别) | Console 直接输出 | 无持久化，无配置 |
| `SecurityType` (完整枚举) | 部分枚举值 | Dashboard 不使用全部类型 |
| `Market` (市场注册表 + 内置市场) | 空壳字典 | 无内置市场数据 |

### 6.4 编译隔离效果

```
Dashboard.csproj 编译依赖链:
  ├── LeanStubs.cs          (94 行，替代 LEAN Common)
  ├── Polymarket API 源码    (6 个文件，<Compile Include>)
  ├── Dashboard 自有代码     (17 个文件)
  ├── Nethereum.Signer      (NuGet)
  ├── Newtonsoft.Json        (NuGet)
  └── ASP.NET Core 7.0      (SDK)

  总依赖: ~6 个 NuGet 包
  LEAN 依赖: 0

Brokerage.csproj 编译依赖链:
  ├── QuantConnect.csproj         (LEAN Common: Symbol, Security, etc.)
  ├── QuantConnect.Brokerages.csproj  (Brokerage 基类)
  ├── QuantConnect.Algorithm.csproj   (算法框架)
  │   ├── QuantConnect.Indicators.csproj
  │   ├── QuantConnect.Research.csproj
  │   └── ... (数十个传递依赖)
  ├── Nethereum.Signer
  ├── Newtonsoft.Json
  └── RestSharp

  总依赖: ~40+ 个包
  LEAN 依赖: 核心框架全部
```

---

## 7. 源码共享机制

### 7.1 `<Compile Include>` 模式

Dashboard 项目文件中的关键配置：

```xml
<ItemGroup>
  <!-- Include Polymarket source files directly to avoid LEAN framework dependency -->
  <Compile Include="..\QuantConnect.Brokerages.Polymarket\Api\PolymarketApiClient.cs"
           Link="Polymarket\PolymarketApiClient.cs" />
  <Compile Include="..\QuantConnect.Brokerages.Polymarket\Api\PolymarketWebSocketClient.cs"
           Link="Polymarket\PolymarketWebSocketClient.cs" />
  <Compile Include="..\QuantConnect.Brokerages.Polymarket\Api\Models\PolymarketModels.cs"
           Link="Polymarket\PolymarketModels.cs" />
  <Compile Include="..\QuantConnect.Brokerages.Polymarket\Auth\PolymarketCredentials.cs"
           Link="Polymarket\PolymarketCredentials.cs" />
  <Compile Include="..\QuantConnect.Brokerages.Polymarket\Auth\EIP712Signer.cs"
           Link="Polymarket\EIP712Signer.cs" />
  <Compile Include="..\QuantConnect.Brokerages.Polymarket\PolymarketSymbolMapper.cs"
           Link="Polymarket\PolymarketSymbolMapper.cs" />
</ItemGroup>
```

### 7.2 三种代码共享方式对比

| 方式 | 优点 | 缺点 | 本项目采用 |
|------|------|------|----------|
| `<ProjectReference>` | 类型安全、IDE 支持好 | 引入完整依赖链 | Brokerage/Strategies 层 |
| `<Compile Include>` | 零额外依赖、源码级共享 | 需要桩来满足编译 | Dashboard 层 |
| NuGet 包 | 版本管理好 | 发布流程复杂 | 未采用 |

### 7.3 共享源码的数据流

```
Polymarket API 源码 (6 个文件)
    │
    ├──[Compile Include]──▶ Dashboard (搭配 LeanStubs.cs)
    │                        ↓
    │                    TradingService 调用 PolymarketApiClient
    │                    MarketDataService 调用 PolymarketWebSocketClient
    │                    DryRunEngine 使用 PolymarketOrderBook 模型
    │
    └──[原始位置]──▶ Brokerage (搭配完整 LEAN 框架)
                         ↓
                     PolymarketBrokerage 调用 PolymarketApiClient
                     订单签名使用 EIP712Signer
                     Symbol 映射使用 PolymarketSymbolMapper
```

---

## 8. LEAN 类型使用全景表

### 8.1 Dashboard 中的 LEAN 类型引用

| LEAN 类型 | 命名空间 | 使用位置 | 来源 |
|-----------|---------|---------|------|
| `Symbol` | `QuantConnect` | PolymarketSymbolMapper (共享源码) | LeanStubs.cs |
| `SymbolId` | `QuantConnect` | PolymarketSymbolMapper (共享源码) | LeanStubs.cs |
| `SecurityType` | `QuantConnect` | PolymarketSymbolMapper (共享源码) | LeanStubs.cs |
| `OptionRight` | `QuantConnect` | ISymbolMapper 接口签名 | LeanStubs.cs |
| `ISymbolMapper` | `QuantConnect` | PolymarketSymbolMapper (共享源码) | LeanStubs.cs |
| `Market` | `QuantConnect` | PolymarketSymbolMapper (共享源码) | LeanStubs.cs |
| `OrderDirection` | `QuantConnect.Orders` | TradingService.PlaceOrder() | LeanStubs.cs |
| `Log` | `QuantConnect.Logging` | PolymarketApiClient (共享源码) | LeanStubs.cs |

**结论**: Dashboard 中**所有** LEAN 类型引用均指向 `LeanStubs.cs` 中的桩定义，不依赖真实的 LEAN 程序集。

### 8.2 Brokerage 中的 LEAN 类型引用

| LEAN 类型 | 命名空间 | 用途 |
|-----------|---------|------|
| `BaseWebsocketsBrokerage` | `QuantConnect.Brokerages` | Brokerage 基类 |
| `IDataQueueHandler` | `QuantConnect.Interfaces` | 实时数据接口 |
| `IAlgorithm` | `QuantConnect.Interfaces` | 算法实例注入 |
| `Symbol` | `QuantConnect.Securities` | 标的标识 |
| `Security` | `QuantConnect.Securities` | 证券对象 |
| `Order` | `QuantConnect.Orders` | 订单对象 |
| `OrderDirection` | `QuantConnect.Orders` | 买卖方向 |
| `OrderStatus` | `QuantConnect.Orders` | 订单状态 |
| `OrderFee` | `QuantConnect.Orders.Fees` | 手续费 |
| `FillModel` | `QuantConnect.Orders.Fills` | 成交模型 |
| `Tick` | `QuantConnect.Data.Market` | 实时行情 |
| `DefaultOrderBook` | `QuantConnect.Data.Market` | 订单簿 |
| `Log` | `QuantConnect.Logging` | 日志 |
| `LiveNodePacket` | `QuantConnect.Packets` | 实盘配置 |

---

## 9. 外部依赖清单

### 9.1 Dashboard 专用

| 包 | 版本 | 用途 |
|----|------|------|
| `Microsoft.NET.Sdk.Web` | 7.0 | ASP.NET Core Web 框架 |
| `Microsoft.AspNetCore.Mvc.NewtonsoftJson` | 7.0.0 | Controller JSON 序列化 |
| `Microsoft.AspNetCore.SignalR.Protocols.NewtonsoftJson` | 7.0.0 | SignalR JSON 协议 |
| `Newtonsoft.Json` | 13.0.2 | JSON 处理 |
| `Nethereum.Signer` | 4.21.4 | 以太坊密钥和 EIP-712 签名 |

### 9.2 Brokerage 专用

| 包 | 版本 | 用途 |
|----|------|------|
| `Newtonsoft.Json` | 13.0.2 | JSON 处理 |
| `Nethereum.Signer` | 4.21.4 | 以太坊签名 |
| `RestSharp` | 106.12.0 | REST HTTP 客户端 |
| `System.ComponentModel.Composition` | 9.0.0 | MEF 插件发现 |

### 9.3 共同依赖

| 包 | Dashboard | Brokerage | 说明 |
|----|-----------|-----------|------|
| `Newtonsoft.Json` | 13.0.2 | 13.0.2 | 版本一致 |
| `Nethereum.Signer` | 4.21.4 | 4.21.4 | 版本一致 |

### 9.4 外部服务依赖

| 服务 | 端点 | 用途 | 使用方 |
|------|------|------|-------|
| Gamma API | `https://gamma-api.polymarket.com` | 市场发现 (公开) | Dashboard TradingService |
| CLOB REST API | `https://clob.polymarket.com` | 订单/余额/订单簿 | Dashboard + Brokerage |
| CLOB WebSocket | `wss://ws-clob.polymarket.com` | 实时行情 | Dashboard MarketDataService |

---

## 10. 架构决策与权衡

### 10.1 为什么 Dashboard 不直接使用 LEAN 引擎？

| 因素 | 使用 LEAN | 独立 Dashboard | 最终选择 |
|------|----------|---------------|---------|
| **启动速度** | LEAN 引擎启动需加载数百个模块 | ASP.NET Core 秒级启动 | Dashboard |
| **部署复杂度** | 需要完整 LEAN 安装 | `dotnet run` 一条命令 | Dashboard |
| **开发迭代** | 受 LEAN API 变更约束 | 完全自主控制 | Dashboard |
| **Web UI** | LEAN 无内置 Web Dashboard | 自定义 SignalR 实时 UI | Dashboard |
| **模拟交易** | LEAN Paper Trading 功能完整但重 | 轻量 DryRun 引擎，针对做市优化 | Dashboard |
| **Target Framework** | LEAN 已升级到 .NET 10.0 | Dashboard 使用成熟的 .NET 7.0 | 各自独立 |

### 10.2 为什么保留 Brokerage 层的 LEAN 集成？

- **回测能力**: 只有通过 LEAN 引擎才能进行历史数据回测
- **生产部署**: 实际资金交易需要 LEAN 的风控、日志、监控基础设施
- **Algorithm Framework**: Kelly 组合构建、跨市场套利等高级策略依赖 LEAN 的 Alpha/Portfolio/Risk 框架
- **社区兼容**: 保持与 QuantConnect 生态的兼容性

### 10.3 两条路径的互补关系

```
┌─────────────────────────────────────────────────────────────┐
│                  策略开发生命周期                              │
│                                                              │
│  [原型验证]                    [生产部署]                      │
│                                                              │
│  Dashboard + DryRun             LEAN + Brokerage             │
│  ┌──────────────────┐          ┌──────────────────┐          │
│  │ 快速迭代          │          │ 历史回测          │          │
│  │ 实时模拟          │   ──▶   │ 风控框架          │          │
│  │ Web 可视化       │  策略    │ 实盘交易          │          │
│  │ 零配置启动       │  迁移    │ 性能监控          │          │
│  └──────────────────┘          └──────────────────┘          │
│                                                              │
│  MM 在 Dashboard 验证通过后                                   │
│  可迁移为 LEAN QCAlgorithm 进行回测和实盘                      │
└─────────────────────────────────────────────────────────────┘
```

### 10.4 源码共享 vs 依赖隔离的平衡

`<Compile Include>` + `LeanStubs.cs` 的模式实现了：

- **代码复用**: Polymarket API 客户端只维护一份源码
- **依赖隔离**: Dashboard 不引入 LEAN 的数十个传递依赖
- **编译安全**: 桩类型提供编译期类型检查
- **运行独立**: Dashboard 可以在没有 LEAN 安装的环境下运行

**代价**: 桩类型是 LEAN 真实类型的简化版本，如果 Brokerage 源码未来使用了更多 LEAN 类型，需要同步扩展 `LeanStubs.cs`。
