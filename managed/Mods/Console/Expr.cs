using System.Globalization;
using System.Text;
using CoreLoader;

namespace CoreConsole;

/// <summary>A console error the user should see as-is (no stack trace).</summary>
public sealed class ConsoleError : Exception
{
    public ConsoleError(string message) : base(message) { }
}

internal enum Tok { Num, Str, Ident, Op, End }

internal readonly record struct Token(Tok Kind, string Text, double Number = 0);

internal static class Lexer
{
    public static List<Token> Lex(string src)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < src.Length)
        {
            char c = src[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int s = i;
                if (c == '0' && i + 1 < src.Length && (src[i + 1] == 'x' || src[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < src.Length && Uri.IsHexDigit(src[i])) i++;
                    list.Add(new Token(Tok.Num, src[s..i], long.Parse(src[(s + 2)..i], NumberStyles.HexNumber)));
                    continue;
                }
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.' || src[i] == 'e' || src[i] == 'E' ||
                                          ((src[i] == '-' || src[i] == '+') && (src[i - 1] == 'e' || src[i - 1] == 'E'))))
                    i++;
                if (!double.TryParse(src[s..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new ConsoleError($"bad number '{src[s..i]}'");
                list.Add(new Token(Tok.Num, src[s..i], d));
                continue;
            }

            if (c == '"' || c == '\'')
            {
                char q = c;
                var sb = new StringBuilder();
                i++;
                while (i < src.Length && src[i] != q)
                {
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        i++;
                        sb.Append(src[i] switch { 'n' => '\n', 't' => '\t', _ => src[i] });
                    }
                    else sb.Append(src[i]);
                    i++;
                }
                if (i >= src.Length) throw new ConsoleError("unterminated string");
                i++;
                list.Add(new Token(Tok.Str, sb.ToString()));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '@')) i++;
                list.Add(new Token(Tok.Ident, src[s..i]));
                continue;
            }

            // Two-character operators first.
            if (i + 1 < src.Length)
            {
                var two = src.Substring(i, 2);
                if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "+=" or "-=" or "*=" or "/=")
                {
                    list.Add(new Token(Tok.Op, two));
                    i += 2;
                    continue;
                }
            }
            if ("+-*/%()[],.=<>!".IndexOf(c) >= 0)
            {
                list.Add(new Token(Tok.Op, c.ToString()));
                i++;
                continue;
            }
            throw new ConsoleError($"unexpected '{c}'");
        }
        list.Add(new Token(Tok.End, ""));
        return list;
    }
}

/// <summary>
/// A small GML-flavoured expression language over the live game:
/// literals, arithmetic and comparison, function calls (builtins, then
/// scripts), global.x, obj.var / obj[n].var, bare asset names, and
/// assignment (=, +=, -=, *=, /=) to globals and instance variables.
/// Everything is evaluated immediately against the game; there is no state
/// beyond `ans` (the last result).
/// </summary>
internal sealed class Evaluator
{
    private List<Token> _t = new();
    private int _p;

    public RValue Ans { get; private set; } = RValue.Undefined;

    public RValue Run(string line)
    {
        _t = Lexer.Lex(line);
        _p = 0;
        var v = Statement();
        if (Peek.Kind != Tok.End) throw new ConsoleError($"unexpected '{Peek.Text}'");

        // `ans` outlives this frame, so it holds its own reference (a copy).
        // Copy first, release the previous one after: v may BE the previous one
        // (typing `ans`), and releasing first would free it before the copy.
        var fresh = Values.CanCopy || v.IsNumber || v.IsUndefined ? Values.Copy(v) : RValue.Undefined;
        var old = Ans;
        Ans = fresh;
        Values.Free(ref old);
        return v;
    }

    private Token Peek => _t[_p];
    private Token Next() => _t[_p++];
    private bool IsOp(string op) => Peek.Kind == Tok.Op && Peek.Text == op;
    private bool IsOpAt(int k, string op) => _p + k < _t.Count && _t[_p + k].Kind == Tok.Op && _t[_p + k].Text == op;

