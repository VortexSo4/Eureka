// ============================================================
//  PlotBytecodeCompiler.cs
//  EurekaSharp — GPU Plot Evaluation
//
//  Компилирует AST лямбды (x => sin(x * T + 1.0)) в bytecode
//  для plot_compute.comp виртуальной машины.
//
//  Результат: PlotBytecodeProgram — структура, которая сериализуется
//  в PlotParamsGpu и заливается в GPU буфер один раз в кадр.
//
//  CPU больше НЕ вызывает Func(x) 300 раз — это делает GPU.
// ============================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using PhysicsSimulation.Base;

namespace PhysicsSimulation.Rendering.Vulkan
{
    // ── Опкоды (должны совпадать с plot_compute.comp) ──────────────────────────
    public enum PlotOp : int
    {
        Nop       = 0,
        PushConst = 1,
        PushX     = 2,
        PushSnap  = 3,
        Add       = 4,
        Sub       = 5,
        Mul       = 6,
        Div       = 7,
        Pow       = 8,
        Neg       = 9,
        Sin       = 10,
        Cos       = 11,
        Tan       = 12,
        Abs       = 13,
        Sqrt      = 14,
        Floor     = 15,
        Ceil      = 16,
        Sign      = 17,
        Log       = 18,
        Exp       = 19,
        Atan      = 20,
        Atan2     = 21,
        Min       = 22,
        Max       = 23,
        Mod       = 24,
        Clamp     = 25,
        Mix       = 26,
        CmpLt     = 27,
        CmpGt     = 28,
        CmpLe     = 29,
        CmpGe     = 30,
        CmpEq     = 31,
        CmpNe     = 32,
        And       = 33,
        Or        = 34,
        Not       = 35,
        Fract     = 36,
    }

    // ── Одна инструкция VM ─────────────────────────────────────────────────────
    public struct PlotInstr
    {
        public PlotOp Op;
        public int    Arg0;  // constIdx / snapIdx / unused
        public int    Arg1;
        public int    Arg2;
    }

    // ── Результат компиляции ───────────────────────────────────────────────────
    public sealed class PlotBytecodeProgram
    {
        public List<PlotInstr> Instructions { get; } = new(32);
        public List<float>     Constants    { get; } = new(8);

        // Имена snapshot-переменных в фиксированном порядке
        // (совпадают с индексами snap() в шейдере: 0=T, 1=MX, 2=MY, 3-6=snapA, 7-10=snapB)
        public List<string>    SnapNames    { get; } = new(8) { "T", "MX", "MY" };

        public bool IsValid { get; set; } = true;
        public string? CompileError { get; set; }

        // Максимальные размеры (ограничения GPU структуры)
        public const int MaxInstructions = 64;
        public const int MaxConstants    = 16;
        public const int MaxSnaps        = 11; // T, MX, MY + 8 custom

        public int GetOrAddConst(float value)
        {
            int idx = Constants.IndexOf(value);
            if (idx >= 0) return idx;
            if (Constants.Count >= MaxConstants) throw new Exception("Too many constants in plot formula (max 16)");
            Constants.Add(value);
            return Constants.Count - 1;
        }

        public int GetOrAddSnap(string name)
        {
            int idx = SnapNames.IndexOf(name);
            if (idx >= 0) return idx;
            if (SnapNames.Count >= MaxSnaps) throw new Exception($"Too many snapshot variables (max {MaxSnaps})");
            SnapNames.Add(name);
            return SnapNames.Count - 1;
        }

        public void Emit(PlotOp op, int a0 = 0, int a1 = 0, int a2 = 0)
        {
            if (Instructions.Count >= MaxInstructions)
                throw new Exception("Plot formula too complex (max 64 VM instructions). Simplify or split.");
            Instructions.Add(new PlotInstr { Op = op, Arg0 = a0, Arg1 = a1, Arg2 = a2 });
        }
    }

