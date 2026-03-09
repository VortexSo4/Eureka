name = "Benchmark"

bgColor(to: [0.04, 0.03, 0.07], duration: 0)

// ═══ STATE ═══
state hue    = 0
state clicks = 0

// ═══ ФОН: тонкая сетка ═══
g = Add(grid(cols: 20, rows: 12, spacing: 0.1))
g.aColor(to: [0.1, 0.1, 0.16, 0.25], start: 0, duration: 0)

// ══════════════════════════════════════════
// ЗОНА 1 — верх-лево: PLOT (синус + косинус)
// ══════════════════════════════════════════
wave = Add(plot(func: x => sin(x * (3.0 + MY * 2.0) + T * 2.0) * 0.12, from: -0.45, to: 0.45, steps: 120))
wave.aMove([-0.52, 0.62], start: 0, duration: 0)
wave.dynColor(0.3 + sin(T) * 0.3, 0.9, 0.6, 0.9)

wave2 = Add(plot(func: x => cos(x * (4.0 + MX * 2.0) + T * 1.5) * 0.12, from: -0.45, to: 0.45, steps: 120))
wave2.aMove([-0.52, 0.62], start: 0, duration: 0)
wave2.dynColor(0.9, 0.5, 0.3, 0.7)

ax1 = Add(axis(length: 0.42))
ax1.aMove([-0.52, 0.62], start: 0, duration: 0)
ax1.aColor(to: [0.3, 0.3, 0.45, 0.5], start: 0, duration: 0)

lbl_plot = Add(text("plot", 0.032))
lbl_plot.aMove([-0.52, 0.42], start: 0, duration: 0)
lbl_plot.dynColor(0.4, 0.9, 0.6, 0.6)

// ══════════════════════════════════════════
// ЗОНА 2 — верх-центр: BEZIER
// ══════════════════════════════════════════
bz = Add(bezier(p0: [-0.22, 0.0], p1: [-0.07, 0.18], p2: [0.07, -0.18], p3: [0.22, 0.0]))
bz.aMove([0.0, 0.68], start: 0, duration: 0)
bz.dynColor(0.7 + sin(T * 2.3) * 0.3, 0.3, 0.9, 0.9)
bz.dynScale(1.0 + sin(T * 0.9) * 0.12)

lbl_bz = Add(text("bezier", 0.032))
lbl_bz.aMove([0.0, 0.49], start: 0, duration: 0)
lbl_bz.dynColor(0.7, 0.3, 0.9, 0.6)

// ══════════════════════════════════════════
// ЗОНА 3 — верх-право: ARCS (спиннеры)
// ══════════════════════════════════════════
arc1 = Add(arc(radius: 0.13, start: 0, end: 260))
arc1.aMove([0.55, 0.65], start: 0, duration: 0)
arc1.dynRot(T * 70.0)
arc1.dynColor(0.3, 0.8, 1.0, 0.9)

arc2 = Add(arc(radius: 0.085, start: 0, end: 200))
arc2.aMove([0.55, 0.65], start: 0, duration: 0)
arc2.dynRot(-T * 110.0)
arc2.dynColor(1.0, 0.55, 0.2, 0.85)

arc3 = Add(arc(radius: 0.042, start: 0, end: 310))
arc3.aMove([0.55, 0.65], start: 0, duration: 0)
arc3.dynRot(T * 180.0)
arc3.dynColor(1.0, 1.0, 0.35, 0.9)

lbl_arc = Add(text("arcs", 0.032))
lbl_arc.aMove([0.55, 0.47], start: 0, duration: 0)
lbl_arc.dynColor(0.3, 0.8, 1.0, 0.6)

// ══════════════════════════════════════════
// ЗОНА 4 — лево-центр: LINE ВЕЕР
// ══════════════════════════════════════════
l1 = Add(line(from: [0.0, 0.0], to: [0.14, 0.0]))
l1.aMove([-0.72, 0.0], start: 0, duration: 0)
l1.dynRot(T * 45.0 + 0.0)
l1.dynColor(0.9, 0.3, 0.3, 0.85)

l2 = Add(line(from: [0.0, 0.0], to: [0.14, 0.0]))
l2.aMove([-0.72, 0.0], start: 0, duration: 0)
l2.dynRot(T * 45.0 + 51.4)
l2.dynColor(0.9, 0.65, 0.2, 0.85)

l3 = Add(line(from: [0.0, 0.0], to: [0.14, 0.0]))
l3.aMove([-0.72, 0.0], start: 0, duration: 0)
l3.dynRot(T * 45.0 + 102.8)
l3.dynColor(0.35, 0.9, 0.35, 0.85)

l4 = Add(line(from: [0.0, 0.0], to: [0.14, 0.0]))
l4.aMove([-0.72, 0.0], start: 0, duration: 0)
l4.dynRot(T * 45.0 + 154.2)
l4.dynColor(0.2, 0.65, 0.9, 0.85)

l5 = Add(line(from: [0.0, 0.0], to: [0.14, 0.0]))
l5.aMove([-0.72, 0.0], start: 0, duration: 0)
l5.dynRot(T * 45.0 + 205.7)
l5.dynColor(0.75, 0.2, 0.9, 0.85)

l6 = Add(line(from: [0.0, 0.0], to: [0.14, 0.0]))
l6.aMove([-0.72, 0.0], start: 0, duration: 0)
l6.dynRot(T * 45.0 + 257.1)
l6.dynColor(0.9, 0.9, 0.3, 0.85)

l7 = Add(line(from: [0.0, 0.0], to: [0.14, 0.0]))
l7.aMove([-0.72, 0.0], start: 0, duration: 0)
l7.dynRot(T * 45.0 + 308.5)
l7.dynColor(0.9, 0.3, 0.7, 0.85)

