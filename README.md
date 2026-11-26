# .NET 10 API Playground

一套可直接点开就用的 .NET 10 API 调试台：**自带文档站页面 + 真实上游数据 + 服务启停控制器**，零 NuGet 依赖，只用 .NET 10 SDK 自带的 ASP.NET Core。

页面地址 <http://127.0.0.1:5188/> —— 打开即可逐条试接口，每个请求都会在卡片里单独给出一块「结论区」，原始 JSON 折叠在下方。

```
┌─────────────────────────┐        ┌──────────────────────────────┐
│  浏览器 :5188           │  HTTP  │  ServiceController           │
│  调试页（静态站点）      │ ─────► │  · 托管页面                   │
│  启动/停止按钮、状态灯   │ 反代    │  · /control/{start,stop,...}  │
└─────────────────────────┘        │  · /api/* 反向代理            │
                                   │  · /openapi.json 转发         │
                                   └──────────────┬───────────────┘
                                                  │ 子进程（退出控制器即停）
                                                  ▼
                                   ┌──────────────────────────────┐
                                   │  ApiPlayground  :5180        │
                                   │  Minimal API · 34 个端点      │
                                   └──────────────────────────────┘
```

- **端口 5188**：控制器，对外唯一入口。页面、`/control/*`、`/api/*` 全走它。
- **端口 5180**：主服务，只监听本机，由控制器拉起与停止。**退出控制器（托盘菜单「退出」，或 `--console` 模式关窗口），主服务会一起退出**，不留后台常驻进程；点主窗口右上角 × 默认只最小化到托盘、服务继续跑，也可在「设置」里改成「退出并停止服务」。

## 快速开始

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（开发用 `10.0.302` 验证）。

| 方式 | 命令 | 说明 |
| --- | --- | --- |
| **单文件启动器（推荐）** | 双击 `Start-ApiPlayground.exe` | **WPF 主窗口**：出正常窗口（状态卡 / 按钮排 / 实时日志 / 「设置」），托盘同时常驻；点 × 的行为由设置决定（默认最小化到托盘）。日志写 `logs/controller-日期.log`。首次需先 `publish.cmd` 生成 |
| **命令行模式** | `Start-ApiPlayground.exe --console` | 老式交互窗口，可手打 `start` / `stop` / `build` / `logs` |
| **纯托盘回退** | `Start-ApiPlayground.exe --tray` | 回到上一版无窗口的托盘模式，右键菜单启停/编译/看日志 |
| **脚本启动** | `start.cmd`（Windows）/ `./start.sh`（Linux、WSL、macOS） | 已编译则跳过编译，直接秒启（Windows 脚本走命令行模式） |
| **强制重编译** | `start.cmd build` | 改了 C# 代码后用；主窗口点「重新编译」等效 |
| **仅主服务** | `cd ApiPlayground && dotnet run` | 不带控制器，页面请直接开 `:5180` |

启动开关：`--no-browser` / `--open-browser` 可临时覆盖「设置」里的「启动时自动打开页面」（偏好存 `logs/ui-settings.json`，默认不自动开浏览器）；`--console` / `--tray` 切换界面模式。`--tray` 与 `--console` 没有窗口，关闭行为设置对它们无效。

`publish.cmd` / `publish.sh` 会发布单文件 `Start-ApiPlayground.exe`（framework-dependent，约 340 KB，需本机装 .NET 10 运行时）。

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
| GET | `/api/dogs/breeds?q=husky&limit=20` | 犬种列表（283 个，关键字/分组/低敏过滤） |
| GET | `/api/dogs/breeds/柴犬` | 犬种详情（UUID / 英文名 / 中文名均可） |
| GET | `/api/dogs/random?breed=Pembroke+Welsh+Corgi&size=medium` | 随机犬图，附品种信息与图片署名 |
| GET | `/api/dogs/image?size=large` | 解析上游 302 图片地址 |
| GET | `/api/dogs/facts?limit=3` | 随机趣闻（上游 no-store，每次不同） |
| GET | `/api/dogs/groups` | 犬种分组统计（AKC 9 组） |
| GET | `/api/tools/uuid?version=7&count=3` | 生成 v1 / v3 / v4 / v5 / v7（1~50 个，v7 时间有序） |
| GET | `/api/tools/hash?text=&algo=sha256` | 文本哈希（md5 / sha1 / sha256 / sha512） |
| GET·POST | `/api/tools/base64?text=&mode=encode` | Base64 编解码（支持 URL 安全变体） |
| GET·POST | `/api/tools/json?text=&mode=format` | JSON 美化 / 压缩 / 校验 |
| GET | `/api/ratelimit` | 令牌桶限流演示，按 IP 计数，超额返回 429 |
| GET | `/api/stream/ticks?count=6&intervalMs=400` | SSE 流式推送（text/event-stream） |
| GET | `/api/upstreams` | 四个上游的连通性与缓存新鲜度（页面顶栏健康灯的数据源） |
| GET | `/api/logs?limit=30` | 最近请求日志（环形缓冲，含状态码与耗时） |
| GET | `/api/config` | 当前生效的运行参数与环境变量名 |
| GET | `/openapi.json` | OpenAPI 3.0 文档，可导入 Postman / Insomnia |

