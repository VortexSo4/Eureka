using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using PhysicsSimulation.Base;
using SkiaSharp;
using LibTessDotNet;

namespace PhysicsSimulation.TextRendering
{
    public sealed class TextMesh : IDisposable
    {
        public int Vbo { get; private set; } = -1;
        public int Vao { get; private set; } = -1;
        public List<(int offset, int count)> TriRanges { get; } = [];
        public List<(int offset, int count)> LineRanges { get; } = [];

        private List<Vector3>? _verts;
        private readonly SKTypeface _typeface;
        private readonly string _text;
        private readonly float _fontSize;
        private readonly float _letterPadding;
        private readonly float _verticalPadding;
        private readonly bool _filled;
        private readonly Text.HorizontalAlignment _hAlign;
        private readonly Text.VerticalAlignment _vAlign;
        public int VertexCount => _verts?.Count ?? 0;

        private TextMesh(string? text, SKTypeface typeface, float fontSize, float letterPadding, float verticalPadding, bool filled,
            Text.HorizontalAlignment hAlign, Text.VerticalAlignment vAlign)
        {
            _text = text ?? string.Empty;
            _typeface = typeface;
            _fontSize = fontSize;
            _letterPadding = letterPadding;
            _verticalPadding = verticalPadding;
            _filled = filled;
            _hAlign = hAlign;
            _vAlign = vAlign;

            BuildVertsAndUpload();
        }

        public static TextMesh CreateFromText(string text, SKTypeface typeface, float fontSize,
            float letterPadding = 0.05f, float verticalPadding = 0.1f, bool filled = false,
            Text.HorizontalAlignment hAlign = Text.HorizontalAlignment.Center, Text.VerticalAlignment vAlign = Text.VerticalAlignment.Center)
            => new(text, typeface, fontSize, letterPadding, verticalPadding, filled, hAlign, vAlign);

        private void BuildVertsAndUpload()
        {
            var glyphJobs = new List<(char ch, float offsetX, float cursorY)>();
            var lines = _text.Replace("\r", "").Split('\n');

            float step = _fontSize + _verticalPadding * _fontSize;
            float totalHeight = lines.Length * _fontSize +
                                Math.Max(0, lines.Length - 1) * (_verticalPadding * _fontSize);
            float line0YRelativeToCenter = totalHeight / 2f - _fontSize / 2f;

            float centerOffset = _vAlign switch
            {
                Text.VerticalAlignment.Top => totalHeight / 2f,
                Text.VerticalAlignment.Bottom => -totalHeight / 2f,
                _ => 0f
            };

            for (int li = 0; li < lines.Length; li++)
            {
                var line = lines[li];
                float cursorY = line0YRelativeToCenter - li * step - centerOffset;

                float lineWidth = 0f;
                for (int i = 0; i < line.Length; i++)
                    lineWidth += CharMap.GetGlyphAdvance(line[i], _fontSize, _typeface) +
                                 (i < line.Length - 1 ? _letterPadding * _fontSize : 0f);

                float offsetXLocal = _hAlign switch
                {
                    Text.HorizontalAlignment.Left => 0f,
                    Text.HorizontalAlignment.Right => -lineWidth,
                    _ => -lineWidth / 2f
                };

                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    glyphJobs.Add((c, offsetXLocal, cursorY));
                    float adv = CharMap.GetGlyphAdvance(c, _fontSize, _typeface);
                    offsetXLocal += adv + (i < line.Length - 1 ? _letterPadding * _fontSize : 0f);
                }
            }

            int nJobs = glyphJobs.Count;
            if (nJobs == 0)
            {
                _verts = [];
                TriRanges.Clear();
                LineRanges.Clear();
                if (Vbo != -1)
                {
                    GL.DeleteBuffer(Vbo);
                    Vbo = -1;
                }

                if (Vao != -1)
                {
                    GL.DeleteVertexArray(Vao);
                    Vao = -1;
                }

                return;
            }

            var contoursPerGlyph = new List<List<List<Vector2>>>(new List<List<Vector2>>[nJobs]);

            var popt = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.For(0, nJobs, popt, i =>
            {
                var job = glyphJobs[i];
                var contours = CharMap.GetCharContours(job.ch, job.offsetX, job.cursorY, _fontSize, _typeface);
                contoursPerGlyph[i] = contours;
            });

