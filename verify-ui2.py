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
        ctx = await browser.new_context(viewport={"width": 1400, "height": 1000})
        await ctx.grant_permissions(["clipboard-read", "clipboard-write"],
                                    origin="http://127.0.0.1:5281")
        page = await ctx.new_page()
        errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.on("console", lambda m: errors.append("console:" + m.text) if m.type == "error" else None)

        await page.goto(BASE, wait_until="networkidle")
        await page.wait_for_timeout(1200)

        print("== 0. 初始化 ==")
        rows0 = await page.locator("#ep-table tr").count()
        chips = await page.locator("#ep-chips button").count()
        check("接口清单渲染 22 行", rows0 == 22, rows0)
        check("模块快筛按钮 = 1 全部 + N 模块", chips >= 5, chips)
        check("工具栏语言下拉已填充 5 项",
              await page.locator("#code-lang option").count() == 5)

        print("== 1. A5 接口清单过滤 ==")
        await page.fill("#ep-q", "dog")
        await page.wait_for_timeout(200)
        rows_dog = await page.locator("#ep-table tr").count()
        hit = await page.inner_text("#ep-hit")
        check("关键字 dog 收窄结果", 0 < rows_dog < 22, f"{rows_dog} 行 / {hit}")
        await page.fill("#ep-q", "zzzz-not-exist")
        await page.wait_for_timeout(200)
        check("无匹配时给出提示行",
              await page.locator("#ep-table tr.no-hit").count() == 1)
        await page.click("text=清空过滤")
        await page.wait_for_timeout(200)
        check("清空后恢复 22 行",
              await page.locator("#ep-table tr").count() == 22)

        # 模块 chip 过滤
        chip_labels = await page.locator("#ep-chips button").all_inner_texts()
        target = next((c for c in chip_labels if "猫" in c or "dog" in c.lower()), chip_labels[-1])
        await page.click(f"#ep-chips button:has-text('{target}')")
        await page.wait_for_timeout(200)
        rows_sec = await page.locator("#ep-table tr").count()
        check(f"模块「{target}」筛选生效", 0 < rows_sec < 22, f"{rows_sec} 行")
        await page.click("text=清空过滤")
        await page.wait_for_timeout(200)

        print("== 2. A1 请求历史 ==")
        await page.evaluate("run('health')")
        await page.wait_for_timeout(600)
        await page.fill("#c-expr", "2^10")
        await page.evaluate("run('calc')")
        await page.wait_for_timeout(600)
        n_hist = await page.locator("#hist-list .hist-item").count()
        first = await page.locator("#hist-list .hist-item").first.inner_text()
        check("历史记录 2 条", n_hist == 2, n_hist)
        check("最新一条在最上面（calc）", "/api/calculator" in first, first.strip())

        # 改掉参数后点历史，应回填旧值并重放
        await page.fill("#c-expr", "999")
        await page.locator("#hist-list .hist-item").first.click()
        await page.wait_for_timeout(900)
        expr_now = await page.input_value("#c-expr")
        calc_txt = await page.inner_text("#final-calc")
        check("点历史回填了当时的参数", expr_now == "2^10", expr_now)
        check("重放结果正确（2^10 = 1024）", "1024" in calc_txt, calc_txt[:60])
        check("重放后又记一条", await page.locator("#hist-list .hist-item").count() == 3)

        print("== 3. A3 多语言代码片段 ==")
        labels = await page.locator("button[onclick^='copyCode(']").all_inner_texts()
        check("默认按钮文案为「复制 cURL」",
              all("cURL" in t for t in labels), labels[:3])
        await page.select_option("#code-lang", "python")
        await page.wait_for_timeout(200)
        labels2 = await page.locator("button[onclick^='copyCode(']").all_inner_texts()
        check("切到 Python 后文案全部更新",
              all("Python" in t for t in labels2), labels2[:3])

        await page.evaluate("copyCode('tnow', document.querySelector(\"button[onclick^='copyCode(']\"))")
        await page.wait_for_timeout(400)
        clip = await page.evaluate("navigator.clipboard.readText()")
        check("剪贴板是 Python 片段", "requests.get" in clip, clip.splitlines()[0] if clip else "")

        await page.select_option("#code-lang", "csharp")
        await page.wait_for_timeout(150)
        await page.evaluate("copyCode('calc', null)")
        await page.wait_for_timeout(300)
        clip2 = await page.evaluate("navigator.clipboard.readText()")
        check("C# 片段含 HttpClient 与 PostAsync",
              "HttpClient" in clip2 and "PostAsync" in clip2, clip2.splitlines()[0] if clip2 else "")

        await page.select_option("#code-lang", "pwsh")
        await page.wait_for_timeout(150)
        await page.evaluate("copyCode('weather', null)")
        await page.wait_for_timeout(300)
        clip3 = await page.evaluate("navigator.clipboard.readText()")
        check("PowerShell 片段含 Invoke-RestMethod",
              "Invoke-RestMethod" in clip3, clip3[:60])

        # 刷新后语言保持
        await page.reload(wait_until="networkidle")
        await page.wait_for_timeout(1000)
        check("刷新后语言仍是 PowerShell",
              await page.input_value("#code-lang") == "pwsh")

        print("== 4. A7 失败一键重试 ==")
        await page.fill("#c-expr", "1/0")
        await page.evaluate("run('calc')")
        await page.wait_for_timeout(800)
        check("失败时出现「重试」按钮",
              await page.locator("#final-calc .retry").count() == 1)
        before = int(await page.inner_text("#hist-count"))
        await page.fill("#c-expr", "6*7")
        await page.locator("#final-calc .retry").click()
        await page.wait_for_timeout(900)
        after_txt = await page.inner_text("#final-calc")
        check("点重试后按当前参数重发并成功", "42" in after_txt, after_txt[:50])
        check("重试也记入历史", int(await page.inner_text("#hist-count")) == min(before + 1, 12))

        print("== 5. 无 JS 报错 ==")
        real = [e for e in errors if "control/status" not in e and "404" not in e]
        check("页面无 JS 异常", not real, real[:3])

        await page.screenshot(path="preview-optim.png", full_page=False)
        await browser.close()

    print("\n结果：" + ("全部通过" if not FAILS else f"{len(FAILS)} 项失败 -> {FAILS}"))


asyncio.run(main())