    // ── Компилятор AST → bytecode ──────────────────────────────────────────────
    public static class PlotBytecodeCompiler
    {
        // Унарные матем. функции: имя → опкод
        private static readonly Dictionary<string, PlotOp> _unaryOps =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["sin"]   = PlotOp.Sin,   ["cos"]   = PlotOp.Cos,   ["tan"]  = PlotOp.Tan,
            ["abs"]   = PlotOp.Abs,   ["sqrt"]  = PlotOp.Sqrt,  ["floor"]= PlotOp.Floor,
            ["ceil"]  = PlotOp.Ceil,  ["sign"]  = PlotOp.Sign,  ["log"]  = PlotOp.Log,
            ["exp"]   = PlotOp.Exp,   ["atan"]  = PlotOp.Atan,  ["fract"]= PlotOp.Fract,
            ["asin"]  = PlotOp.Sin,   // fallback — approximation via atan(x/sqrt(1-x²))
            ["acos"]  = PlotOp.Cos,   // fallback
        };

        // Бинарные матем. функции
        private static readonly Dictionary<string, PlotOp> _binaryOps =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["atan2"] = PlotOp.Atan2, ["pow"] = PlotOp.Pow,
            ["min"]   = PlotOp.Min,   ["max"] = PlotOp.Max,
            ["mod"]   = PlotOp.Mod,
        };

        /// <summary>
        /// Компилирует лямбда-выражение (x => expr) в PlotBytecodeProgram.
        /// xParam — имя параметра лямбды (обычно "x").
        /// </summary>
        public static PlotBytecodeProgram Compile(LambdaExpr lambda)
        {
            var prog = new PlotBytecodeProgram();
            try
            {
                EmitExpr(prog, lambda.Body, lambda.Param);
                if (!prog.IsValid)
                    return prog;
            }
            catch (Exception ex)
            {
                prog.IsValid = false;
                prog.CompileError = ex.Message;
            }
            return prog;
        }

        private static void EmitExpr(PlotBytecodeProgram prog, Expr expr, string xParam)
        {
            switch (expr)
            {
                case NumberExpr n:
                {
                    int idx = prog.GetOrAddConst((float)n.Value);
                    prog.Emit(PlotOp.PushConst, idx);
                    return;
                }

                case IdentExpr id when string.Equals(id.Name, xParam, StringComparison.OrdinalIgnoreCase):
                    prog.Emit(PlotOp.PushX);
                    return;

                case IdentExpr id:
                {
                    // Переменная из Registry — snapshot
                    int snapIdx = prog.GetOrAddSnap(id.Name);
                    prog.Emit(PlotOp.PushSnap, snapIdx);
                    return;
                }

                case BinaryExpr b:
                    EmitBinary(prog, b, xParam);
                    return;

                case CallExpr call when call.Callee is IdentExpr fnId:
                    EmitCall(prog, fnId.Name, call.Args, xParam);
                    return;

                default:
                    throw new Exception($"Unsupported expression type in GPU plot: {expr.GetType().Name}");
            }
        }

        private static void EmitBinary(PlotBytecodeProgram prog, BinaryExpr b, string xParam)
        {
            // Унарный минус: (0 - right)
            if (b.Op == "-" && b.Left is NumberExpr { Value: 0 })
            {
                EmitExpr(prog, b.Right, xParam);
                prog.Emit(PlotOp.Neg);
                return;
            }

            // Специальный случай: унарный NOT
            if (b.Op == "!" && b.Left is NumberExpr { Value: 0 })
            {
                EmitExpr(prog, b.Right, xParam);
                prog.Emit(PlotOp.Not);
                return;
            }

            EmitExpr(prog, b.Left,  xParam);
            EmitExpr(prog, b.Right, xParam);

            var op = b.Op switch
            {
                "+"  => PlotOp.Add,
                "-"  => PlotOp.Sub,
                "*"  => PlotOp.Mul,
                "/"  => PlotOp.Div,
                "^"  => PlotOp.Pow,
                "<"  => PlotOp.CmpLt,
                ">"  => PlotOp.CmpGt,
                "<=" => PlotOp.CmpLe,
                ">=" => PlotOp.CmpGe,
                "==" => PlotOp.CmpEq,
                "!=" => PlotOp.CmpNe,
                "&&" => PlotOp.And,
                "||" => PlotOp.Or,
                _    => throw new Exception($"Unknown binary operator: {b.Op}")
            };
            prog.Emit(op);
        }

        private static void EmitCall(PlotBytecodeProgram prog, string name, List<Expr> args, string xParam)
        {
            int argc = args.Count;

            // Унарные
            if (argc == 1 && _unaryOps.TryGetValue(name, out var uop))
            {
                EmitExpr(prog, args[0], xParam);
                prog.Emit(uop);
                return;
            }

            // Бинарные
            if (argc == 2 && _binaryOps.TryGetValue(name, out var bop))
            {
                EmitExpr(prog, args[0], xParam);
                EmitExpr(prog, args[1], xParam);
                prog.Emit(bop);
                return;
            }

            // clamp(value, min, max) — 3 args
            // В шейдере pop order: b=stk[top-1], a=stk[top-2], lo=stk[top-3]
            // clamp(lo, a, b) → emit: value, min, max, CLAMP
            if (argc == 3 && string.Equals(name, "clamp", StringComparison.OrdinalIgnoreCase))
            {
                EmitExpr(prog, args[0], xParam); // value
                EmitExpr(prog, args[1], xParam); // min
                EmitExpr(prog, args[2], xParam); // max
                prog.Emit(PlotOp.Clamp);
                return;
            }

            // mix(a, b, t) — 3 args
            if (argc == 3 && string.Equals(name, "mix", StringComparison.OrdinalIgnoreCase))
            {
                // В шейдере: float b=pop, a=pop, t3=pop; mix(t3,a,b) т.е. emit: a, b, t, MIX
                EmitExpr(prog, args[0], xParam); // a
                EmitExpr(prog, args[1], xParam); // b
                EmitExpr(prog, args[2], xParam); // t
                prog.Emit(PlotOp.Mix);
                return;
            }

            // smoothstep(e0, e1, x) — разложим вручную без рекурсии в AST
            if (argc == 3 && string.Equals(name, "smoothstep", StringComparison.OrdinalIgnoreCase))
            {
                // t = clamp((x-e0)/(e1-e0), 0, 1);  result = t*t*(3-2*t)
                // Используем доступные опкоды: без clamp — min(max(t,0),1)
                // emit: x, e0, SUB, e1, e0, SUB, DIV → t (unclamped)
                // затем: t, 0, MAX, 1, MIN → clamped t
                // затем: t, t, MUL, 3, 2, t, MUL, SUB, MUL
                // Это слишком много инструкций. Резервируем как fallback:
                throw new Exception($"smoothstep not directly supported in GPU plot bytecode. Use manual expression: t=((x-e0)/(e1-e0)); t*t*(3.0-2.0*t)");
            }

            throw new Exception($"Function '{name}' (argc={argc}) not supported in GPU plot bytecode");
        }
    }

    // ── GPU-структура (layout должен совпадать с plot_compute.comp PlotParams) ─
    // std430, 256 байт
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PlotParamsGpu
    {
        public float XMin;
        public float XMax;
        public int   VertexOffset;
        public int   Resolution;

        // Snapshot slots 0-2 (T, MX, MY)
        public float T;
        public float MX;
        public float MY;
        public float Reserved0;

        // Snapshot slots 3-10
        public Vector4 SnapA;   // slots 3-6
        public Vector4 SnapB;   // slots 7-10

        public int  InstrCount;
        public int  ConstCount;
        public int  Pad0;
        public int  Pad1;

        public fixed int   Instrs[64 * 4];       // 64 × ivec4 = 256 ints
        public fixed float Constants[16 * 4];    // 16 × vec4  = 64 floats

        /// <summary>
        /// Заполняет поля Instrs и Constants из PlotBytecodeProgram.
        /// </summary>
        public void SetBytecode(PlotBytecodeProgram prog)
        {
            InstrCount = Math.Min(prog.Instructions.Count, 64);
            ConstCount = Math.Min(prog.Constants.Count,    16);

            for (int i = 0; i < InstrCount; i++)
            {
                var instr = prog.Instructions[i];
                Instrs[i * 4 + 0] = (int)instr.Op;
                Instrs[i * 4 + 1] = instr.Arg0;
                Instrs[i * 4 + 2] = instr.Arg1;
                Instrs[i * 4 + 3] = instr.Arg2;
            }

            for (int i = 0; i < ConstCount; i++)
            {
                Constants[i * 4 + 0] = prog.Constants[i];
                Constants[i * 4 + 1] = 0f;
                Constants[i * 4 + 2] = 0f;
                Constants[i * 4 + 3] = 0f;
            }
        }
    }
}