#version 450

// ── Push constants ────────────────────────────────────────────────────────────
layout(push_constant) uniform PushConstants {
    float aspectRatio;  // u_aspectRatio
    float time;
    int   primIndex;    // u_primIndex — какой примитив рисуем
    float reserved;
} pc;

// ── Структуры ─────────────────────────────────────────────────────────────────
struct RenderInstance {
    vec4  row0;
    vec4  row1;
    vec4  row2;
    vec4  color;
    ivec4 meta;  // .x = offsetM (начало вершин в GeometryArena)
    vec4  dash;
};

// ── SSBOs ─────────────────────────────────────────────────────────────────────
// Вместо VAO: читаем вершины напрямую из буфера геометрии по gl_VertexIndex.
// Это главное отличие от OpenGL версии где был: layout(location=0) in vec2 in_pos;
layout(std430, set = 0, binding = 3) readonly buffer GeometryArena   { vec2 geom[]; };
layout(std430, set = 0, binding = 4) readonly buffer RenderInstances { RenderInstance instances[]; };

// ── Выход во fragment shader ──────────────────────────────────────────────────
layout(location = 0) out vec4 fragColor;
layout(location = 1) out float fragDiscard; // 1.0 = NaN-сепаратор, отбросить фрагмент

void main() {
    RenderInstance inst = instances[pc.primIndex];

    // Читаем вершину по индексу: offsetM + gl_VertexIndex
    // offsetM хранится в inst.meta.x (поле OffsetM из RenderInstanceCpu)
    int  vertexOffset = inst.meta.x;
    vec2 in_pos       = geom[vertexOffset + gl_VertexIndex];

    // NaN-сепаратор между контурами — выталкиваем за пределы clip space.
    // Аналог OpenGL версии: gl_Position = vec4(2.0, 2.0, 2.0, 1.0)
    if (isnan(in_pos.x) || isnan(in_pos.y)) {
        gl_Position  = vec4(2.0, 2.0, 2.0, 1.0);
        fragColor    = vec4(0.0);
        fragDiscard  = 1.0;
        return;
    }

    // Применяем трансформацию: rotate + scale (mat2 из row0/row1) + translate (row2.xy)
    mat2 rs = mat2(inst.row0.x, inst.row1.x,
                   inst.row0.y, inst.row1.y);
    vec2 p  = rs * in_pos + inst.row2.xy;

    // Коррекция aspect ratio по X (чтобы не было растяжения)
    p.x /= pc.aspectRatio;

    gl_Position = vec4(p, 0.0, 1.0);
    gl_Position.y = -gl_Position.y;
    fragColor   = inst.color;
    fragDiscard = 0.0;
}