    private void Expect(string op)
    {
        if (!IsOp(op)) throw new ConsoleError($"expected '{op}' but found '{(Peek.Kind == Tok.End ? "end of line" : Peek.Text)}'");
        _p++;
    }

    // ---------------------------------------------------------------- lvalues

    private abstract record Place
    {
        public abstract RValue Get();
        public abstract void Set(RValue v);
    }

    private sealed record GlobalPlace(string Name) : Place
    {
        public override RValue Get() => Globals.Get(Name);
        public override void Set(RValue v) => Globals.Set(Name, v);
    }

    private sealed record InstancePlace(InstanceRef Inst, string Object, string Name) : Place
    {
        public override RValue Get()
        {
            if (!Inst.Has(Name)) throw new ConsoleError($"{Object} has no variable '{Name}'");
            return Inst.Get(Name);
        }
        public override void Set(RValue v) => Inst.Set(Name, v);
    }

    // Recognises `global.x`, `obj.var` and `obj[n].var` at the cursor without
    // consuming anything unless it matches.
    private Place? TryPlace()
    {
        if (Peek.Kind != Tok.Ident) return null;
        string head = Peek.Text;

        if (head == "global" && IsOpAt(1, ".") && _t[_p + 2].Kind == Tok.Ident)
        {
            _p += 3;
            return new GlobalPlace(_t[_p - 1].Text);
        }

        if (IsOpAt(1, ".") && _t[_p + 2].Kind == Tok.Ident && !IsOpAt(3, "("))
        {
            _p += 3;
            return new InstancePlace(InstanceOf(head, 0), head, _t[_p - 1].Text);
        }

        if (IsOpAt(1, "["))
        {
            int save = _p;
            _p += 2;
            var idx = Expr();
            Expect("]");
            if (IsOp(".") && _t[_p + 1].Kind == Tok.Ident)
            {
                _p++;
                var name = Next().Text;
                return new InstancePlace(InstanceOf(head, (int)Num(idx, "an instance index")), head, name);
            }
            _p = save;
        }
        return null;
    }

    private static InstanceRef InstanceOf(string objectName, int n)
    {
        var o = GmlObject.Find(objectName) ?? throw new ConsoleError($"no object named '{objectName}'");
        int count = o.InstanceCount;
        if (count == 0) throw new ConsoleError($"no live {objectName} instance");
        if (n < 0 || n >= count) throw new ConsoleError($"{objectName}[{n}]: only {count} live instance(s)");
        return o.Instance(n);
    }

    // -------------------------------------------------------------- grammar

    private RValue Statement()
    {
        int save = _p;
        var place = TryPlace();
        if (place != null && Peek.Kind == Tok.Op && Peek.Text is "=" or "+=" or "-=" or "*=" or "/=")
        {
            string op = Next().Text;
            var rhs = Expr();
            var value = op == "=" ? rhs : Arith(op[0].ToString(), place.Get(), rhs);
            place.Set(value);
            return value;
        }
        _p = save;
        return Expr();
    }

    private RValue Expr() => Or();

    private RValue Or()
    {
        var l = And();
        while (IsOp("||")) { _p++; var r = And(); l = RValue.FromBool(Truthy(l) || Truthy(r)); }
        return l;
    }

    private RValue And()
    {
        var l = Compare();
        while (IsOp("&&")) { _p++; var r = Compare(); l = RValue.FromBool(Truthy(l) && Truthy(r)); }
        return l;
    }

