using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UtilMaster3000;

// ======================================================
// ADVANCED CALCULATOR
//   2+2*3   (4-1)^2   17%5   5!   50% of 80   100+10%
//   5squared   6cubed   3root(27)   root(27,3)   sqrt(2)   √9
//   sin(30deg)   ln(e)   log(1000)   log(8,2)   2pi   3(4+5)
//   5 plus 3 times 2   10 divided by 4   2 to the power of 8
//   sum(1,2,3)   avg(4,8)   gcd(12,18)   10 choose 3   x=5 then x*2
//   ans = the previous result
// ======================================================

internal sealed class CalcException : Exception
{
    public CalcException(string message) : base(message) { }
}

internal static class Calc
{
    /// <summary>Angles for sin/cos/tan are degrees instead of radians.</summary>
    public static bool Degrees { get; set; }

    public static double Ans { get; private set; }

    public static readonly List<(string Expression, string Result)> History = new();

    private static readonly Dictionary<string, double> Variables = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearHistory() => History.Clear();

    // ---------- used by the home search box ----------

    /// <summary>
    /// For the search box: only answers when the text really is a
    /// calculation (an operator, function, or similar), never a bare word.
    /// </summary>
    public static bool TryEvaluate(string input, out string result)
    {
        result = "";

        string text = input.Trim();

        if (text.Length < 3 || text.Contains('='))
            return false;

        if (!text.Any(char.IsDigit) && !Regex.IsMatch(text, @"π|\bpi\b|\btau\b|\bphi\b", RegexOptions.IgnoreCase))
            return false;

        try
        {
            Parser p = new(text, remember: false);
            double value = p.Run();

            if (!p.UsedOperation || double.IsNaN(value) || double.IsInfinity(value))
                return false;

            result = Format(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- used by the calculator page ----------

    /// <summary>Evaluates anything; the error says what is wrong.</summary>
    public static double Evaluate(string input, out string? error, out string? assignedTo)
    {
        error = null;
        assignedTo = null;

        try
        {
            string text = input.Trim();
            Match assign = Regex.Match(text, @"^([A-Za-z_][A-Za-z_0-9]*)\s*=\s*(.+)$");

            if (assign.Success && !Parser.IsKnownName(assign.Groups[1].Value))
            {
                assignedTo = assign.Groups[1].Value;
                text = assign.Groups[2].Value;
            }

            Parser p = new(text, remember: true);
            double value = p.Run();

            if (double.IsNaN(value))
                throw new CalcException("That is not a number");

            if (double.IsInfinity(value))
                throw new CalcException("Too big (or divided by zero)");

            if (assignedTo != null)
                Variables[assignedTo] = value;

            return value;
        }
        catch (CalcException ex)
        {
            error = ex.Message;
        }
        catch (Exception)
        {
            error = "Couldn't read that";
        }

        return double.NaN;
    }

    public static void Remember(string expression, double value)
    {
        Ans = value;

        if (History.Count > 0 && History[0].Expression == expression)
            return;

        History.Insert(0, (expression, Format(value)));

        if (History.Count > 20)
            History.RemoveAt(History.Count - 1);
    }

    public static string VariablesText() =>
        string.Join(",  ", Variables.Select(v => $"{v.Key} = {Format(v.Value)}"));

    // ---------- formatting ----------

    public static string Format(double v)
    {
        if (v == 0)
            return "0";

        double abs = Math.Abs(v);

        if (abs >= 1e15 || abs < 1e-9)
        {
            string s = v.ToString("0.###########E+0", CultureInfo.InvariantCulture);
            return s;
        }

        string r = Math.Round(v, 10).ToString("0.##########", CultureInfo.InvariantCulture);
        return r == "-0" ? "0" : r;
    }

    /// <summary>Other ways to read the same value (hex, binary, a fraction...).</summary>
    public static List<string> Extras(double v)
    {
        List<string> list = new();

        if (v == Math.Floor(v) && Math.Abs(v) < 9e15)
        {
            long n = (long)v;

            if (Math.Abs(n) >= 1000)
                list.Add("Thousands: " + n.ToString("N0", CultureInfo.InvariantCulture));

            if (n >= 0)
            {
                list.Add("Hex: 0x" + n.ToString("X"));
                list.Add("Binary: 0b" + Convert.ToString(n, 2));
            }
        }
        else if (Math.Abs(v) < 1e9)
        {
            string? fraction = AsFraction(v);

            if (fraction != null)
                list.Add("Fraction: " + fraction);
        }

        if (v != 0 && (Math.Abs(v) >= 1e6 || Math.Abs(v) < 1e-3))
            list.Add("Scientific: " + v.ToString("0.#######E+0", CultureInfo.InvariantCulture));

        return list;
    }

    private static string? AsFraction(double value)
    {
        double x = Math.Abs(value);
        long h1 = 1, h0 = 0, k1 = 0, k0 = 1;
        double b = x;

        for (int i = 0; i < 20; i++)
        {
            long a = (long)Math.Floor(b);
            long h2 = a * h1 + h0;
            long k2 = a * k1 + k0;

            h0 = h1; h1 = h2; k0 = k1; k1 = k2;

            if (k1 > 2000)
                return null;

            if (Math.Abs(x - (double)h1 / k1) < 1e-10)
                return (value < 0 ? "-" : "") + $"{h1}/{k1}";

            double frac = b - a;

            if (frac < 1e-12)
                break;

            b = 1 / frac;
        }

        return null;
    }

    // ---------- the parser ----------

    private sealed class Parser
    {
        private enum Kind { Num, Id, Op, End }

        private readonly record struct Token(Kind Kind, string Text, double Value);

        private readonly List<Token> tokens = new();
        private int at;

        public bool UsedOperation;

        private static readonly HashSet<string> Constants = new(StringComparer.OrdinalIgnoreCase)
            { "pi", "e", "tau", "phi", "ans" };

        private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
        {
            "plus", "minus", "times", "over", "divided", "by", "mod", "of", "to", "the", "power",
            "squared", "cubed", "deg", "degrees", "rad", "radians", "x", "choose", "and", "percent"
        };

        public static bool IsKnownName(string name) =>
            Constants.Contains(name) ||
            (Words.Contains(name) && !name.Equals("x", StringComparison.OrdinalIgnoreCase)) ||
            Functions.ContainsKey(name);

        public Parser(string text, bool remember)
        {
            Tokenize(text);
        }

        public double Run()
        {
            double v = Sum();

            if (Peek.Kind != Kind.End)
                throw new CalcException("Couldn't read: " + Peek.Text);

            return v;
        }

        // ----- tokens -----

        private Token Peek => at < tokens.Count ? tokens[at] : new Token(Kind.End, "", 0);

        private Token PeekAt(int offset) =>
            at + offset < tokens.Count ? tokens[at + offset] : new Token(Kind.End, "", 0);

        private Token Next() => tokens[at++];

        private bool IsOp(string text) => Peek.Kind == Kind.Op && Peek.Text == text;

        private bool IsWord(string text) =>
            Peek.Kind == Kind.Id && Peek.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

        private bool TakeOp(string text)
        {
            if (!IsOp(text))
                return false;

            at++;
            return true;
        }

        private bool TakeWord(string text)
        {
            if (!IsWord(text))
                return false;

            at++;
            return true;
        }

        private void Tokenize(string s)
        {
            s = s.Replace('−', '-').Replace('×', '*').Replace('÷', '/').Replace('·', '*');

            int i = 0;

            while (i < s.Length)
            {
                char c = s[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    tokens.Add(ReadNumber(s, ref i));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;

                    while (i < s.Length && (char.IsLetter(s[i]) || s[i] == '_' || (char.IsDigit(s[i]) && i > start)))
                    {
                        // letters followed by digits: log2, log10, atan2 stay together
                        i++;
                    }

                    string word = s.Substring(start, i - start);

                    // "π" is a letter in .NET: a lone constant
                    tokens.Add(new Token(Kind.Id, word, 0));
                    continue;
                }

                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    tokens.Add(new Token(Kind.Op, "^", 0));
                    i += 2;
                    continue;
                }

                if (c == '²') { tokens.Add(new Token(Kind.Id, "squared", 0)); i++; continue; }
                if (c == '³') { tokens.Add(new Token(Kind.Id, "cubed", 0)); i++; continue; }
                if (c == '°') { tokens.Add(new Token(Kind.Id, "deg", 0)); i++; continue; }

                if ("+-*/^()%!,√∛".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token(Kind.Op, c.ToString(), 0));
                    i++;
                    continue;
                }

                throw new CalcException("Unknown symbol: " + c);
            }
        }

        private static Token ReadNumber(string s, ref int i)
        {
            if (s[i] == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            {
                int start = i + 2;
                int j = start;

                while (j < s.Length && Uri.IsHexDigit(s[j]))
                    j++;

                if (j > start)
                {
                    i = j;
                    return new Token(Kind.Num, "", Convert.ToInt64(s.Substring(start, j - start), 16));
                }
            }

            if (s[i] == '0' && i + 1 < s.Length && (s[i + 1] == 'b' || s[i + 1] == 'B') &&
                i + 2 < s.Length && (s[i + 2] == '0' || s[i + 2] == '1'))
            {
                int start = i + 2;
                int j = start;

                while (j < s.Length && (s[j] == '0' || s[j] == '1'))
                    j++;

                i = j;
                return new Token(Kind.Num, "", Convert.ToInt64(s.Substring(start, j - start), 2));
            }

            int from = i;

            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '_'))
                i++;

            // 1e5 / 2.5e-3 (but "2e" alone is 2 times e)
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                int j = i + 1;

                if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                    j++;

                if (j < s.Length && char.IsDigit(s[j]))
                {
                    while (j < s.Length && char.IsDigit(s[j]))
                        j++;

                    i = j;
                }
            }

            string text = s.Substring(from, i - from).Replace("_", "");

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new CalcException("Bad number: " + text);

            return new Token(Kind.Num, "", value);
        }

