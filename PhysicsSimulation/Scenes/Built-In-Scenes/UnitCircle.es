name = "UnitCircle"

// ——— BACKGROUND ———
bgColor(to: [0.06, 0.06, 0.10], duration: 0)

// ——— AXES ———
ax = Add(axis(0.85))
ax.aColor(to: [0.35, 0.35, 0.45, 1], start: 0, duration: 0)

// ——— UNIT CIRCLE ———
c = Add(circle(0.6, 120))
c.aColor(to: [0.3, 0.3, 0.5, 1], start: 0, duration: 0)
c.aColor(to: [0.5, 0.5, 0.9, 1], start: 0.5, duration: 1.5, ease: "out")

// ——— LABELS: axis ends ———
lx = Add(text("1", 0.07))
lx.aColor(to: [0.6, 0.6, 0.7, 1], start: 0, duration: 0)
lx.aMove([0.68, -0.07], start: 0, duration: 0)

lxn = Add(text("-1", 0.07))
lxn.aColor(to: [0.6, 0.6, 0.7, 1], start: 0, duration: 0)
lxn.aMove([-0.72, -0.07], start: 0, duration: 0)

ly = Add(text("1", 0.07))
ly.aColor(to: [0.6, 0.6, 0.7, 1], start: 0, duration: 0)
ly.aMove([0.05, 0.64], start: 0, duration: 0)

// ——— RADIUS LINE (angle = 0, rotates to 60 deg) ———
// Starts horizontal, then rotates to show angle
r = Add(line(0, 0, 0.6, 0))
r.aColor(to: [1.0, 0.85, 0.3, 1], start: 0, duration: 0)
r.aColor(to: [1.0, 0.92, 0.4, 1], start: 1, duration: 1, ease: "out")

// Rotate radius to 60 degrees
r.aRot(0, start: 0, duration: 0)
r.aRot(60, start: 2, duration: 2.5, ease: "inout")

// ——— POINT ON CIRCLE ———
// Starts at (0.6, 0), moves to (0.3, 0.52) = (cos60, sin60) * 0.6
pt = Add(circle(0.018, 32))
pt.aColor(to: [1.0, 0.85, 0.3, 1], start: 0, duration: 0)
pt.aMove([0.6, 0.0], start: 0, duration: 0)
pt.aMove([0.3, 0.52], start: 2, duration: 2.5, ease: "inout")

// ——— COS PROJECTION (vertical dashed line from point to X axis) ———
cos_line = Add(line(0.6, 0.0, 0.6, 0.0))
cos_line.aColor(to: [0.3, 0.85, 0.5, 1], start: 2.5, duration: 0.5, ease: "out")
cos_line.aMove([0.3, 0.0], start: 2, duration: 2.5, ease: "inout")

// ——— SIN PROJECTION (horizontal dashed line from point to Y axis) ———
sin_line = Add(line(0.0, 0.52, 0.0, 0.52))
sin_line.aColor(to: [0.85, 0.3, 0.5, 1], start: 2.5, duration: 0.5, ease: "out")
sin_line.aMove([0.0, 0.26], start: 2, duration: 2.5, ease: "inout")

// ——— ANGLE ARC ———
ang = Add(arc(0.15, 0, 60))
ang.aColor(to: [1.0, 0.85, 0.3, 0.6], start: 2, duration: 1, ease: "out")

// ——— LABELS: appear after animation ———
label_angle = Add(text("60°", 0.065))
label_angle.aColor(to: [0.0, 0.0, 0.0, 0], start: 0, duration: 0)
label_angle.aMove([0.18, 0.07], start: 0, duration: 0)
label_angle.aColor(to: [1.0, 0.85, 0.3, 1], start: 3.5, duration: 0.8, ease: "out")

label_cos = Add(text("cos 60° = 0.5", 0.055))
label_cos.aColor(to: [0.0, 0.0, 0.0, 0], start: 0, duration: 0)
label_cos.aMove([0.3, -0.12], start: 0, duration: 0)
label_cos.aColor(to: [0.3, 0.95, 0.5, 1], start: 4.0, duration: 0.8, ease: "out")

label_sin = Add(text("sin 60° = 0.87", 0.055))
label_sin.aColor(to: [0.0, 0.0, 0.0, 0], start: 0, duration: 0)
label_sin.aMove([-0.38, 0.26], start: 0, duration: 0)
label_sin.aColor(to: [0.95, 0.35, 0.55, 1], start: 4.5, duration: 0.8, ease: "out")

// ——— TITLE ———
title = Add(text("Unit Circle", 0.075))
title.aColor(to: [0.0, 0.0, 0.0, 0], start: 0, duration: 0)
title.aMove([0.0, 0.82], start: 0, duration: 0)
title.aColor(to: [0.85, 0.85, 1.0, 1], start: 0.3, duration: 1.2, ease: "out")
