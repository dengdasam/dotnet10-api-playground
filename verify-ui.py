import asyncio, json
from playwright.async_api import async_playwright

BASE = "http://127.0.0.1:5281/"

async def main():
    async with async_playwright() as p:
        b = await p.chromium.launch()
        ctx = await b.new_context(viewport={"width": 1400, "height": 1000})
        page = await ctx.new_page()
        errs = []
        page.on("pageerror", lambda e: errs.append(str(e)))
        page.on("console", lambda m: errs.append("console:" + m.text) if m.type == "error" else None)
        await page.goto(BASE, wait_until="networkidle")

        print("== 1. 页面初始化 ==")
        print("  endpoints:", await page.inner_text("#stat-endpoints"),
              "| tb-count:", await page.inner_text("#tb-count"),
              "| tb-status:", await page.inner_text("#tb-status"))

        print("== 2. 复制 JSON 按钮注入 ==")
        pres = await page.eval_on_selector_all('pre.result[id^="res-"]', "els => els.map(e => e.id)")
        btns = await page.eval_on_selector_all('button.copy-json', "els => els.map(e => e.id)")
        print("  结果区:", len(pres), "| 复制按钮:", len(btns), "| 一一对应:", len(pres) == len(btns))

        print("== 3. 输入框 Enter 直接发送 ==")
        await page.fill("#c-expr", "sqrt(144)+2^3")
        await page.press("#c-expr", "Enter")
        await page.wait_for_function("document.getElementById('pill-calc').textContent.indexOf('success') >= 0", timeout=8000)
        print("  pill:", await page.inner_text("#pill-calc"))
        print("  final:", await page.inner_text("#final-calc"))

        print("== 4. 复制 JSON（先有数据再复制）==")
        await page.click("#copy-calc")
        await page.wait_for_timeout(150)
        print("  按钮文案:", await page.inner_text("#copy-calc"))
        # 未调用过的接口
        await page.click("#copy-dogfacts")
        await page.wait_for_timeout(80)
        print("  未调用接口按钮文案:", await page.inner_text("#copy-dogfacts"))

        print("== 5. 防连点（并发两次 run，只应发一次）==")
        n0 = await page.evaluate("calls")
        await page.evaluate("Promise.all([run('weather'), run('weather')]).then(()=>0)")
        await page.wait_for_timeout(900)
        n1 = await page.evaluate("calls")
        print("  调用计数增量:", n1 - n0, "（期望 1）")

        print("== 6. 参数记忆：改值 -> 刷新 ==")
        await page.evaluate("document.querySelectorAll('details').forEach(d => d.open = true)")
        await page.fill("#c-expr", "7*6")
        await page.fill("#dog-q", "柯基")
        await page.select_option("#cat-size", "med")
        await page.wait_for_timeout(600)   # 等去抖 250ms 落盘
        saved = await page.evaluate("localStorage.getItem('apifields.v1')")
        print("  已存字段数:", len(json.loads(saved)) if saved else 0)
        await page.reload(wait_until="networkidle")
        await page.wait_for_timeout(500)
        print("  c-expr 恢复:", repr(await page.input_value("#c-expr")))
        print("  dog-q 恢复:", repr(await page.input_value("#dog-q")))
        print("  cat-size 恢复:", repr(await page.input_value("#cat-size")))
        print("  u-cat 恢复:", repr(await page.input_value("#u-cat")), "| u-to:", repr(await page.input_value("#u-to")))

        print("== 7. 重置参数 ==")
        await page.click("text=重置参数")
        await page.wait_for_load_state("networkidle")
        await page.wait_for_timeout(400)
        print("  重置后 c-expr:", repr(await page.input_value("#c-expr")),
              "| storage:", await page.evaluate("localStorage.getItem('apifields.v1')"))

        print("== 8. 截图 ==")
        await page.screenshot(path="preview-ui.png", full_page=False)
        print("  saved preview-ui.png")

        print("== JS 报错 ==")
        print("  ", errs if errs else "无")
        await b.close()

asyncio.run(main())
