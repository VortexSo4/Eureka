﻿// File: PrimitivesGPU.cs
// Namespace: PhysicsSimulation.Rendering.PrimitiveRendering.GPU
// Purpose: GPU-first primitive definitions (CPU-side metadata + SSBO serialization helpers).
// Note: Actual compute shaders + rendering path will be implemented in AnimationEngine / RenderEngine.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;
using PhysicsSimulation.Base;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{

    public enum AnimType
    {
        None = 0,
        Translate = 1,
        Rotate = 2,
        Scale = 3,
        Color = 4,
        Morph = 5,
        DashLengths = 6,
        // extend as needed
    }

    public enum EaseType
    {
        Linear = 0,
        EaseIn = 1,
        EaseOut = 2,
        EaseInOut = 3
    }

    [Flags]
    public enum PrimitiveFlags
    {
        None = 0,
        Filled = 1 << 0,
        Closed = 1 << 1,
        UseGlobalCoords = 1 << 2,
        // add dashed/hatch flags later if helpful
    }

    public struct AnimEntryCpu
    {
        public int MetaType;
        public int PrimitiveId;
        public int MetaR2;
        public int MetaR3;

        public float Start;
        public float End;
        public float EaseAsFloat;
        public float TimesR;

        public Vector4 From;
        public Vector4 To;

        public int MorphOffsetA;
        public int MorphOffsetB;
        public int MorphOffsetM;
        public int MorphVertexCount;
        public bool PendingOnGpu;

        public AnimEntryCpu(
            AnimType type,
            int primitiveId,
            float start,
            float end,
            EaseType ease,
            Vector4 from,
            Vector4 to,
            int morphA = 0, int morphB = 0, int morphM = 0, int morphCount = 0)
        {
            MetaType = (int)type;
            PrimitiveId = primitiveId;
            MetaR2 = 0;
            MetaR3 = 0;
            Start = start;
            End = end;
            EaseAsFloat = (float)ease;
            TimesR = 0f;
            From = from;
            To = to;
            MorphOffsetA = morphA;
            MorphOffsetB = morphB;
            MorphOffsetM = morphM;
            MorphVertexCount = morphCount;
            PendingOnGpu = true;
        }
    }

    public struct AnimIndexCpu
    {
        public int Start;
        public int Count;
        public int R2;
        public int R3;

        public AnimIndexCpu(int start, int count)
        {
            Start = start;
            Count = count;
            R2 = 0;
            R3 = 0;
        }
    }

    public struct MorphDescCpu
    {
        public float CurrentT;
        public float EaseAsFloat;
        public float R0;
        public float R1;
        public int OffsetA;
        public int OffsetB;
        public int OffsetM;
        public int VertexCount;
    }

    public struct RenderInstanceCpu
    {
        public Vector4 TransformRow0;
        public Vector4 TransformRow1;
        public Vector4 TransformRow2;
        public Vector4 Color;
        public int OffsetM;
        public int VertexCount;
        public int Flags;
        public int Reserved;
        public Vector4 DashInfo; // filledLen, emptyLen, offset, reserved
    }

    public class GeometryArena
    {
        private int _nextOffset = 0;
        private readonly List<(int offset, int count)> _allocations = [];

        public int Allocate(int vertexCount)
        {
            DebugManager.Alloc($"GeometryArena.Allocate: Allocating {vertexCount} vertices.");
            if (vertexCount <= 0) return -1;
            int off = _nextOffset;
            _allocations.Add((off, vertexCount));
            _nextOffset += vertexCount;
            DebugManager.Alloc($"GeometryArena.Allocate: Allocated at offset {off}, new nextOffset {_nextOffset}.");
            return off;
        }

        public int TotalVertexCount => _nextOffset;

        public void Reset()
        {
            DebugManager.Alloc("GeometryArena.Reset: Resetting arena.");
            _nextOffset = 0;
            _allocations.Clear();
            DebugManager.Alloc("GeometryArena.Reset: Arena reset complete.");
        }

        // Flatten contours into a single vertex array with NaN separators between contours
        public static Vector2[] FlattenContours(IReadOnlyList<List<Vector2>> contours)
        {
            DebugManager.Alloc($"GeometryArena.FlattenContours: Flattening {contours.Count} contours.");
            var list = new List<Vector2>();
            for (int i = 0; i < contours.Count; i++)
            {
                var c = contours[i];
                if (c == null || c.Count == 0) continue;
                foreach (var v in c) list.Add(v);
                if (i < contours.Count - 1) list.Add(new Vector2(float.NaN, float.NaN));
            }

            DebugManager.Alloc($"GeometryArena.FlattenContours: Flattened to {list.Count} vertices.");
            return list.ToArray();
        }
    }

    public abstract class PrimitiveGpu
    {
        // assigned by engine
        public int PrimitiveId { get; internal set; } = -1;
        private const int ANIM_ENTRY_SIZE_BYTES = 80;
        public bool IsDynamic { get; set; } = false;
        internal bool IsGeometryRegistered { get; private set; } = false;
        protected abstract void RegisterGeometryInternal(GeometryArena arena);
        public void InvalidateGeometry() => IsGeometryRegistered = false;
        internal void MarkGeometryRegistered() => IsGeometryRegistered = true;
        public string Name { get; set; } = "";

        // geometry offsets (in vertex units)
        public int VertexOffsetRaw { get; protected set; } = -1;
        public int VertexOffsetA { get; protected set; } = -1;
        public int VertexOffsetB { get; protected set; } = -1;
        public int VertexOffsetM { get; protected set; } = -1;
        public int VertexCount { get; protected set; } = 0;

        // transform & visuals
        public Vector2 Position = Vector2.Zero;
        public float Rotation = 0f;
        public float Scale = 1f;
        public Vector4 Color = new(1f, 1f, 1f, 1f);

        // dash params (world units)
        public float FilledLength = 0f;
        public float EmptyLength = 0f;
        public float DashOffset = 0f;

        public PrimitiveFlags Flags = PrimitiveFlags.None;

        // pending anims (per-primitive)
        internal readonly List<AnimEntryCpu> PendingAnimations = [];

        // convenience user slot
        public object? Tag;

        private static int _counter = 0;

        protected PrimitiveGpu(bool isDynamic = false)
        {
            var id = Interlocked.Increment(ref _counter);
            Name = $"{GetType().Name}_{id}";
            IsDynamic = isDynamic;
            DebugManager.Geometry($"PrimitiveGpu.ctor: Creating primitive with name '{Name}'.");
            IsDynamic = isDynamic;
            DebugManager.Geometry($"PrimitiveGpu.ctor: Primitive created.");
        }

        internal void EnsureGeometryRegistered(GeometryArena arena)
        {
            if (IsGeometryRegistered) return;

            DebugManager.Geometry($"PrimitiveGpu.EnsureGeometryRegistered: Registering '{Name}' ({GetType().Name})");
            RegisterGeometryInternal(arena);
            MarkGeometryRegistered();

            DebugManager.Geometry(
                $"PrimitiveGpu.EnsureGeometryRegistered: Registered. Offset={VertexOffsetRaw}, Count={VertexCount}");
        }

        protected void RegisterRawGeometry(GeometryArena arena, Vector2[] flatVertices)
        {
            DebugManager.Geometry(
                $"PrimitiveGpu.RegisterRawGeometry: Registering raw geometry for primitive '{Name}', {flatVertices.Length} vertices.");
            if (arena == null) throw new ArgumentNullException(nameof(arena));
            if (flatVertices == null || flatVertices.Length == 0) return;
            VertexOffsetRaw = arena.Allocate(flatVertices.Length);
            VertexCount = flatVertices.Length;
            DebugManager.Geometry(
                $"PrimitiveGpu.RegisterRawGeometry: Registered at offset {VertexOffsetRaw}, count {VertexCount}.");
        }

        public void RegisterMorphTarget(GeometryArena arena, Vector2[] verticesA, Vector2[] verticesB)
        {
            DebugManager.Geometry(
                $"PrimitiveGpu.RegisterMorphTarget: Registering morph targets for primitive '{Name}'.");
            if (arena == null) throw new ArgumentNullException(nameof(arena));
            if (verticesA == null || verticesB == null || verticesA.Length != verticesB.Length ||
                verticesA.Length == 0)
                throw new ArgumentException("Morph targets must be non-null and same length >0");

            int len = verticesA.Length;
            VertexOffsetA = arena.Allocate(len);
            VertexOffsetB = arena.Allocate(len);
            VertexOffsetM = arena.Allocate(len);
            VertexCount = len;

            // Note: actual vertex data upload happens in engine.UploadGeometryFromPrimitives
            DebugManager.Geometry(
                $"PrimitiveGpu.RegisterMorphTarget: Morph targets registered: A={VertexOffsetA}, B={VertexOffsetB}, M={VertexOffsetM}, count={len}.");
        }

        public PrimitiveGpu AnimatePosition(float start, float duration, EaseType ease, Vector2 to)
        {
            float end = start + duration;
            DebugManager.Anim($"PrimitiveGpu.AnimatePosition: Adding position animation for '{Name}' to {to}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Translate, PrimitiveId, start, end, ease,
                Vector4.Zero, new Vector4(to.X, to.Y, 0f, 0f)));
            DebugManager.Anim($"PrimitiveGpu.AnimatePosition: Position animation added.");
            IsDynamic = true;
            return this;
        }

        public PrimitiveGpu AnimateRotation(float start, float duration, EaseType ease, float toDegrees)
        {
            float end = start + duration;
            float toRad = MathHelper.DegreesToRadians(toDegrees);
            DebugManager.Anim(
                $"PrimitiveGpu.AnimateRotation: Adding rotation animation for '{Name}' to {toDegrees} degrees.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Rotate, PrimitiveId, start, end, ease,
                Vector4.Zero, new Vector4(toRad, 0f, 0f, 0f)));
            DebugManager.Anim($"PrimitiveGpu.AnimateRotation: Rotation animation added.");
            IsDynamic = true;
            return this;
        }

        public PrimitiveGpu AnimateScale(float start, float duration, EaseType ease, float to)
        {
            float end = start + duration;
            DebugManager.Anim($"PrimitiveGpu.AnimateScale: Adding scale animation for '{Name}' to {to}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Scale, PrimitiveId, start, end, ease,
                Vector4.Zero, new Vector4(to, 0f, 0f, 0f)));
            DebugManager.Anim($"PrimitiveGpu.AnimateScale: Scale animation added.");
            IsDynamic = true;
            return this;
        }

        public PrimitiveGpu AnimateColor(float start, float duration, EaseType ease, Vector4 to)
        {
            float end = start + duration;
            DebugManager.Anim($"PrimitiveGpu.AnimateColor: Adding color animation for '{Name}' to {to}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Color, PrimitiveId, start, end, ease,
                Vector4.Zero, to));
            DebugManager.Anim($"PrimitiveGpu.AnimateColor: Color animation added.");
            IsDynamic = true;
            return this;
        }

        public PrimitiveGpu AnimateMorph(float start, float duration, EaseType ease, int offsetA, int offsetB,
            int offsetM, int vertexCount)
        {
            float end = start + duration;
            DebugManager.Anim($"PrimitiveGpu.AnimateMorph: Adding morph animation for '{Name}'.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Morph, PrimitiveId, start, end, ease,
                Vector4.Zero, Vector4.Zero, offsetA, offsetB, offsetM, vertexCount));
            DebugManager.Anim($"PrimitiveGpu.AnimateMorph: Morph animation added.");
            IsDynamic = true;
            return this;
        }

        public PrimitiveGpu AnimateDash(float start, float duration, EaseType ease, Vector2 toLengths)
        {
            float end = start + duration;
            DebugManager.Anim($"PrimitiveGpu.AnimateDash: Adding dash animation for '{Name}' to {toLengths}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.DashLengths, PrimitiveId, start, end, ease,
                Vector4.Zero, new Vector4(toLengths.X, toLengths.Y, 0f, 0f)));
            DebugManager.Anim($"PrimitiveGpu.AnimateDash: Dash animation added.");
            IsDynamic = true;
            return this;
        }

        public RenderInstanceCpu ToRenderInstanceCpu()
        {
            DebugManager.Geometry($"PrimitiveGpu.ToRenderInstanceCpu: Converting primitive '{Name}' to RenderInstanceCpu.");
            float c = MathF.Cos(Rotation);
            float s = MathF.Sin(Rotation);
            var row0 = new Vector4(Scale * c, Scale * s, 0f, 0f);
            var row1 = new Vector4(-Scale * s, Scale * c, 0f, 0f);
            var row2 = new Vector4(Position, 1f, 0f);
            var inst = new RenderInstanceCpu
            {
                TransformRow0 = row0,
                TransformRow1 = row1,
                TransformRow2 = row2,
                Color = Color,
                OffsetM = VertexOffsetM >= 0 ? VertexOffsetM : VertexOffsetRaw,
                VertexCount = VertexCount,
                Flags = (int)Flags,
                Reserved = 0,
                DashInfo = new Vector4(FilledLength, EmptyLength, DashOffset, 0f)
            };
            DebugManager.Geometry($"PrimitiveGpu.ToRenderInstanceCpu: Conversion complete.");
            return inst;
        }

        public static byte[] SerializeAnimEntries(List<AnimEntryCpu> entries)
        {
            DebugManager.Anim($"PrimitiveGpu.SerializeAnimEntries: Serializing {entries.Count} animation entries.");
            if (entries == null || entries.Count == 0) return [];
            int bytes = entries.Count * ANIM_ENTRY_SIZE_BYTES;
            var data = new byte[bytes];
            var span = data.AsSpan();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var offset = i * ANIM_ENTRY_SIZE_BYTES;

                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 0, 4), e.MetaType);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 4, 4), e.PrimitiveId);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 8, 4), e.MetaR2);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 12, 4), e.MetaR3);

                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), e.Start);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), e.End);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 24, 4), e.EaseAsFloat);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 28, 4), e.TimesR);

                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 32, 4), e.From.X);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 36, 4), e.From.Y);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 40, 4), e.From.Z);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 44, 4), e.From.W);

                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 48, 4), e.To.X);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 52, 4), e.To.Y);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 56, 4), e.To.Z);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 60, 4), e.To.W);

                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 64, 4), e.MorphOffsetA);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 68, 4), e.MorphOffsetB);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 72, 4), e.MorphOffsetM);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 76, 4), e.MorphVertexCount);
            }

            DebugManager.Anim($"PrimitiveGpu.SerializeAnimEntries: Serialization complete, {bytes} bytes.");
            return data;
        }

        public static AnimIndexCpu[] BuildAnimIndex(List<AnimEntryCpu> allEntries, int primitiveCount)
        {
            DebugManager.Anim(
                $"PrimitiveGpu.BuildAnimIndex: Building index for {primitiveCount} primitives, {allEntries.Count} entries.");
            var result = new AnimIndexCpu[primitiveCount];
            if (allEntries == null || allEntries.Count == 0)
            {
                for (int i = 0; i < primitiveCount; i++) result[i] = new AnimIndexCpu(0, 0);
                DebugManager.Anim("PrimitiveGpu.BuildAnimIndex: No entries, default indices built.");
                return result;
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < allEntries.Count; i++)
            {
                int pid = allEntries[i].PrimitiveId;
                if (!groups.TryGetValue(pid, out var l))
                {
                    l = [];
                    groups[pid] = l;
                }

                l.Add(i);
            }

            for (int pid = 0; pid < primitiveCount; pid++)
            {
                if (groups.TryGetValue(pid, out var idxList) && idxList.Count > 0)
                {
                    int start = idxList.Min();
                    int count = idxList.Count;
                    result[pid] = new AnimIndexCpu(start, count);
                }
                else
                {
                    result[pid] = new AnimIndexCpu(0, 0);
                }
            }

            DebugManager.Anim("PrimitiveGpu.BuildAnimIndex: Index built.");
            return result;
        }
    }

    public class PolygonGpu : PrimitiveGpu
    {
        public IReadOnlyList<List<Vector2>> Contours => _contours;
        protected readonly List<List<Vector2>> _contours = [];

        public PolygonGpu(IReadOnlyList<List<Vector2>> contours, bool isDynamic = false)
        {
            if (contours != null && contours.Count > 0)
                SetContours(contours);
            IsDynamic = isDynamic;
        }
        
        protected void SetContours(IReadOnlyList<List<Vector2>> contours)
        {
            _contours.Clear();
            if (contours != null)
            {
                foreach (var contour in contours)
                {
                    if (contour != null)
                        _contours.Add([..contour]); // глубокая копия
                }
            }
        }

        protected override void RegisterGeometryInternal(GeometryArena arena)
        {
            if (_contours.Count == 0)
                throw new InvalidOperationException($"Primitive '{Name}' has no contours to register!");

            var flat = GeometryArena.FlattenContours(_contours);
            RegisterRawGeometry(arena, flat);
        }

        public Vector2[] GetFlattenedVertices() => GeometryArena.FlattenContours(Contours);

        public void SetDash(bool enabled, float filledLen = 0.05f, float emptyLen = 0.03f, float phase = 0f)
        {
            DebugManager.Geometry($"PolygonGpu.SetDash: Setting dash for '{Name}', enabled={enabled}.");
            if (enabled) Flags |= PrimitiveFlags.None;
            FilledLength = filledLen;
            EmptyLength = emptyLen;
            DashOffset = phase;
            DebugManager.Geometry($"PolygonGpu.SetDash: Dash set.");
        }
    }

    public class RectGpu : PolygonGpu
    {
        public float Width { get; set; }
        public float Height { get; set; }

        public RectGpu(float width = 1f, float height = 1f, bool isDynamic = false)
            : base(CreateRectangleContours(width, height))
        {
            Width = width;
            Height = height;
            IsDynamic = isDynamic;
        }

        private static List<List<Vector2>> CreateRectangleContours(float width, float height)
        {
            DebugManager.Geometry($"RectGpu.CreateRectangleContours: Creating contours for width={width}, height={height}.");
            float hw = width / 2f;
            float hh = height / 2f;

            var contour = new List<Vector2>
            {
                new(-hw, -hh),
                new(hw, -hh),
                new(hw,  hh),
                new(-hw, hh),
                new(-hw, -hh)
            };

            DebugManager.Geometry("RectGpu.CreateRectangleContours: Contours created.");
            return [contour];
        }
    }

    public class LineGpu : PolygonGpu
    {
        public LineGpu(float x1 = 0f, float y1 = 0f, float x2 = 0.5f, float y2 = 0f)
            : base([[new Vector2(0, -0.5f), new Vector2(0, 0.5f)]])
        {
            SetContours([[Vector2.Zero, new Vector2(x2 - x1, y2 - y1)]]);
            Flags = PrimitiveFlags.None;
        }

        // Удобный конструктор от двух точек
        public LineGpu(Vector2 from, Vector2 to)
            : this(from.X, from.Y, to.X, to.Y) { }
    }

    public class TriangleGpu : PolygonGpu
    {
        private static readonly Vector2 DefaultA = new(-0.1f, -0.1f);
        private static readonly Vector2 DefaultB = new(0.1f, -0.1f);
        private static readonly Vector2 DefaultC = new(0.0f, 0.15f);

        public TriangleGpu(Vector2 a = default, Vector2 b = default, Vector2 c = default,
            bool filled = true, bool isDynamic = false)
            : base([[DefaultA,DefaultB,DefaultC]])
        {
            var va = a == default ? new Vector2(-0.1f, -0.1f) : a;
            var vb = b == default ? new Vector2( 0.1f, -0.1f) : b;
            var vc = c == default ? new Vector2( 0.0f,  0.15f) : c;

            SetContours([[va, vb, vc, va]]);
            Flags = filled ? PrimitiveFlags.Filled | PrimitiveFlags.Closed : PrimitiveFlags.Closed;
            IsDynamic = isDynamic;
        }

        // Удобный конструктор с позицией
        public TriangleGpu(Vector2 center, float size = 0.2f, bool filled = true)
            : this(
                new Vector2(-size, -size),
                new Vector2(size, -size),
                new Vector2(0, size * 1.732f),
                filled)
        {
            Position = center;
        }
    }

    public class CircleGpu : PolygonGpu
    {
        public int Segments { get; }

        public CircleGpu(float radius = 0.2f, int segments = 80, bool filled = false, bool isDynamic = false)
            : base([GenerateCircleContour(radius, 80)], false)
        {
            Segments = Math.Max(8, segments);
            SetContours([GenerateCircleContour(radius, Segments)]);

            Flags = filled
                ? PrimitiveFlags.Filled | PrimitiveFlags.Closed
                : PrimitiveFlags.Closed;
            IsDynamic = isDynamic;
        }
        private static List<Vector2> GenerateCircleContour(float radius, int segments)
        {
            var points = new List<Vector2>(segments + 1);
            for (int i = 0; i < segments; i++)
            {
                float a = 2f * MathF.PI * i / segments;
                points.Add(new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius);
            }
            points.Add(points[0]);
            return points;
        }
    }

    public class PlotGpu : PolygonGpu
    {
        public Func<float, float> Func { get; }
        public float XMin { get; private set; }
        public float XMax { get; private set; }
        public int Resolution { get; private set; }

        public PlotGpu(Func<float, float> func, float xMin = -1f, float xMax = 1f,
            int resolution = 300,  bool isDynamic = false)
            : base([])
        {
            Func = func ?? throw new ArgumentNullException(nameof(func));
            Resolution = Math.Max(2, resolution);
            UpdateRange(xMin, xMax);
            Flags = PrimitiveFlags.None;
            IsDynamic = isDynamic;
        }

        public void UpdateRange(float xMin, float xMax, int? resolution = null)
        {
            XMin = xMin;
            XMax = xMax;
            if (resolution.HasValue) Resolution = Math.Max(2, resolution.Value);
            var points = new List<Vector2>(Resolution + 1);
            for (int i = 0; i <= Resolution; i++)
            {
                float t = i / (float)Resolution;
                float x = MathHelper.Lerp(XMin, XMax, t);
                float y = Func(x);
                points.Add(new Vector2(x, y));
            }
            SetContours([points]);
            InvalidateGeometry();
        }
        
        protected override void RegisterGeometryInternal(GeometryArena arena)
        {
            DebugManager.Geometry($"plot: Regenerating points for '{Name}'");
    
            if (IsDynamic)
            {
        
                var points = new List<Vector2>(Resolution + 1);
                for (int i = 0; i <= Resolution; i++)
                {
                    float t = i / (float)Resolution;
                    float x = MathHelper.Lerp(XMin, XMax, t);
                    float y = Func(x);
                    points.Add(new Vector2(x, y));
                }
                SetContours([points]);
            }

            if (_contours.Count == 0)
                throw new InvalidOperationException($"Primitive '{Name}' has no contours to register!");

            var flat = GeometryArena.FlattenContours(_contours);
            RegisterRawGeometry(arena, flat);
        }
    }

    public class TextGpu : PrimitiveGpu
    {
        public string Text { get; set; } = "";

        public List<List<List<Vector2>>> GlyphContours { get; private set; } = [];

        public float FontSize { get; set; } = 0.1f;
        public float LetterPadding { get; set; } = 0.05f;
        public float LineSpacing { get; set; } = 0.1f;

        public enum HorizontalAlignment
        {
            Left,
            Center,
            Right
        }

        public enum VerticalAlignment
        {
            Top,
            Center,
            Bottom
        }

        public HorizontalAlignment HAlign { get; set; } = HorizontalAlignment.Center;
        public VerticalAlignment VAlign { get; set; } = VerticalAlignment.Center;

        public string? FontKey { get; set; }
        public Func<string>? DynamicTextSource { get; set; }

        public TextGpu(string text = "Empty text", float fontSize = 0.1f, string? fontKey = null, string name = "", bool isDynamic = false)
        {
            Text = text ?? "";
            FontSize = fontSize;
            FontKey = fontKey;
            IsDynamic = isDynamic;
        }

        protected override void RegisterGeometryInternal(GeometryArena arena)
        {
            if (arena == null) throw new ArgumentNullException(nameof(arena));

            // Важно: если контуры ещё не сгенерированы — ничего не делаем!
            // Это нормально — текст может быть инициализирован позже через InitGlyphContours
            if (GlyphContours == null || GlyphContours.Count == 0)
            {
                DebugManager.Geometry($"TextGpu '{Name}': No glyph contours yet — geometry registration skipped (will be done later via InitGlyphContours)");
                return;
            }

            DebugManager.Geometry($"TextGpu '{Name}': Auto-registering {GlyphContours.Count} glyphs...");

            var allContours = new List<List<Vector2>>();

            foreach (var glyph in GlyphContours)
            {
                if (glyph != null)
                {
                    foreach (var contour in glyph)
                    {
                        if (contour != null && contour.Count > 0)
                            allContours.Add(contour);
                    }
                }
            }

            var flat = GeometryArena.FlattenContours(allContours);
            RegisterRawGeometry(arena, flat);

            DebugManager.Geometry($"TextGpu '{Name}': Geometry auto-registered. {flat.Length} vertices.");
        }

        public void InitGlyphContours(GeometryArena arena, IEnumerable<List<List<Vector2>>> glyphContours)
        {
            DebugManager.Geometry($"TextGpu.InitGlyphContours: Initializing glyph contours for '{Name}'.");

            GlyphContours = glyphContours
                .Select(g => g?.Select(c => new List<Vector2>(c ?? [])).ToList() ?? [])
                .ToList();
            EnsureGeometryRegistered(arena);
            DebugManager.Geometry($"TextGpu.InitGlyphContours: Done. Text ready for rendering.");
        }
    }
    
    public class EllipseGpu : PolygonGpu
    {
        public float RadiusX { get; }
        public float RadiusY { get; }
        public int Segments { get; }

        public EllipseGpu(float radiusX = 0.3f, float radiusY = 0.2f, int segments = 64, bool filled = false, bool isDynamic = false)
            : base([])
        {
            RadiusX = radiusX;
            RadiusY = radiusY;
            Segments = Math.Max(8, segments);

            SetContours([GenerateEllipse()]);
            Flags = filled ? PrimitiveFlags.Filled | PrimitiveFlags.Closed : PrimitiveFlags.Closed;
            IsDynamic = isDynamic;
        }

        private List<Vector2> GenerateEllipse()
        {
            var pts = new List<Vector2>(Segments + 1);
            for (int i = 0; i < Segments; i++)
            {
                float a = 2f * MathF.PI * i / Segments;
                pts.Add(new Vector2(MathF.Cos(a) * RadiusX, MathF.Sin(a) * RadiusY));
            }
            pts.Add(pts[0]);
            return pts;
        }
    }

    public class ArcGpu : PolygonGpu
    {
        public float Radius { get; }
        public float StartAngle { get; }
        public float EndAngle { get; }
        public int Segments { get; }

        public ArcGpu(float radius = 0.3f, float startAngleRad = 0f, float endAngleRad = MathF.PI, int segments = 64, bool isDynamic = false)
            : base([])
        {
            Radius = radius;
            StartAngle = startAngleRad;
            EndAngle = endAngleRad;
            Segments = Math.Max(2, segments);

            SetContours([GenerateArc()]);
            Flags = PrimitiveFlags.None;
            IsDynamic = isDynamic;
        }

        private List<Vector2> GenerateArc()
        {
            var pts = new List<Vector2>(Segments + 1);
            for (int i = 0; i <= Segments; i++)
            {
                float t = i / (float)Segments;
                float a = MathHelper.Lerp(StartAngle, EndAngle, t);
                pts.Add(new Vector2(MathF.Cos(a) * Radius, MathF.Sin(a) * Radius));
            }
            return pts;
        }
    }

    public class ArrowGpu : PolygonGpu
    {
        public ArrowGpu(Vector2 from, Vector2 to, float headSize = 0.1f, float headAngleDeg = 25f, bool isDynamic = false)
            : base([])
        {
            SetContours([GenerateArrow(from, to, headSize, headAngleDeg)]);
            Flags = PrimitiveFlags.None;
            IsDynamic = isDynamic;
        }

        private static List<Vector2> GenerateArrow(Vector2 from, Vector2 to, float headSize, float angleDeg)
        {
            var dir = Vector2.Normalize(to - from);
            var back = to - dir * headSize;
            float a = MathHelper.DegreesToRadians(angleDeg);

            Vector2 left = Rotate(dir, +a);
            Vector2 right = Rotate(dir, -a);

            return new List<Vector2>
            {
                from,
                to,
                back + left * headSize,
                to,
                back + right * headSize
            };
        }

        private static Vector2 Rotate(Vector2 v, float a)
            => new(
                v.X * MathF.Cos(a) - v.Y * MathF.Sin(a),
                v.X * MathF.Sin(a) + v.Y * MathF.Cos(a)
            );
    }

    public class BezierCurveGpu : PolygonGpu
    {
        public BezierCurveGpu(Vector2 p0, Vector2 p1, Vector2 p2, int segments = 64, bool isDynamic = false)
            : base([])
        {
            SetContours([Generate(p0, p1, p2, segments)]);
            Flags = PrimitiveFlags.None;
            IsDynamic = isDynamic;
        }

        private static List<Vector2> Generate(Vector2 p0, Vector2 p1, Vector2 p2, int seg)
        {
            var pts = new List<Vector2>(seg + 1);
            for (int i = 0; i <= seg; i++)
            {
                float t = i / (float)seg;
                float u = 1f - t;
                pts.Add(u * u * p0 + 2f * u * t * p1 + t * t * p2);
            }
            return pts;
        }
    }

    public class GridGpu : PolygonGpu
    {
        public GridGpu(int cellsX = 10, int cellsY = 10, float size = 1f, bool isDynamic = false)
            : base([])
        {
            SetContours(GenerateGrid(cellsX, cellsY, size));
            Flags = PrimitiveFlags.None;
            IsDynamic = isDynamic;
        }

        private static List<List<Vector2>> GenerateGrid(int x, int y, float size)
        {
            var contours = new List<List<Vector2>>();
            float hx = size * 0.5f;
            float hy = size * 0.5f;

            for (int i = 0; i <= x; i++)
            {
                float tx = MathHelper.Lerp(-hx, hx, i / (float)x);
                contours.Add([new Vector2(tx, -hy), new Vector2(tx, hy)]);
            }

            for (int j = 0; j <= y; j++)
            {
                float ty = MathHelper.Lerp(-hy, hy, j / (float)y);
                contours.Add([new Vector2(-hx, ty), new Vector2(hx, ty)]);
            }

            return contours;
        }
    }

    public class AxisGpu : PolygonGpu
    {
        public AxisGpu(float size = 1f, bool isDynamic = false)
            : base([
                [new Vector2(-size, 0f), new Vector2(size, 0f)],
                [new Vector2(0f, -size), new Vector2(0f, size)]
            ])
        {
            Flags = PrimitiveFlags.None;
            IsDynamic = isDynamic;
        }
    }
}