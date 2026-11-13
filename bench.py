"""基准测试：长连接（keep-alive）逐接口打点，输出 p50/p95/avg，支持并发吞吐测试。

用法:
  python bench.py                        # 默认 http://127.0.0.1:5188，每接口 200 次
  python bench.py http://127.0.0.1:5180 500
  python bench.py --concurrency 20
"""
import http.client
import statistics
import sys
import threading
import time
from urllib.parse import urlsplit

BASE = "http://127.0.0.1:5188"
N = 200
CONC = 0

args = [a for a in sys.argv[1:]]
i = 0
while i < len(args):
    a = args[i]
    if a == "--concurrency":
        CONC = int(args[i + 1])
        i += 2
        continue
    if a.startswith("http"):
        BASE = a
    elif a.isdigit():
        N = int(a)
    i += 1

u = urlsplit(BASE)
HOST, PORT = u.hostname, u.port or 80

EPS = [
    ("/api", None, "GET"),
    ("/api/health", None, "GET"),
    ("/api/weather/cities", None, "GET"),
    ("/api/weather?city=%E5%AE%81%E6%B3%A2", None, "GET"),
    ("/api/calculator?expression=12*(3%2B4)%2Bsqrt(81)", None, "GET"),
    ("/api/calculator", '{"expression":"(12+8)*3/4^2"}', "POST"),
    ("/api/time/now?tz=Asia/Shanghai", None, "GET"),
    ("/api/time/zones", None, "GET"),
    ("/api/time/convert?datetime=2026-11-12T10:00&from=Asia/Shanghai&to=America/New_York", None, "GET"),
    ("/api/time/diff?start=2026-11-01&end=2026-11-12", None, "GET"),
    ("/api/units/categories", None, "GET"),
    ("/api/units/convert?category=length&value=1&from=m&to=ft", None, "GET"),
    ("/api/units/convert?category=currency&value=100&from=CNY&to=USD", None, "GET"),
    ("/api/cats/breeds?limit=20", None, "GET"),
    ("/api/cats/breeds/pers", None, "GET"),
]
# 注：/api/cats/random 会真实请求上游，故意不纳入压测循环，避免压测把上游打爆。


def one(conn, path, body, method):
    conn.request(method, path, body=body, headers={"Content-Type": "application/json"} if body else {})
    r = conn.getresponse()
    data = r.read()
    return r.status, len(data)


def bench(path, body, method, n):
    conn = http.client.HTTPConnection(HOST, PORT, timeout=10)
    for _ in range(5):  # 预热
        one(conn, path, body, method)
    samples = []
    for _ in range(n):
        t0 = time.perf_counter()
        status, size = one(conn, path, body, method)
        samples.append((time.perf_counter() - t0) * 1000)
    conn.close()
    samples.sort()
    return status, size, samples


def pct(s, p):
    return s[min(len(s) - 1, int(len(s) * p))]


print(f"target={BASE}  n={N}  keep-alive")
print(f"{'endpoint':<58}{'p50':>8}{'p95':>8}{'avg':>8}{'size':>8}")
for path, body, method in EPS:
    status, size, s = bench(path, body, method, N)
    label = (method + " " + path)[:56]
    print(f"{label:<58}{pct(s,0.5):>8.2f}{pct(s,0.95):>8.2f}{statistics.mean(s):>8.2f}{size:>8}")

if CONC:
    path, body, method = "/api/health", None, "GET"
    lat, errs = [], []
    lock = threading.Lock()

    def worker(k):
        conn = http.client.HTTPConnection(HOST, PORT, timeout=10)
        for _ in range(k):
            t0 = time.perf_counter()
            try:
                one(conn, path, body, method)
                with lock:
                    lat.append((time.perf_counter() - t0) * 1000)
            except Exception as e:  # noqa: BLE001
                with lock:
                    errs.append(str(e))
        conn.close()

    per = max(1, N // CONC)
    threads = [threading.Thread(target=worker, args=(per,)) for _ in range(CONC)]
    t0 = time.perf_counter()
    [t.start() for t in threads]
    [t.join() for t in threads]
    wall = time.perf_counter() - t0
    lat.sort()
    print(f"\n并发 {CONC} × {per} 次 /api/health：总耗时 {wall*1000:.0f} ms，"
          f"吞吐 {len(lat)/wall:.0f} req/s，p50 {pct(lat,0.5):.2f} ms，p95 {pct(lat,0.95):.2f} ms，错误 {len(errs)}")
