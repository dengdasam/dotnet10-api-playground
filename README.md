# .NET 10 API Playground

一套可直接点开就用的 .NET 10 API 调试台：**自带文档站页面 + 真实上游数据 + 服务启停控制器**，零 NuGet 依赖，只用 .NET 10 SDK 自带的 ASP.NET Core。

页面地址 <http://127.0.0.1:5188/> —— 打开即可逐条试接口，每个请求都会在卡片里单独给出一块「结论区」，原始 JSON 折叠在下方。

```
┌─────────────────────────┐        ┌──────────────────────────────┐
│  浏览器 :5188           │  HTTP  │  ServiceController           │
│  调试页（静态站点）      │ ─────► │  · 托管页面                   │
│  启动/停止按钮、状态灯   │ 反代    │  · /control/{start,stop,...}  │
└─────────────────────────┘        │  · /api/* 反向代理            │
                                   └──────────────┬───────────────┘
                                                  │ 子进程（关闭窗口即停）
                                                  ▼
                                   ┌──────────────────────────────┐
                                   │  ApiPlayground  :5180        │
                                   │  Minimal API · 16 个端点      │
                                   └──────────────────────────────┘
```

- **端口 5188**：控制器，对外唯一入口。页面、`/control/*`、`/api/*` 全走它。
- **端口 5180**：主服务，只监听本机，由控制器拉起与停止。**关掉控制器窗口，主服务会一起退出**，不留后台常驻进程。

## 快速开始

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（开发用 `10.0.302` 验证）。

| 方式 | 命令 | 说明 |
| --- | --- | --- |
| **单文件启动器（推荐）** | 双击 `Start-ApiPlayground.exe` | 自动打开浏览器；窗口关掉服务就停。首次需先 `publish.cmd` 生成 |
| **脚本启动** | `start.cmd`（Windows）/ `./start.sh`（Linux、WSL、macOS） | 已编译则跳过编译，直接秒启 |
| **强制重编译** | `start.cmd build` | 改了 C# 代码后用；也可在窗口里输入 `build` |
| **仅主服务** | `cd ApiPlayground && dotnet run` | 不带控制器，页面请直接开 `:5180` |

`publish.cmd` / `publish.sh` 会发布单文件 `Start-ApiPlayground.exe`（framework-dependent，约 220 KB，需本机装 .NET 10 运行时）。

## 接口一览

所有响应统一信封，**业务错误也返回 HTTP 200**，靠 `success` / `error.code` 区分，方便前端统一处理：

```json
{ "success": true, "data": { }, "error": null, "elapsedMs": 0.42, "timestamp": "2026-11-12T17:19:18.123+08:00" }
```

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api` | 服务自描述的接口清单 |
| GET | `/api/health` | 健康检查与运行时信息 |
| GET | `/api/weather?city=宁波` | 中文城市名查实时天气（Open-Meteo，离线自动兜底） |
| GET | `/api/weather/cities` | 内置中文城市表（经纬度） |
| GET·POST | `/api/calculator` | 数学表达式求值（自写递归下降解析器，不用 `eval`） |
| GET | `/api/time/now?tz=Asia/Shanghai` | 指定时区的当前时间 |
| GET | `/api/time/zones` | 常用 IANA 时区表 |
| GET | `/api/time/convert?datetime=...&from=...&to=...` | 跨时区时间换算 |
| GET | `/api/time/diff?start=...&end=...` | 两个时间之间的差值 |
| GET | `/api/units/categories` | 9 类 77 个单位（含 14 种货币） |
| GET | `/api/units/convert?category=length&value=1&from=m&to=ft` | 长度/重量/温度/数据/速度/面积/体积/时间/汇率换算 |
| GET | `/api/cats/random?limit=2&breed=pers&hasBreeds=1` | 随机猫图，可按品种 / 数量 / 尺寸筛选 |
| GET | `/api/cats/breeds?q=siamese&limit=20` | TheCatAPI 品种列表（107 个，缓存 30 分钟） |
| GET | `/api/cats/breeds/pers` | 按 4 字母 ID 查品种详情 |
| GET | `/api/cats/images/{id}` | 按图片 ID 查图（命中本地缓存则不请求上游） |

汇率走 <https://open.er-api.com> 公开接口（免 Key），以 USD 交叉换算；上游不可用时回落到内置快照，响应里 `provider` / `note` 会标明来源。

### 配置 TheCatAPI 密钥

猫咪接口支持官方公开匿名额度，**不配置也能用**。想用自己的 key 提升额度时，二选一：

```bash
# 方式一：环境变量（优先级最高）
set THECATAPI_KEY=live_你的密钥        # Windows CMD
export THECATAPI_KEY=live_你的密钥     # bash / macOS / Linux

