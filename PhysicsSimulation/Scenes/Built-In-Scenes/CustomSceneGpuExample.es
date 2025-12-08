name = "EpicTimedShowcase"

// ——— РЕКТ ———
r = Add(rect(0.6, 0.4, "true"))
r.aColor(to: [1, 0.3, 0.5, 1], start: 0.5, duration: 2, ease: "out")
r.aScale(1.6, start: 1, duration: 1.2, ease: "out")
r.aMove([0.6, 0.6], start: 0, duration: 4, ease: "linear")
r.aRot(0, start: 0, duration: 0)
r.aRot(720, start: 1, duration: 6, ease: "linear")

// ——— ЭЛЛИПС ———
e = Add(ellipse(0.4, 0.2, "true"))
e.aColor(to: [0.2, 0.8, 1, 1], start: 0, duration: 3, ease: "inout")
e.aScale(1.2, start: 0.5, duration: 2, ease: "in")
e.aMove([-0.5, 0.5], start: 0, duration: 4, ease: "linear")

// ——— ДУГА ———
a = Add(arc(0.3, 0.6, 0, 180, "true"))
a.aColor(to: [1, 1, 0.3, 1], start: 1, duration: 3, ease: "out")
a.aRot(90, start: 1, duration: 4, ease: "inout")
a.aMove([0.5, -0.5], start: 0, duration: 4, ease: "linear")

// ——— СТРЕЛКА ———
arrow = Add(arrow([0,0], [0.5,0.5], "true"))
arrow.aColor(to: [1, 0.5, 0.5, 1], start: 2, duration: 2, ease: "in")
arrow.aMove([0.0, 0.6], start: 2, duration: 3, ease: "linear")
arrow.aScale(1.5, start: 2.5, duration: 2, ease: "out")

// ——— КРИВАЯ БЕЗЬЕ ———
bz = Add(bezier([[ -0.5, -0.5], [0, 0.5], [0.5, -0.5]], "true"))
bz.aColor(to: [0.8, 1, 0.5, 1], start: 3, duration: 3, ease: "inout")
bz.aScale(1.3, start: 3.5, duration: 2, ease: "out")
bz.aMove([0.0, -0.5], start: 3, duration: 3, ease: "linear")

// ——— СЕТКА ———
grid = Add(grid(3, 3, "true"))
grid.aColor(to: [0.5, 0.5, 1, 1], start: 4, duration: 3, ease: "linear")
grid.aScale(1.1, start: 4, duration: 2, ease: "inout")

text = Add(text("something"))

// ——— ОСИ ———
axis = Add(axis(1.0, 1.0, "true"))
axis.aColor(to: [1, 0.7, 0.2, 1], start: 5, duration: 3, ease: "out")
axis.aMove([-0.6, -0.6], start: 5, duration: 4, ease: "linear")
axis.aScale(1.2, start: 5, duration: 2, ease: "in")
