// ESharpEngine.cs
// Modular E# engine — parser + registry + evaluator
// Usage:
//   var engine = new ESharpEngine(arena);
//   engine.RegisterVar("pi", Math.PI);
//   engine.RegisterFunc("bgColor", new Func<object[],Dictionary<string,object>,object>(...));
//   engine.SetScene(myScene); // or engine.RegisterSceneFactory(...)
//
// Then engine.LoadScene(source, "fallbackName");

using System.Globalization;
using System.Numerics;
using System.Reflection;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;

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
                if (Current.Type == TokenType.Ident && Current.Text == "func" && Peek().Type == TokenType.Colon)
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
        public void RegisterInstanceMethods(object target,
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance)
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
                    for (int i = 0; i < parameters.Length && i < args.Length; i++)
                        callArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
                    return m.Invoke(target, callArgs);
                }));
            }
        }

        private static object ConvertArg(object src, Type targetType)
        {
            if (src == null) return null;
            if (targetType.IsInstanceOfType(src)) return src;
            try
            {
                return Convert.ChangeType(src, targetType);
            }
            catch
            {
                return src;
            }
        }

        // Try to invoke registered function. Supports three delegate shapes:
        // - delegate(object[]) -> object
        // - delegate(object[], Dictionary<string,object>) -> object
        // - delegates with normal parameter lists (including Func<object, object>, etc.)
        public bool TryInvoke(string name, object[] posArgs, Dictionary<string, object> namedArgs, out object result)
        {
            result = null;
            if (!_funcs.TryGetValue(name, out var list)) return false;

            foreach (var del in list)
            {
                var m = del.Method;
                var ps = m.GetParameters();

                // 1) exact shape: Func<object[], object>
                if (ps.Length == 1 && ps[0].ParameterType == typeof(object[]))
                {
                    try
                    {
                        result = del.DynamicInvoke(new object[] { posArgs });
                        return true;
                    }
                    catch
                    {
                        /* try next */
                    }
                }

                // 2) exact shape: Func<object[], Dictionary<string,object>, object>
                if (ps.Length == 2 && ps[0].ParameterType == typeof(object[]) &&
                    (ps[1].ParameterType == typeof(Dictionary<string, object>) ||
                     typeof(IDictionary<string, object>).IsAssignableFrom(ps[1].ParameterType)))
                {
                    try
                    {
                        result = del.DynamicInvoke(posArgs, namedArgs);
                        return true;
                    }
                    catch
                    {
                        /* try next */
                    }
                }

                // 3) delegate with a single non-array parameter (e.g. Func<object, object>) — if script supplied exactly one positional arg, pass it directly
                if (ps.Length == 1)
                {
                    try
                    {
                        if (posArgs.Length == 1)
                        {
                            // pass the single arg (not the whole array)
                            result = del.DynamicInvoke(posArgs[0]);
                            return true;
                        }
                        else
                        {
                            // try to call with no wrapping (attempt to match multiple parameters)
                            result = del.DynamicInvoke(posArgs);
                            return true;
                        }
                    }
                    catch
                    {
                        /* try next */
                    }
                }

                // 4) last resort: try DynamicInvoke with posArgs as varargs (covers delegates with N parameters)
                try
                {
                    result = del.DynamicInvoke(posArgs);
                    return true;
                }
                catch
                {
                    // try next overload / delegate
                }
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

        public ESharpEngine(GeometryArena arena, SceneGpu initialScene = null)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            if (initialScene != null) SetScene(initialScene);

            // default globals
            Registry.RegisterVar("PI", Math.PI);
            Registry.RegisterVar("T", 0.0);

            // ------------------ Registration helpers ------------------
            void RegisterPrimitives()
            {
                Registry.RegisterFunc("bgColor", new Func<object[], Dictionary<string, object>, object>((args, named) =>
                {
                    if (CurrentScene == null) throw new Exception("CurrentScene не установлена");

                    Vector3 color = new Vector3(0.1f, 0.1f, 0.1f);
                    if (args.Length > 0 && args[0] is float[] arr && arr.Length >= 3)
                        color = new Vector3(arr[0], arr[1], arr[2]);

                    float duration = named != null && named.TryGetValue("duration", out var d)
                        ? Convert.ToSingle(d)
                        : 1f;
                    CurrentScene.AnimateBackground(color, CurrentScene.T, CurrentScene.T + duration);
                    return null;
                }));

                Registry.RegisterFunc("Add", new Func<object, object>((arg) =>
                {
                    if (arg is PrimitiveGpu prim)
                    {
                        CurrentScene.AddPrimitive(prim);
                        return prim;
                    }

                    throw new Exception($"Add: expected PrimitiveGpu, got {arg?.GetType().Name}");
                }));

                Registry.RegisterFunc("plot", new Func<object[], Dictionary<string, object>, object>((posArgs, named) =>
                {
                    var lambda = parseFunc(named, "func");
                    float xmin = parseFloat(named, "xmin", -1);
                    float xmax = parseFloat(named, "xmax", 1);
                    int segments = parseInt(named, "segments", 80);
                    bool dynamic = parseBool(named, "dynamic");

                    var plot = new PlotGpu(x =>
                    {
                        var t = Registry.TryGetVar("T", out var tv) ? Convert.ToDouble(tv) : 0.0;
                        return (float)EvalMathExpr(lambda.Body, x, (float)t);
                    }, xmin, xmax, segments, dynamic);

                    CurrentScene?.AddPrimitive(plot);
                    return plot;
                }));

                Registry.RegisterFunc("rect", new Func<object[], Dictionary<string, object>, object>((_, named) =>
                    new RectGpu(
                        parseFloat(named, "width", 1f),
                        parseFloat(named, "height", 1f),
                        parseBool(named, "dynamic")
                    )
                ));

                Registry.RegisterFunc("circle", new Func<object[], Dictionary<string, object>, object>((_, named) =>
                    new CircleGpu(
                        parseFloat(named, "radius", 0.2f),
                        parseInt(named, "segments", 80),
                        parseBool(named, "filled"),
                        parseBool(named, "dynamic")
                    )
                ));

                Registry.RegisterFunc("line", new Func<object[], Dictionary<string, object>, object>((_, named) =>
                    new LineGpu(
                        parseFloat(named, "x0"),
                        parseFloat(named, "y0"),
                        parseFloat(named, "x1"),
                        parseFloat(named, "y1")
                    )
                ));

                Registry.RegisterFunc("triangle", new Func<object[], Dictionary<string, object>, object>((_, named) =>
                    new TriangleGpu(
                        new Vector2(parseFloat(named, "cx"), parseFloat(named, "cy")),
                        parseFloat(named, "size"),
                        parseBool(named, "filled", true)
                    )
                ));

                Registry.RegisterFunc("text", new Func<object[], Dictionary<string, object>, object>((_, named) =>
                {
                    string text = parseString(named, "text");
                    string font = parseString(named, "font", "");
                    float size = parseFloat(named, "size", 0.1f);
                    return new TextGpu(text, size, font);
                }));

                Registry.RegisterFunc("aColor", new Func<object[], Dictionary<string, object>, object>((args, named) =>
                {
                    if (args == null || args.Length == 0) throw new Exception("aColor: missing target");
                    if (args[0] is not PrimitiveGpu p) throw new Exception("aColor: target is not a PrimitiveGpu");

                    Vector4 from = p.Color;
                    Vector4 to = p.Color;

                    if (named != null)
                    {
                        if (named.TryGetValue("from", out var fArr)) from = ConvertToVector4(fArr, p.Color);
                        if (named.TryGetValue("to", out var tArr)) to = ConvertToVector4(tArr, p.Color);
                    }

                    float relStart = named != null && named.TryGetValue("start", out var s) ? Convert.ToSingle(s) : 0f;
                    float duration = named != null && named.TryGetValue("duration", out var d) ? Convert.ToSingle(d) : 1f;
                    float sceneT = CurrentScene != null ? CurrentScene.T : 0f;
                    float startAbs = sceneT + relStart;
                    float endAbs = startAbs + duration;

                    EaseType ease = EaseType.Linear;
                    if (named != null && named.TryGetValue("ease", out var e) && e is string es)
                        ease = EaseHelper.Parse(es);

                    // 🔹 если анимация должна уже начаться, сразу выставляем from
                    if (sceneT >= startAbs)
                        p.Color = from;

                    if (duration <= 0f || sceneT >= endAbs)
                    {
                        p.Color = to; // мгновенный переход
                    }
                    else
                    {
                        // добавляем в систему анимаций GPU
                        p.AnimateColor(startAbs, endAbs, ease, to);
                    }

                    return p;
                }));
            }

            RegisterPrimitives();
        }

        private float parseFloat(Dictionary<string, object> named, string key, float defaultValue = 0f)
        {
            if (named != null && named.TryGetValue(key, out var val))
                return Convert.ToSingle(val);
            return defaultValue;
        }

        private int parseInt(Dictionary<string, object> named, string key, int defaultValue = 0)
        {
            if (named != null && named.TryGetValue(key, out var val))
                return Convert.ToInt32(val);
            return defaultValue;
        }

        private bool parseBool(Dictionary<string, object> named, string key, bool defaultValue = false)
        {
            if (named != null && named.TryGetValue(key, out var val))
            {
                if (val is bool b) return b;
                if (val is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (val is double d) return d != 0;
                if (val is int i) return i != 0;
            }
            return defaultValue;
        }

        private LambdaExpr parseFunc(Dictionary<string, object> named, string key)
        {
            if (named == null || !named.TryGetValue(key, out var val) || val is not LambdaExpr lambda)
                throw new Exception($"{key} требуется как LambdaExpr: x => ...");
            return lambda;
        }
        
        private string parseString(Dictionary<string, object> named, string key, string defaultValue = "")
        {
            if (named != null && named.TryGetValue(key, out var val))
            {
                return val switch
                {
                    Expr e => Eval(e)?.ToString() ?? defaultValue,
                    _ => val?.ToString() ?? defaultValue
                };
            }
            return defaultValue;
        }
        
        private Vector4 ConvertToVector4(object obj, Vector4 defaultValue)
        {
            if (obj is float[] fArr && fArr.Length >= 4) return new Vector4(fArr[0], fArr[1], fArr[2], fArr[3]);
            if (obj is double[] dArr && dArr.Length >= 4) return new Vector4((float)dArr[0], (float)dArr[1], (float)dArr[2], (float)dArr[3]);
            if (obj is Array a && a.Length >= 4)
            {
                float[] tmp = new float[4];
                for (int i = 0; i < 4; i++) tmp[i] = Convert.ToSingle(a.GetValue(i));
                return new Vector4(tmp[0], tmp[1], tmp[2], tmp[3]);
            }
            return defaultValue;
        }

        // Caller can provide factory if they want engine to build scenes by name
        public void RegisterSceneFactory(Func<GeometryArena, string, SceneGpu> factory) => SceneFactory = factory;

        // Or set scene manually (preferred if you have a concrete scene instance)
        public void SetScene(SceneGpu scene)
        {
            CurrentScene = scene ?? throw new ArgumentNullException(nameof(scene));
            Registry.RegisterVar("scene", CurrentScene);
            Registry.RegisterInstanceMethods(CurrentScene);
        }

        public void UpdateTime(double t) => Registry.RegisterVar("T", t);

        public void LoadSceneFromFile(string path) => LoadScene(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

        public void LoadScene(string source, string fallbackName)
        {
            // Создаем сцену через фабрику, если текущая сцена не установлена
            if (CurrentScene == null)
            {
                if (SceneFactory == null)
                    throw new Exception("No scene set and no SceneFactory registered. Call SetScene(...) or RegisterSceneFactory(...).");

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
            ArrayExpr a => a.Items.Select(Eval).Select(Convert.ToDouble).Select(d => (float)d).ToArray(),
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
            // evaluate named args into values for engine-level helpers; keep Expr map for plot special-case
            var namedValues = call.NamedArgs?.ToDictionary(k => k.Key, k => Eval(k.Value), StringComparer.OrdinalIgnoreCase);
            var namedExprs = call.NamedArgs; // keep exprs if needed (plot)

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
                try { return del.DynamicInvoke(pos); } catch { }
                try { return del.DynamicInvoke(new object[] { pos }); } catch { }
                try { return del.DynamicInvoke(pos, namedValues); } catch { }
            }

            throw new Exception("Невозможно вызвать выражение как функцию");
        }

        private object EvalMemberCall(MemberCallExpr call)
        {
            var target = Eval(call.Target);
            var pos = call.Args.Select(Eval).ToArray();
            Dictionary<string, object> namedValues = null;
            if (call.NamedArgs != null)
            {
                namedValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in call.NamedArgs)
                    namedValues[kv.Key] = Eval(kv.Value);
            }

            if (target == null) throw new Exception($"Null target for method {call.Method}");

            // --- 🔹 ключевая правка: prepend target к аргументам для глобальной функции
            if (Registry.TryInvoke(call.Method, PrependArg(target, pos), namedValues, out var res))
                return res;

            // дальше уже обычный поиск метода на объекте
            var t = target.GetType();
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Equals(call.Method, StringComparison.OrdinalIgnoreCase))
                .ToArray();

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
                }
            }

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (ps.Length != 2 ||
                    !typeof(object[]).IsAssignableFrom(ps[0].ParameterType) ||
                    !typeof(IDictionary<string, object>).IsAssignableFrom(ps[1].ParameterType)) continue;
                try
                {
                    return m.Invoke(target, new object[] { pos, namedValues });
                }
                catch
                {
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

        private double EvalMathExpr(Expr expr, float x, float t)
        {
            return expr switch
            {
                NumberExpr n => n.Value,
                IdentExpr i => i.Name == "x" ? x : i.Name == "T" ? t : (Registry.TryGetVar(i.Name, out var v) ? Convert.ToDouble(v) : 0.0),
                BinaryExpr b => EvalBinaryMath(b, x, t),
                CallExpr c when c.Callee is IdentExpr id => CallMathFunc(id.Name, c.Args.Select(a => (float)EvalMathExpr(a, x, t)).ToArray()),
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
