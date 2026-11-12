using System.Globalization;

namespace ApiPlayground.Services;

public sealed record UnitDef(
    string Id,
    string Label,
    double Factor = 1.0,
    Func<double, double>? ToBase = null,
    Func<double, double>? FromBase = null);

public sealed record UnitCategory(string Id, string Label, string BaseUnit, UnitDef[] Units);

public static class UnitConverter
{
    // 线性单位：value * Factor = 基准单位；温度用 ToBase / FromBase 走开尔文中转
    private static readonly UnitCategory[] Categories =
    [
        new("length", "长度", "m", [
            new("m", "米 (m)"),
            new("km", "千米 (km)", 1000),
            new("cm", "厘米 (cm)", 0.01),
            new("mm", "毫米 (mm)", 0.001),
            new("mi", "英里 (mi)", 1609.344),
            new("yd", "码 (yd)", 0.9144),
            new("ft", "英尺 (ft)", 0.3048),
            new("in", "英寸 (in)", 0.0254),
            new("nmi", "海里 (nmi)", 1852),
            new("li", "里", 500),
            new("zhang", "丈", 10.0 / 3.0),
            new("chi", "尺", 1.0 / 3.0),
        ]),
        new("weight", "重量", "kg", [
            new("kg", "千克 (kg)"),
            new("g", "克 (g)", 0.001),
            new("mg", "毫克 (mg)", 1e-6),
            new("t", "吨 (t)", 1000),
            new("lb", "磅 (lb)", 0.45359237),
            new("oz", "盎司 (oz)", 0.028349523125),
            new("st", "英石 (st)", 6.35029318),
            new("jin", "斤", 0.5),
            new("liang", "两", 0.05),
            new("dan", "担", 50),
        ]),
        new("temperature", "温度", "K", [
            new("c", "摄氏度 (°C)", 1, v => v + 273.15, v => v - 273.15),
            new("f", "华氏度 (°F)", 1, v => (v - 32) * 5.0 / 9.0 + 273.15, v => (v - 273.15) * 9.0 / 5.0 + 32),
            new("k", "开尔文 (K)", 1, v => v, v => v),
        ]),
        new("data", "数据存储", "B", [
            new("b", "字节 (B)"),
            new("kb", "千字节 KB (10³)", 1e3),
            new("mb", "兆字节 MB (10⁶)", 1e6),
            new("gb", "吉字节 GB (10⁹)", 1e9),
            new("tb", "太字节 TB (10¹²)", 1e12),
            new("kib", "KiB (2¹⁰)", 1024),
            new("mib", "MiB (2²⁰)", 1048576),
            new("gib", "GiB (2³⁰)", 1073741824),
            new("tib", "TiB (2⁴⁰)", 1099511627776),
        ]),
        new("speed", "速度", "m/s", [
            new("ms", "米/秒 (m/s)"),
            new("kmh", "千米/时 (km/h)", 1.0 / 3.6),
            new("mph", "英里/时 (mph)", 0.44704),
            new("knot", "节 (kn)", 1852.0 / 3600.0),
            new("fts", "英尺/秒 (ft/s)", 0.3048),
            new("mach", "马赫（海平面近似）", 340.29),
        ]),
        new("area", "面积", "m²", [
            new("m2", "平方米 (m²)"),
            new("km2", "平方千米 (km²)", 1e6),
            new("cm2", "平方厘米 (cm²)", 1e-4),
            new("ha", "公顷 (ha)", 10000),
            new("mu", "亩", 2000.0 / 3.0),
            new("ft2", "平方英尺 (ft²)", 0.09290304),
            new("acre", "英亩 (acre)", 4046.8564224),
            new("mi2", "平方英里 (mi²)", 2589988.110336),
        ]),
        new("volume", "体积", "L", [
            new("l", "升 (L)"),
            new("ml", "毫升 (mL)", 0.001),
            new("m3", "立方米 (m³)", 1000),
            new("gal", "美制加仑 (gal)", 3.785411784),
            new("qt", "美制夸脱 (qt)", 0.946352946),
            new("pt", "美制品脱 (pt)", 0.473176473),
            new("cup", "美制杯 (cup)", 0.2365882365),
            new("floz", "美制液量盎司 (fl oz)", 0.0295735295625),
            new("ft3", "立方英尺 (ft³)", 28.316846592),
        ]),
        new("time", "时间", "s", [
            new("s", "秒 (s)"),
            new("ms", "毫秒 (ms)", 0.001),
            new("min", "分钟 (min)", 60),
            new("h", "小时 (h)", 3600),
            new("d", "天 (d)", 86400),
            new("wk", "周 (wk)", 604800),
        ]),
        // 汇率是动态值，不走 Factor 线性换算；这里只提供币种清单，实际数值由 ExchangeRateService 提供
        new("currency", "汇率换算", "USD",
            ExchangeRateService.Labels.Select(l => new UnitDef(l.Code.ToLowerInvariant(), l.Label)).ToArray()),
    ];

