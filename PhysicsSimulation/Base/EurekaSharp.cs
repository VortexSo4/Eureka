// ESharpEngine.cs
// Refactored — unified call parsing via CallContext, removed plot special-casing,
// added convenient parsing helpers for arguments (ParseFloat/Bool/Vector2/etc.)
// Usage: Registry.RegisterFunc("mycmd", new Func<object[], Dictionary<string, object>, object>((pos, named) => { var ctx = new CallContext(pos, named, this); ... }));
// Or use the provided builtins registered in the constructor which use CallContext.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenTK.Mathematics;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using SkiaSharp;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace PhysicsSimulation.Base
{
    // ------------------ Lexer & tokens ------------------
    public enum TokenType
    {
        EOF, Ident, Number, String,
        LParen, RParen, LBracket, RBracket,
        Assign, Dot, Comma, Colon, Arrow,
        Plus, Minus, Star, Slash, Caret
    }

    public record Token(TokenType Type, string Text, int Line);

    public class ESharpLexer
    {
        private readonly string _source;
        private int _pos;
        private int _line = 1;

        public ESharpLexer(string source) => _source = source ?? string.Empty;

        private char Peek(int offset = 0) => _pos + offset < _source.Length ? _source[_pos + offset] : '\0';
        private char Advance() { if (_pos < _source.Length) _pos++; return _pos > 0 ? _source[_pos - 1] : '\0'; }

        public Token NextToken()
        {
            while (char.IsWhiteSpace(Peek()))
            {
                if (Peek() == '\n') _line++;
                Advance();
            }

            if (_pos >= _source.Length) return new(TokenType.EOF, "", _line);

            var c = Peek();

            // single-line comment
            if (c == '/' && Peek(1) == '/')
            {
                while (Peek() != '\n' && Peek() != '\0') Advance();
                return new(TokenType.Ident, "//", _line);
            }

            if (c == '=' && Peek(1) == '>') { Advance(); Advance(); return new(TokenType.Arrow, "=>", _line); }

            if (char.IsLetter(c) || c == '_')
            {
                var start = _pos;
                while (char.IsLetterOrDigit(Peek()) || Peek() == '_') Advance();
                return new(TokenType.Ident, _source[start.._pos], _line);
            }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1))))
            {
                var start = _pos;
                while (char.IsDigit(Peek()) || Peek() == '.') Advance();
                return new(TokenType.Number, _source[start.._pos], _line);
            }

            if (c == '"')
            {
                Advance();
                var start = _pos;
                while (Peek() != '"' && Peek() != '\0') Advance();
                var text = _source[start.._pos];
                if (Peek() == '"') Advance();
                return new(TokenType.String, text, _line);
            }

            return c switch
            {
                '(' => AdvanceToken(TokenType.LParen, "("),
                ')' => AdvanceToken(TokenType.RParen, ")"),
                '[' => AdvanceToken(TokenType.LBracket, "["),
                ']' => AdvanceToken(TokenType.RBracket, "]"),
                ',' => AdvanceToken(TokenType.Comma, ","),
                ':' => AdvanceToken(TokenType.Colon, ":"),
                '.' => AdvanceToken(TokenType.Dot, "."),
                '=' => AdvanceToken(TokenType.Assign, "="),
                '+' => AdvanceToken(TokenType.Plus, "+"),
                '-' => AdvanceToken(TokenType.Minus, "-"),
                '*' => AdvanceToken(TokenType.Star, "*"),
                '/' => AdvanceToken(TokenType.Slash, "/"),
                '^' => AdvanceToken(TokenType.Caret, "^"),
                _ => throw new Exception($"Неизвестный символ: {c} на строке {_line}")
            };
        }

        private Token AdvanceToken(TokenType type, string text) { Advance(); return new(type, text, _line); }
    }

    // ------------------ AST ------------------
    public abstract record Expr;
    public record IdentExpr(string Name) : Expr;
    public record NumberExpr(double Value) : Expr;
    public record StringExpr(string Value) : Expr;
    public record ArrayExpr(List<Expr> Items) : Expr;
    public record BinaryExpr(Expr Left, string Op, Expr Right) : Expr;
    public record CallExpr(Expr Callee, List<Expr> Args, Dictionary<string, Expr> NamedArgs) : Expr; // function or delegate call
    public record MemberCallExpr(Expr Target, string Method, List<Expr> Args, Dictionary<string, Expr> NamedArgs) : Expr; // target.method(...)
    public record LambdaExpr(string Param, Expr Body) : Expr;

    public abstract record Stmt;
    public record ExprStmt(Expr Expr) : Stmt;
    public record AssignStmt(string Name, Expr Value) : Stmt;

    // ------------------ Parser ------------------
    public class ESharpParser
    {
        private readonly List<Token> _tokens;
        private int _pos;
        private Token Current => _pos < _tokens.Count ? _tokens[_pos] : new(TokenType.EOF, "", 0);

        public ESharpParser(ESharpLexer lexer)
        {
            _tokens = new();
            Token t;
            while ((t = lexer.NextToken()).Type != TokenType.EOF)
                if (t.Type != TokenType.Ident || t.Text != "//") // skip comments
                    _tokens.Add(t);
        }

        private Token Advance() => _tokens[_pos++];
        private Token Previous() => _tokens[_pos - 1];
        private void Expect(TokenType type)
        {
            if (Current.Type != type)
                throw new Exception($"Ожидался {type}, а получен {Current.Type} ({Current.Text}) на строке {Current.Line}");
            Advance();
        }

        public List<Stmt> Parse()
        {
            var stmts = new List<Stmt>();
            while (Current.Type != TokenType.EOF)
                stmts.Add(ParseStatement());
            return stmts;
        }

        private Stmt ParseStatement()
        {
            if (Current.Type == TokenType.Ident && Peek().Type == TokenType.Assign)
            {
                var name = Advance().Text;
                Advance(); // skip '='
                var value = ParseExpression();
                return new AssignStmt(name, value);
            }

            return new ExprStmt(ParseExpression());
        }

        private Token Peek(int offset = 1) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : new(TokenType.EOF, "", 0);

        private Expr ParseExpression() => ParseCallChain();

        private Expr ParseUnary()
        {
            if (Current.Type == TokenType.Minus)
            {
                Advance();
                var expr = ParseUnary();
                return new BinaryExpr(new NumberExpr(0), "-", expr);
            }
            if (Current.Type == TokenType.Plus)
            {
                Advance();
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private Expr ParseBinary(int precedence = 0)
        {
            var left = ParseUnary();

            while (true)
            {
                int opPrec = Current.Type switch
                {
                    TokenType.Plus or TokenType.Minus => 1,
                    TokenType.Star or TokenType.Slash => 2,
                    TokenType.Caret => 3,
                    _ => 0
                };

                if (opPrec == 0 || opPrec <= precedence) break;

                var op = Current.Text;
                Advance();
                var right = ParseBinary(opPrec);
                left = new BinaryExpr(left, op, right);
            }

            return left;
        }

        private Expr ParseCallChain()
        {
            Expr expr = ParseBinary();

            // function call (expr(...)) or chaining: expr.method(...)
            while (true)
            {
                // function call: expr(...)
                if (Current.Type == TokenType.LParen)
                {
                    Advance();
                    var (args, named) = ParseArgList();
                    expr = new CallExpr(expr, args, named);
                    continue;
                }

                // method call via dot: expr.method(...)
                if (Current.Type == TokenType.Dot)
                {
                    Advance();
                    var method = ExpectIdent();
                    Expect(TokenType.LParen);
                    var (args, named) = ParseArgList();
                    expr = new MemberCallExpr(expr, method, args, named);
                    continue;
                }

                break;
            }

            return expr;
        }

        private Expr ParsePrimary()
        {
            switch (Current.Type)
            {
                case TokenType.String: return new StringExpr(Advance().Text);
                case TokenType.Number: return new NumberExpr(double.Parse(Advance().Text, CultureInfo.InvariantCulture));
                case TokenType.LBracket: return ParseArray();
                case TokenType.LParen:
                    Advance();
                    var expr = ParseExpression();
                    Expect(TokenType.RParen);
                    return expr;
                case TokenType.Ident: return ParseIdentOrCall();
                default: throw new Exception($"Неожиданный токен: {Current.Type} ({Current.Text}) на строке {Current.Line}");
            }
        }

        private Expr ParseIdentOrCall()
        {
            var name = Advance().Text;

            if (Current.Type == TokenType.LParen)
            {
                Advance();
                var (args, named) = ParseArgList();
                return new CallExpr(new IdentExpr(name), args, named);
            }

            return new IdentExpr(name);
        }

        private ArrayExpr ParseArray()
        {
            Advance();
            var items = new List<Expr>();
            while (Current.Type != TokenType.RBracket)
            {
                items.Add(ParseExpression());
                if (Current.Type == TokenType.Comma) Advance();
            }
            Expect(TokenType.RBracket);
            return new ArrayExpr(items);
        }

        private (List<Expr>, Dictionary<string, Expr>) ParseArgList()
        {
            var args = new List<Expr>();
            var named = new Dictionary<string, Expr>(StringComparer.OrdinalIgnoreCase);

            while (Current.Type != TokenType.RParen)
            {
                // special case: func: x => ...
                if (Current is { Type: TokenType.Ident, Text: "func" } && Peek().Type == TokenType.Colon)
                {
                    Advance(); // func
                    Advance(); // :
                    var param = ExpectIdent();
                    Expect(TokenType.Arrow);
                    // body is any expression
                    var body = ParseExpression();
                    named["func"] = new LambdaExpr(param, body);
                }
                else if (Current.Type == TokenType.Ident && Peek().Type == TokenType.Colon)
                {
                    var key = Advance().Text;
                    Advance(); // :
                    named[key] = ParseExpression();
                }
                else
                {
                    args.Add(ParseExpression());
                }

                if (Current.Type == TokenType.Comma) Advance();
            }

            Advance(); // consume RParen
            return (args, named);
        }

        private string ExpectIdent()
        {
            return Current.Type != TokenType.Ident ? throw new Exception($"Ожидался идентификатор, а получен {Current.Type}") : Advance().Text;
        }
    }

    // ------------------ DSL registry (modular) ------------------
    public class DslRegistry
    {
        readonly Dictionary<string, object> _vars = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<Delegate>> _funcs = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterVar(string name, object value) => _vars[name] = value;
        public bool TryGetVar(string name, out object value) => _vars.TryGetValue(name, out value);

        public void RegisterFunc(string name, Delegate func)
        {
            if (!_funcs.TryGetValue(name, out var list)) _funcs[name] = list = new List<Delegate>();
            list.Add(func);
        }

        public bool HasFunc(string name) => _funcs.ContainsKey(name);

        // Register all public instance methods of an object as functions (bound to that object)
        public void RegisterInstanceMethods(object target, BindingFlags flags = BindingFlags.Public | BindingFlags.Instance)
        {
            if (target == null) return;
            var t = target.GetType();
            foreach (var m in t.GetMethods(flags).Where(x => !x.IsSpecialName))
            {
                // wrapper: accepts object[] and optional named args
                RegisterFunc(m.Name, new Func<object[], Dictionary<string, object>, object>((args, _) =>
                {
                    var parameters = m.GetParameters();
                    var callArgs = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length && i < args.Length; i++) callArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
                    return m.Invoke(target, callArgs);
                }));
            }
        }

        private static object ConvertArg(object src, Type targetType)
        {
            if (src == null) return null;
            if (targetType.IsInstanceOfType(src)) return src;
            try { return Convert.ChangeType(src, targetType); } catch { return src; }
        }

        // Try to invoke registered function. Supports three delegate shapes:
        // - delegate(object[]) -> object
        // - delegate(object[], Dictionary<string,object>) -> object
        // - any delegate that can be DynamicInvoke'd with posArgs
        public bool TryInvoke(string name, object[] posArgs, Dictionary<string, object> namedArgs, out object result)
        {
            result = null;
            if (!_funcs.TryGetValue(name, out var list)) return false;

            foreach (var del in list)
            {
                var m = del.Method;
                var ps = m.GetParameters();

                // shape: Func<object[], object>
                if (ps.Length == 1 && ps[0].ParameterType == typeof(object[]))
                {
                    try { result = del.DynamicInvoke(new object[] { posArgs }); return true; } catch { }
                }

                // shape: Func<object[], Dictionary<string,object>, object>
                if (ps.Length == 2 && ps[0].ParameterType == typeof(object[]) && ps[1].ParameterType == typeof(Dictionary<string, object>))
                {
                    try { result = del.DynamicInvoke(posArgs, namedArgs); return true; } catch { }
                }

                // last resort: try to DynamicInvoke directly (posArgs must match)
                try { result = del.DynamicInvoke(posArgs); return true; } catch { }
            }

            return false;
        }
    }

    // ------------------ Engine ------------------
    public class ESharpEngine
    {
        private readonly GeometryArena _arena;
        public SceneGpu CurrentScene { get; set; }
        public static DslRegistry Registry { get; } = new DslRegistry();

        // Optional factory if caller wants engine to create scenes by name
        public Func<GeometryArena, string, SceneGpu> SceneFactory { get; private set; }

        // ---------------- CallContext ----------------
        // Удобный контекст, который создаётся при каждом вызове зарегистрированной функции.
        // Позволяет безопасно и удобно парсить positional / named аргументы.
        public class CallContext
        {
            public object[] Pos { get; }
            public Dictionary<string, object> Named { get; }
            private readonly ESharpEngine _engine;

            public CallContext(object[] pos, Dictionary<string, object> named, ESharpEngine engine)
            {
                Pos = pos ?? Array.Empty<object>();
                Named = named ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _engine = engine;
            }

            // Helpers: Try get by name first, then by positional index if name looks like integer or param not found.
            private bool TryGetByNameOrIndex(string name, out object value)
            {
                if (string.IsNullOrEmpty(name))
                {
                    value = null;
                    return false;
                }

                if (Named != null && Named.TryGetValue(name, out value)) return true;
                // if name is integer index
                if (int.TryParse(name, out var idx) && idx >= 0 && idx < Pos.Length)
                {
                    value = Pos[idx];
                    return true;
                }

                // fallback: try interpret name as single-letter param a/b/c mapping to positional 0/1/2 (convention)
                if (name.Length == 1)
                {
                    var ch = name[0];
                    if (char.IsLetter(ch))
                    {
                        int idx2 = char.ToLower(ch) - 'a';
                        if (idx2 >= 0 && idx2 < Pos.Length)
                        {
                            value = Pos[idx2];
                            return true;
                        }
                    }
                }

                value = null;
                return false;
            }

            public object GetPosOrNamed(int idx)
            {
                if (idx >= 0 && idx < Pos.Length) return Pos[idx];
                return null;
            }

            public object GetNamed(string name)
            {
                if (Named != null && Named.TryGetValue(name, out var v)) return v;
                return null;
            }

            public float ParseFloat(int idx, float def = 0f)
            {
                var v = GetPosOrNamed(idx);
                if (v == null) return def;
                try
                {
                    return Convert.ToSingle(v);
                }
                catch
                {
                    return def;
                }
            }

            public float ParseFloat(string name, float def = 0f)
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    try
                    {
                        return Convert.ToSingle(v);
                    }
                    catch
                    {
                        return def;
                    }
                }

                return def;
            }

            public int ParseInt(int idx, int def = 0)
            {
                var v = GetPosOrNamed(idx);
                if (v == null) return def;
                try
                {
                    return Convert.ToInt32(v);
                }
                catch
                {
                    return def;
                }
            }

            public int ParseInt(string name, int def = 0)
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    try
                    {
                        return Convert.ToInt32(v);
                    }
                    catch
                    {
                        return def;
                    }
                }

                return def;
            }

            public bool ParseBool(int idx, bool def = false)
            {
                var v = GetPosOrNamed(idx);
                if (v == null) return def;
                if (v is bool b) return b;
                if (v is string s)
                {
                    if (bool.TryParse(s, out var r)) return r;
                    if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                }

                try
                {
                    return Convert.ToBoolean(v);
                }
                catch
                {
                    return def;
                }
            }

            public bool ParseBool(string name, bool def = false)
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    if (v is bool b) return b;
                    if (v is string s)
                    {
                        if (bool.TryParse(s, out var r)) return r;
                        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                    }

                    try
                    {
                        return Convert.ToBoolean(v);
                    }
                    catch
                    {
                        return def;
                    }
                }

                return def;
            }

            public Vector2 ParseVector2(int idx, Vector2? def = null)
            {
                var v = GetPosOrNamed(idx);
                if (v == null) return def ?? Vector2.Zero;
                if (v is Vector2 vv) return vv;
                if (v is float[] { Length: >= 2 } fa) return new Vector2(fa[0], fa[1]);
                if (v is double[] { Length: >= 2 } da) return new Vector2((float)da[0], (float)da[1]);
                if (v is object[] { Length: >= 2 } oa)
                    return new Vector2(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]));
                return def ?? Vector2.Zero;
            }

            public Vector2 ParseVector2(string name, Vector2? def = null)
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    if (v is Vector2 vv) return vv;
                    if (v is float[] { Length: >= 2 } fa) return new Vector2(fa[0], fa[1]);
                    if (v is double[] { Length: >= 2 } da) return new Vector2((float)da[0], (float)da[1]);
                    if (v is object[] { Length: >= 2 } oa)
                        return new Vector2(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]));
                }

                return def ?? Vector2.Zero;
            }

            public Vector3 ParseVector3(int idx, Vector3? def = null)
            {
                var v = GetPosOrNamed(idx);
                if (v == null) return def ?? Vector3.Zero;
                if (v is Vector3 vv) return vv;
                if (v is float[] { Length: >= 3 } fa) return new Vector3(fa[0], fa[1], fa[2]);
                if (v is double[] { Length: >= 3 } da) return new Vector3((float)da[0], (float)da[1], (float)da[2]);
                if (v is object[] { Length: >= 3 } oa)
                    return new Vector3(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]), Convert.ToSingle(oa[2]));
                return def ?? Vector3.Zero;
            }

            public Vector3 ParseVector3(string name, Vector3? def = null)
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    if (v is Vector3 vv) return vv;
                    if (v is float[] { Length: >= 3 } fa) return new Vector3(fa[0], fa[1], fa[2]);
                    if (v is double[] { Length: >= 3 } da) return new Vector3((float)da[0], (float)da[1], (float)da[2]);
                    if (v is object[] { Length: >= 3 } oa)
                        return new Vector3(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]), Convert.ToSingle(oa[2]));
                }

                return def ?? Vector3.Zero;
            }

            public Vector4 ParseVector4(int idx, Vector4? def = null)
            {
                var v = GetPosOrNamed(idx);
                if (v == null) return def ?? Vector4.Zero;
                if (v is Vector4 vv) return vv;
                if (v is float[] { Length: >= 4 } fa) return new Vector4(fa[0], fa[1], fa[2], fa[3]);
                if (v is double[] { Length: >= 4 } da)
                    return new Vector4((float)da[0], (float)da[1], (float)da[2], (float)da[3]);
                if (v is object[] { Length: >= 4 } oa)
                    return new Vector4(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]), Convert.ToSingle(oa[2]),
                        Convert.ToSingle(oa[3]));
                return def ?? Vector4.Zero;
            }

            public Vector4 ParseVector4(string name, Vector4? def = null)
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    if (v is Vector4 vv) return vv;
                    if (v is float[] { Length: >= 4 } fa) return new Vector4(fa[0], fa[1], fa[2], fa[3]);
                    if (v is double[] { Length: >= 4 } da)
                        return new Vector4((float)da[0], (float)da[1], (float)da[2], (float)da[3]);
                    if (v is object[] { Length: >= 4 } oa)
                        return new Vector4(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]), Convert.ToSingle(oa[2]),
                            Convert.ToSingle(oa[3]));
                }

                return def ?? Vector4.Zero;
            }

            // Color is represented as Vector3 or Vector4; prefer Vector4 if alpha present
            public Vector4 ParseColor(string name, Vector4? def = null)
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    if (v is Vector4 vv) return vv;
                    if (v is Vector3 v3) return new Vector4(v3, 1f);
                    if (v is float[] fa)
                    {
                        if (fa.Length >= 4) return new Vector4(fa[0], fa[1], fa[2], fa[3]);
                        if (fa.Length >= 3) return new Vector4(fa[0], fa[1], fa[2], 1f);
                    }

                    if (v is object[] oa)
                    {
                        if (oa.Length >= 4)
                            return new Vector4(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]),
                                Convert.ToSingle(oa[2]), Convert.ToSingle(oa[3]));
                        if (oa.Length >= 3)
                            return new Vector4(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]),
                                Convert.ToSingle(oa[2]), 1f);
                    }
                }

                return def ?? new Vector4(0, 0, 0, 1);
            }

            public string ParseString(int idx, string def = "")
            {
                var v = GetPosOrNamed(idx);
                if (v == null) return def;
                return v.ToString() ?? def;
            }

            public string ParseString(string name, string def = "")
            {
                if (TryGetByNameOrIndex(name, out var v))
                {
                    return v?.ToString() ?? def;
                }

                return def;
            }

            public LambdaExpr GetLambda(string name)
            {
                if (TryGetByNameOrIndex(name, out var v) && v is LambdaExpr le) return le;
                return null;
            }

            // Engine access: evaluate math lambda expressions using engine-private EvalMathExpr
            public double EvalMathExpr(LambdaExpr lambda, float x, float t)
            {
                if (lambda == null) return 0;
                return _engine.EvalMathExpr(lambda.Body, x, t);
            }
        }

        // ---------------- End CallContext ----------------

        public ESharpEngine(GeometryArena arena, SceneGpu initialScene = null)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            if (initialScene != null) SetScene(initialScene);

            // default globals
            Registry.RegisterVar("PI", Math.PI);
            Registry.RegisterVar("T", 0.0);

            // Register builtin functions using CallContext to parse args in a consistent manner.
            RegisterBuiltinFunctions();
        }

        // Caller can provide factory if caller wants engine to create scenes by name
        public void RegisterSceneFactory(Func<GeometryArena, string, SceneGpu> factory) => SceneFactory = factory;

        // Or set scene manually (preferred if you have a concrete scene instance)
        public void SetScene(SceneGpu scene)
        {
            CurrentScene = scene ?? throw new ArgumentNullException(nameof(scene));
            Registry.RegisterVar("scene", CurrentScene);
            Registry.RegisterInstanceMethods(CurrentScene);
        }

        public void UpdateTime(double t) => Registry.RegisterVar("T", t);

        public void LoadSceneFromFile(string path) =>
            LoadScene(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

        public void LoadScene(string source, string fallbackName)
        {
            // Создаем сцену через фабрику, если текущая сцена не установлена
            if (CurrentScene == null)
            {
                if (SceneFactory == null)
                    throw new Exception(
                        "No scene set and no SceneFactory registered. Call SetScene(...) or RegisterSceneFactory(...).");

                CurrentScene = SceneFactory(_arena, fallbackName);
                Registry.RegisterVar("scene", CurrentScene);
                Registry.RegisterInstanceMethods(CurrentScene);
            }

            var lexer = new ESharpLexer(source);
            var parser = new ESharpParser(lexer);
            var stmts = parser.Parse();

            foreach (var stmt in stmts)
            {
                // Если DSL задает новое имя сцены, создаем новую через фабрику
                if (stmt is AssignStmt { Name: "name" } a && Eval(a.Value) is string n)
                {
                    if (SceneFactory != null)
                    {
                        CurrentScene = SceneFactory(_arena, n);
                        Registry.RegisterVar("scene", CurrentScene);
                        Registry.RegisterInstanceMethods(CurrentScene);
                    }

                    continue;
                }

                Execute(stmt);
            }

            CurrentScene.Initialize();
        }

        private void Execute(Stmt stmt)
        {
            switch (stmt)
            {
                case AssignStmt a: Registry.RegisterVar(a.Name, Eval(a.Value)); break;
                case ExprStmt e: Eval(e.Expr); break;
            }
        }

        // ------------------ Evaluator ------------------
        private object Eval(Expr expr) => expr switch
        {
            IdentExpr i => EvaluateIdent(i.Name),
            NumberExpr n => n.Value,
            StringExpr s => s.Value,
            ArrayExpr a => a.Items.Select(Eval).ToArray(),
            BinaryExpr b => EvalBinary(b),
            CallExpr c => EvalCall(c),
            MemberCallExpr m => EvalMemberCall(m),
            LambdaExpr l => l,
            _ => null
        };

        private object EvaluateIdent(string name)
        {
            if (Registry.TryGetVar(name, out var v)) return v;
            // unknown identifier -> try to find 0-arg function and call it
            if (Registry.TryInvoke(name, new object[0], null, out var res)) return res;
            throw new Exception($"Неизвестно: {name}");
        }

        private object EvalBinary(BinaryExpr b)
        {
            var l = Convert.ToDouble(Eval(b.Left));
            var r = Convert.ToDouble(Eval(b.Right));
            return b.Op switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => l / r,
                "^" => Math.Pow(l, r),
                _ => 0.0
            };
        }

        private object EvalCall(CallExpr call)
        {
            // evaluate positional args
            var pos = call.Args.Select(Eval).ToArray();
            // evaluate named args into values (LambdaExpr stays LambdaExpr because Eval(lambda) returns LambdaExpr)
            var namedValues =
                call.NamedArgs?.ToDictionary(k => k.Key, k => Eval(k.Value), StringComparer.OrdinalIgnoreCase);
            // if callee is an identifier (function name)
            if (call.Callee is IdentExpr ident)
            {
                if (Registry.TryInvoke(ident.Name, pos, namedValues, out var res)) return res;

                if (Registry.HasFunc(ident.Name))
                    throw new Exception($"Вызов функции {ident.Name} не удался: сигнатуры не подошли");

                throw new Exception($"Неизвестно: {ident.Name}");
            }

            // callee is an expression that should evaluate to a delegate/object
            var calleeObj = Eval(call.Callee);

            if (calleeObj is Delegate del)
            {
                // attempt to invoke delegate directly. Try common shapes.
                try
                {
                    return del.DynamicInvoke(pos);
                }
                catch
                {
                }

                try
                {
                    return del.DynamicInvoke(new object[] { pos });
                }
                catch
                {
                }

                try
                {
                    return del.DynamicInvoke(pos, namedValues);
                }
                catch
                {
                }
            }

            throw new Exception("Невозможно вызвать выражение как функцию");
        }

        private object EvalMemberCall(MemberCallExpr call)
        {
            var target = Eval(call.Target);
            var pos = call.Args.Select(Eval).ToArray();
            var named = call.NamedArgs?.ToDictionary(k => k.Key, k => Eval(k.Value), StringComparer.OrdinalIgnoreCase);

            if (target is object[] targets)
            {
                var results = new List<object>();
                foreach (var t1 in targets)
                {
                    var posWithTarget = call.Args.Select(Eval).Prepend(t1).ToArray();
                    var namedArgs = call.NamedArgs?.ToDictionary(k => k.Key, k => Eval(k.Value),
                        StringComparer.OrdinalIgnoreCase);
                    if (Registry.TryInvoke(call.Method, posWithTarget, namedArgs, out var res1))
                        results.Add(res1);
                }

                return results.Count == 1 ? results[0] : results.ToArray();
            }

            // first, try registry-registered function by method name (allows overriding instance methods)
            if (Registry.TryInvoke(call.Method, PrependArg(target, pos), named, out var res)) return res;

            if (target == null) throw new Exception($"Null target for method {call.Method}");

            // reflection fallback: find method on target
            var t = target.GetType();
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Equals(call.Method, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (ps.Length != pos.Length) continue;
                try
                {
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++) args[i] = Convert.ChangeType(pos[i], ps[i].ParameterType);
                    return m.Invoke(target, args);
                }
                catch
                {
                    /* try next overload */
                }
            }

            throw new Exception($"Метод {call.Method} не найден у {target.GetType().Name}");
        }

        private static object[] PrependArg(object first, object[] rest)
        {
            var arr = new object[rest.Length + 1];
            arr[0] = first;
            Array.Copy(rest, 0, arr, 1, rest.Length);
            return arr;
        }

        // ------------------ Plot & Math evaluation ------------------
        // EvalMathExpr остается приватным помощником для вычисления лямбд в Plot и т.п.
        private double EvalMathExpr(Expr expr, float x, float t)
        {
            return expr switch
            {
                NumberExpr n => n.Value,
                IdentExpr i => i.Name == "x" ? x :
                    i.Name == "T" ? t : (Registry.TryGetVar(i.Name, out var v) ? Convert.ToDouble(v) : 0.0),
                BinaryExpr b => EvalBinaryMath(b, x, t),
                CallExpr { Callee: IdentExpr id } c => CallMathFunc(id.Name,
                    c.Args.Select(a => (float)EvalMathExpr(a, x, t)).ToArray()),
                _ => 0
            };
        }

        private double EvalBinaryMath(BinaryExpr b, float x, float t)
        {
            var l = EvalMathExpr(b.Left, x, t);
            var r = EvalMathExpr(b.Right, x, t);
            return b.Op switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => l / r,
                "^" => Math.Pow(l, r),
                _ => 0
            };
        }

        private double CallMathFunc(string name, float[] args) => name.ToLower() switch
        {
            "sin" => Math.Sin(args[0]),
            "cos" => Math.Cos(args[0]),
            "abs" => Math.Abs(args[0]),
            "sqrt" => Math.Sqrt(args[0]),
            "pow" => Math.Pow(args[0], args.Length > 1 ? args[1] : 2),
            "max" => Math.Max(args[0], args.Length > 1 ? args[1] : args[0]),
            "min" => Math.Min(args[0], args.Length > 1 ? args[1] : args[0]),
            _ => 0
        };

        // ------------------ Builtin registration ------------------
        private void RegisterBuiltinFunctions()
        {
            Registry.RegisterFunc("bgColor", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                if (CurrentScene == null)
                    throw new Exception("CurrentScene is not set. Call SetScene(...) or RegisterSceneFactory(...).");
                var ctx = new CallContext(pos, named, this);
                var colorVec = ctx.ParseVector3(0);
                // allow named "to" override
                if (named != null && named.TryGetValue("to", out var tv))
                {
                    if (tv is float[] { Length: >= 3 } arr) colorVec = new Vector3(arr[0], arr[1], arr[2]);
                    else if (tv is Vector3 v3) colorVec = v3;
                }

                var duration = named != null && named.TryGetValue("duration", out var d) ? Convert.ToDouble(d) : 1.0;
                CurrentScene.AnimateBackground(colorVec, CurrentScene.T, CurrentScene.T + (float)duration);
                return null;
            }));
            Registry.RegisterFunc("Add", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                if (pos == null || pos.Length == 0) throw new Exception("Add требует примитив как аргумент");
                if (CurrentScene == null) throw new Exception("CurrentScene не установлена");
                if (pos[0] is not PrimitiveGpu prim)
                    throw new Exception("Первый аргумент Add должен быть PrimitiveGpu");
                CurrentScene.AddPrimitive(prim);
                return prim;
            }));



            Registry.RegisterFunc("plot", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                var lambda = ctx.GetLambda("func");
                if (lambda == null) throw new Exception("plot требует func: x => ...");

                float xmin = named != null && named.TryGetValue("xmin", out var xm) ? Convert.ToSingle(xm) : -1f;
                float xmax = named != null && named.TryGetValue("xmax", out var xM) ? Convert.ToSingle(xM) : 1f;
                bool dynamic = named != null && named.TryGetValue("dynamic", out var dv) && Convert.ToBoolean(dv);

                var plot = new PlotGpu((float x) =>
                {
                    if (!Registry.TryGetVar("T", out var tv)) tv = 0.0;
                    double t = Convert.ToDouble(tv);
                    return (float)EvalMathExpr(lambda.Body, x, (float)t);
                }, xmin, xmax, 80, dynamic);

                return plot;
            }));
            Registry.RegisterFunc("rect", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                float w = ctx.ParseFloat(0, 1f);
                float h = ctx.ParseFloat(1, 1f);
                bool dyn = ctx.ParseBool(2, true);
                return new RectGpu(w, h, dyn);
            }));
            Registry.RegisterFunc("circle", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                float r = ctx.ParseFloat(0, 0.2f);
                int seg = ctx.ParseInt(1, 80);
                bool filled = ctx.ParseBool(2, false);
                bool dyn = ctx.ParseBool(3, true);
                return new CircleGpu(r, seg, filled, dyn);
            }));
            Registry.RegisterFunc("line", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                if (pos is { Length: >= 4 })
                    return new LineGpu(Convert.ToSingle(pos[0]), Convert.ToSingle(pos[1]), Convert.ToSingle(pos[2]),
                        Convert.ToSingle(pos[3]));
                return new LineGpu();
            }));
            Registry.RegisterFunc("triangle", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                // case: named vertices a/b/c provided
                if (named != null && (named.ContainsKey("a") || named.ContainsKey("b") || named.ContainsKey("c")))
                {
                    var a = ctx.ParseVector2("a");
                    var b = ctx.ParseVector2("b");
                    var c = ctx.ParseVector2("c");
                    bool filled = ctx.ParseBool("filled", true);
                    return new TriangleGpu(a, b, c, filled); // assuming TriangleGpu has such ctor
                }

                // fallback: centerX, centerY, size, filled
                if (pos is { Length: >= 3 })
                {
                    var center = new Vector2(Convert.ToSingle(pos[0]), Convert.ToSingle(pos[1]));
                    float size = Convert.ToSingle(pos[2]);
                    bool filled = pos.Length > 3 ? Convert.ToBoolean(pos[3]) : true;
                    return new TriangleGpu(center, size, filled);
                }

                return new TriangleGpu();
            }));
            Registry.RegisterFunc("text", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                string content = pos.Length > 0 ? Convert.ToString(pos[0]) ?? "" : "";
                float size = pos.Length > 1 ? Convert.ToSingle(pos[1]) : 0.1f;

                SKTypeface? typeface = null;
                if (pos.Length > 2 && pos[2] is string fontStr)
                {
                    typeface = SKTypeface.FromFamilyName(fontStr); // или используйте ваш FontManager, если есть
                }
                else if (named.TryGetValue("font", out var fontObj) && fontObj is string fontName)
                {
                    typeface = SKTypeface.FromFamilyName(fontName);
                }

                var txt = new TextGpu(content,typeface, size, isDynamic: true);

                if (named.TryGetValue("align", out var alignObj) && alignObj is string alignStr)
                {
                    var parts = alignStr.ToLower().Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 1)
                    {
                        txt.HAlign = parts[0] switch
                        {
                            "left" => TextGpu.HorizontalAlignment.Left,
                            "right" => TextGpu.HorizontalAlignment.Right,
                            _ => TextGpu.HorizontalAlignment.Center
                        };
                    }
                    if (parts.Length >= 2)
                    {
                        txt.VAlign = parts[1] switch
                        {
                            "top" => TextGpu.VerticalAlignment.Top,
                            "bottom" => TextGpu.VerticalAlignment.Bottom,
                            _ => TextGpu.VerticalAlignment.Center
                        };
                    }
                }

                if (named.TryGetValue("letterSpacing", out var ls)) txt.LetterSpacing = Convert.ToSingle(ls);
                if (named.TryGetValue("lineHeight", out var lh)) txt.LineHeight = Convert.ToSingle(lh);

                return txt;
            }));
            Registry.RegisterFunc("ellipse", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                float w = ctx.ParseFloat(0, 1f);
                float h = ctx.ParseFloat(1, 0.6f);
                int seg = ctx.ParseInt("segments", 80);
                bool filled = ctx.ParseBool("filled", false);
                bool dynamic = ctx.ParseBool("dynamic", true);

                return new EllipseGpu(w, h, seg, filled, dynamic);
            }));
            Registry.RegisterFunc("arc", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                float r = ctx.ParseFloat(2, 0.5f);
                float start = ctx.ParseFloat(3, 0f);
                float end = ctx.ParseFloat(4, 360f);
                int seg = ctx.ParseInt("segments", 64);
                bool dynamic = ctx.ParseBool("dynamic", true);

                return new ArcGpu(r,
                    MathHelper.DegreesToRadians(start),
                    MathHelper.DegreesToRadians(end),
                    seg, dynamic);
            }));
            Registry.RegisterFunc("arrow", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);

                Vector2 from = ctx.ParseVector2("from", new Vector2(-0.3f, -0.3f));
                Vector2 to   = ctx.ParseVector2("to",   new Vector2( 0.3f,  0.3f));
                float headSize = ctx.ParseFloat("headSize", 0.12f);
                float headAngle = ctx.ParseFloat("headAngle", 30f);
                bool dynamic = ctx.ParseBool("dynamic", true);

                return new ArrowGpu(from, to, headSize, headAngle, dynamic);
            }));
            Registry.RegisterFunc("bezier", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);

                Vector2 p0, p1, p2, p3 = Vector2.Zero;
                int segments = ctx.ParseInt("segments", 64);
                bool dynamic = ctx.ParseBool("dynamic", true);

                if (pos != null && pos.Length > 0 && pos[0] is object[] arr && arr.Length >= 3)
                {
                    // Вариант: bezier([[x,y], [x,y], [x,y], [x,y]?])
                    p0 = ParseVec(arr[0]);
                    p1 = ParseVec(arr[1]);
                    p2 = ParseVec(arr[2]);
                }
                else
                {
                    p0 = ctx.ParseVector2(0, Vector2.Zero);
                    p1 = ctx.ParseVector2(1, new Vector2(0.5f, 1f));
                    p2 = ctx.ParseVector2(2, new Vector2(-0.5f, -1f));
                }

                return new BezierCurveGpu(p0, p1, p2, segments, dynamic);

                Vector2 ParseVec(object o) => o switch
                {
                    Vector2 v => v,
                    float[] fa when fa.Length >= 2 => new Vector2(fa[0], fa[1]),
                    object[] oa when oa.Length >= 2 => new Vector2(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1])),
                    _ => Vector2.Zero
                };
            }));
            Registry.RegisterFunc("grid", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                int cx = ctx.ParseInt(0, 10);
                int cy = ctx.ParseInt(1, 10);
                float size = ctx.ParseFloat("size", 1f);
                bool dynamic = ctx.ParseBool("dynamic", true);

                return new GridGpu(cx, cy, size, dynamic);
            }));
            Registry.RegisterFunc("axis", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                var ctx = new CallContext(pos, named, this);
                float size = ctx.ParseFloat(0, 1f);
                bool dynamic = ctx.ParseBool("dynamic", true);

                return new AxisGpu(size, dynamic);
            }));



            Registry.RegisterFunc("aColor", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                if (pos == null || pos.Length == 0) throw new Exception("aColor: missing target");
                if (pos[0] is not PrimitiveGpu p) throw new Exception("aColor: target is not PrimitiveGpu");

                Vector4 to = p.Color;
                if (pos.Length > 1)
                {
                    var arg = pos[1];
                    to = arg switch
                    {
                        Vector4 v => v,
                        float[] fa when fa.Length >= 4 => new Vector4(fa[0], fa[1], fa[2], fa[3]),
                        object[] oa when oa.Length >= 4 => new Vector4(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]),
                            Convert.ToSingle(oa[2]), Convert.ToSingle(oa[3])),
                        _ => to
                    };
                }
                else if (named != null && named.TryGetValue("to", out var tv))
                {
                    to = tv switch
                    {
                        Vector4 v => v,
                        float[] fa when fa.Length >= 4 => new Vector4(fa[0], fa[1], fa[2], fa[3]),
                        object[] oa when oa.Length >= 4 => new Vector4(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1]),
                            Convert.ToSingle(oa[2]), Convert.ToSingle(oa[3])),
                        _ => to
                    };
                }

                float start = named != null && named.TryGetValue("start", out var s)
                    ? Convert.ToSingle(s)
                    : (CurrentScene?.T ?? 0f);
                float duration = named != null && named.TryGetValue("duration", out var d) ? Convert.ToSingle(d) : 1f;

                EaseType ease = EaseType.Linear;
                if (named != null && named.TryGetValue("ease", out var e) && e is string es)
                    ease = EaseHelper.Parse(es);

                p.AnimateColor(start, duration, ease, to);
                return p;
            }));
            Registry.RegisterFunc("aScale", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                if (pos == null || pos.Length == 0) throw new Exception("aScale: missing target");
                if (pos[0] is not PrimitiveGpu p) throw new Exception("aScale: target is not PrimitiveGpu");

                float to = p.Scale;
                if (pos.Length > 1) to = Convert.ToSingle(pos[1]);
                else if (named != null && named.TryGetValue("to", out var tv)) to = Convert.ToSingle(tv);

                float start = named != null && named.TryGetValue("start", out var s)
                    ? Convert.ToSingle(s)
                    : (CurrentScene?.T ?? 0f);
                float duration = named != null && named.TryGetValue("duration", out var d) ? Convert.ToSingle(d) : 1f;

                EaseType ease = EaseType.Linear;
                if (named != null && named.TryGetValue("ease", out var e) && e is string es)
                    ease = EaseHelper.Parse(es);

                p.AnimateScale(start, duration, ease, to);
                return p;
            }));
            Registry.RegisterFunc("aMove", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                if (pos == null || pos.Length == 0) throw new Exception("aMove: missing target");
                if (pos[0] is not PrimitiveGpu p) throw new Exception("aMove: target is not PrimitiveGpu");

                Vector2 to = p.Position;
                if (pos.Length >= 3)
                    to = new Vector2(Convert.ToSingle(pos[1]), Convert.ToSingle(pos[2]));
                else if (pos.Length >= 2)
                {
                    var arg = pos[1];
                    to = arg switch
                    {
                        Vector2 v => v,
                        float[] fa when fa.Length >= 2 => new Vector2(fa[0], fa[1]),
                        double[] da when da.Length >= 2 => new Vector2((float)da[0], (float)da[1]),
                        object[] oa when oa.Length >= 2 =>
                            new Vector2(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1])),
                        _ => to
                    };
                }
                else if (named != null && named.TryGetValue("to", out var tv))
                {
                    to = tv switch
                    {
                        Vector2 v => v,
                        float[] fa when fa.Length >= 2 => new Vector2(fa[0], fa[1]),
                        double[] da when da.Length >= 2 => new Vector2((float)da[0], (float)da[1]),
                        object[] oa when oa.Length >= 2 =>
                            new Vector2(Convert.ToSingle(oa[0]), Convert.ToSingle(oa[1])),
                        _ => to
                    };
                }

                float start = named != null && named.TryGetValue("start", out var s)
                    ? Convert.ToSingle(s)
                    : (CurrentScene?.T ?? 0f);
                float duration = named != null && named.TryGetValue("duration", out var d) ? Convert.ToSingle(d) : 1f;

                EaseType ease = EaseType.Linear;
                if (named != null && named.TryGetValue("ease", out var e) && e is string es)
                    ease = EaseHelper.Parse(es);

                p.AnimatePosition(start, duration, ease, to);
                return p;
            }));
            Registry.RegisterFunc("aRot", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                if (pos == null || pos.Length == 0) throw new Exception("aRot: missing target");
                if (pos[0] is not PrimitiveGpu p) throw new Exception("aRot: target is not PrimitiveGpu");

                float to = p.Rotation;
                if (pos.Length > 1) to = Convert.ToSingle(pos[1]);
                else if (named != null && named.TryGetValue("to", out var tv)) to = Convert.ToSingle(tv);

                float start = named != null && named.TryGetValue("start", out var s)
                    ? Convert.ToSingle(s)
                    : (CurrentScene?.T ?? 0f);
                float duration = named != null && named.TryGetValue("duration", out var d) ? Convert.ToSingle(d) : 1f;

                EaseType ease = EaseType.Linear;
                if (named != null && named.TryGetValue("ease", out var e) && e is string es)
                    ease = EaseHelper.Parse(es);

                p.AnimateRotation(start, duration, ease, to);
                return p;
            }));
            Registry.RegisterFunc("aMorph", new Func<object[], Dictionary<string, object>, object>((pos, named) =>
            {
                if (pos == null || pos.Length == 0)
                    throw new Exception("aMorph: missing target");

                if (pos[0] is not PrimitiveGpu p)
                    throw new Exception("aMorph: target is not a PrimitiveGpu");

                int offsetA = named.TryGetValue("offsetA", out var a) ? Convert.ToInt32(a) : 0;
                int offsetB = named.TryGetValue("offsetB", out var b) ? Convert.ToInt32(b) : 0;
                int offsetM = named.TryGetValue("offsetM", out var m) ? Convert.ToInt32(m) : 0;
                int vertexCount = named.TryGetValue("vertexCount", out var v) ? Convert.ToInt32(v) : 0;

                float start = named.TryGetValue("start", out var s) ? Convert.ToSingle(s) : 0f;
                float end = named.TryGetValue("duration", out var d) ? start + Convert.ToSingle(d) : 1f;

                EaseType ease = EaseType.EaseInOut;
                if (named.TryGetValue("ease", out var e) && e is string es)
                    ease = EaseHelper.Parse(es);

                p.AnimateMorph(start, end, ease, offsetA, offsetB, offsetM, vertexCount);
                return p;
            }));
        }

        // ------------------ End Builtin registration ------------------
    }

    public static class EaseHelper
    {
        public static EaseType Parse(string ease)
        {
            if (string.IsNullOrWhiteSpace(ease))
                return EaseType.Linear;

            switch (ease.Trim().ToLower())
            {
                case "linear": return EaseType.Linear;
                case "in": return EaseType.EaseIn;
                case "out": return EaseType.EaseOut;
                case "inout":
                case "easeinout": return EaseType.EaseInOut;
                default: return EaseType.Linear; // fallback на Linear
            }
        }
    }
}
