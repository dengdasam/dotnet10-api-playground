"""只压本地端点（不含需要上游的 cats），避免上游超时污染对比。"""
import http.client, statistics, sys
from urllib.parse import urlsplit

BASE = sys.argv[1]; N = int(sys.argv[2])
u = urlsplit(BASE); HOST, PORT = u.hostname, u.port

EPS = [
    "/api", "/api/health", "/api/weather/cities", "/api/calculator?expression=12*(3%2B4)%2Bsqrt(81)",
    "/api/time/zones", "/api/time/now?tz=Asia/Shanghai", "/api/units/categories",
    "/api/units/convert?category=length&value=1&from=m&to=ft",
    "/api/dogs/breeds?limit=20", "/api/dogs/breeds?q=husky",
    "/api/dogs/breeds/Shiba%20Inu", "/api/dogs/groups",
]

def pct(s, p): return s[min(len(s)-1, int(len(s)*p))]

out = {}
for path in EPS:
    conn = http.client.HTTPConnection(HOST, PORT, timeout=10)
    for _ in range(20):
        conn.request("GET", path); conn.getresponse().read()
    samples = []
    for _ in range(N):
        t = __import__("time").perf_counter()
        conn.request("GET", path); r = conn.getresponse(); r.read()
        samples.append((__import__("time").perf_counter() - t) * 1000)
    conn.close()
    samples.sort()
    out[path] = (pct(samples, 0.5), pct(samples, 0.95), statistics.mean(samples))
import json; print(json.dumps(out))
