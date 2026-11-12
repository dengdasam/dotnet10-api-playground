using System.Globalization;

namespace ApiPlayground.Services;

public sealed record EvaluationResult(string Expression, double Result, string Display);

/// <summary>
/// 手写递归下降解析器（不 eval、不编译表达式、不引用任何第三方包）。
/// 文法：expr -&gt; term (('+'|'-') term)* ; term -&gt; power (('*'|'/') power)* ;
///       power -&gt; unary ('^' power)? ; unary -&gt; ('+'|'-') unary | primary ;
///       primary -&gt; number | '(' expr ')' | 常量 | 函数 '(' args ')'
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly string _text;
    private int _pos;

    private ExpressionEvaluator(string text) => _text = text;

    public static EvaluationResult Evaluate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ApiException("EMPTY_EXPRESSION", "表达式不能为空，例如：1+2*(3-4)/5");
        var src = expression.Trim();
        if (src.Length > 200)
            throw new ApiException("EXPRESSION_TOO_LONG", "表达式长度不能超过 200 个字符");

        var parser = new ExpressionEvaluator(src);
        var value = parser.ParseExpression();
        parser.SkipSpaces();
        if (!parser.AtEnd)
            throw new ApiException("INVALID_TOKEN", $"位置 {parser._pos + 1} 处存在无法解析的内容：「{src[parser._pos..]}」");
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ApiException("MATH_DOMAIN_ERROR", "计算结果无效（无穷大或 NaN），请检查函数定义域与除数");

        return new EvaluationResult(src, value, Num.Format(value));
    }

    private bool AtEnd => _pos >= _text.Length;
    private char Peek() => AtEnd ? '\0' : _text[_pos];
    private void SkipSpaces() { while (!AtEnd && char.IsWhiteSpace(_text[_pos])) _pos++; }

    private double ParseExpression()
    {
        var value = ParseTerm();
        while (true)
        {
            SkipSpaces();
            if (Peek() == '+') { _pos++; value += ParseTerm(); }
            else if (Peek() == '-') { _pos++; value -= ParseTerm(); }
            else break;
        }
        return value;
    }

    private double ParseTerm()
    {
        var value = ParsePower();
        while (true)
        {
            SkipSpaces();
            if (Peek() == '*') { _pos++; value *= ParsePower(); }
            else if (Peek() == '/')
            {
                _pos++;
                var divisor = ParsePower();
                if (Math.Abs(divisor) < 1e-15)
                    throw new ApiException("DIVIDE_BY_ZERO", "除数不能为零");
                value /= divisor;
            }
            else break;
        }
        return value;
    }

    // 幂运算右结合：2^3^2 = 2^(3^2) = 512
    private double ParsePower()
    {
        var b = ParseUnary();
        SkipSpaces();
        if (Peek() == '^') { _pos++; return Math.Pow(b, ParsePower()); }
        return b;
    }

    private double ParseUnary()
    {
        SkipSpaces();
        if (Peek() == '+') { _pos++; return ParseUnary(); }
        if (Peek() == '-') { _pos++; return -ParseUnary(); }
        return ParsePrimary();
    }

    private double ParsePrimary()
    {
        SkipSpaces();
        if (AtEnd)
            throw new ApiException("UNEXPECTED_END", "表达式意外结束，缺少数字或括号内容");

        var c = Peek();
        if (char.IsDigit(c) || c == '.') return ParseNumber();
        if (c == '(')
        {
            _pos++;
            var v = ParseExpression();
            SkipSpaces();
            if (Peek() != ')')
                throw new ApiException("UNMATCHED_PARENTHESIS", "缺少右括号 ')'");
            _pos++;
            return v;
        }
        if (c == ')')
            throw new ApiException("UNMATCHED_PARENTHESIS", "多余的右括号 ')'");
        if (char.IsLetter(c)) return ParseIdentifier();
        throw new ApiException("INVALID_TOKEN", $"位置 {_pos + 1} 处出现非法字符「{c}」");
    }

    private double ParseNumber()
    {
        var start = _pos;
        while (!AtEnd && (char.IsDigit(Peek()) || Peek() == '.')) _pos++;
        if (!AtEnd && (Peek() == 'e' || Peek() == 'E'))
        {
            _pos++;
            if (!AtEnd && (Peek() == '+' || Peek() == '-')) _pos++;
            while (!AtEnd && char.IsDigit(Peek())) _pos++;
        }
        var s = _text[start.._pos];
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new ApiException("INVALID_NUMBER", $"数字格式无效：「{s}」");
        return v;
    }

    private double ParseIdentifier()
    {
        var start = _pos;
        while (!AtEnd && (char.IsLetterOrDigit(Peek()) || Peek() == '_')) _pos++;
        var name = _text[start.._pos].ToLowerInvariant();
        SkipSpaces();

        if (Peek() != '(')
        {
            return name switch
            {
                "pi" or "π" => Math.PI,
                "e" => Math.E,
                "tau" or "τ" => 2 * Math.PI,
                _ => throw new ApiException("UNKNOWN_IDENTIFIER", $"未知标识符「{name}」，可用常量：pi、e、tau")
            };
        }

        _pos++; // 吃掉 '('
        var args = new List<double>();
        SkipSpaces();
        if (Peek() == ')') _pos++;
        else
        {
            while (true)
            {
                args.Add(ParseExpression());
                SkipSpaces();
                if (Peek() == ',') { _pos++; continue; }
                if (Peek() == ')') { _pos++; break; }
                throw new ApiException("INVALID_FUNCTION_CALL", $"函数 {name} 的参数列表缺少右括号 ')' 或逗号");
            }
        }
        return Apply(name, args);
    }

    private static double Apply(string name, List<double> a)
    {
        switch (name)
        {
            case "sqrt": One(a, name); if (a[0] < 0) throw Domain("sqrt 的参数不能为负数"); return Math.Sqrt(a[0]);
            case "abs": One(a, name); return Math.Abs(a[0]);
            case "sin": One(a, name); return Math.Sin(a[0]);
            case "cos": One(a, name); return Math.Cos(a[0]);
            case "tan": One(a, name); return Math.Tan(a[0]);
            case "asin": One(a, name); if (a[0] < -1 || a[0] > 1) throw Domain("asin 的参数须在 [-1, 1] 内"); return Math.Asin(a[0]);
            case "acos": One(a, name); if (a[0] < -1 || a[0] > 1) throw Domain("acos 的参数须在 [-1, 1] 内"); return Math.Acos(a[0]);
            case "atan": One(a, name); return Math.Atan(a[0]);
            case "ln": One(a, name); if (a[0] <= 0) throw Domain("ln 的参数必须大于 0"); return Math.Log(a[0]);
            case "log": One(a, name); if (a[0] <= 0) throw Domain("log 的参数必须大于 0"); return Math.Log10(a[0]);
            case "log10": One(a, name); if (a[0] <= 0) throw Domain("log10 的参数必须大于 0"); return Math.Log10(a[0]);
            case "exp": One(a, name); return Math.Exp(a[0]);
            case "floor": One(a, name); return Math.Floor(a[0]);
            case "ceil": One(a, name); return Math.Ceiling(a[0]);
            case "round": One(a, name); return Math.Round(a[0], MidpointRounding.AwayFromZero);
            case "sign": One(a, name); return Math.Sign(a[0]);
            case "deg": One(a, name); return a[0] * 180.0 / Math.PI;
            case "rad": One(a, name); return a[0] * Math.PI / 180.0;
            case "pow": Two(a, name); return Math.Pow(a[0], a[1]);
            case "min": Two(a, name); return Math.Min(a[0], a[1]);
            case "max": Two(a, name); return Math.Max(a[0], a[1]);
            case "hypot": Two(a, name); return Math.Sqrt(a[0] * a[0] + a[1] * a[1]);
            default:
                throw new ApiException("UNKNOWN_FUNCTION",
                    $"不支持的函数「{name}」。可用：sqrt, abs, sin, cos, tan, asin, acos, atan, ln, log, exp, pow, min, max, hypot, round, floor, ceil, sign, deg, rad");
        }
    }

    private static void One(List<double> a, string n)
    {
        if (a.Count != 1) throw new ApiException("BAD_ARGUMENT_COUNT", $"函数 {n} 需要 1 个参数，实际收到 {a.Count} 个");
    }

    private static void Two(List<double> a, string n)
    {
        if (a.Count != 2) throw new ApiException("BAD_ARGUMENT_COUNT", $"函数 {n} 需要 2 个参数，实际收到 {a.Count} 个");
    }

    private static ApiException Domain(string m) => new("MATH_DOMAIN_ERROR", m);
}
