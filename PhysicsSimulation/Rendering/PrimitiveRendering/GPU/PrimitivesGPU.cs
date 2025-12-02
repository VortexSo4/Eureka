// File: PrimitivesGPU.cs
// Namespace: PhysicsSimulation.Rendering.PrimitiveRendering.GPU
// Purpose: GPU-first primitive definitions (CPU-side metadata + SSBO serialization helpers).
// Note: Actual compute shaders + rendering path will be implemented in AnimationEngine / RenderEngine.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

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
        }
    }

    public struct AnimIndexCpu
    {
        public int Start;
        public int Count;
        public int R2;
        public int R3;
        public AnimIndexCpu(int start, int count) { Start = start; Count = count; R2 = 0; R3 = 0; }
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
            if (vertexCount <= 0) return -1;
            int off = _nextOffset;
            _allocations.Add((off, vertexCount));
            _nextOffset += vertexCount;
            return off;
        }

        public int TotalVertexCount => _nextOffset;

        public void Reset()
        {
            _nextOffset = 0;
            _allocations.Clear();
        }

        // Flatten contours into a single vertex array with NaN separators between contours
        public static Vector2[] FlattenContours(IReadOnlyList<List<Vector2>> contours)
        {
            var list = new List<Vector2>();
            for (int i = 0; i < contours.Count; i++)
            {
                var c = contours[i];
                if (c == null || c.Count == 0) continue;
                foreach (var v in c) list.Add(v);
                if (i < contours.Count - 1) list.Add(new Vector2(float.NaN, float.NaN));
            }
            return list.ToArray();
        }
    }

    #endregion

    #region PrimitiveGPU base

    public abstract class PrimitiveGpu
    {
        // assigned by engine
        public int PrimitiveId { get; internal set; } = -1;
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

        protected PrimitiveGpu(string name = "")
        {
            Name = name ?? "";
        }

        #region Geometry registration helpers

        protected int RegisterRawGeometry(GeometryArena arena, Vector2[] flattenedVertices)
        {
            if (flattenedVertices == null || flattenedVertices.Length == 0)
                throw new ArgumentException("flattenedVertices is empty");

            int off = arena.Allocate(flattenedVertices.Length);
            VertexOffsetRaw = off;
            VertexOffsetA = off;
            VertexCount = flattenedVertices.Length;
            return off;
        }

        public void ReserveMorphTargets(GeometryArena arena, int reserveCount)
        {
            if (reserveCount <= 0) return;
            if (VertexOffsetB == -1) VertexOffsetB = arena.Allocate(reserveCount);
            if (VertexOffsetM == -1) VertexOffsetM = arena.Allocate(reserveCount);
        }

        #endregion

        #region Animation scheduling API

        public void ScheduleAnimation(
            AnimType type,
            float start,
            float end,
            EaseType ease,
            Vector4 from,
            Vector4 to,
            int morphOffsetA = 0,
            int morphOffsetB = 0,
            int morphOffsetM = 0,
            int morphVertexCount = 0)
        {
            var entry = new AnimEntryCpu(
                type,
                PrimitiveId,
                start,
                end,
                ease,
                from,
                to,
                morphOffsetA,
                morphOffsetB,
                morphOffsetM,
                morphVertexCount
            );
            PendingAnimations.Add(entry);
        }

        public void ClearPendingAnimations() => PendingAnimations.Clear();

        #endregion

        #region Convenience anim helpers (old-style friendly API)

        // Animates position (vec2)
        public void AnimatePosition(float start, float end, EaseType ease, Vector2 from, Vector2 to)
        {
            ScheduleAnimation(AnimType.Translate, start, end, ease, new Vector4(from, 0f, 0f), new Vector4(to, 0f, 0f));
        }

        // Animates color (vec4)
        public void AnimateColor(float start, float end, EaseType ease, Vector4 from, Vector4 to)
        {
            ScheduleAnimation(AnimType.Color, start, end, ease, from, to);
        }

        // Animates scale (single float stored in .x)
        public void AnimateScale(float start, float end, EaseType ease, float from, float to)
        {
            ScheduleAnimation(AnimType.Scale, start, end, ease, new Vector4(from, 0f,0f,0f), new Vector4(to,0f,0f,0f));
        }

        // Morph request: we pass offsets/count so compute can read proper ranges
        public void AnimateMorph(float start, float end, EaseType ease, int offsetA, int offsetB, int offsetM, int vertexCount)
        {
            ScheduleAnimation(AnimType.Morph, start, end, ease, Vector4.Zero, Vector4.One, offsetA, offsetB, offsetM, vertexCount);
        }

        // Dash lengths tuning
        public void AnimateDashLengths(float start, float end, EaseType ease, float fromFilled, float fromEmpty, float toFilled, float toEmpty)
        {
            ScheduleAnimation(AnimType.DashLengths, start, end, ease, new Vector4(fromFilled, fromEmpty, 0, 0), new Vector4(toFilled, toEmpty, 0, 0));
        }

        #endregion

        #region Render instance conversion + serialization helpers

        public RenderInstanceCpu ToRenderInstanceCpu()
        {
            float c = MathF.Cos(Rotation);
            float s = MathF.Sin(Rotation);

            var row0 = new Vector4(Scale * c, Scale * s, 0f, 0f);
            var row1 = new Vector4(-Scale * s, Scale * c, 0f, 0f);
            var row2 = new Vector4(Position.X, Position.Y, 1f, 0f);

            var inst = new RenderInstanceCpu
            {
                TransformRow0 = row0,
                TransformRow1 = row1,
                TransformRow2 = row2,
                Color = Color,
                OffsetM = VertexOffsetM >= 0 ? VertexOffsetM : VertexOffsetA,
                VertexCount = VertexCount,
                Flags = (int)Flags,
                Reserved = 0,
                DashInfo = new Vector4(FilledLength, EmptyLength, DashOffset, 0f)
            };

            return inst;
        }

        public static byte[] SerializeAnimEntries(IList<AnimEntryCpu> arr)
        {
            const int entrySize = 80;
            var outBytes = new byte[arr.Count * entrySize];
            int offset = 0;
            for (int i = 0; i < arr.Count; i++)
            {
                var e = arr[i];
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.MetaType); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.PrimitiveId); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.MetaR2); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.MetaR3); offset += 4;

                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.Start); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.End); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.EaseAsFloat); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.TimesR); offset += 4;

                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.From.X); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.From.Y); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.From.Z); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.From.W); offset += 4;

                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.To.X); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.To.Y); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.To.Z); offset += 4;
                BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(outBytes, offset, 4), e.To.W); offset += 4;

                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.MorphOffsetA); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.MorphOffsetB); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.MorphOffsetM); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(outBytes, offset, 4), e.MorphVertexCount); offset += 4;
            }
            return outBytes;
        }

        public static AnimIndexCpu[] BuildAnimIndex(IList<AnimEntryCpu> allEntries, int primitiveCount)
        {
            var result = new AnimIndexCpu[primitiveCount];
            if (allEntries == null || allEntries.Count == 0)
            {
                for (int i = 0; i < primitiveCount; i++) result[i] = new AnimIndexCpu(0, 0);
                return result;
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < allEntries.Count; i++)
            {
                int pid = allEntries[i].PrimitiveId;
                if (!groups.TryGetValue(pid, out var l)) { l = []; groups[pid] = l; }
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
            return result;
        }

        public static List<AnimEntryCpu> AggregateEntries(IEnumerable<PrimitiveGpu> primitives)
        {
            var outList = new List<AnimEntryCpu>();
            foreach (var p in primitives.OrderBy(p => p.PrimitiveId))
            {
                if (p.PendingAnimations.Count == 0) continue;
                outList.AddRange(p.PendingAnimations);
            }
            return outList;
        }

        #endregion
    }

    #endregion

    #region PolygonGPU & RectGPU

    public class PolygonGpu : PrimitiveGpu
    {
        public List<List<Vector2>> Contours { get; private set; } = [];

        public PolygonGpu(string name = "") : base(name) { }

        // protected convenience ctor for derived primitives that know their contours at creation time
        protected PolygonGpu(List<List<Vector2>> contours, string name = "") : base(name)
        {
            if (contours != null) Contours = contours.Select(c => new List<Vector2>(c)).ToList();
        }

        /// <summary>
        /// Initialize polygon contours and register raw geometry into arena.
        /// This does not upload to GPU itself; engine will perform the upload.
        /// </summary>
        public void InitGeometry(GeometryArena arena, IReadOnlyList<List<Vector2>> contours)
        {
            if (contours == null) throw new ArgumentNullException(nameof(contours));
            Contours = contours.Select(c => new List<Vector2>(c)).ToList();
            var flat = GeometryArena.FlattenContours(Contours);
            RegisterRawGeometry(arena, flat);
        }

        public Vector2[] GetFlattenedVertices() => GeometryArena.FlattenContours(Contours);

        // convenience: set dashed flag quickly (old API compatibility)
        public void SetDash(bool enabled, float filledLen = 0.05f, float emptyLen = 0.03f, float phase = 0f)
        {
            if (enabled) Flags |= PrimitiveFlags.None; // keep flags extensible
            FilledLength = filledLen;
            EmptyLength = emptyLen;
            DashOffset = phase;
        }
    }

    public class RectGpu : PolygonGpu
    {
        public float Width { get; set; }
        public float Height { get; set; }

        public RectGpu(float width = 1f, float height = 1f, string name = "")
            : base(CreateRectangleContours(width, height), name)
        {
            Width = width;
            Height = height;
        }

        private static List<List<Vector2>> CreateRectangleContours(float width, float height)
        {
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

            return [contour];
        }
    }

    #endregion

    #region TextGPU (basic, glyph contours are provided by engine/CharMap)

    public class TextGpu : PrimitiveGpu
    {
        public string Text { get; set; } = "";
        // GlyphContours: per-glyph list of contours (each contour is list of vec2)
        public List<List<List<Vector2>>> GlyphContours { get; private set; } = [];

        public float FontSize { get; set; } = 0.1f;
        public float LetterPadding { get; set; } = 0.05f;
        public float LineSpacing { get; set; } = 0.1f;

        public enum HorizontalAlignment { Left, Center, Right }
        public enum VerticalAlignment { Top, Center, Bottom }

        public HorizontalAlignment HAlign { get; set; } = HorizontalAlignment.Center;
        public VerticalAlignment VAlign { get; set; } = VerticalAlignment.Center;

        public string? FontKey { get; set; }
        public Func<string>? DynamicTextSource { get; set; }

        public TextGpu(string text = "Empty text", float fontSize = 0.1f, string? fontKey = null, string name = "")
            : base(name)
        {
            Text = text ?? "";
            FontSize = fontSize;
            FontKey = fontKey;
        }

        // Engine should call this after generating glyph contours (via CharMap)
        public void InitGlyphContours(GeometryArena arena, IEnumerable<List<List<Vector2>>> glyphContours)
        {
            GlyphContours = glyphContours.Select(g => g.Select(c => new List<Vector2>(c)).ToList()).ToList();

            var allContours = new List<List<Vector2>>();
            foreach (var g in GlyphContours)
            {
                foreach (var c in g) allContours.Add(c);
                // optional: add NaN glyph separator between glyphs if desired by renderer (we do contour separators already)
            }

            var flat = GeometryArena.FlattenContours(allContours);
            RegisterRawGeometry(arena, flat);
        }
    }

    #endregion
}
