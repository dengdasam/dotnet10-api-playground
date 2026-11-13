# .NET 10 API 调试台

一套基于 **.NET 10 / ASP.NET Core Minimal API** 的示例接口服务，附带一个沿用「双栏文档站」主题的单文件静态调试页面。所有接口都能在页面上直接填参数、点按钮、看原始 JSON 响应，也能复制对应的 cURL 命令。

> 零 NuGet 依赖：项目只引用 `Microsoft.AspNetCore.App` 共享框架，`dotnet build` 无需联网拉包。

## 架构与端口

页面由 **ServiceController（控制台应用）** 托管，主服务由它按需拉起与停止。它不是后台服务，就是你双击打开的那个窗口：窗口在则服务在，关窗口全部停止。

| 组件 | 端口 | 职责 |
| --- | --- | --- |
| `ServiceController` | 5188（对外） | 托管同一份 `wwwroot/index.html`、提供启动/停止接口、把 `/api/*` 反向代理到主服务 |
| `ApiPlayground` | 5180（内部） | 全部业务 API；由控制器作为子进程启动，父进程消失即自行退出 |

因此即使主服务已停止，页面依然能打开，并可直接再点「启动服务」。两个项目都只引用 `Microsoft.AspNetCore.App` 共享框架，**零 NuGet 包**。

## 双击即用（Windows exe）

工作区根目录打包了一个控制台程序 **`Start-ApiPlayground.exe`**（单文件约 212 KB，使用本机已安装的 .NET 10 运行时）。双击它就会：

1. 检查主服务是否已编译，缺少 dll 时自动执行 `dotnet build`；
2. 启动控制器（5188）并拉起主服务（5180）；
3. 服务就绪后**自动用默认浏览器打开** `http://127.0.0.1:5188/`；
4. 窗口里继续可用 `start / stop / restart / build / logs / open` 等命令。

关闭窗口 = 全部停止，不留后台进程。

- 不想自动开浏览器：`Start-ApiPlayground.exe --no-browser`
- 改过 `ServiceController` 自身代码后：运行根目录 `publish.cmd`（或 `bash publish.sh`）重新生成 exe；
- 只改主服务代码或页面：不用重新发布，窗口里输入 `build`，或直接重新双击 exe 即可；
- exe 可重命名、可建桌面快捷方式，它通过逐级向上查找 `ApiPlayground/ApiPlayground.csproj` 定位项目，也可用环境变量 `API_PLAYGROUND_ROOT` 指定。

## 重复启动与异常退出

Windows 上 `SO_REUSEADDR` 有时允许两个进程绑同一端口，过去反复双击 exe 会出现英文堆栈 `Failed to bind to address http://127.0.0.1:5188: address already in use` 并闪退。现在按下面三层处理：

| 情形 | 现在的行为 |
| --- | --- |
| **重复双击 exe** | 先发 HTTP 问 `127.0.0.1:5188/control/status`，确认是自己在跑 → 提示「已有控制器在运行」，直接打开页面，本次进程退出（退出码 0），不再抢端口 |
| **5188 被别的程序占用** | 中文提示占用端口与排查命令（`netstat -ano \| findstr 5188`），退出码 1，窗口停住等按键，不会一闪而过 |
| **5180 已被主服务占用** | 主服务内置按端口的命名互斥体，第二个实例只打印「已有主服务在运行，本次不重复启动」后退出，避免两个后端并存把请求分走 |
| **主服务中途崩溃** | 控制器捕获 `Exited` 事件，窗口打印「主服务异常退出（退出码 N）」＋最后几行输出＋处置建议；页面徽标变为 **服务异常退出**，接口返回 `SERVICE_UNAVAILABLE` 信封且 `error.message` 带崩溃原因 |

定位思路：只看页面会以为是「请求失败」，实际根因大多在窗口里。窗口是唯一的日志出口，`logs [n]` 可随时翻最近输出。

## 环境与启动

要求：本机已安装 .NET SDK 10（`dotnet --version`，本机实测 10.0.302）。

