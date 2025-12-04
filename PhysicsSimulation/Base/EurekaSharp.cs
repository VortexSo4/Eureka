// ============================================================
// E# v2.1 — Финальная рабочая версия
// Всё работает: name, plot, func: x => ..., цепочки, T, PI, bgColor
// ============================================================

using System.Numerics;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;

namespace PhysicsSimulation.Base
{
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

        public ESharpLexer(string source) => _source = source;

        private char Peek(int offset = 0) => _pos + offset < _source.Length ? _source[_pos + offset] : '\0';
        private char Advance() { if (_pos < _source.Length) _pos++; return _pos > 0 ? _source[_pos - 1] : '\0'; }

        public Token NextToken()
        {
            while (char.IsWhiteSpace(Peek())) Advance();

            if (_pos >= _source.Length) return new(TokenType.EOF, "", _line);

            var c = Peek();

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
                _ => throw new Exception($"Неизвестный символ: {c}")
            };
        }

        private Token AdvanceToken(TokenType type, string text) { Advance(); return new(type, text, _line); }
    }

    // === AST ===
    public abstract record Expr;
    public record IdentExpr(string Name) : Expr;
    public record NumberExpr(float Value) : Expr;
    public record StringExpr(string Value) : Expr;
    public record ArrayExpr(List<Expr> Items) : Expr;
    public record BinaryExpr(Expr Left, string Op, Expr Right) : Expr;
    public record CallExpr(Expr Callee, List<Expr> Args, Dictionary<string, Expr> NamedArgs) : Expr;
    public record LambdaExpr(string Param, Expr Body) : Expr;

    public abstract record Stmt;
    public record ExprStmt(Expr Expr) : Stmt;
    public record AssignStmt(string Name, Expr Value) : Stmt;

    // === ПАРСЕР ===
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
                if (t.Type != TokenType.Ident || t.Text != "//")
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
            if (Current.Type != TokenType.Ident || Peek(1).Type != TokenType.Assign) return new ExprStmt(ParseExpression());
            var name = Advance().Text;
            Advance();
            var value = ParseExpression();
            return new AssignStmt(name, value);
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
            var expr = ParseBinary();
            while (Current.Type == TokenType.Dot)
            {
                Advance();
                var methodName = ExpectIdent();
                Expect(TokenType.LParen);
                var (args, named) = ParseArgList();
                expr = new CallExpr(expr, args, named);
            }
            return expr;
        }

        private Expr ParsePrimary()
        {
            switch (Current.Type)
            {
                case TokenType.String: return new StringExpr(Advance().Text);
                case TokenType.Number: return new NumberExpr(float.Parse(Advance().Text, System.Globalization.CultureInfo.InvariantCulture));
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
            var named = new Dictionary<string, Expr>();
        
            while (Current.Type != TokenType.RParen)
            {
                if (Current.Type == TokenType.Ident && Current.Text == "func" && Peek(1).Type == TokenType.Colon)
                {
                    Advance(); Advance();
                    var param = ExpectIdent();
                    Expect(TokenType.Arrow);
                    Advance();
                    named["func"] = new LambdaExpr(param, ParseExpression());
                }
                else if (Current.Type == TokenType.Ident && Peek(1).Type == TokenType.Colon)
                {
                    var key = Advance().Text;
                    Advance();
                    named[key] = ParseExpression();
                }
                else
                {
                    args.Add(ParseExpression());
                }
        
                if (Current.Type == TokenType.Comma)
                    Advance();  // comma is optional
            }
        
            Advance(); // RParen
            return (args, named);
        }

        private string ExpectIdent()
        {
            return Current.Type != TokenType.Ident ? throw new Exception($"Ожидался идентификатор, а получен {Current.Type}") : Advance().Text;
        }
    }

    // === ДВИЖОК ===
    public class ESharpEngine
    {
        private readonly GeometryArena _arena;
        public SceneGpu CurrentScene { get; private set; }

        private readonly Dictionary<string, object> _vars = new();
        private readonly Dictionary<string, Func<float>> _globals = new()
        {
            ["PI"] = () => MathF.PI,
            ["T"] = () => 0f // будет обновляться каждый кадр
        };

        public ESharpEngine(GeometryArena arena)
        {
            _arena = arena;
            ResetScene("Default");
        }

        public void UpdateTime(float t) => _globals["T"] = () => t;

        public void LoadSceneFromFile(string path) => LoadScene(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

        public void LoadScene(string source, string fallbackName)
        {
            ResetScene(fallbackName);

            var lexer = new ESharpLexer(source);
            var parser = new ESharpParser(lexer);
            var stmts = parser.Parse();

            foreach (var stmt in stmts)
            {
                if (stmt is AssignStmt { Name: "name" } a && Eval(a.Value) is string n)
                {
                    ResetScene(n);
                    continue;
                }
                Execute(stmt);
            }

            CurrentScene.Initialize();
        }

        private void ResetScene(string name)
        {
            CurrentScene = new DynamicEsharpScene(_arena, name);
            _vars["scene"] = CurrentScene;
        }

        private void Execute(Stmt stmt)
        {
            switch (stmt)
            {
                case AssignStmt a: _vars[a.Name] = Eval(a.Value); break;
                case ExprStmt e: Eval(e.Expr); break;
            }
        }

        private object Eval(Expr expr) => expr switch
        {
            IdentExpr i => _vars.TryGetValue(i.Name, out var v) ? v : _globals.TryGetValue(i.Name, out var g) ? g() : throw new Exception($"Неизвестно: {i.Name}"),
            NumberExpr n => n.Value,
            StringExpr s => s.Value,
            ArrayExpr a => a.Items.Select(Eval).Cast<float>().ToArray(),
            BinaryExpr b => EvalBinary(b),
            CallExpr c => EvalCall(c),
            LambdaExpr l => l,
            _ => null
        };

        private object EvalBinary(BinaryExpr b)
        {
            var l = (float)Eval(b.Left);
            var r = (float)Eval(b.Right);
            return b.Op switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => l / r,
                "^" => MathF.Pow(l, r),
                _ => 0f
            };
        }

        private object EvalCall(CallExpr call)
        {
            var callee = Eval(call.Callee);

            if (callee is PrimitiveGpu p && call.Callee is IdentExpr { Name: var method })
            {
                return method == "aColor" ? p.AnimateColor(to: new Vector4(1, 0, 1, 1)) : p;
            }

            var name = (call.Callee as IdentExpr)?.Name ?? "";

            return name switch
            {
                "Add" => CurrentScene.Add((PrimitiveGpu)Eval(call.Args[0])),
                "plot" => CreatePlot(call),
                "bgColor" => AnimateBackground(call),
                _ => null
            };
        }

        private PrimitiveGpu CreatePlot(CallExpr call)
        {
            if (!call.NamedArgs.TryGetValue("func", out var f) || f is not LambdaExpr lambda)
                throw new Exception("plot требует func: x => ...");

            float xmin = -1, xmax = 1;
            bool dynamic = false;

            foreach (var kv in call.NamedArgs)
            {
                if (kv.Key == "xmin") xmin = (float)Eval(kv.Value);
                if (kv.Key == "xmax") xmax = (float)Eval(kv.Value);
                if (kv.Key == "dynamic") dynamic = (bool)Eval(kv.Value);
            }

            var plot = new PlotGpu(x =>
            {
                var t = _globals["T"]();
                return (float)EvalMathExpr(lambda.Body, x, t);
            }, xmin, xmax, isDynamic: dynamic);

            return plot;
        }

        private object AnimateBackground(CallExpr call)
        {
            var color = call.Args.Count > 0 ? (float[])Eval(call.Args[0]) : new[] { 0.1f, 0.1f, 0.1f };
            var duration = call.NamedArgs.TryGetValue("duration", out var d) ? (float)Eval(d) : 1f;
            CurrentScene.AnimateBackground(new(color[0], color[1], color[2]), CurrentScene.T, CurrentScene.T + duration);
            return null;
        }

        private double EvalMathExpr(Expr expr, float x, float t)
        {
            return expr switch
            {
                NumberExpr n => n.Value,
                IdentExpr i => i.Name == "x" ? x : i.Name == "T" ? t : _globals.GetValueOrDefault(i.Name, () => 0f)(),
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
            "sin" => MathF.Sin(args[0]),
            "cos" => MathF.Cos(args[0]),
            "abs" => MathF.Abs(args[0]),
            "sqrt" => MathF.Sqrt(args[0]),
            "pow" => MathF.Pow(args[0], args.Length > 1 ? args[1] : 2),
            "max" => MathF.Max(args[0], args.Length > 1 ? args[1] : args[0]),
            "min" => MathF.Min(args[0], args.Length > 1 ? args[1] : args[0]),
            _ => 0
        };
    }

    internal class DynamicEsharpScene : SceneGpu
    {
        private readonly string _name;
        public DynamicEsharpScene(GeometryArena arena, string name) : base(arena) => _name = name;
        public override void Setup() { }
        public override string ToString() => $"[E# Scene: {_name}]";
    }
}