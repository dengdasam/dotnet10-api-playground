#!/usr/bin/env bash
# 简易基准测试：逐接口 N 次串行请求，输出 p50 / 平均 / 最大（毫秒）
# 用法: bash bench.sh [BASE_URL] [N]
set -u
BASE="${1:-http://127.0.0.1:5188}"
N="${2:-30}"

EPS=(
  "GET|/api"
  "GET|/api/health"
  "GET|/api/weather/cities"
  "GET|/api/calculator?expression=12*(3%2B4)%2Bsqrt(81)"
  "GET|/api/time/now?tz=Asia/Shanghai"
  "GET|/api/time/zones"
  "GET|/api/time/convert?datetime=2026-11-12T10:00&from=Asia/Shanghai&to=America/New_York"
  "GET|/api/time/diff?start=2026-11-01&end=2026-11-12"
  "GET|/api/units/categories"
  "GET|/api/units/convert?category=length&value=1&from=m&to=ft"
)

OUT="$(pwd)/.bench_body.json"
printf "%-58s %8s %8s %8s %8s\n" "endpoint" "p50" "avg" "max" "size"
for spec in "${EPS[@]}"; do
  url="${spec#*|}"
  times=""
  for i in $(seq 1 "$N"); do
    t=$(curl -s -m 10 -o "$OUT" -w "%{time_total}" --noproxy '*' "$BASE$url")
    times="$times$t\n"
  done
  size=$(wc -c < "$OUT" | tr -d ' ')
  printf "$times" | awk -v u="$url" -v sz="$size" '
    { v[NR]=$1*1000; s+=$1*1000 }
    END {
      n=NR; asort(v);
      p50 = (n%2) ? v[int(n/2)+1] : (v[n/2]+v[n/2+1])/2;
      printf "%-58s %8.2f %8.2f %8.2f %8d\n", u, p50, s/n, v[n], sz;
    }'
done
rm -f "$OUT"