```bash
# 在仓库根目录执行；已编译过就直接启动，不重复编译
start.cmd              # Windows
bash start.sh          # Git Bash / macOS / Linux

# 强制重新编译两个项目后启动
start.cmd build
bash start.sh build

# 或手动
dotnet build ApiPlayground/ApiPlayground.csproj
dotnet build ServiceController/ServiceController.csproj
dotnet run --project ServiceController
```

**关于编译**：`start.cmd` 只有在 `bin/Debug/net10.0/*.dll` 不存在时才编译，否则直接启动（实测无改动的全量编译需 6.7 秒，跳过编译启动只需约 6 秒）。改了 C# 代码有两个办法生效：

- 在控制台窗口里输入 `build` —— 自动停止主服务、编译、再重启，约 9 秒；
- 关闭窗口后运行 `start.cmd build` —— 编译两个项目（包括控制器本身）后启动。

只改 `wwwroot/index.html` 不需要编译，刷新浏览器即生效（静态文件由控制器直接读取）。若启动时检测到 `.cs` 源码比 dll 新，窗口会给出黄色提醒，避免误跑旧版本。

浏览器打开 **http://127.0.0.1:5188/**（地址始终不变）。控制器启动后自动拉起主服务，保持控制器窗口不关闭即可。

> 只想单独调试后端时：`dotnet run --project ApiPlayground`，它监听内部 `http://127.0.0.1:5180`，此模式下页面不提供启动/停止能力。
> 可用环境变量 `API_URL` 改主服务监听地址、`API_PLAYGROUND_ROOT` 指定工作区根（供控制器定位项目）。

## 服务启动与停止

页面顶部工具栏有状态灯与「启动服务 / 停止服务」按钮，状态每 5 秒刷新，悬停可看 PID 与运行时长。

