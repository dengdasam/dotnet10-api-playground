using System.Globalization;

namespace ApiPlayground.Services;

public static class Num
{
    /// <summary>去掉浮点尾巴，大数/极小数回退到科学计数法。</summary>
    public static string Format(double v)
    {
        if (Math.Abs(v) < 1e-12) return "0";
        if (Math.Abs(v) >= 1e15 || Math.Abs(v) < 1e-6)
            return v.ToString("R", CultureInfo.InvariantCulture);
        var rounded = Math.Round(v, 10, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
