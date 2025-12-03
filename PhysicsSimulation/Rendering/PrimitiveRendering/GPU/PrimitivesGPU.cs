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
    #region Enums & Flags

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

    #endregion

    #region CPU-side SSBO structs (plain-old-data views)

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

    #endregion

    #region GeometryArena

    public class GeometryArena
    {
        private int _nextOffset = 0;
        private readonly List<(int offset, int count)> _allocations = [];

        public int Allocate(int vertexCount)
        {
            DebugManager.Gpu($"GeometryArena.Allocate: Allocating {vertexCount} vertices.");
            if (vertexCount <= 0) return -1;
            int off = _nextOffset;
            _allocations.Add((off, vertexCount));
            _nextOffset += vertexCount;
            DebugManager.Gpu($"GeometryArena.Allocate: Allocated at offset {off}, new nextOffset {_nextOffset}.");
            return off;
        }

        public int TotalVertexCount => _nextOffset;

        public void Reset()
        {
            DebugManager.Gpu("GeometryArena.Reset: Resetting arena.");
            _nextOffset = 0;
            _allocations.Clear();
            DebugManager.Gpu("GeometryArena.Reset: Arena reset complete.");
        }

        // Flatten contours into a single vertex array with NaN separators between contours
        public static Vector2[] FlattenContours(IReadOnlyList<List<Vector2>> contours)
        {
            DebugManager.Gpu($"GeometryArena.FlattenContours: Flattening {contours.Count} contours.");
            var list = new List<Vector2>();
            for (int i = 0; i < contours.Count; i++)
            {
                var c = contours[i];
                if (c == null || c.Count == 0) continue;
                foreach (var v in c) list.Add(v);
                if (i < contours.Count - 1) list.Add(new Vector2(float.NaN, float.NaN));
            }

            DebugManager.Gpu($"GeometryArena.FlattenContours: Flattened to {list.Count} vertices.");
            return list.ToArray();
        }
    }

    #endregion

    #region PrimitiveGPU base

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

        protected PrimitiveGpu(string name = "", bool isDynamic = false)
        {
            DebugManager.Gpu($"PrimitiveGpu.ctor: Creating primitive with name '{name}'.");
            Name = name ?? "";
            isDynamic = IsDynamic;
            DebugManager.Gpu($"PrimitiveGpu.ctor: Primitive created.");
        }

        #region Geometry registration helpers

        internal void EnsureGeometryRegistered(GeometryArena arena)
        {
            if (IsGeometryRegistered) return;

            DebugManager.Gpu($"PrimitiveGpu.EnsureGeometryRegistered: Registering '{Name}' ({GetType().Name})");
            RegisterGeometryInternal(arena);
            MarkGeometryRegistered();

            DebugManager.Gpu($"PrimitiveGpu.EnsureGeometryRegistered: Registered. Offset={VertexOffsetRaw}, Count={VertexCount}");
        }

        protected void RegisterRawGeometry(GeometryArena arena, Vector2[] flatVertices)
        {
            DebugManager.Gpu(
                $"PrimitiveGpu.RegisterRawGeometry: Registering raw geometry for primitive '{Name}', {flatVertices.Length} vertices.");
            if (arena == null) throw new ArgumentNullException(nameof(arena));
            if (flatVertices == null || flatVertices.Length == 0) return;
            VertexOffsetRaw = arena.Allocate(flatVertices.Length);
            VertexCount = flatVertices.Length;
            DebugManager.Gpu(
                $"PrimitiveGpu.RegisterRawGeometry: Registered at offset {VertexOffsetRaw}, count {VertexCount}.");
        }

        public void RegisterMorphTarget(GeometryArena arena, Vector2[] verticesA, Vector2[] verticesB)
        {
            DebugManager.Gpu($"PrimitiveGpu.RegisterMorphTarget: Registering morph targets for primitive '{Name}'.");
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
            DebugManager.Gpu(
                $"PrimitiveGpu.RegisterMorphTarget: Morph targets registered: A={VertexOffsetA}, B={VertexOffsetB}, M={VertexOffsetM}, count={len}.");
        }

        #endregion

        #region Animation helpers

        public void AnimatePosition(float start, float end, EaseType ease, Vector2 to)
        {
            Vector2 from = Position;
            DebugManager.Gpu(
                $"PrimitiveGpu.AnimatePosition: Adding position animation for '{Name}' from {from} to {to}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Translate, PrimitiveId, start, end, ease,
                new Vector4(from, 0f, 0f), new Vector4(to, 0f, 0f)));
            DebugManager.Gpu($"PrimitiveGpu.AnimatePosition: Position animation added.");
        }

        public void AnimateRotation(float start, float end, EaseType ease, float to)
        {
            float from = Rotation;
            DebugManager.Gpu(
                $"PrimitiveGpu.AnimateRotation: Adding rotation animation for '{Name}' from {from} to {to}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Rotate, PrimitiveId, start, end, ease,
                new Vector4(from, 0f, 0f, 0f), new Vector4(to, 0f, 0f, 0f)));
            DebugManager.Gpu($"PrimitiveGpu.AnimateRotation: Rotation animation added.");
        }

        public void AnimateScale(float start, float end, EaseType ease, float to)
        {
            float from = Scale;
            DebugManager.Gpu($"PrimitiveGpu.AnimateScale: Adding scale animation for '{Name}' from {from} to {to}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Scale, PrimitiveId, start, end, ease,
                new Vector4(from, 0f, 0f, 0f), new Vector4(to, 0f, 0f, 0f)));
            DebugManager.Gpu($"PrimitiveGpu.AnimateScale: Scale animation added.");
        }

        public void AnimateColor(float start, float end, EaseType ease, Vector4 to)
        {
            Vector4 from = Color;
            DebugManager.Gpu($"PrimitiveGpu.AnimateColor: Adding color animation for '{Name}' from {from} to {to}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Color, PrimitiveId, start, end, ease, from, to));
            DebugManager.Gpu($"PrimitiveGpu.AnimateColor: Color animation added.");
        }

        public void AnimateMorph(float start, float end, EaseType ease, int offsetA, int offsetB, int offsetM,
            int vertexCount)
        {
            DebugManager.Gpu($"PrimitiveGpu.AnimateMorph: Adding morph animation for '{Name}'.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.Morph, PrimitiveId, start, end, ease, Vector4.Zero,
                Vector4.Zero, offsetA, offsetB, offsetM, vertexCount));
            DebugManager.Gpu($"PrimitiveGpu.AnimateMorph: Morph animation added.");
        }

        public void AnimateDash(float start, float end, EaseType ease, Vector2 fromLengths, Vector2 toLengths)
        {
            DebugManager.Gpu(
                $"PrimitiveGpu.AnimateDash: Adding dash animation for '{Name}' from {fromLengths} to {toLengths}.");
            PendingAnimations.Add(new AnimEntryCpu(AnimType.DashLengths, PrimitiveId, start, end, ease,
                new Vector4(fromLengths, 0f, 0f), new Vector4(toLengths, 0f, 0f)));
            DebugManager.Gpu($"PrimitiveGpu.AnimateDash: Dash animation added.");
        }

        // ScheduleAnimation (generic for other types)
        public void ScheduleAnimation(AnimType type, float start, float end, EaseType ease, Vector4 from, Vector4 to,
            int morphA = 0, int morphB = 0, int morphM = 0, int morphCount = 0)
        {
            DebugManager.Gpu($"PrimitiveGpu.ScheduleAnimation: Scheduling {type} animation for '{Name}'.");
            PendingAnimations.Add(new AnimEntryCpu(type, PrimitiveId, start, end, ease, from, to, morphA, morphB,
                morphM, morphCount));
            DebugManager.Gpu($"PrimitiveGpu.ScheduleAnimation: Animation scheduled.");
        }

        #endregion

        #region Serialization helpers

        public RenderInstanceCpu ToRenderInstanceCpu()
        {
            DebugManager.Gpu($"PrimitiveGpu.ToRenderInstanceCpu: Converting primitive '{Name}' to RenderInstanceCpu.");
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
            DebugManager.Gpu($"PrimitiveGpu.ToRenderInstanceCpu: Conversion complete.");
            return inst;
        }

        public static byte[] SerializeAnimEntries(List<AnimEntryCpu> entries)
        {
            DebugManager.Gpu($"PrimitiveGpu.SerializeAnimEntries: Serializing {entries.Count} animation entries.");
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

            DebugManager.Gpu($"PrimitiveGpu.SerializeAnimEntries: Serialization complete, {bytes} bytes.");
            return data;
        }

        public static AnimIndexCpu[] BuildAnimIndex(List<AnimEntryCpu> allEntries, int primitiveCount)
        {
            DebugManager.Gpu(
                $"PrimitiveGpu.BuildAnimIndex: Building index for {primitiveCount} primitives, {allEntries.Count} entries.");
            var result = new AnimIndexCpu[primitiveCount];
            if (allEntries == null || allEntries.Count == 0)
            {
                for (int i = 0; i < primitiveCount; i++) result[i] = new AnimIndexCpu(0, 0);
                DebugManager.Gpu("PrimitiveGpu.BuildAnimIndex: No entries, default indices built.");
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

            DebugManager.Gpu("PrimitiveGpu.BuildAnimIndex: Index built.");
            return result;
        }

        public static List<AnimEntryCpu> AggregateEntries(IEnumerable<PrimitiveGpu> primitives)
        {
            DebugManager.Gpu("PrimitiveGpu.AggregateEntries: Aggregating entries from primitives.");
            var outList = new List<AnimEntryCpu>();
            foreach (var p in primitives.OrderBy(p => p.PrimitiveId))
            {
                if (p.PendingAnimations.Count == 0) continue;
                outList.AddRange(p.PendingAnimations);
            }

            DebugManager.Gpu($"PrimitiveGpu.AggregateEntries: Aggregated {outList.Count} entries.");
            return outList;
        }

        public static List<AnimEntryCpu> AggregateNewEntries(IEnumerable<PrimitiveGpu> primitives)
        {
            DebugManager.Gpu("PrimitiveGpu.AggregateNewEntries: Aggregating new entries from primitives.");
            var outList = new List<AnimEntryCpu>();
            foreach (var p in primitives.OrderBy(p => p.PrimitiveId))
            {
                foreach (var e in p.PendingAnimations)
                    if (e.PendingOnGpu)
                        outList.Add(e);
            }

            DebugManager.Gpu($"PrimitiveGpu.AggregateNewEntries: Aggregated {outList.Count} new entries.");
            return outList;
        }

        public static void MarkEntriesUploaded(List<AnimEntryCpu> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entry.PendingOnGpu = true;
                entries[i] = entry;
            }
        }

        #endregion
    }

    #endregion

    #region PolygonGPU & RectGPU

    public class PolygonGpu : PrimitiveGpu
    {
        public IReadOnlyList<List<Vector2>> Contours => _contours;
        private readonly List<List<Vector2>> _contours = [];

        public PolygonGpu(string name = "") : base(name)
        {
        }

        protected PolygonGpu(IReadOnlyList<List<Vector2>> contours, string name = "", bool isDynamic = false) : base(name)
        {
            if (contours != null && contours.Count > 0)
                SetContours(contours);
            isDynamic = IsDynamic;
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
            DebugManager.Gpu($"PolygonGpu.SetDash: Setting dash for '{Name}', enabled={enabled}.");
            if (enabled) Flags |= PrimitiveFlags.None;
            FilledLength = filledLen;
            EmptyLength = emptyLen;
            DashOffset = phase;
            DebugManager.Gpu($"PolygonGpu.SetDash: Dash set.");
        }
    }

    public class RectGpu : PolygonGpu
    {
        public float Width { get; set; }
        public float Height { get; set; }

        public RectGpu(float width = 1f, float height = 1f, string name = "", bool isDynamic = false)
            : base(CreateRectangleContours(width, height), name)
        {
            Width = width;
            Height = height;
            isDynamic = IsDynamic;
        }

        private static List<List<Vector2>> CreateRectangleContours(float width, float height)
        {
            DebugManager.Gpu($"RectGpu.CreateRectangleContours: Creating contours for width={width}, height={height}.");
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

            DebugManager.Gpu("RectGpu.CreateRectangleContours: Contours created.");
            return [contour];
        }
    }

    #region LineGpu

    public class LineGpu : PolygonGpu
    {
        public LineGpu(float x1 = 0f, float y1 = 0f, float x2 = 0.5f, float y2 = 0f, string name = "Line")
            : base(name)
        {
            SetContours([[Vector2.Zero, new Vector2(x2 - x1, y2 - y1)]]);
            Flags = PrimitiveFlags.None;
        }

        // Удобный конструктор от двух точек
        public LineGpu(Vector2 from, Vector2 to, string name = "Line")
            : this(from.X, from.Y, to.X, to.Y, name) { }
    }

    #endregion

    #region TriangleGpu

    public class TriangleGpu : PolygonGpu
    {
        private static readonly Vector2 DefaultA = new(-0.1f, -0.1f);
        private static readonly Vector2 DefaultB = new(0.1f, -0.1f);
        private static readonly Vector2 DefaultC = new(0.0f, 0.15f);

        public TriangleGpu(Vector2 a = default, Vector2 b = default, Vector2 c = default,
            bool filled = true, string name = "Triangle", bool isDynamic = false)
            : base(name)
        {
            var va = a == default ? new Vector2(-0.1f, -0.1f) : a;
            var vb = b == default ? new Vector2( 0.1f, -0.1f) : b;
            var vc = c == default ? new Vector2( 0.0f,  0.15f) : c;

            SetContours([[va, vb, vc, va]]);
            Flags = filled ? PrimitiveFlags.Filled | PrimitiveFlags.Closed : PrimitiveFlags.Closed;
            isDynamic = IsDynamic;
        }

        // Удобный конструктор с позицией
        public TriangleGpu(Vector2 center, float size = 0.2f, bool filled = true, string name = "Triangle")
            : this(
                new Vector2(-size, -size),
                new Vector2(size, -size),
                new Vector2(0, size * 1.732f),
                filled, name)
        {
            Position = center;
        }
    }

    #endregion

    #region CircleGpu

    public class CircleGpu : PolygonGpu
    {
        public int Segments { get; }

        public CircleGpu(float radius = 0.2f, int segments = 80, bool filled = false, string name = "Circle", bool isDynamic = false)
            : base(name)
        {
            Segments = Math.Max(8, segments);
            SetContours([GenerateCircleContour(radius, Segments)]);

            Flags = filled
                ? PrimitiveFlags.Filled | PrimitiveFlags.Closed
                : PrimitiveFlags.Closed;
            isDynamic = IsDynamic;
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

    #endregion

    #region PlotGpu — САМЫЙ КРУТОЙ

    public class PlotGpu : PolygonGpu
    {
        public Func<float, float> Func { get; }
        public float XMin { get; private set; }
        public float XMax { get; private set; }
        public int Resolution { get; private set; }

        public PlotGpu(Func<float, float> func, float xMin = -1f, float xMax = 1f,
            int resolution = 300, string name = "Plot",  bool isDynamic = false)
            : base(name)
        {
            Func = func ?? throw new ArgumentNullException(nameof(func));
            Resolution = Math.Max(2, resolution);
            UpdateRange(xMin, xMax);
            Flags = PrimitiveFlags.None;
            isDynamic = IsDynamic;
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

        // Удобные методы для анимации
        public void AnimateRange(float toMin, float toMax, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            float fromMin = XMin;
            float fromMax = XMax;
            ScheduleAnimation(AnimType.Morph, 0, duration, ease,
                new Vector4(fromMin, fromMax, Resolution, 0),
                new Vector4(toMin, toMax, Resolution, 0));
        }
    }

    #endregion

    #endregion

    #region TextGPU (basic, glyph contours are provided by engine/CharMap)

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
            : base(name)
        {
            Text = text ?? "";
            FontSize = fontSize;
            FontKey = fontKey;
            isDynamic = IsDynamic;
        }

        protected override void RegisterGeometryInternal(GeometryArena arena)
        {
            if (arena == null) throw new ArgumentNullException(nameof(arena));

            // Важно: если контуры ещё не сгенерированы — ничего не делаем!
            // Это нормально — текст может быть инициализирован позже через InitGlyphContours
            if (GlyphContours == null || GlyphContours.Count == 0)
            {
                DebugManager.Gpu(
                    $"TextGpu '{Name}': No glyph contours yet — geometry registration skipped (will be done later via InitGlyphContours)");
                return;
            }

            DebugManager.Gpu($"TextGpu '{Name}': Auto-registering {GlyphContours.Count} glyphs...");

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

            DebugManager.Gpu($"TextGpu '{Name}': Geometry auto-registered. {flat.Length} vertices.");
        }

        public void InitGlyphContours(GeometryArena arena, IEnumerable<List<List<Vector2>>> glyphContours)
        {
            DebugManager.Gpu($"TextGpu.InitGlyphContours: Initializing glyph contours for '{Name}'.");

            GlyphContours = glyphContours
                .Select(g => g?.Select(c => new List<Vector2>(c ?? [])).ToList() ?? [])
                .ToList();
            EnsureGeometryRegistered(arena);
            DebugManager.Gpu($"TextGpu.InitGlyphContours: Done. Text ready for rendering.");
        }
    }

    #endregion
}