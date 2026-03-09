#version 450

// ── Вход из vertex shader ─────────────────────────────────────────────────────
layout(location = 0) in vec4  fragColor;
layout(location = 1) in float fragDiscard;

// ── Выход ─────────────────────────────────────────────────────────────────────
layout(location = 0) out vec4 outColor;

void main() {
    // NaN-сепараторы отбрасываем (vertex shader вытолкнул их за clip space,
    // но на всякий случай — discard если fragDiscard == 1.0)
    if (fragDiscard > 0.5) discard;

    outColor = fragColor;
}
