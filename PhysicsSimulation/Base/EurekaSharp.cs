// ESharpEngine.cs
// Modular E# engine — parser + registry + evaluator
// Usage:
//   var engine = new ESharpEngine(arena);
//   engine.RegisterVar("pi", Math.PI);
//   engine.RegisterFunc("bgColor", new Func<object[],Dictionary<string,object>,object>(...));
//   engine.SetScene(myScene); // or engine.RegisterSceneFactory(...)
//
// Then engine.LoadScene(source, "fallbackName");

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private int _pos = 0;
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
            if (Current.Type == TokenType.Ident && Peek(1).Type == TokenType.Assign)
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
                case TokenType.Number: return new NumberExpr(double.Parse(Advance().Text, System.Globalization.CultureInfo.InvariantCulture));
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
                if (Current.Type == TokenType.Ident && Current.Text == "func" && Peek(1).Type == TokenType.Colon)
                {
                    Advance(); // func
                    Advance(); // :
                    var param = ExpectIdent();
                    Expect(TokenType.Arrow);
                    // body is any expression
                    var body = ParseExpression();
                    named["func"] = new LambdaExpr(param, body);
                }
                else if (Current.Type == TokenType.Ident && Peek(1).Type == TokenType.Colon)
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
                    try { result = del.DynamicInvoke(new object[] { posArgs, namedArgs }); return true; } catch { }
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
        public DslRegistry Registry { get; } = new DslRegistry();

        // Optional factory if caller wants engine to create scenes by name
        public Func<GeometryArena, string, SceneGpu> SceneFactory { get; private set; }

        public ESharpEngine(GeometryArena arena, SceneGpu initialScene = null)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            if (initialScene != null) SetScene(initialScene);

            // default globals
            Registry.RegisterVar("PI", Math.PI);
            Registry.RegisterVar("T", 0.0);

            // core helper: bgColor (works if scene provided)
            Registry.RegisterFunc("bgColor", new Func<object[], Dictionary<string, object>, object>((args, named) =>
            {
                if (CurrentScene == null) throw new Exception("CurrentScene is not set. Call SetScene(...) or RegisterSceneFactory(...).");
                var color = args.Length > 0 ? (float[])args[0] : new[] { 0.1f, 0.1f, 0.1f };
                var duration = named != null && named.TryGetValue("duration", out var d) ? Convert.ToDouble(d) : 1.0;
                CurrentScene.AnimateBackground(new Vector3(color[0], color[1], color[2]), CurrentScene.T, CurrentScene.T + (float)duration);
                return null;
            }));
            
            Registry.RegisterFunc("Add", new Func<object[], object>(args =>
            {
                if (args == null || args.Length == 0) throw new Exception("Add требует примитив как аргумент");
                if (CurrentScene == null) throw new Exception("CurrentScene не установлена");
                if (args[0] is not PrimitiveGpu prim) throw new Exception("Первый аргумент Add должен быть PrimitiveGpu");
                CurrentScene.AddPrimitive(prim);
                return prim;
            }));

            Registry.RegisterFunc("plot", new Func<object[], object>(_ => throw new Exception("plot handled internally by engine.")));
            
            Registry.RegisterFunc("rect", new Func<object[], object>(args =>
            {
                float w = args.Length > 0 ? Convert.ToSingle(args[0]) : 1f;
                float h = args.Length > 1 ? Convert.ToSingle(args[1]) : 1f;
                bool dyn = args.Length > 2 ? Convert.ToBoolean(args[2]) : false;
                return new RectGpu(w, h, dyn);
            }));

            Registry.RegisterFunc("circle", new Func<object[], object>(args =>
            {
                float r = args.Length > 0 ? Convert.ToSingle(args[0]) : 0.2f;
                int seg = args.Length > 1 ? Convert.ToInt32(args[1]) : 80;
                bool filled = args.Length > 2 ? Convert.ToBoolean(args[2]) : false;
                bool dyn = args.Length > 3 ? Convert.ToBoolean(args[3]) : false;
                return new CircleGpu(r, seg, filled, dyn);
            }));

            Registry.RegisterFunc("line", new Func<object[], object>(args =>
            {
                if (args.Length >= 4)
                    return new LineGpu(Convert.ToSingle(args[0]), Convert.ToSingle(args[1]), Convert.ToSingle(args[2]), Convert.ToSingle(args[3]));
                return new LineGpu();
            }));

            Registry.RegisterFunc("triangle", new Func<object[], object>(args =>
            {
                // overloads can be extended. simple center/size variant:
                if (args.Length >= 3 && args[0] is float cx)
                {
                    // user provided center/size/filled
                    var center = new System.Numerics.Vector2(Convert.ToSingle(args[0]), Convert.ToSingle(args[1]));
                    float size = Convert.ToSingle(args[2]);
                    bool filled = args.Length > 3 ? Convert.ToBoolean(args[3]) : true;
                    var tri = new TriangleGpu(center, size, filled);
                    return tri;
                }
                return new TriangleGpu();
            }));

            Registry.RegisterFunc("text", new Func<object[], object>(args =>
            {
                string text = args.Length > 0 ? args[0]?.ToString() ?? "" : "";
                float size = args.Length > 1 ? Convert.ToSingle(args[1]) : 0.1f;
                string fontKey = args.Length > 2 ? args[2]?.ToString() : null;
                return new TextGpu(text, size, fontKey);
            }));
            
            Registry.RegisterFunc("aColor", new Func<object[], Dictionary<string, object>, object>((args, named) =>
            {
                if (args == null || args.Length == 0) throw new Exception("aColor: missing target");
                if (args[0] is PrimitiveGpu p)
                {
                    Vector4 to = p.Color;
                    if (named != null && named.TryGetValue("to", out var tv) && tv is float[] arr && arr.Length >= 4)
                        to = new Vector4(arr[0], arr[1], arr[2], arr[3]);

                    float start = named != null && named.TryGetValue("start", out var s) ? Convert.ToSingle(s) : 0f;
                    float end = named != null && named.TryGetValue("duration", out var d) ? start + Convert.ToSingle(d) : 1f;

                    EaseType ease = EaseType.Linear;
                    if (named != null && named.TryGetValue("ease", out var e) && e is string es)
                        ease = EaseHelper.Parse(es);

                    p.AnimateColor(start, end, ease, to);
                    return p;
                }
                throw new Exception("aColor: target is not a PrimitiveGpu");
            }));
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
                // special-case plot: engine-level handling if func lambda provided
                if (string.Equals(ident.Name, "plot", StringComparison.OrdinalIgnoreCase))
                {
                    return CreatePlotFromArgs(pos, namedExprs);
                }

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
                try { return del.DynamicInvoke(new object[] { pos, namedValues }); } catch { }
            }

            throw new Exception("Невозможно вызвать выражение как функцию");
        }

        private object EvalMemberCall(MemberCallExpr call)
        {
            var target = Eval(call.Target);
            var pos = call.Args.Select(Eval).ToArray();
            var named = call.NamedArgs?.ToDictionary(k => k.Key, k => Eval(k.Value), StringComparer.OrdinalIgnoreCase);

            // first, try registry-registered function by method name (allows overriding instance methods)
            if (Registry.TryInvoke(call.Method, PrependArg(target, pos), named, out var res)) return res;

            if (target == null) throw new Exception($"Null target for method {call.Method}");

            // reflection fallback: find method on target
            var t = target.GetType();
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.Equals(call.Method, StringComparison.OrdinalIgnoreCase)).ToArray();
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
                catch { /* try next overload */ }
            }

            throw new Exception($"Метод {call.Method} не найден у {target.GetType().Name}");
        }

        private static object[] PrependArg(object first, object[] rest)
        {
            var arr = new object[rest.Length + 1]; arr[0] = first; Array.Copy(rest, 0, arr, 1, rest.Length); return arr;
        }

        // ------------------ Plot integration ------------------
        // Expects that PlotGpu(Func<float,float>, float xmin, float xmax, bool isDynamic) exists in project.
        // If your PlotGpu signature differs — adjust this method accordingly.
        private PrimitiveGpu CreatePlotFromArgs(object[] pos, Dictionary<string, Expr> namedExprs)
        {
            // namedExprs contains 'func' => LambdaExpr
            if (namedExprs == null || !namedExprs.TryGetValue("func", out var fexpr) || fexpr is not LambdaExpr lambda)
                throw new Exception("plot требует func: x => ...");

            float xmin = -1, xmax = 1; bool dynamic = false;
            if (namedExprs.TryGetValue("xmin", out var xminE)) xmin = Convert.ToSingle(Eval(xminE));
            if (namedExprs.TryGetValue("xmax", out var xmaxE)) xmax = Convert.ToSingle(Eval(xmaxE));
            if (namedExprs.TryGetValue("dynamic", out var dynE)) dynamic = Convert.ToBoolean(Eval(dynE));

            // create PlotGpu using the project's implementation
            try
            {
                var plot = new PlotGpu(x =>
                {
                    double t = Convert.ToDouble(Registry.TryGetVar("T", out var tv) ? tv : 0.0);
                    return (float)EvalMathExpr(lambda.Body, (float)x, (float)t);
                }, xmin, xmax, isDynamic: dynamic);
                
                return plot;
            }
            catch (Exception ex)
            {
                throw new Exception("Не удалось создать PlotGpu — проверьте сигнатуру конструктора PlotGpu в проекте. " + ex.Message, ex);
            }
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