        // ----- grammar -----

        // a + b - c  (and: 100 + 10%  means  100 + 10% of 100)
        private double Sum()
        {
            double v = Term();

            while (true)
            {
                bool plus = IsOp("+") || IsWord("plus");
                bool minus = IsOp("-") || IsWord("minus");

                if (!plus && !minus)
                    return v;

                at++;
                UsedOperation = true;

                double right = Term(out bool wasPercent);

                if (wasPercent)
                    right = v * right;

                v = plus ? v + right : v - right;
            }
        }

        private double Term() => Term(out _);

        private double Term(out bool onlyPercent)
        {
            int startAt = at;
            double v = Power(out bool pct);
            bool single = pct;

            while (true)
            {
                if (IsOp("*") || IsWord("times") || IsWord("x") && StartsOperand(1))
                {
                    at++;
                    v *= Power();
                    single = false;
                    UsedOperation = true;
                }
                else if (IsOp("/") || IsWord("over"))
                {
                    at++;
                    double d = Power();

                    if (d == 0)
                        throw new CalcException("Can't divide by zero");

                    v /= d;
                    single = false;
                    UsedOperation = true;
                }
                else if (IsWord("divided") && PeekAt(1).Kind == Kind.Id &&
                         PeekAt(1).Text.Equals("by", StringComparison.OrdinalIgnoreCase))
                {
                    at += 2;
                    double d = Power();

                    if (d == 0)
                        throw new CalcException("Can't divide by zero");

                    v /= d;
                    single = false;
                    UsedOperation = true;
                }
                else if (IsWord("mod") || (IsOp("%") && StartsOperand(1)))
                {
                    at++;
                    double d = Power();

                    if (d == 0)
                        throw new CalcException("Can't divide by zero");

                    v %= d;
                    single = false;
                    UsedOperation = true;
                }
                else if (IsWord("of"))
                {
                    // 20% of 150
                    at++;
                    v *= Power();
                    single = false;
                    UsedOperation = true;
                }
                else if (IsWord("choose"))
                {
                    at++;
                    v = NCr(v, Power());
                    single = false;
                    UsedOperation = true;
                }
                else if (StartsImplicit())
                {
                    // 2pi, 3(4+5), 2sqrt(9)
                    v *= Power();
                    single = false;
                    UsedOperation = true;
                }
                else
                {
                    onlyPercent = single && at > startAt;
                    return v;
                }
            }
        }