# 方式二：复制示例文件后填入密钥，重启服务即可
cp .env.example .env.local
```

服务端按 `THECATAPI_KEY` → 仓库根 `.env.local` 的顺序读取，请求时自动附加 `x-api-key` 头；**密钥不会出现在页面、URL 或响应里**，`/api/health` 只回报是否已配置。`.env.local` 已被 `.gitignore` 排除，请务必不要把它提交到公开仓库。

## 控制台窗口

双击 exe 或运行 `start.cmd` 后会得到一个交互窗口，服务随手启停：

| 命令 | 作用 |
| --- | --- |
| `status`（或直接回车） | 运行状态、PID、已运行时长、最近消息 |
| `start` / `stop` / `restart` | 启动 / 优雅停止（5 秒未退才强杀）/ 先停再启 |
| `build` | 编译主服务并自动重启，改完 C# 代码用这个 |
| `logs [n]` | 精简显示最近 n 条输出；`logs raw [n]` 看原始日志 |
| `stream [on/off]` | 实时流水开关（默认开） |
| `open` | 用默认浏览器打开页面 |
| `clear` / `help` / `exit` | 清屏 / 帮助 / 停止服务并退出 |

窗口里的日志是**精简过的**，框架的 `info: Microsoft.Hosting.Lifetime[14]` 这类结构行不会刷屏：

```
  │ ▸ 监听地址 http://127.0.0.1:5180
  │ ▸ 主服务已就绪
  │ → GET /api/time/now  200  15.6ms
```

只保留「监听 / 启停 / 每请求一行结果 / 告警」四类，`Request starting`、`Hosting environment`、序列化细节、`/_internal/*` 统一隐藏。

## 设计要点

- **零 NuGet 依赖**：只用 framework reference；页面是单文件 HTML/CSS/JS，无构建步骤。
- **天气三级缓存**：按城市缓存 3 分钟 + 单航班（并发同一城市只打一次上游）+ 失败回退旧缓存，响应带 `cached` / `cacheAgeSeconds`。
- **汇率单航班 + 启动预热**：服务就绪即在后台拉一次汇率，首次换算不再等 1.3 秒。
- **猫咪接口**：品种列表 30 分钟缓存 + 单航班；最近 20 张图留作上游故障兜底（标 `stale`）；连接超时 2 秒、总超时 6 秒；密钥只从本机环境变量 / `.env.local` 读取。
- **单实例保护**：控制器与主服务各按端口加命名互斥体，重复双击只会打开页面，不会崩，也不会两个后端分走请求。
- **父子联动**：主服务通过 `API_PARENT_PID` 反向监视控制器，控制器被强杀也会自行退出。
- **反代正确处理 body**：转发前先读成字节数组，避免 POST 丢包。

实测（keep-alive，p50）：直连 `/api/health` 约 0.28 ms，经代理约 0.37 ms；天气命中缓存 0.33 ms；并发 20 约 3600 req/s（日志开）～4500 req/s（日志关）。

## 目录结构

```
.
├── ApiPlayground/                # 主服务（Minimal API，:5180）
│   ├── Program.cs                # 端点注册、日志、单实例、父进程守护
│   ├── ApiEnvelope.cs            # 统一响应信封
│   ├── Services/                 # 天气 / 汇率 / 时间 / 单位 / 表达式 / 猫图 / 数字格式
│   │   └── CatService.cs         # TheCatAPI 封装（密钥注入、缓存、降级）
│   ├── wwwroot/index.html        # 调试页（双栏文档站，单文件零依赖）
│   └── README.md                 # 主服务详细文档（接口、缓存、性能）
├── ServiceController/            # 控制器（:5188，页面 + 反代 + 进程管理）
│   ├── Program.cs                # 静态托管、/control/*、/api/* 反代
│   ├── BackendProcess.cs         # 子进程启停、日志收集、崩溃判定
│   ├── ConsoleShell.cs           # 窗口交互命令、精简日志渲染
│   └── ServiceBuild.cs           # 编译封装
├── .env.example                  # 密钥配置示例（占位值，可入库）
├── .env.local                    # 本机真实密钥（已被忽略，不入库）
├── start.cmd / start.sh          # 一键启动（已编译则跳过编译）
├── publish.cmd / publish.sh      # 发布单文件 Start-ApiPlayground.exe
├── bench.py                      # keep-alive 精测 + 并发吞吐基准
└── bench.sh                      # curl 粗测
```

## License

[MIT](LICENSE)
