using System.Globalization;

namespace Noexs.Misc;

public sealed class ExpressionEvaluator
{
    public interface IVariableProvider
    {
        long Get(string name);

        bool ContainsVar(string value);
    }

    public delegate long MemoryProvider(long addr);

    private enum Type
    {
        DEREF_START,
        DEREF_END,
        ARITHMETIC,
        CONSTANT,
        WHITESPACE,
        VARIABLE,
    }

    private sealed class Token
    {
        public Token? Prev, Next;
        public Type Type;
        public string Text = "";
        public long Number;

        public override string ToString()
        {
            return "Token{type=" + Type + ", value=" + (Type == Type.CONSTANT ? Number.ToString() : Text) + '}';
        }
    }

    private readonly IVariableProvider vars;
    private readonly MemoryProvider mem;

    public ExpressionEvaluator(IVariableProvider variables, MemoryProvider mem)
    {
        vars = variables;
        this.mem = mem;
    }

    public long Eval(string str)
    {
        List<Token> tokens = Setup(str);
        var values = new Stack<long>();
        var ops = new Stack<char>();
        foreach (Token t in tokens)
        {
            switch (t.Type)
            {
                case Type.VARIABLE:
                    values.Push(vars.Get(t.Text));
                    break;
                case Type.CONSTANT:
                    values.Push(t.Number);
                    break;
                case Type.ARITHMETIC:
                    // left-associative with * / binding tighter than + -
                    // (the Java version evaluated "a-b+c" as "a-(b+c)")
                    while (ops.Count > 0 && ops.Peek() != '[' && Precedence(ops.Peek()) >= Precedence(t.Text[0]))
                    {
                        values.Push(EvalOp(ops.Pop(), values.Pop(), values.Pop()));
                    }
                    ops.Push(t.Text[0]);
                    break;
                case Type.DEREF_START:
                    ops.Push(t.Text[0]);
                    break;
                case Type.DEREF_END:
                    while (ops.Peek() != '[')
                    {
                        values.Push(EvalOp(ops.Pop(), values.Pop(), values.Pop()));
                    }
                    ops.Pop();
                    long addr = values.Pop();
                    values.Push(mem(addr));
                    break;
            }
        }
        while (ops.Count > 0)
        {
            values.Push(EvalOp(ops.Pop(), values.Pop(), values.Pop()));
        }
        return values.Pop();
    }

    private List<Token> Setup(string str)
    {
        ArgumentNullException.ThrowIfNull(str);
        //parse into tokens
        List<Token> tokens = GetTokens(str);
        //verify the tokens
        VerifyFormatting(tokens);
        //if valid format, set tokens
        return tokens;
    }

    private static int Precedence(char op)
    {
        return op is '*' or '/' ? 2 : 1;
    }

    private static long EvalOp(char type, long v1, long v2)
    {
        return type switch
        {
            '-' => v2 - v1,
            '+' => v2 + v1,
            '*' => v2 * v1,
            '/' => v1 == 0 ? throw new InvalidFormatException("Division by zero") : v2 / v1,
            _ => throw new NotSupportedException("op:" + type),
        };
    }

    private void VerifyFormatting(List<Token> tokens)
    {
        int derefStart = 0;
        int derefEnd = 0;
        foreach (Token t in tokens)
        {
            switch (t.Type)
            {
                case Type.DEREF_START:
                    derefStart++;
                    break;
                case Type.DEREF_END:
                    derefEnd++;
                    break;
                case Type.VARIABLE:
                    if (!vars.ContainsVar(t.Text))
                    {
                        throw new InvalidFormatException("Unknown variable:" + t.Text);
                    }
                    // The Java switch fell through into the operator checks here, so any
                    // expression starting or ending with a variable ("main+10") was rejected.
                    break;
                case Type.ARITHMETIC:
                    if (t.Prev == null)
                    {
                        throw new InvalidFormatException("Missing left param for op:" + t.Text);
                    }
                    if (t.Next == null)
                    {
                        throw new InvalidFormatException("Missing right param for op:" + t.Text);
                    }
                    break;
            }
        }
        if (derefEnd != derefStart)
        {
            throw new InvalidFormatException("Reference mismatch. start count:" + derefStart + " end count:" + derefEnd);
        }
    }

    private static List<Token> GetTokens(string str)
    {
        var res = new List<Token>();
        foreach (char c in str)
        {
            res.Add(new Token { Type = GetType(c), Text = c.ToString() });
        }

        //merge all consecutive variable tokens together
        for (int i = 0; i < res.Count; i++)
        {
            if (Merge(res, i))
            {
                i--;
            }
        }
        //remove whitespace
        res.RemoveAll(t => t.Type == Type.WHITESPACE);

        //make number types actually numbers (Long.parseLong(s, 16) semantics: no overflow into the sign bit)
        foreach (Token t in res)
        {
            if (t.Type == Type.VARIABLE
                && ulong.TryParse(t.Text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong v)
                && v <= long.MaxValue)
            {
                t.Number = (long)v;
                t.Type = Type.CONSTANT;
            }
        }
        //finally link all tokens together
        for (int i = 0; i < res.Count; i++)
        {
            Token t = res[i];
            if (i > 0)
            {
                t.Prev = res[i - 1];
            }
            if (i + 1 < res.Count)
            {
                t.Next = res[i + 1];
            }
        }
        return res;
    }

    private static bool Merge(List<Token> tokens, int index)
    {
        if (index + 1 >= tokens.Count)
        {
            return false;
        }
        Token curr = tokens[index];
        Token next = tokens[index + 1];
        if (curr.Type != Type.VARIABLE || next.Type != Type.VARIABLE)
        {
            return false;
        }
        tokens.RemoveAt(index + 1);
        curr.Text += next.Text;
        return true;
    }

    private static Type GetType(char c)
    {
        if (char.IsWhiteSpace(c))
        {
            return Type.WHITESPACE;
        }
        return c switch
        {
            '[' => Type.DEREF_START,
            ']' => Type.DEREF_END,
            '+' or '-' or '*' or '/' => Type.ARITHMETIC,
            _ => Type.VARIABLE,
        };
    }

    public sealed class InvalidFormatException : Exception
    {
        internal InvalidFormatException(string str) : base(str)
        {
        }
    }
}
