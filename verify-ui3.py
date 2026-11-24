import asyncio
from playwright.async_api import async_playwright

BASE = "http://127.0.0.1:5281/"
FAILS = []


def check(label, cond, extra=""):
    print(("  PASS  " if cond else "  FAIL  ") + label + (("  " + str(extra)) if extra else ""))
    if not cond:
        FAILS.append(label)


async def main():
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        page = await browser.new_page(viewport={"width": 1440, "height": 1000})
        errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.on("console", lambda m: errors.append("console:" + m.text) if m.type == "error" else None)

        await page.goto(BASE, wait_until="networkidle")
        await page.wait_for_timeout(1500)

        print("== 0. 结构与顶栏 ==")
        rows = await page.locator("#ep-table tr").count()
        navs = await page.locator(".nav a").count()
        check("接口清单 34 行", rows == 34, rows)
        check("侧栏 11 个章节", navs == 11, navs)
        check("端点计数从 health 读到 34",
              await page.inner_text("#stat-endpoints") == "34",
              await page.inner_text("#stat-endpoints"))
        lit = await page.locator(".up-lights .up.on").count()
        check("四个上游灯已点亮", lit == 4, lit)
        check("上游卡片渲染 4 张", await page.locator("#up-grid .up-card").count() == 4)

        print("== 1. 工具箱 ==")
        await page.evaluate("run('uuid')")
        await page.wait_for_timeout(500)
        t = await page.inner_text("#final-uuid")
        check("UUID 摘要正确", "生成 3 个" in t, t[:50])

        await page.evaluate("run('hash')")
        await page.wait_for_timeout(500)
        t = await page.inner_text("#final-hash")
        check("哈希摘要含 sha256", "sha256" in t, t[:60])

        await page.select_option("#b64-mode", "encode")
        await page.fill("#b64-text", "你好")
        await page.evaluate("run('b64')")
        await page.wait_for_timeout(500)
        chk = await page.evaluate("lastJson.b64.data.output")
        check("Base64 编码正确", chk == "5L2g5aW9", chk)

        await page.select_option("#b64-mode", "decode")
        await page.fill("#b64-text", "5L2g5aW9")
        await page.evaluate("run('b64')")
        await page.wait_for_timeout(500)
        out = await page.evaluate("lastJson.b64.data.output")
        check("Base64 解码还原中文", out == "你好", repr(out))

        await page.select_option("#json-mode", "format")
        await page.fill("#json-text", '{"a":1,"b":[1,2]}')
        await page.evaluate("run('jsonfmt')")
        await page.wait_for_timeout(500)
        depth = await page.evaluate("lastJson.jsonfmt.data.depth")
        check("JSON 格式化返回深度 2", depth == 2, depth)

        print("== 2. 限流 ==")
        vals = []
        for _ in range(3):
            await page.evaluate("run('ratelimit')")
            await page.wait_for_timeout(320)
            vals.append(await page.evaluate("lastJson.ratelimit.data.tokensLeft"))
        check("连续调用令牌递减", vals[0] > vals[1] > vals[2], vals)
        bar = await page.evaluate("document.getElementById('lmt-fill').style.width")
        check("额度条宽度已更新", bar and bar != "0%", bar)
        txt = await page.inner_text("#lmt-txt")
        check("额度文案含剩余次数", "剩余" in txt and "/" in txt, txt)

        print("== 3. SSE 实时流 ==")
        await page.fill("#sse-count", "5")
        await page.fill("#sse-gap", "200")
        await page.click("button:has-text('开始接收')")
        await page.wait_for_timeout(800)
        early = await page.locator("#sse-log .sse-row").count()
        await page.wait_for_timeout(2500)
        late = await page.locator("#sse-log .sse-row").count()
        body = await page.inner_text("#sse-log")
        check("事件是逐条到达（不是最后一次性刷出）", 0 < early < late, f"{early} -> {late}")
        check("收到全部 5 条 tick", late >= 5, late)
        check("结束标记写明条数", "推送结束" in body and "5 条" in body, body[-60:].replace("\n", " "))

        print("== 4. 运维 ==")
        await page.evaluate("run('upstreams')")
        await page.wait_for_timeout(600)
        check("上游网格仍有 4 张卡", await page.locator("#up-grid .up-card").count() == 4)
        cards = await page.locator("#up-grid .up-card .d").all_inner_texts()
        check("卡片里有缓存/索引细节", any(("缓存" in c or "索引" in c) for c in cards), cards[0][:60])

        await page.fill("#logs-limit", "15")
        await page.evaluate("run('logs')")
        await page.wait_for_timeout(600)
        n_log = await page.locator("#logs-body tr").count()
        check("日志表出现记录", n_log > 3, n_log)
        first_row = await page.inner_text("#logs-body tr:first-child")
        check("日志行含方法/路径/状态", "GET" in first_row or "POST" in first_row, first_row[:70])

        await page.evaluate("run('config')")
        await page.wait_for_timeout(500)
        cfg = await page.inner_text("#final-config")
        check("配置摘要含限流与缓存", "限流" in cfg and "缓存" in cfg, cfg[:70])

        print("== 5. 历史 / 复制语言 / 无报错 ==")
        hist = await page.locator("#hist-list .hist-item").count()
        check("请求历史已累计多条", hist >= 5, hist)
        kinds = await page.locator("button[onclick^='copyCode(']").all_inner_texts()
        check("新卡片也有复制按钮", len(kinds) >= 19, len(kinds))

        real = [e for e in errors if "control/status" not in e and "404" not in e]
        check("无 JS 异常", not real, real[:3])

        await page.evaluate("window.scrollTo(0,0)")
        await page.wait_for_timeout(300)
        await page.screenshot(path="preview-tools.png")
        await page.evaluate("document.getElementById('sec10').scrollIntoView()")
        await page.wait_for_timeout(400)
        await page.screenshot(path="preview-stream.png")
        await browser.close()

    print("\n结果：" + ("全部通过" if not FAILS else f"{len(FAILS)} 项失败 -> {FAILS}"))


asyncio.run(main())