    public static object List() => Categories.Select(c => new
    {
        id = c.Id,
        label = c.Label,
        baseUnit = c.BaseUnit,
        unitCount = c.Units.Length,
        units = c.Units.Select(u => new { id = u.Id, label = u.Label }).ToArray()
    }).ToArray();

    /// <summary>统一入口：汇率取动态数据源，其余类别走本地线性换算。</summary>
    public static async Task<object> ConvertAsync(string? category, string? from, string? to, string? value)
    {
        if (string.Equals(category, "currency", StringComparison.OrdinalIgnoreCase))
            return await ConvertCurrencyAsync(from, to, value);
        return ConvertLinear(category, from, to, value);
    }

    private static async Task<object> ConvertCurrencyAsync(string? from, string? to, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            || double.IsNaN(v) || double.IsInfinity(v))
            throw new ApiException("INVALID_VALUE", "value 必须是合法数字，例如 100 或 1234.56");

        var cat = Categories.First(c => c.Id == "currency");
        var f = Find(cat, from, "from");
        var t = Find(cat, to, "to");

        var snapshot = await ExchangeRateService.GetAsync();
        var fromRate = snapshot.Rates[f.Id.ToUpperInvariant()];
        var toRate = snapshot.Rates[t.Id.ToUpperInvariant()];
        if (fromRate <= 0 || toRate <= 0)
            throw new ApiException("RATE_UNAVAILABLE", "汇率数据不可用，请稍后重试");

        // Rates[X] = 1 USD 可兑换的 X 数量；交叉汇率 = Rates[to] / Rates[from]
        var baseRate = toRate / fromRate;       // 1 from = baseRate to
        var reverseRate = fromRate / toRate;    // 1 to = reverseRate from
        var result = v * baseRate;
        if (double.IsNaN(result) || double.IsInfinity(result))
            throw new ApiException("MATH_DOMAIN_ERROR", "换算结果溢出，请检查输入值");

        var fromCode = f.Id.ToUpperInvariant();
        var toCode = t.Id.ToUpperInvariant();
        return new
        {
            category = "currency",
            categoryLabel = cat.Label,
            baseUnit = cat.BaseUnit,
            from = f.Id,
            fromLabel = f.Label,
            to = t.Id,
            toLabel = t.Label,
            input = v,
            result,
            display = Num.Format(result),
            baseRate,
            reverseRate,
            rateExpression = $"1 {fromCode} = {Num.Format(baseRate)} {toCode} · 1 {toCode} = {Num.Format(reverseRate)} {fromCode}",
            source = snapshot.Source,
            provider = snapshot.Provider,
            rateUpdatedAt = snapshot.UpdatedAt,
            note = snapshot.Note,
            formula = $"{Num.Format(v)} × ({Num.Format(toRate)} ÷ {Num.Format(fromRate)}) = {Num.Format(result)} {toCode}（以 USD 为基准交叉换算）"
        };
    }

    private static object ConvertLinear(string? category, string? from, string? to, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            || double.IsNaN(v) || double.IsInfinity(v))
            throw new ApiException("INVALID_VALUE", "value 必须是合法数字，例如 1.8 或 -40");

        var cat = Categories.FirstOrDefault(c => c.Id.Equals(category, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ApiException("INVALID_CATEGORY", $"未知换算类别「{category}」，请用 /api/units/categories 查看");
        if (cat.Id == "currency")
            throw new ApiException("UNSUPPORTED_CATEGORY", "汇率换算需走异步数据源路径");
        var f = Find(cat, from, "from");
        var t = Find(cat, to, "to");

        var baseVal = f.ToBase is not null ? f.ToBase(v) : v * f.Factor;
        var result = t.FromBase is not null ? t.FromBase(baseVal) : baseVal / t.Factor;
        if (double.IsNaN(result) || double.IsInfinity(result))
            throw new ApiException("MATH_DOMAIN_ERROR", "换算结果溢出，请检查输入值");

        var formula = cat.Id == "temperature"
            ? $"{v} {f.Id} → {Num.Format(baseVal)} K → {Num.Format(result)} {t.Id}（经基准单位中转）"
            : $"{Num.Format(v)} × {f.Factor.ToString("0.############", CultureInfo.InvariantCulture)} ÷ "
              + $"{t.Factor.ToString("0.############", CultureInfo.InvariantCulture)} = {Num.Format(result)} {t.Id}";

        return new
        {
            category = cat.Id,
            categoryLabel = cat.Label,
            baseUnit = cat.BaseUnit,
            from = f.Id,
            fromLabel = f.Label,
            to = t.Id,
            toLabel = t.Label,
            input = v,
            result,
            display = Num.Format(result),
            formula
        };
    }

    private static UnitDef Find(UnitCategory cat, string? id, string param)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ApiException("MISSING_UNIT", $"请提供 {param} 参数");
        return cat.Units.FirstOrDefault(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? throw new ApiException("INVALID_UNIT",
                   $"类别「{cat.Id}」中不存在单位「{id}」。可用：{string.Join(", ", cat.Units.Select(u => u.Id))}");
    }
}