        private bool StartsOperand(int offset)
        {
            Token t = PeekAt(offset);

            return t.Kind == Kind.Num ||
                   (t.Kind == Kind.Op && (t.Text is "(" or "√" or "∛" or "-" or "+")) ||
                   (t.Kind == Kind.Id && !Words.Contains(t.Text));
        }

        private bool StartsImplicit()
        {
            Token t = Peek;

            if (t.Kind == Kind.Op)
                return t.Text is "(" or "√" or "∛";

            return t.Kind == Kind.Id && !Words.Contains(t.Text);
        }

        // a ^ b  (right to left), also "to the power of"
        private double Power() => Power(out _);

        private double Power(out bool isPercent)
        {
            // -2^2 is -(2^2)
            if (TakeOp("-"))
            {
                UsedOperation = true;
                double neg = -Power(out _);
                isPercent = false;
                return neg;
            }

            double b = Unary(out isPercent);

            if (IsOp("^"))
            {
                at++;
                UsedOperation = true;
                isPercent = false;
                return Math.Pow(b, Unary(out _, allowPowerChain: true));
            }

            if (IsWord("to") && PeekAt(1).Kind == Kind.Id &&
                PeekAt(1).Text.Equals("the", StringComparison.OrdinalIgnoreCase) &&
                PeekAt(2).Kind == Kind.Id &&
                PeekAt(2).Text.Equals("power", StringComparison.OrdinalIgnoreCase))
            {
                at += 3;
                TakeWord("of");
                UsedOperation = true;
                isPercent = false;
                return Math.Pow(b, Unary(out _, allowPowerChain: true));
            }

            if (IsWord("power") && PeekAt(1).Kind == Kind.Id &&
                PeekAt(1).Text.Equals("of", StringComparison.OrdinalIgnoreCase))
            {
                at += 2;
                UsedOperation = true;
                isPercent = false;
                return Math.Pow(b, Unary(out _, allowPowerChain: true));
            }

            return b;
        }

