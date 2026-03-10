#version 450

// ── Push constants ────────────────────────────────────────────────────────────
layout(push_constant) uniform PushConstants {
    float aspectRatio;  // u_aspectRatio
    float time;
    int   primIndex;    // теперь не используется в vertex shader — оставлен для совместимости layout'а с compute
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
layout(std430, set = 0, binding = 3) readonly buffer GeometryArena   { vec2 geom[]; };
layout(std430, set = 0, binding = 4) readonly buffer RenderInstances { RenderInstance instances[]; };

// ── Выход во fragment shader ──────────────────────────────────────────────────
layout(location = 0) out vec4  fragColor;
layout(location = 1) out float fragDiscard;

void main() {
    // gl_InstanceIndex == firstInstance из VkDrawIndexedIndirectCommand,
    // которое мы устанавливаем равным PrimitiveId.
    // Один CmdDrawIndexedIndirect с N командами заменяет N отдельных CmdDrawIndexed.
    RenderInstance inst = instances[gl_InstanceIndex];

    int  vertexOffset = inst.meta.x;
    vec2 in_pos       = geom[vertexOffset + gl_VertexIndex];

    if (isnan(in_pos.x) || isnan(in_pos.y)) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        fragColor   = vec4(0.0);
        fragDiscard = 1.0;
        return;
    }

    mat2 rs = mat2(inst.row0.x, inst.row1.x,
                   inst.row0.y, inst.row1.y);
    vec2 p  = rs * in_pos + inst.row2.xy;

    p.x /= pc.aspectRatio;

    gl_Position   = vec4(p, 0.0, 1.0);
    gl_Position.y = -gl_Position.y;
    fragColor     = inst.color;
    fragDiscard   = 0.0;
}