控制接口（控制器提供，仅监听 `127.0.0.1`）：

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/control/status` | 运行状态、PID、启动时间、运行时长、后端地址 |
| POST | `/control/start` | 拉起 `dotnet ApiPlayground.dll`，等待 5180 端口就绪 |
| POST | `/control/stop` | 先调主服务内部端点 `/_internal/shutdown` 优雅停机，5 秒未退出则强制结束进程 |

主服务未启动时 `/api/*` 返回 `503` 与统一信封（`error.code = SERVICE_UNAVAILABLE`），页面提示先点「启动服务」。控制器退出时会一并结束主服务，避免留下孤儿进程。

## 控制台窗口

控制器本身就是一个带命令行的前台程序：`start.cmd` / `start.sh` 打开的窗口里可直接输入命令管理服务，窗口标题实时显示当前状态（运行中 / 已停止 + PID）。

| 命令 | 作用 |
| --- | --- |
| `status`（或直接回车） | 运行状态、PID、已运行时长、页面/主服务地址、最近消息 |
| `start` | 启动主服务，已在运行时不会重复启动 |
| `stop` | 优雅停止主服务，5 秒未退出才强制结束 |
| `restart` | 先停再启，改完代码后重启用（不编译） |
| `build` | 停止主服务 → `dotnet build` → 重启，改完 C# 代码用这个 |
| `logs [n]` | 精简显示最近 n 行输出（默认 12 行）；`logs raw [n]` 看原始日志 |
| `stream [on/off]` | 开关主服务输出的实时流水（默认开，不带参数即切换） |
| `open` | 用默认浏览器打开 `http://127.0.0.1:5188/` |
| `clear` | 清屏 |
| `help` | 帮助 |
| `exit` | 优雅停止主服务并退出控制器 |

主服务的输出通过重定向被控制器收集（保留最近 400 行），并且**实时打印在本窗口**；控制器拉起主服务时会注入 `API_LOG_LEVEL=Information`，所以**页面点「启动服务」或窗口里 `start`，立刻能看到启动与请求记录**。

窗口显示的是**精简后的日志**，框架那些 `info: Microsoft.Hosting.Lifetime[14]` 之类的结构行不会出现（主服务侧用 `AddSimpleConsole(SingleLine)` 输出单行，控制器侧再按类别收敛）：

```
  │ ▸ 监听地址 http://127.0.0.1:5180
  │ ▸ 主服务已就绪
  │ → GET /api/time/now  200  15.6ms
  │ ⚠ 某条警告文本
```

保留的只有四类：监听地址、启动/停机、每请求一行结果（`→ 方法 路径 状态码 耗时`）、以及 `warn`/`fail` 告警。`Request starting`、`Hosting environment`、`Content root path`、序列化细节、`/_internal/*` 内部端点一律不显示。想看原文用 `logs raw [n]`；想看全量框架日志给控制器加 `API_CHILD_LOG_LEVEL=Debug`。窗口每秒轮询一次状态，主服务意外退出会在窗口里即时提示原因与最后几行输出。

> 直接 `dotnet run` 主服务时仍默认 `Warning`（压测时每请求的同步 I/O 比业务本身还贵）；只有在控制台窗口里跑才开 Info。

`exit`、`Ctrl+C`、关闭窗口三种方式都会结束控制器并一并停掉主服务。标准输入被重定向时（例如注册为后台服务），命令循环自动退出，控制器继续无界面运行，页面与控制接口照常可用。

**不会在后台残留进程**，有三重保障：

1. 主服务由控制器作为子进程启动，并带上环境变量 `API_PARENT_PID`；主服务每 2 秒检查父进程，控制器一消失就自行停止（实测强杀控制器后 2 秒内 5180 端口释放）。
2. 控制器 `ApplicationStopping` 与 `ProcessExit` 都会调用 `Kill(entireProcessTree)` 清理子进程。
3. 关闭控制台窗口时，同一控制台内的子进程会一并收到关闭事件。

换句话说：**窗口就是应用本体，开窗即启动、关窗即全停，不需要也不会有后台服务常驻。**

## 接口清单（21 组 / 22 个端点）

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api` | 接口清单（服务自描述，页面表格由它渲染） |
| GET | `/api/health` | 服务健康、运行时版本、监听地址、端点数 |
| GET | `/api/weather?city=宁波` | 中文城市名查实时天气，3 分钟缓存，离线自动兜底 |
| GET | `/api/weather/cities` | 内置 25 个中文城市及经纬度 |
| GET / POST | `/api/calculator` | 数学表达式求值（GET 用 `?expression=`，POST 用 JSON 体） |
| GET | `/api/time/now?tz=Asia/Shanghai` | 指定时区当前时间 |
| GET | `/api/time/zones` | 常用时区表 |
| GET | `/api/time/convert?datetime=&from=&to=` | 跨时区时间换算 |
| GET | `/api/time/diff?start=&end=` | 两个时间的差值 |
| GET | `/api/units/categories` | 9 类单位及各自单位列表（含 14 种货币） |
| GET | `/api/units/convert?category=&from=&to=&value=` | 单位换算，含 `category=currency` 汇率换算 |
| GET | `/api/cats/random?limit=&breed=&hasBreeds=&size=` | 随机猫图，可按品种 ID、数量、尺寸筛选 |
| GET | `/api/cats/breeds?q=&limit=` | 品种列表（107 个，关键字过滤，缓存 30 分钟） |
| GET | `/api/cats/breeds/{id}` | 按 4 字母 ID 查品种详情（含中文名） |
| GET | `/api/cats/images/{id}` | 按图片 ID 查图，命中本地缓存则不请求上游 |
| GET | `/api/dogs/breeds?q=&group=&limit=&hypo=` | 犬种列表（283 个，关键字/分组/低敏过滤） |
| GET | `/api/dogs/breeds/{id}` | 犬种详情（UUID / 英文名 / 中文名均可，含 11 项性格评分） |
| GET | `/api/dogs/random?breed=&size=` | 随机犬图，附品种信息与 Wikimedia 署名 |
| GET | `/api/dogs/image?breed=&size=` | 解析上游 302 图片地址（不自动跟随重定向） |
| GET | `/api/dogs/facts?limit=1..5` | 随机趣闻，上游 no-store，每次不同 |
| GET | `/api/dogs/groups` | 犬种分组统计（AKC 9 组，由索引聚合） |

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

# 随机两张波斯猫图（只要带品种信息的）
curl 'http://127.0.0.1:5188/api/cats/random?limit=2&breed=pers&hasBreeds=1'

# 品种详情
curl 'http://127.0.0.1:5188/api/cats/breeds/pers'

# 随机犬图（指定柯基）与犬种详情
curl 'http://127.0.0.1:5188/api/dogs/random?breed=Pembroke+Welsh+Corgi&size=medium'
curl 'http://127.0.0.1:5188/api/dogs/breeds/柴犬'
```

### TheCatAPI 密钥配置

猫咪接口开箱即用：不配置密钥时走官方公开匿名额度，响应 `authenticated=false`。要提升额度按下面二选一配置，**密钥只留在本机**：

```bash
# 方式一：环境变量（优先级最高，Windows CMD 用 set）
export THECATAPI_KEY=live_你的密钥

# 方式二：复制示例文件后填入密钥，重启服务
cp .env.example .env.local
```

服务端读取顺序为 `THECATAPI_KEY` → 仓库根 `.env.local`，请求时自动附加 `x-api-key`；`/api/health` 的 `catApi` 字段只回报 `keyConfigured` / `keySource`，**不含密钥内容**。`.env.local` 已被 `.gitignore` 排除，切勿提交到公开仓库。

## 实现要点

- **计算器**：手写递归下降解析器，不 `eval`、不动态编译、不引第三方包。支持 `+ - * / ^`、括号、一元正负号、科学计数法、常量 `pi/e/tau`、21 个数学函数；`^` 为右结合。除零、括号失配、定义域越界、未知函数都返回中文错误提示。
- **天气**：上游为 Open-Meteo 公开接口（免注册免 Key）。内置城市直接带经纬度请求 `/v1/forecast`，任意城市名先走 `/v1/search` 地理编码。4 秒超时或异常时降级为本地确定性模拟数据（按「城市名 + UTC 小时」生成，同一小时内稳定）；连续失败后 2 分钟内抑制重试，避免每次空等。响应 `source` 字段标明 `live` / `fallback`。
  性能上做了三层：① 结果按城市缓存 3 分钟（上游本身就是 15 分钟级更新，缓存不改变数据新鲜度）；② **单航班**——同一时刻只放一个请求打上游，其余请求等它回填缓存，避免并发打爆上游或重复超时；③ 降级顺序为「缓存（`cached:true` + `cacheAgeSeconds`）→ 本地模拟」，任何情况下都不会让调用方干等满超时。响应新增 `cached`、`cacheAgeSeconds` 两个字段。
- **时间**：使用 IANA 时区标识，Windows 环境查不到时自动回退到对应 Windows 时区 ID。不带偏移的时间按源时区解释，带 `Z` 或 `±HH:mm` 的按绝对偏移解析。
- **单位换算**：9 类 77 个单位，线性单位以基准单位为中转（`value × fromFactor ÷ toFactor`）；温度以开尔文为中转处理非线性偏移；数据存储同时提供十进制 KB 与二进制 KiB 两套。
- **汇率换算**：新增第 9 类 `currency`，14 种常用货币。数据源为 open.er-api.com 公开免 Key 接口（每日更新、以 USD 为基准），交叉汇率 `Rates[to] / Rates[from]`，成功结果内存缓存 30 分钟；上游失败/超时降级为 2026-11-12 的内置快照，连续失败 2 分钟内抑制重试。响应返回 `baseRate`、`reverseRate`、`rateExpression`、`source`、`provider`、`rateUpdatedAt` 与换算公式。汇率为参考值，非逐笔实时报价。
- **狗狗犬种（dogapi.dog）**：上游完全免密钥（官方声明 "No key, no sign up"）。全量犬种索引 1.84 MB、国内实测单次 15~40 秒，因此采用「内存索引 → 仓库种子 Data/dog-breeds.json → 7 天磁盘缓存 .cache/」三层结构：冷启动读种子（毫秒级）立即可用，后台以单航班（Interlocked 标志）刷新上游并落盘；响应 `source` 标明 `live` / `cache` / `seed`。详情支持 UUID、英文名、中文名与别名查询，附 11 项 1–5 分性格评分（运动量为分钟）。图片来自 Wikimedia Commons，响应携带 `attribution`（作者/许可/来源页），页面预览把署名渲染进图注。302 图片端点关闭自动跟随、原样返回 `Location`，方便观察重定向本身。
- **猫咪图片（TheCatAPI）**：`/api/cats/*` 四个端点封装 `images/search`、`images/{id}`、`breeds`。密钥只从环境变量 / `.env.local` 注入，页面与 URL 都拿不到。品种列表 30 分钟缓存 + 单航班，最近 20 张成功图片留作上游故障兜底（`stale=true`）；连接超时 2 秒、总超时 6 秒；401/429 分别映射为 `CAT_API_KEY_INVALID` / `CAT_API_RATE_LIMIT`。常见品种附中文名（如 `pers` → 波斯猫），上游图片 CDN 直出，页面预览失败时自动给文字提示。
- **页面**：单文件 `wwwroot/index.html`，左侧 238px sticky 目录（章节锚点 + 滚动高亮）、右侧内容区；冷调低饱和、纯白卡片、深靛主色 `#3B5A6A`、细边框、无渐变无阴影，含回到顶部、窄屏横向导航、打印隐藏侧栏。响应以中性配色高亮，附服务端 / 客户端耗时与调用计数。
- **每个请求各自的最终结果**：每个接口卡片在原始 JSON 上方都有一块醒目的结论区，调用后显示人类可读的最终结果，如「宁波（浙江）22.2℃ · 阴 · 实时数据」「1+2*(3-4)/5 = 0.6」「100 CNY = 14.957 USD · …」；失败时显示中文错误提示。原始 JSON 仍完整保留在下方供对照，页面初始化时的辅助数据拉取不产生结论。

## 性能

### 实测（Windows 11 / .NET 10.0.10 / 本机回环，400 次 keep-alive 取 p50）

| 场景 | 优化前 | 优化后 |
| --- | --- | --- |
| `GET /api/health`（直连 5180） | 0.21 ms | **0.14 ms** |
| `GET /api/time/now`（直连） | 0.22 ms | **0.13 ms** |
| `GET /api/units/categories`（直连） | 0.26 ms | **0.14 ms** |
| 同上，经代理 5188 | 0.80 ms | **0.37 ms** |
| `GET /api/weather?city=宁波`（命中缓存） | 226 ms | **0.18 ms**（直连）/ **0.53 ms**（代理） |
| `GET /api/units/convert?category=currency`（首次） | 1270 ms | **0.16 ms**（启动已预热） |
| `GET /api/cats/breeds?limit=20`（代理，品种缓存命中） | — | **0.57 ms** |
| `GET /api/cats/breeds/pers`（代理） | — | **0.53 ms** |
| `GET /api/cats/random`（真实上游往返） | — | **约 320 ms**，不计入压测循环 |
| `GET /api/dogs/breeds?limit=20`（索引命中） | — | **0.40 ms** |
| `GET /api/dogs/breeds?q=husky`（关键字过滤） | — | **0.56 ms** |
| `GET /api/dogs/breeds/Shiba Inu`（详情） | — | **0.40 ms** |
| `GET /api/dogs/random`（索引随机取图，0 上游请求） | — | **< 0.1 ms** |
| `GET /api/dogs/image`（真实 302 往返） | — | **约 1.8 s**，不计入压测循环 |
| 页面首屏 FCP / 数据就绪 | 264 / 322 ms | 264 / 322 ms（本就没问题，未动） |
| 并发 20 × 20 次 `/api/health` | — | **4573 req/s**，p50 3.0 ms，0 错误（经代理） |

### 2026-11-13 第五轮：索引与预热（base/new 交替 A/B，每端点 1200 次）

| 项 | 改动前 | 改动后 |
| --- | --- | --- |
| 冷启动到就绪（发布版，两轮交替） | 738 / 675 ms | **642 / 641 ms** |
| `GET /api/dogs/breeds?limit=20` p50 | 0.569 ms | **0.425 ms**（p95 1.10 → 0.87） |
| `GET /api/dogs/breeds?q=husky` p50 | 0.804 ms | 0.733 ms |
| 常驻内存 RSS | 74 MB | 78 MB（波动范围内，未改善） |

改动：预热四路并行（不再串行累加 2 s）；`Label` / `groupCount` / `hypoallergenicCount` 在索引装载时预计算；索引文件改流式解析（去掉 1.84 MB 整块 `byte[]`）；每请求计时改 `Stopwatch.GetTimestamp()`，零分配。

> 说明：在 0.1–0.4 ms 量级上，单次采样的噪声大于改动本身。除上表前两行外，其余端点差异不显著，不能算作收益。

### 已知问题（压测前必读）

~~猫咪接口在**上游不可达 / 无密钥**时失败结果不缓存，每条请求都要重新等上游（实测 380–1100 ms，且会持续打上游）~~ **已修复（2026-11-13 第六轮）**：任一猫端点上游失败（含 429 限流）后进入 30 秒**负缓存**——有旧图回旧图（标记 `stale`），没有旧图就毫秒级快速失败；`/api/cats/random` 另加**单航班**（相同查询的并发请求共享一次上游往返）。实测（把 BaseUrl 指向不可路由地址模拟宕机）：失败后所有猫端点 2–8 ms 直接返回错误，30 秒内零上游请求；冷却过期后仅做一次 2 秒的真实探测，失败再进冷却。`bench.py` 里的 cats 端点现在只有冷却过期后的第一下会慢，压测仍建议用 `bench-local.py`。

天气冷路径（换一个没查过的城市）仍是 200~290 ms 的真实上游往返，这是网络本身，缓存只负责别让它每次都发生。

### 做了哪些优化

1. **天气三级缓存**：3 分钟 TTL + 单航班（同一城市并发只打一次上游）+ 失败回退旧缓存。热路径从"每次等上游"变成"内存命中"。
2. **汇率单航班 + 启动预热**：并发首次换算不再同时打 N 次上游；服务一就绪就在后台先拉一次汇率和默认城市（宁波）天气，把 1.3 秒的上游往返挪到用户请求之外。
3. **日志分级 + 精简**：直接跑主服务默认 `Warning`（Kestrel 每请求 4 行 Info，stdout 被控制器接管时是同步 I/O，开销比业务本身还大）；**从控制台窗口拉起时自动注入 `API_LOG_LEVEL=Information`**，`AddFilter` 压掉 `Http.Result` / `EndpointMiddleware`，再配合 `AddSimpleConsole(SingleLine)` 让每行自成一条，控制器侧只把「监听 / 启停 / 请求结果 / 告警」重写成 `▸`、`→`、`⚠` 开头的一行。
4. **常量数据预建索引**：单位类别 / 单位 / 时区 / 城市全部在建表时建好 `Dictionary`，请求期不再 `FirstOrDefault` 线性扫数组；城市表、时区表、单位清单这些不会变的对象只构建一次。
5. **`TimeZoneInfo` 解析结果缓存**：包含「IANA → Windows ID」的兜底解析结果，避免每次请求重复尝试。
6. **HTTP 客户端调优**：天气/汇率客户端单独设 `ConnectTimeout`（2~3 秒），上游不通时快速失败而不是把总超时耗在握手；连接池生命周期 10 分钟。控制器反代客户端改用 `SocketsHttpHandler`（`ConnectTimeout` 2 秒、`MaxConnectionsPerServer` 256）。
7. **Kestrel 不写 `Server` 头**，省一次 header 序列化。
8. **启动探测粒度 250 ms → 100 ms**，冷启动少最多 150 ms 的空等；窗口新增「控制器已就绪（X ms）」打点。
9. **页面**：状态轮询在标签页隐藏时暂停、切回立刻刷新；状态签名没变就不写 DOM，避免每 5 秒一次无谓重排。
10. **猫咪失败负缓存 + 单航班（2026-11-13 第六轮）**：任一猫端点上游失败（含 429）→ 30 秒内全部猫端点不再碰上游（有旧数据回旧数据、没有就快速失败）；`/api/cats/random` 相同查询并发共享一次上游往返；`EnsureBreedsAsync` 门内外双重冷却检查，排队请求不再逐个撞上游。

### 自己复测

```bash
bash bench.sh http://127.0.0.1:5188 30        # 粗测（curl，含进程开销，看趋势）
python bench.py http://127.0.0.1:5180 400     # 精测（keep-alive，看真实服务端延迟）
python bench.py http://127.0.0.1:5188 400 --concurrency 20   # 附带并发吞吐
```

`bench.py` 用长连接，测的是服务端真实延迟；`bench.sh` 每次起一个 curl 进程，会自带 3~4 ms 开销，只适合看相对变化。

## 目录结构

```
<workspace-root>/
├── ApiPlayground/                    # 主服务（内部 5180）
│   ├── ApiPlayground.csproj          # 仅 framework reference
│   ├── Program.cs                    # Minimal API 端点注册 + /_internal/shutdown
├── Data/                         # 犬种索引种子（dogapi.dog 全量快照，离线可用）
│   ├── ApiEnvelope.cs                # 统一响应信封 + 异常包装
│   ├── Services/
│   │   ├── ExpressionEvaluator.cs    # 递归下降表达式解析
│   │   ├── WeatherService.cs         # Open-Meteo + 离线兜底
│   │   ├── TimeService.cs            # 时区 / 时间换算 / 差值
│   │   ├── UnitConverter.cs          # 9 类单位换算（含 currency 类别）
│   │   ├── ExchangeRateService.cs    # 公开汇率接口 + 内置快照兜底
│   │   ├── CatService.cs             # TheCatAPI（密钥注入、品种缓存、图片兜底）
│   │   ├── DogService.cs             # dogapi.dog（免密钥；种子+磁盘缓存+后台单航班刷新）
│   │   └── NumberFormat.cs           # 浮点显示格式化
│   ├── wwwroot/index.html            # 单文件调试页面（含启动/停止控制区）
│   └── README.md
├── ServiceController/                # 控制台应用（对外 5188）
│   ├── ServiceController.csproj      # 控制台项目 + framework reference
│   ├── Program.cs                    # 静态托管 + /control/* + /api/* 反代 + 自动开浏览器
│   ├── BackendProcess.cs             # 主进程启动 / 优雅停止 / 超时强杀 / 日志收集
│   ├── ConsoleShell.cs               # 窗口内的交互式命令（start/stop/build/logs…）
│   └── ServiceBuild.cs               # dotnet build 封装（首次启动与 build 命令共用）
├── Start-ApiPlayground.exe           # 发布好的单文件启动器，双击即用
├── .env.example / .env.local         # 密钥示例（入库）/ 本机真实密钥（已忽略）
├── start.cmd / start.sh              # 一键启动控制台窗口（已编译则跳过编译，可带 build 参数）
├── publish.cmd / publish.sh          # 重新生成上面这个 exe
├── bench.py / bench.sh               # 性能基准：keep-alive 精测 / curl 粗测
└── dist/                             # publish 原始输出目录
```

## 限制

面向本地演示，开放访问、无鉴权无限流；天气降级数据是模拟值，不可用于生产决策。猫咪接口的密钥只存本机，页面与响应都不回传，但上游图片走外部 CDN，预览依赖浏览器外网连通性。狗狗接口无需密钥，图片来自 Wikimedia Commons，二次使用请保留响应里的 attribution 署名。若部署到公网，需补充鉴权、限流与超时策略。控制台窗口就是应用本体：窗口在则服务在，关窗口全部停止，不会留下后台常驻进程；`/control/*` 仅绑定 `127.0.0.1` 且未做鉴权，不要把它暴露到公网或不可信网络。