### POST 接口（17 个）

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/echo` | 原样回显请求体、Content-Type、请求头与字节数（自动识别 JSON / 表单 / 文本 / 二进制） |
| POST | `/api/forms/upload` | multipart 文件上传（单文件 2 MB / 合计 4 MB，返回 SHA-256，不落盘） |
| POST | `/api/tools/hash` | 同上，POST 走 JSON body 支持长文本（≤ 64 KB） |
| POST | `/api/tools/jwt` | JWT 解码（header / payload / iat / exp，**不验签**） |
| POST | `/api/tools/regex` | 正则 matches / replace / split，带 100~2000 ms 超时保护 |
| POST | `/api/tools/text` | 命名风格、大小写、URL/HTML 转义、行排序去重、字符统计 |
| POST | `/api/tools/diff` | 两段文本逐行 diff（LCS，单侧 ≤ 1000 行） |
| POST | `/api/notes` | 创建便签（**201 + Location**，支持 `Idempotency-Key` 幂等重放） |
| GET | `/api/notes?q=&tag=&limit=` | 便签列表（pinned 优先，关键字 / 标签过滤） |
| GET | `/api/notes/{id}` | 便签详情 |
| DELETE | `/api/notes/{id}` | 删除便签 |
| POST | `/api/webhook` | 接收任意 JSON 回调（记录事件名、大小、来源 IP） |
| GET | `/api/webhook/recent?limit=` | 最近 Webhook 事件（环形缓冲 50 条） |
| POST | `/api/batch` | 一次提交多条内部 GET（≤ 8 条，单条 3 s / 整体 8 s 超时） |
| POST | `/api/jobs` | 提交模拟耗时任务，**立即返回 jobId** |
| GET | `/api/jobs?limit=` | 最近任务列表与当前并发数 |
| GET | `/api/jobs/{id}` | 任务状态与进度 |

`/api/notes`、`/api/webhook`、`/api/jobs` 都是**内存存储**，进程重启即清空——它们是演示用的有状态接口，刻意不写磁盘。`/api/batch` 只允许 `/api/` 下的 GET，且不能嵌套调用自身，避免被当成任意请求转发器。所有 POST body 统一上限 64 KB（上传单独 2 MB/文件）。

调试页新增 4 个章节（13 请求体与上传 / 14 工具扩展 / 15 资源、幂等与 Webhook / 16 批量与异步），每个接口一张操作卡，可直接填参数、上传文件、连点观察幂等重放与任务进度。

工具箱端点全部本地计算、零上游；限流只作用于演示端点自身；SSE 经 5188 也是逐条实时到达（控制器代理按块 flush）。运行参数可通过环境变量覆盖，完整清单见 [`ApiPlayground/README.md`](ApiPlayground/README.md) 的「配置外置」表。

汇率走 <https://open.er-api.com> 公开接口（免 Key），以 USD 交叉换算；上游不可用时回落到内置快照，响应里 `provider` / `note` 会标明来源。狗狗接口走 <https://dogapi.dog>（官方免密钥）：283 个犬种的索引以仓库内置种子打底、后台按需刷新，冷启动不必等上游 1.84 MB 响应；图片来自 Wikimedia Commons，请保留 `attribution` 署名。

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

## 界面：WPF 主窗口（默认）、托盘回退与命令行

双击 `Start-ApiPlayground.exe` 走 **WPF 主窗口模式**：正常窗口显示状态卡（绿/灰/红状态圆点、PID、运行时长、两个地址）、按钮排（启动 / 停止 / 重新编译 / 打开页面 / 查看日志 / 日志目录 / 清空 / 复制 / 设置）、实时日志区；同时任务栏通知区保留托盘图标。

| 状态色 | 含义 |
| --- | --- |
| 绿 | 主服务运行中 |
| 灰 | 已停止 |
| 红 | 异常退出（会弹气泡提示退出码与原因） |

状态卡的圆点与托盘图标共用这组状态色。托盘图标是应用图标的**同一个图形**（开源 Lucide `plug-zap`），只换底色；exe 文件图标与窗口标题栏用深靛底。

### 设置弹窗

点主窗口「设置」或托盘菜单「设置…」打开，三项分卡片：

| 分组 | 设置项 | 说明 |
| --- | --- | --- |
| 启动 | 启动时自动打开浏览器调试页 | 默认关；`--open-browser` / `--no-browser` 可临时覆盖，下次启动生效 |
| 关闭窗口 | 最小化到托盘，服务继续运行（默认） / 退出并停止服务，释放 5188 / 5180 端口 | 点右上角 × 时的行为，**保存后立即生效** |
| 关于 | 图标来源与许可、查看许可文件 | 见下节 |

设置写入 `logs/ui-settings.json`（已被忽略）。旧配置文件没有 `ExitOnClose` 字段时自动按「最小化到托盘」处理。状态卡里的 × 行为提示会随设置同步变化，不会出现「提示与实际行为不一致」。

**点 × 的两种行为**：默认只把窗口隐藏到托盘、主服务继续运行；改成「退出并停止服务」后，点 × 会优雅停止主服务、释放端口并退出控制器。托盘菜单「退出（同时停止服务）」始终执行后者。

托盘菜单：显示主窗口、设置…、打开调试页、启动服务、停止服务、重新编译并重启、查看日志、打开日志文件夹、退出（同时停止服务）。双击托盘图标，或第二次双击 exe（`/control/show`），都会把已有窗口调回前台。

窗口里的实时日志与 `logs/controller-yyyy-MM-dd.log` 是同一份日志的两个出口（单日上限 8 MB）。主服务输出、页面触发的启停、状态变化（上线 / 离线及原因）都会记进去——窗口最小化后也能事后对时点。按钮「复制」把窗口日志送进剪贴板，「清空」只清窗口视图、不动文件。

需要交互命令时加 `--console`（`start.cmd` 已默认带上），行为与改动前一致：

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

### 图标与许可

exe 文件图标、窗口标题栏、托盘三态都用同一个开源图形：**Lucide `plug-zap`**（ISC 许可，经 [Iconify](https://iconify.design) 获取）。本项目只做白色重涂与纯色圆角底合成，许可不变；完整 ISC 文本见 `ServiceController/assets/icons/NOTICE.md`。设置弹窗「关于」里也标了图标署名，并可直接打开许可文件。

| 文件 | 用途 |
| --- | --- |
| `ServiceController/assets/icons/plug-zap.svg` | vendored 源 SVG（24×24，stroke 2） |
| `ServiceController/assets/icons/plug-zap-small.svg` | 16–32px 加粗变体（stroke 3），小尺寸不糊 |
| `ServiceController/assets/icons/NOTICE.md` | ISC 许可全文与来源 |
| `ServiceController/assets/app.ico` | 多尺寸 16–256，csproj `ApplicationIcon` → exe 文件图标 |
| `ServiceController/assets/glyph.png` | 白色线稿，嵌入程序集，运行时合成托盘三态 |
| `tools/make_icon.py` | Playwright 栅格化 → Pillow 合成，可重复生成 |

重新生成：`python tools/make_icon.py`（需 Pillow + Playwright + Chromium）。运行时由 `AppIcons.MakeIcon(color)` 画圆角底并叠线稿：深靛 = 应用，绿 / 灰 / 红 = 托盘运行中 / 已停止 / 异常。全程零运行时下载、零 NuGet。

## 设计要点

- **零 NuGet 依赖**：只用 framework reference；页面是单文件 HTML/CSS/JS，无构建步骤。主窗口与设置弹窗均为 WPF（XAML + 自定义控件模板），仅系统托盘图标复用框架自带的 WinForms `NotifyIcon`（无可见窗口/控件），早期错误提示走纯 Win32 消息框；应用图标是本地嵌入的开源 SVG 资源，不联网、不引包。
- **天气三级缓存**：按城市缓存 3 分钟 + 单航班（并发同一城市只打一次上游）+ 失败回退旧缓存，响应带 `cached` / `cacheAgeSeconds`。
- **汇率单航班 + 启动预热**：服务就绪即在后台拉一次汇率，首次换算不再等 1.3 秒。
- **猫咪接口**：品种列表 30 分钟缓存 + 单航班；最近 20 张图留作上游故障兜底（标 `stale`）；连接超时 2 秒、总超时 6 秒；密钥只从本机环境变量 / `.env.local` 读取。
- **狗狗接口**：dogapi.dog 官方免密钥。上游全量索引 1.84 MB、国内实测 15~40 秒，采用「内存索引 → 内置种子 → 7 天磁盘缓存」三层结构，冷启动毫秒级可用，后台单航班刷新；图片端点原样解析 302 的 `Location`。
- **单实例保护**：控制器与主服务各按端口加命名互斥体，重复双击不会崩，只会把已有实例的主窗口调到前台，也不会两个后端分走请求。
- **POST 实验接口**：所有 JSON body 统一 64 KB 上限与错误码（`BODY_TOO_LARGE` / `INVALID_JSON` / `INVALID_BODY`）；正则带超时保护，灾难性回溯返回 `REGEX_TIMEOUT` 而不是卡死线程；便签用 `Idempotency-Key` 做幂等重放（24 小时窗口，命中返回首次结果且不重复创建）；批量调用只允许内部 GET，并有单条 3 秒 / 整体 8 秒双超时；任务队列最多 4 并发、保留 60 条。全部内存实现，重启清空。
- **父子联动**：主服务通过 `API_PARENT_PID` 反向监视控制器，控制器被强杀也会自行退出。
- **反代正确处理 body**：转发前先读成字节数组，避免 POST 丢包；响应改为按 8 KB 块转发并逐块 flush，SSE 才能逐条实时到达浏览器。
- **请求日志中间件**：只记 `/api` 调用的一行摘要，写进固定大小的环形缓冲，不存请求体/响应体；`/api/logs` 可查。
- **OpenAPI 自生成**：从接口目录直接生成 `/openapi.json`，控制器额外转发该路径，经 5188 访问时 `servers` 自动指向 5188。

实测（keep-alive，p50）：直连 `/api/health` 约 0.28 ms，经代理约 0.37 ms；天气命中缓存 0.33 ms；并发 20 约 3600 req/s（日志开）～4500 req/s（日志关）。

> 注：这组压测数字测于 2026-11-13。之后新增了请求日志中间件与代理逐块 flush，若要引用新数字请用 `bench-local.py` 重测。

## 目录结构

```
.
├── ApiPlayground/                # 主服务（Minimal API，:5180）
│   ├── Program.cs                # 端点注册、日志、单实例、父进程守护
│   ├── ApiEnvelope.cs            # 统一响应信封
│   ├── Services/                 # 业务与工具服务
│   │   ├── CatService.cs         # TheCatAPI 封装（密钥注入、缓存、降级）
│   │   ├── DogService.cs         # dogapi.dog 封装（免密钥；种子+磁盘缓存+单航班刷新）
│   │   ├── PostBody.cs           # POST 请求体读取（64 KB 上限、统一错误码）
│   │   ├── PostLabService.cs     # echo / 上传 / JWT / 正则 / 文本变换 / diff
│   │   ├── PostStore.cs          # 便签 CRUD + Idempotency-Key + Webhook 环形缓冲
│   │   └── JobService.cs         # 异步任务队列 + 批量内部调用
│   ├── wwwroot/index.html        # 调试页（双栏文档站，单文件零依赖）
│   └── README.md                 # 主服务详细文档（接口、缓存、性能）
├── ServiceController/            # 控制器（:5188，页面 + 反代 + 进程管理）
│   ├── Program.cs                # 静态托管、/control/*（含 /control/show）、/api/* 反代、界面模式分流
│   ├── BackendProcess.cs         # 子进程启停、日志收集、崩溃判定
│   ├── IControllerUi.cs          # 界面抽象（WPF 窗口 / 托盘 / 命令行共用同一套启停逻辑）
│   ├── MainWindow.xaml           # WPF 主窗口（冷调文档感：状态卡 / 按钮排 / 实时日志 / 控件模板）
│   ├── MainWindow.xaml.cs        # 窗口展示与事件转发
│   ├── SettingsWindow.xaml(.cs)  # 设置弹窗：启动偏好 / 关闭行为 / 关于与图标许可
│   ├── WindowShell.cs            # WPF 宿主：Application + Dispatcher + 托盘 + 状态轮询 + 设置保存
│   ├── AppIcons.cs               # 从嵌入线稿合成深靛应用图标与绿/灰/红托盘三态
│   ├── UiSettings.cs             # 设置项持久化（logs/ui-settings.json）
│   ├── assets/                   # 开源图标资源：app.ico / glyph.png / icons/*.svg + NOTICE.md
│   ├── TrayShell.cs              # 纯托盘回退：图标、右键菜单、气泡提示、日志落盘
│   ├── NativeMethods.cs          # 纯 Win32 消息框（早期错误提示）
│   ├── ConsoleHost.cs            # WinExe 下按需挂控制台（--console 回退用）
│   ├── ConsoleShell.cs           # 窗口交互命令、精简日志渲染
│   └── ServiceBuild.cs           # 编译封装
├── tools/make_icon.py            # 从 vendored SVG 重新生成 app.ico 与 glyph.png
├── logs/                         # 控制器日志与设置（已被忽略）
├── .env.example                  # 密钥配置示例（占位值，可入库）
├── .env.local                    # 本机真实密钥（已被忽略，不入库）
├── start.cmd / start.sh          # 一键启动（已编译则跳过编译）
├── publish.cmd / publish.sh      # 发布单文件 Start-ApiPlayground.exe
├── bench.py                      # keep-alive 精测 + 并发吞吐基准
└── bench.sh                      # curl 粗测
```

## License

[MIT](LICENSE)