        // -x, +x, then x with its suffixes (!, %, squared, cubed, deg...)
        private double Unary(out bool isPercent, bool allowPowerChain = false)
        {
            isPercent = false;

            if (TakeOp("-"))
            {
                UsedOperation = true;
                return -Unary(out _, allowPowerChain);
            }

            if (TakeOp("+"))
                return Unary(out isPercent, allowPowerChain);

            double v = Postfix(out isPercent);

            // 2^3^2 = 2^(3^2)
            if (allowPowerChain && IsOp("^"))
            {
                at++;
                UsedOperation = true;
                isPercent = false;
                return Math.Pow(v, Unary(out _, allowPowerChain: true));
            }

            return v;
        }

        private double Postfix(out bool isPercent)
        {
            isPercent = false;
            double v = Primary();

            while (true)
            {
                if (TakeOp("!"))
                {
                    v = Factorial(v);
                    UsedOperation = true;
                }
                else if (IsOp("%") && !StartsOperand(1))
                {
                    at++;
                    v /= 100;
                    isPercent = true;
                    UsedOperation = true;
                }
                else if (TakeWord("percent"))
                {
                    v /= 100;
                    isPercent = true;
                    UsedOperation = true;
                }
                else if (TakeWord("squared"))
                {
                    v *= v;
                    UsedOperation = true;
                }
                else if (TakeWord("cubed"))
                {
                    v = v * v * v;
                    UsedOperation = true;
                }
                else if (TakeWord("deg") || TakeWord("degrees"))
                {
                    v = v * Math.PI / 180;
                    UsedOperation = true;
                }
                else if (TakeWord("rad") || TakeWord("radians"))
                {
                    UsedOperation = true;
                }
                else
                {
                    return v;
                }
            }
        }

