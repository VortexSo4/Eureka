name = "DynDemo"

bgColor(to: [0.04, 0.04, 0.08], duration: 0)

// ——— МЫШЬ: точка следует за курсором ———
cursor = Add(circle(0.03, 32))
.aColor(to: [1.0, 1.0, 1.0, 1.0], start: 0, duration: 0)
.dynPos(MX, MY)

// ——— МЫШЬ: кольцо вокруг курсора, пульсирует при клике ———
ring = Add(circle(0.07, 32))
.aColor(to: [0.4, 0.8, 1.0, 0.6], start: 0, duration: 0)
.dynPos(MX, MY)
.dynScale(1.0 + CLICK * 0.5)

// ——— ЛИССАЖУ: фигура из двух синусов ———
lissa = Add(circle(0.008, 32))
.aColor(to: [1.0, 0.4, 0.8, 1.0], start: 0, duration: 0)
.dynPos(sin(T * 2.0) * 0.5, sin(T * 3.0) * 0.5)

// ——— СЛЕД ЛИССАЖУ: несколько фазовых сдвигов ———
l2 = Add(circle(0.006, 32))
.aColor(to: [0.8, 0.3, 1.0, 0.7], start: 0, duration: 0)
.dynPos(sin(T * 2.0 + 0.5) * 0.5, sin(T * 3.0 + 0.5) * 0.5)

l3 = Add(circle(0.005, 32))
.aColor(to: [0.5, 0.2, 0.8, 0.5], start: 0, duration: 0)
.dynPos(sin(T * 2.0 + 1.0) * 0.5, sin(T * 3.0 + 1.0) * 0.5)

// ——— ЦВЕТОВОЙ ПЕРЕЛИВ: большой круг меняет цвет ———
rainbow = Add(circle(0.18, 80))
.dynColor(
    0.5 + sin(T) * 0.5,
    0.5 + sin(T + 2.094) * 0.5,
    0.5 + sin(T + 4.189) * 0.5,
    1.0
)

// ——— СТРЕЛКА СМОТРИТ НА МЫШЬ ———
ptr = Add(arrow(from: [0.0, 0.0], to: [0.12, 0.0]))
.aColor(to: [1.0, 0.9, 0.3, 1.0], start: 0, duration: 0)
.dynRot(atan2(-MY, MX) * 57.2958)

// ——— ПУЛЬСИРУЮЩИЙ КВАДРАТ ———
sq = Add(rect(0.15, 0.15))
.aColor(to: [0.3, 1.0, 0.6, 0.8], start: 0, duration: 0)
.aMove([0.7, 0.6], start: 0, duration: 0)
.dynScale(0.8 + sin(T * 3.0) * 0.3)

// ——— ТЕКСТ: показывает координаты мыши (обновляется каждый кадр) ———
// (текст статичный в .es, но позиция динамическая)
label = Add(text("dynDemo", 0.055))
.aColor(to: [0.7, 0.7, 0.9, 1.0], start: 0, duration: 0)
.aMove([0.0, -0.85], start: 0, duration: 0)

// ——— ИНТЕРАКТИВ: нажми мышь — появляется вспышка ———
flash = Add(circle(0.4, 64))
.dynPos(MX, MY)
.dynColor(1.0, 0.6, 0.2, CLICK * 0.35)

// ——— ОСИ для ориентира ———
ax = Add(axis(0.95))
.aColor(to: [0.2, 0.2, 0.3, 1.0], start: 0, duration: 0)