            _verts = [];
            TriRanges.Clear();
            LineRanges.Clear();

            for (int i = 0; i < nJobs; i++)
            {
                var glyphContours = contoursPerGlyph[i];

                if (_filled && glyphContours.Count > 0)
                {
                    var triVerts = TriangulateGlyph(glyphContours);
                    if (triVerts.Count > 0)
                    {
                        int start = _verts.Count;
                        foreach (var p in triVerts) _verts.Add(new Vector3(p.X, p.Y, 0f));
                        int cnt = _verts.Count - start;
                        if (cnt > 0) TriRanges.Add((start, cnt));
                    }
                }

                foreach (var contour in glyphContours)
                {
                    if (contour.Count < 2) continue;
                    int start = _verts.Count;
                    foreach (var p in contour)
                        _verts.Add(new Vector3(p.X, p.Y, 0f));
                    int count = _verts.Count - start;
                    if (count > 0) LineRanges.Add((start, count));
                }
            }

            if (Vbo != -1)
            {
                GL.DeleteBuffer(Vbo);
                Vbo = -1;
            }

            if (_verts.Count > 0)
            {
                Vbo = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.ArrayBuffer, Vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, _verts.Count * Vector3.SizeInBytes, _verts.ToArray(),
                    BufferUsageHint.StaticDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

                if (Vao == -1)
                    Vao = GL.GenVertexArray();

                GL.BindVertexArray(Vao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, Vbo);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindVertexArray(0);
            }
            else
            {
                if (Vao != -1)
                {
                    GL.DeleteVertexArray(Vao);
                    Vao = -1;
                }
            }
        }

        public void Render(int program, float x, float y, float scale, float rotation, Vector3 color, float lineWidth = 1f)
        {
            if (_verts == null || _verts.Count == 0) return;
            if (Vbo == -1 || Vao == -1) return;

            GL.UseProgram(program);

            int colorLoc = GL.GetUniformLocation(program, "u_color");
            if (colorLoc >= 0) GL.Uniform3(colorLoc, color);

            float cos = MathF.Cos(rotation);
            float sin = MathF.Sin(rotation);

            int transLoc = GL.GetUniformLocation(program, "u_translate");
            if (transLoc >= 0) GL.Uniform2(transLoc, new Vector2(x, y));

            int cosLoc = GL.GetUniformLocation(program, "u_cos");
            if (cosLoc >= 0) GL.Uniform1(cosLoc, cos);

            int sinLoc = GL.GetUniformLocation(program, "u_sin");
            if (sinLoc >= 0) GL.Uniform1(sinLoc, sin);

            int scaleLoc = GL.GetUniformLocation(program, "u_scale");
            if (scaleLoc >= 0) GL.Uniform1(scaleLoc, scale);

            if (lineWidth > 0) GL.LineWidth(lineWidth);

            GL.BindVertexArray(Vao);

            foreach (var (offset, count) in TriRanges)
                GL.DrawArrays(PrimitiveType.Triangles, offset, count);

            foreach (var (offset, count) in LineRanges)
                GL.DrawArrays(PrimitiveType.LineStrip, offset, count);

            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        public void Dispose()
        {
            if (Vbo != -1)
            {
                GL.DeleteBuffer(Vbo);
                Vbo = -1;
            }

            if (Vao != -1)
            {
                GL.DeleteVertexArray(Vao);
                Vao = -1;
            }

            _verts = null;
        }
        
        private List<Vector2> TriangulateGlyph(List<List<Vector2>> contours)
        {
            var tess = new Tess();

            foreach (var contour in contours)
            {
                var vertices = contour.Select(v => new ContourVertex { Position = new Vec3 { X = v.X, Y = v.Y, Z = 0 } }).ToArray();
                tess.AddContour(vertices);
            }

            tess.Tessellate();

            var result = new List<Vector2>();
            for (int i = 0; i < tess.ElementCount; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int idx = tess.Elements[i * 3 + j];
                    var p = tess.Vertices[idx].Position;
                    result.Add(new Vector2(p.X, p.Y));
                }
            }

            return result;
        }
    }
}