        private double Primary()
        {
            Token t = Peek;

            if (t.Kind == Kind.Num)
            {
                at++;

                // 3root(27): the 3rd root of 27
                if (Peek.Kind == Kind.Id && IsRootWord(Peek.Text) && PeekAt(1).Kind == Kind.Op && PeekAt(1).Text == "(")
                {
                    at++;
                    List<double> args = Args();
                    UsedOperation = true;

                    if (args.Count != 1)
                        throw new CalcException("root(...) takes one number after the degree");

                    return NthRoot(args[0], t.Value);
                }

                return t.Value;
            }

            if (t.Kind == Kind.Op)
            {
                if (t.Text == "(")
                {
                    at++;
                    double v = Sum();

                    if (!TakeOp(")"))
                        throw new CalcException("Missing )");

                    return v;
                }

                if (t.Text is "√" or "∛")
                {
                    at++;
                    UsedOperation = true;
                    double operand = IsOp("(") ? Primary() : Postfix(out _);
                    return t.Text == "√" ? Sqrt(operand) : Math.Cbrt(operand);
                }

                throw new CalcException("Unexpected " + t.Text);
            }

            if (t.Kind == Kind.Id)
            {
                string name = t.Text.ToLowerInvariant();

                if (name is "pi" or "π") { at++; UsedOperation = true; return Math.PI; }
                if (name == "tau") { at++; UsedOperation = true; return Math.Tau; }
                if (name == "phi") { at++; UsedOperation = true; return (1 + Math.Sqrt(5)) / 2; }

                // "e" is Euler's number, but 2e3 was read as a number above.
                if (name == "e" && !(PeekAt(1).Kind == Kind.Op && PeekAt(1).Text == "("))
                {
                    at++;
                    UsedOperation = true;
                    return Math.E;
                }

                if (name == "ans") { at++; UsedOperation = true; return Ans; }

                if (Variables.TryGetValue(t.Text, out double stored))
                {
                    at++;
                    UsedOperation = true;
                    return stored;
                }

                // xroot(5): an unknown degree means the square root
                if (name == "xroot")
                {
                    if (PeekAt(1).Kind == Kind.Op && PeekAt(1).Text == "(")
                    {
                        at++;
                        List<double> a = Args();
                        UsedOperation = true;
                        return a.Count == 2 ? NthRoot(a[0], a[1]) : Sqrt(a[0]);
                    }
                }

                if (Functions.TryGetValue(name, out FuncInfo? f))
                {
                    at++;
                    UsedOperation = true;

                    List<double> args;

                    if (IsOp("("))
                    {
                        args = Args();
                    }
                    else
                    {
                        // sqrt 16, sin 30
                        args = new List<double> { Postfix(out _) };
                    }

                    if (args.Count < f.Min || args.Count > f.Max)
                        throw new CalcException($"{name}() takes {(f.Min == f.Max ? f.Min.ToString() : f.Min + "-" + (f.Max == int.MaxValue ? "many" : f.Max.ToString()))} value(s)");

                    return f.Run(args);
                }

                throw new CalcException("Unknown: " + t.Text);
            }

            throw new CalcException("Incomplete");
        }

        private List<double> Args()
        {
            if (!TakeOp("("))
                throw new CalcException("Missing (");

            List<double> list = new();

            if (TakeOp(")"))
                return list;

            do
            {
                list.Add(Sum());
            }
            while (TakeOp(","));

            if (!TakeOp(")"))
                throw new CalcException("Missing )");

            return list;
        }

        private static bool IsRootWord(string w) =>
            w.Equals("root", StringComparison.OrdinalIgnoreCase) ||
            w.Equals("rt", StringComparison.OrdinalIgnoreCase) ||
            w.Equals("xroot", StringComparison.OrdinalIgnoreCase);

        // ----- maths -----

        private static double Sqrt(double v)
        {
            if (v < 0)
                throw new CalcException("No real square root of a negative number");

            return Math.Sqrt(v);
        }

        private static double NthRoot(double value, double degree)
        {
            if (degree == 0)
                throw new CalcException("The root degree can't be 0");

            if (value < 0)
            {
                if (degree == Math.Floor(degree) && Math.Abs(degree) % 2 == 1)
                    return -Math.Pow(-value, 1 / degree);

                throw new CalcException("No real root of a negative number");
            }

            return Math.Pow(value, 1 / degree);
        }

        private static double Factorial(double v)
        {
            if (v < 0 || v != Math.Floor(v))
                throw new CalcException("Factorial needs a whole number, 0 or more");

            if (v > 170)
                throw new CalcException("Too big");

            double r = 1;

            for (int i = 2; i <= (int)v; i++)
                r *= i;

            return r;
        }

        private static double NCr(double n, double k)
        {
            if (n < 0 || k < 0 || k > n || n != Math.Floor(n) || k != Math.Floor(k))
                throw new CalcException("choose needs whole numbers, with k <= n");

            k = Math.Min(k, n - k);
            double r = 1;

            for (int i = 1; i <= (int)k; i++)
                r = r * (n - k + i) / i;

            return Math.Round(r);
        }

        private static double NPr(double n, double k)
        {
            if (n < 0 || k < 0 || k > n || n != Math.Floor(n) || k != Math.Floor(k))
                throw new CalcException("nPr needs whole numbers, with k <= n");

            double r = 1;

            for (int i = 0; i < (int)k; i++)
                r *= n - i;

            return r;
        }

        private static double Gcd(double a, double b)
        {
            a = Math.Abs(Math.Round(a));
            b = Math.Abs(Math.Round(b));

            while (b != 0)
                (a, b) = (b, a % b);

            return a;
        }