lbl_lines = Add(text("lines", 0.032))
lbl_lines.aMove([-0.72, -0.17], start: 0, duration: 0)
lbl_lines.dynColor(0.9, 0.7, 0.4, 0.6)

// ══════════════════════════════════════════
// ЗОНА 5 — центр: RECT + TRIANGLE + onClick
// ══════════════════════════════════════════
r1 = Add(rect(0.1, 0.1))
r1.aMove([0.0, 0.0], start: 0, duration: 0)
r1.aScale(to: 1.12, start: 0.0, duration: 0.5)
r1.aScale(to: 0.88, start: 0.5, duration: 0.5)
r1.aScale(to: 1.12, start: 1.0, duration: 0.5)
r1.dynRot(T * 22.0)
r1.dynColor(
    0.5 + sin(T * 2.5 + getState("hue")) * 0.5,
    0.5 + sin(T * 2.5 + getState("hue") + 2.094) * 0.5,
    0.5 + sin(T * 2.5 + getState("hue") + 4.189) * 0.5,
    0.9
)
r1.onClick({
    setState("clicks", getState("clicks") + 1)
    setState("hue", getState("hue") + 1.1)
})

tri = Add(triangle(size: 0.042))
tri.dynPos(sin(T * 3.2) * 0.18, cos(T * 3.2) * 0.18)
tri.dynRot(-T * 130.0)
tri.dynColor(1.0, 0.88, 0.2, 0.95)

lbl_center = Add(text("click rect", 0.032))
lbl_center.aMove([0.0, -0.17], start: 0, duration: 0)
lbl_center.dynColor(
    0.5 + sin(T + getState("hue")) * 0.5,
    0.5 + sin(T + getState("hue") + 2.094) * 0.5,
    0.5 + sin(T + getState("hue") + 4.189) * 0.5,
    0.75
)

// ══════════════════════════════════════════
// ЗОНА 6 — право-центр: ARROW + ELLIPSE
// ══════════════════════════════════════════
el = Add(ellipse(rx: 0.11, ry: 0.065, segments: 48))
el.aMove([0.72, 0.0], start: 0, duration: 0)
el.dynRot(T * 28.0)
el.dynScale(1.0 + sin(T * 2.8) * 0.2)
el.dynColor(
    0.5 + cos(T * 1.4 + getState("hue")) * 0.5,
    0.35 + sin(T * 1.9) * 0.3,
    0.85, 0.8
)

el2 = Add(ellipse(rx: 0.065, ry: 0.11, segments: 48))
el2.aMove([0.72, 0.0], start: 0, duration: 0)
el2.dynRot(-T * 42.0)
el2.dynScale(1.0 + cos(T * 2.8) * 0.15)
el2.dynColor(1.0, 0.4, 0.55, 0.5)

ptr = Add(arrow(from: [0.0, 0.0], to: [0.12, 0.0]))
ptr.aMove([0.72, 0.0], start: 0, duration: 0)
ptr.dynRot(atan2(-(MY - 0.0), MX - 0.72) * 57.2958)
ptr.dynColor(1.0, 0.85, 0.3, 0.85 + CLICK * 0.15)
ptr.dynScale(0.85 + CLICK * 0.6)

lbl_right = Add(text("ellipse + arrow", 0.032))
lbl_right.aMove([0.72, -0.17], start: 0, duration: 0)
lbl_right.dynColor(0.6, 0.4, 0.9, 0.6)

// ══════════════════════════════════════════
// ЗОНА 7 — низ: CIRCLES пульс
// ══════════════════════════════════════════
cc1 = Add(circle(0.055, 48))
cc1.aMove([-0.45, -0.65], start: 0, duration: 0)
cc1.dynScale(0.7 + sin(T * 3.5 + 0.0) * 0.45)
cc1.dynColor(1.0, 0.3, 0.35, 0.85)

cc2 = Add(circle(0.055, 48))
cc2.aMove([-0.22, -0.65], start: 0, duration: 0)
cc2.dynScale(0.7 + sin(T * 3.5 + 1.047) * 0.45)
cc2.dynColor(1.0, 0.7, 0.2, 0.85)

cc3 = Add(circle(0.055, 48))
cc3.aMove([0.0, -0.65], start: 0, duration: 0)
cc3.dynScale(0.7 + sin(T * 3.5 + 2.094) * 0.45)
cc3.dynColor(0.3, 0.95, 0.4, 0.85)

cc4 = Add(circle(0.055, 48))
cc4.aMove([0.22, -0.65], start: 0, duration: 0)
cc4.dynScale(0.7 + sin(T * 3.5 + 3.142) * 0.45)
cc4.dynColor(0.25, 0.6, 1.0, 0.85)

cc5 = Add(circle(0.055, 48))
cc5.aMove([0.45, -0.65], start: 0, duration: 0)
cc5.dynScale(0.7 + sin(T * 3.5 + 4.189) * 0.45)
cc5.dynColor(0.8, 0.25, 1.0, 0.85)

lbl_circles = Add(text("circles", 0.032))
lbl_circles.aMove([0.0, -0.82], start: 0, duration: 0)
lbl_circles.dynColor(0.6, 0.8, 1.0, 0.6)

// ══════════════════════════════════════════
// КУРСОР + клик-вспышка
// ══════════════════════════════════════════
flash = Add(circle(0.05, 32))
flash.dynPos(MX, MY)
flash.dynColor(1.0, 1.0, 1.0, CLICK * 0.35)
flash.dynScale(1.0 + CLICK * 2.2)

cursor = Add(circle(0.015, 32))
cursor.dynPos(MX, MY)
cursor.dynColor(1.0, 1.0, 1.0, 0.9)
cursor.dynScale(1.0 + CLICK * 0.5)