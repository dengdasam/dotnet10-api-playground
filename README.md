# .NET 10 API 调试台

一套基于 **.NET 10 / ASP.NET Core Minimal API** 的示例接口服务，附带一个沿用「双栏文档站」主题的单文件静态调试页面。所有接口都能在页面上直接填参数、点按钮、看原始 JSON 响应，也能复制对应的 cURL 命令。

> 零 NuGet 依赖：项目只引用 `Microsoft.AspNetCore.App` 共享框架，`dotnet build` 无需联网拉包。

## 环境与启动

要求：本机已安装 .NET SDK 10（`dotnet --version`，本机实测 10.0.302）。

```bash
# 方式一：直接运行
dotnet run --project ApiPlayground

# 方式二：一键脚本
start.cmd          # Windows
bash start.sh      # Git Bash / macOS / Linux
```

默认监听 `http://127.0.0.1:5188`，浏览器打开该地址即为调试页面。

自定义监听地址：

```bash
dotnet run --project ApiPlayground --urls http://127.0.0.1:8080
# 或
API_URL=http://127.0.0.1:8080 dotnet run --project ApiPlayground
```

## 接口清单（11 组 / 12 个端点）

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api` | 接口清单（服务自描述，页面表格由它渲染） |
| GET | `/api/health` | 服务健康、运行时版本、监听地址、端点数 |
| GET | `/api/weather?city=宁波` | 中文城市名查实时天气，离线自动兜底 |
| GET | `/api/weather/cities` | 内置 25 个中文城市及经纬度 |
| GET / POST | `/api/calculator` | 数学表达式求值（GET 用 `?expression=`，POST 用 JSON 体） |
| GET | `/api/time/now?tz=Asia/Shanghai` | 指定时区当前时间 |
| GET | `/api/time/zones` | 常用时区表 |
| GET | `/api/time/convert?datetime=&from=&to=` | 跨时区时间换算 |
| GET | `/api/time/diff?start=&end=` | 两个时间的差值 |
| GET | `/api/units/categories` | 9 类单位及各自单位列表（含 14 种货币） |
| GET | `/api/units/convert?category=&from=&to=&value=` | 单位换算，含 `category=currency` 汇率换算 |

### 响应契约

所有接口（含参数错误）都以 HTTP 200 返回统一信封：

```json
{
  "success": true,
  "data": { "...": "业务数据" },
  "error": null,
  "elapsedMs": 1.24,
  "timestamp": "2026-11-12T13:36:18.1234567+08:00"
}
```

失败时 `success=false`、`data=null`、`error={code,message}`，`code` 为可枚举的错误码（如 `DIVIDE_BY_ZERO`、`INVALID_TIMEZONE`、`MISSING_CITY`）。

### 常用示例

```bash
curl 'http://127.0.0.1:5188/api/weather?city=%E5%AE%81%E6%B3%A2'

curl -X POST 'http://127.0.0.1:5188/api/calculator' \
  -H 'Content-Type: application/json' \
  -d '{"expression":"sqrt(9)+pow(2,10)"}'

curl 'http://127.0.0.1:5188/api/time/convert?datetime=2026-11-12%2013:30&from=Asia/Shanghai&to=America/New_York'

curl 'http://127.0.0.1:5188/api/units/convert?category=temperature&from=c&to=f&value=100'

# 汇率换算：100 人民币 → 美元
curl 'http://127.0.0.1:5188/api/units/convert?category=currency&from=cny&to=usd&value=100'
```

## 实现要点

- **计算器**：手写递归下降解析器，不 `eval`、不动态编译、不引第三方包。支持 `+ - * / ^`、括号、一元正负号、科学计数法、常量 `pi/e/tau`、21 个数学函数；`^` 为右结合。除零、括号失配、定义域越界、未知函数都返回中文错误提示。
- **天气**：上游为 Open-Meteo 公开接口（免注册免 Key）。内置城市直接带经纬度请求 `/v1/forecast`，任意城市名先走 `/v1/search` 地理编码。4 秒超时或异常时降级为本地确定性模拟数据（按「城市名 + UTC 小时」生成，同一小时内稳定）；连续失败后 2 分钟内抑制重试，避免每次空等。响应 `source` 字段标明 `live` / `fallback`。
- **时间**：使用 IANA 时区标识，Windows 环境查不到时自动回退到对应 Windows 时区 ID。不带偏移的时间按源时区解释，带 `Z` 或 `±HH:mm` 的按绝对偏移解析。
- **单位换算**：9 类 77 个单位，线性单位以基准单位为中转（`value × fromFactor ÷ toFactor`）；温度以开尔文为中转处理非线性偏移；数据存储同时提供十进制 KB 与二进制 KiB 两套。
- **汇率换算**：新增第 9 类 `currency`，14 种常用货币。数据源为 open.er-api.com 公开免 Key 接口（每日更新、以 USD 为基准），交叉汇率 `Rates[to] / Rates[from]`，成功结果内存缓存 30 分钟；上游失败/超时降级为 2026-11-12 的内置快照，连续失败 2 分钟内抑制重试。响应返回 `baseRate`、`reverseRate`、`rateExpression`、`source`、`provider`、`rateUpdatedAt` 与换算公式。汇率为参考值，非逐笔实时报价。
- **页面**：单文件 `wwwroot/index.html`，左侧 238px sticky 目录（章节锚点 + 滚动高亮）、右侧内容区；冷调低饱和、纯白卡片、深靛主色 `#3B5A6A`、细边框、无渐变无阴影，含回到顶部、窄屏横向导航、打印隐藏侧栏。响应以中性配色高亮，附服务端 / 客户端耗时与调用计数。
- **每个请求各自的最终结果**：每个接口卡片在原始 JSON 上方都有一块醒目的结论区，调用后显示人类可读的最终结果，如「宁波（浙江）22.2℃ · 阴 · 实时数据」「1+2*(3-4)/5 = 0.6」「100 CNY = 14.957 USD · …」；失败时显示中文错误提示。原始 JSON 仍完整保留在下方供对照，页面初始化时的辅助数据拉取不产生结论。

## 目录结构

```
ApiPlayground/
├── ApiPlayground.csproj          # 仅 framework reference
├── Program.cs                    # Minimal API 端点注册
├── ApiEnvelope.cs                # 统一响应信封 + 异常包装
├── Services/
│   ├── ExpressionEvaluator.cs    # 递归下降表达式解析
│   ├── WeatherService.cs         # Open-Meteo + 离线兜底
│   ├── TimeService.cs            # 时区 / 时间换算 / 差值
│   ├── UnitConverter.cs          # 9 类单位换算（含 currency 类别）
│   ├── ExchangeRateService.cs    # 公开汇率接口 + 内置快照兜底
│   └── NumberFormat.cs           # 浮点显示格式化
├── wwwroot/index.html            # 单文件调试页面
├── start.cmd / start.sh          # 一键启动
└── README.md
```

## 限制

面向本地演示，开放访问、无鉴权无限流；天气降级数据是模拟值，不可用于生产决策。若部署到公网，需补充鉴权、限流与超时策略。