        private static double Angle(double v) => Degrees ? v * Math.PI / 180 : v;
        private static double FromAngle(double v) => Degrees ? v * 180 / Math.PI : v;

        private static double Log(double v, double b = 10)
        {
            if (v <= 0)
                throw new CalcException("log needs a number above 0");

            return Math.Log(v) / Math.Log(b);
        }

        private sealed record FuncInfo(int Min, int Max, Func<List<double>, double> Run);

        private static FuncInfo F1(Func<double, double> f) => new(1, 1, a => f(a[0]));
        private static FuncInfo F2(Func<double, double, double> f) => new(2, 2, a => f(a[0], a[1]));

        private static readonly Random Rng = new();

        private static readonly Dictionary<string, FuncInfo> Functions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["sqrt"] = F1(Sqrt),
                ["cbrt"] = F1(Math.Cbrt),
                ["root"] = new(1, 2, a => a.Count == 2 ? NthRoot(a[0], a[1]) : Sqrt(a[0])),
                ["abs"] = F1(Math.Abs),
                ["sign"] = F1(v => Math.Sign(v)),
                ["floor"] = F1(Math.Floor),
                ["ceil"] = F1(Math.Ceiling),
                ["trunc"] = F1(Math.Truncate),
                ["round"] = new(1, 2, a => a.Count == 2
                    ? Math.Round(a[0], (int)Math.Clamp(a[1], 0, 15), MidpointRounding.AwayFromZero)
                    : Math.Round(a[0], MidpointRounding.AwayFromZero)),
                ["sin"] = F1(v => Math.Sin(Angle(v))),
                ["cos"] = F1(v => Math.Cos(Angle(v))),
                ["tan"] = F1(v => Math.Tan(Angle(v))),
                ["asin"] = F1(v => FromAngle(Math.Asin(v))),
                ["acos"] = F1(v => FromAngle(Math.Acos(v))),
                ["atan"] = F1(v => FromAngle(Math.Atan(v))),
                ["atan2"] = F2((y, x) => FromAngle(Math.Atan2(y, x))),
                ["sinh"] = F1(Math.Sinh),
                ["cosh"] = F1(Math.Cosh),
                ["tanh"] = F1(Math.Tanh),
                ["ln"] = F1(v => Log(v, Math.E)),
                ["log"] = new(1, 2, a => a.Count == 2 ? Log(a[0], a[1]) : Log(a[0])),
                ["log10"] = F1(v => Log(v)),
                ["log2"] = F1(v => Log(v, 2)),
                ["exp"] = F1(Math.Exp),
                ["pow"] = F2(Math.Pow),
                ["mod"] = F2((a, b) => b == 0 ? throw new CalcException("Can't divide by zero") : a % b),
                ["hypot"] = F2((a, b) => Math.Sqrt(a * a + b * b)),
                ["min"] = new(1, int.MaxValue, a => a.Min()),
                ["max"] = new(1, int.MaxValue, a => a.Max()),
                ["sum"] = new(1, int.MaxValue, a => a.Sum()),
                ["avg"] = new(1, int.MaxValue, a => a.Average()),
                ["mean"] = new(1, int.MaxValue, a => a.Average()),
                ["median"] = new(1, int.MaxValue, a =>
                {
                    var s = a.OrderBy(x => x).ToList();
                    int m = s.Count / 2;
                    return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
                }),
                ["gcd"] = F2(Gcd),
                ["lcm"] = F2((a, b) => a == 0 || b == 0 ? 0 : Math.Abs(Math.Round(a) * Math.Round(b)) / Gcd(a, b)),
                ["fact"] = F1(Factorial),
                ["factorial"] = F1(Factorial),
                ["ncr"] = F2(NCr),
                ["choose"] = F2(NCr),
                ["npr"] = F2(NPr),
                ["rad"] = F1(v => v * Math.PI / 180),
                ["deg"] = F1(v => v * 180 / Math.PI),
                ["rand"] = new(0, 1, a => a.Count == 0 ? Rng.NextDouble() : Math.Floor(Rng.NextDouble() * a[0]) + 1),
                ["random"] = new(0, 1, a => a.Count == 0 ? Rng.NextDouble() : Math.Floor(Rng.NextDouble() * a[0]) + 1),
                ["sq"] = F1(v => v * v),
                ["cube"] = F1(v => v * v * v),
            };
    }
}