    private RValue Compare()
    {
        var l = Sum();
        while (Peek.Kind == Tok.Op && Peek.Text is "==" or "!=" or "<" or ">" or "<=" or ">=")
        {
            string op = Next().Text;
            var r = Sum();
            bool res;
            if (l.Kind == RValueKind.String || r.Kind == RValueKind.String)
            {
                int c = string.CompareOrdinal(l.ToString(), r.ToString());
                res = op switch { "==" => c == 0, "!=" => c != 0, "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 };
            }
            else
            {
                double a = Num(l, "a comparison"), b = Num(r, "a comparison");
                res = op switch { "==" => a == b, "!=" => a != b, "<" => a < b, ">" => a > b, "<=" => a <= b, _ => a >= b };
            }
            l = RValue.FromBool(res);
        }
        return l;
    }

    private RValue Sum()
    {
        var l = Product();
        while (IsOp("+") || IsOp("-"))
        {
            string op = Next().Text;
            l = Arith(op, l, Product());
        }
        return l;
    }

    private RValue Product()
    {
        var l = Unary();
        while (IsOp("*") || IsOp("/") || IsOp("%"))
        {
            string op = Next().Text;
            l = Arith(op, l, Unary());
        }
        return l;
    }

    private RValue Unary()
    {
        if (IsOp("-")) { _p++; return RValue.FromReal(-Num(Unary(), "'-'")); }
        if (IsOp("!")) { _p++; return RValue.FromBool(!Truthy(Unary())); }
        return Primary();
    }

    private RValue Primary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case Tok.Num: _p++; return RValue.FromReal(t.Number);
            case Tok.Str: _p++; return RValue.FromString(t.Text);
            case Tok.Op when t.Text == "(":
                _p++;
                var v = Expr();
                Expect(")");
                return v;
            case Tok.Ident:
                var place = TryPlace();
                if (place != null) return place.Get();
                _p++;
                if (IsOp("(")) return Call(t.Text);
                return Name(t.Text);
            default:
                throw new ConsoleError(t.Kind == Tok.End ? "incomplete expression" : $"unexpected '{t.Text}'");
        }
    }

    private RValue Name(string name)
    {
        switch (name)
        {
            case "true": return RValue.FromBool(true);
            case "false": return RValue.FromBool(false);
            case "undefined": return RValue.Undefined;
            case "noone": return RValue.FromReal(-4);
            case "all": return RValue.FromReal(-3);
            case "global": return RValue.FromReal(-5);
            case "pi": return RValue.FromReal(Math.PI);
            case "ans": return Ans;
        }
        // A bare name is an asset: objects, sprites, rooms, sounds, scripts.
        var idx = Game.CallBuiltin("asset_get_index", name);
        if (idx.IsNumber && idx.AsReal < 0)
            throw new ConsoleError($"'{name}' is not a known name, asset or function (try: find {name})");
        return idx;
    }

    private RValue Call(string name)
    {
        Expect("(");
        var args = new List<RValue>();
        if (!IsOp(")"))
        {
            do { args.Add(Expr()); } while (IsOp(",") && Next().Text == ",");
        }
        Expect(")");

        if (Game.BuiltinArity(name) is { } arity)
        {
            if (arity >= 0 && arity != args.Count)
                throw new ConsoleError($"{name} takes {arity} argument{(arity == 1 ? "" : "s")} in this game, got {args.Count}");
            return Game.CallBuiltin(name, args.ToArray());
        }
        if (Game.FindSymbol("gml_Script_" + name) != 0 || Game.FindSymbol(name) != 0)
            return Game.CallScript(name, args.ToArray());
        throw new ConsoleError($"no builtin or script named '{name}' (try: find {name})");
    }

    // ---------------------------------------------------------------- values

    private static RValue Arith(string op, RValue a, RValue b)
    {
        if (op == "+" && (a.Kind == RValueKind.String || b.Kind == RValueKind.String))
            return RValue.FromString(a.ToString() + b.ToString());
        double x = Num(a, $"'{op}'"), y = Num(b, $"'{op}'");
        return RValue.FromReal(op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => y == 0 ? throw new ConsoleError("division by zero") : x / y,
            "%" => x % y,
            _ => throw new ConsoleError($"unknown operator {op}"),
        });
    }

    private static double Num(RValue v, string what)
    {
        if (v.IsNumber) return v.AsReal;
        if (v.Kind == RValueKind.Reference) return v.Int64 & 0xFFFFFFFF;   // asset refs
        throw new ConsoleError($"{what} needs a number, got {Gml.TypeOf(v)}");
    }

    private static bool Truthy(RValue v) => v.IsNumber ? v.AsReal > 0.5 : !v.IsUndefined;
}
