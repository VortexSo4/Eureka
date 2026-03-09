name = "InteractiveDemo"

bgColor(to: [0.03, 0.03, 0.06], duration: 0)

// ——— STATE ———
state clicks = 0
state hue = 0
state spawned = 0

// ——— ФОН: пульсирует от кликов ———
bg = Add(circle(2.0, 64))
bg.dynColor(
    0.04 + getState("clicks") * 0.02,
    0.03,
    0.08 + getState("clicks") * 0.015,
    1.0
)

// ——— ЦЕНТРАЛЬНАЯ КНОПКА ———
btn = Add(circle(0.12, 64))
btn.aMove([0.0, 0.0], start: 0, duration: 0)
btn.dynColor(
    0.5 + sin(T * 2 + getState("hue")) * 0.5,
    0.5 + sin(T * 2 + getState("hue") + 2.094) * 0.5,
    0.5 + sin(T * 2 + getState("hue") + 4.189) * 0.5,
    1.0
)
btn.dynScale(1.0 + CLICK * 0.3 + sin(T * 4) * 0.05)
btn.onClick({
    setState("clicks", getState("clicks") + 1)
    setState("hue", getState("hue") + 0.8)
})

// ——— КОЛЬЦО ВОКРУГ КНОПКИ ———
ring = Add(circle(0.18, 64))
ring.aMove([0.0, 0.0], start: 0, duration: 0)
ring.dynColor(
    0.5 + sin(T * 2 + getState("hue") + 1.0) * 0.5,
    0.5 + sin(T * 2 + getState("hue") + 3.094) * 0.5,
    0.5 + sin(T * 2 + getState("hue") + 5.189) * 0.5,
    0.4
)
ring.dynScale(1.0 + sin(T * 3 + getState("clicks")) * 0.1)

// ——— 6 ОРБИТАЛЬНЫХ ТОЧЕК ———
o1 = Add(circle(0.035, 32))
o1.dynPos(sin(T * 1.2 + 0.0) * 0.45, cos(T * 1.2 + 0.0) * 0.45)
o1.dynColor(0.9, 0.4, 0.4, 0.9)
o1.dynScale(0.8 + sin(T * 3.0 + 0.0) * 0.3)

o2 = Add(circle(0.035, 32))
o2.dynPos(sin(T * 1.2 + 1.047) * 0.45, cos(T * 1.2 + 1.047) * 0.45)
o2.dynColor(0.9, 0.7, 0.2, 0.9)
o2.dynScale(0.8 + sin(T * 3.0 + 1.047) * 0.3)

o3 = Add(circle(0.035, 32))
o3.dynPos(sin(T * 1.2 + 2.094) * 0.45, cos(T * 1.2 + 2.094) * 0.45)
o3.dynColor(0.4, 0.9, 0.4, 0.9)
o3.dynScale(0.8 + sin(T * 3.0 + 2.094) * 0.3)

o4 = Add(circle(0.035, 32))
o4.dynPos(sin(T * 1.2 + 3.142) * 0.45, cos(T * 1.2 + 3.142) * 0.45)
o4.dynColor(0.2, 0.7, 0.9, 0.9)
o4.dynScale(0.8 + sin(T * 3.0 + 3.142) * 0.3)

o5 = Add(circle(0.035, 32))
o5.dynPos(sin(T * 1.2 + 4.189) * 0.45, cos(T * 1.2 + 4.189) * 0.45)
o5.dynColor(0.5, 0.3, 0.9, 0.9)
o5.dynScale(0.8 + sin(T * 3.0 + 4.189) * 0.3)

o6 = Add(circle(0.035, 32))
o6.dynPos(sin(T * 1.2 + 5.236) * 0.45, cos(T * 1.2 + 5.236) * 0.45)
o6.dynColor(0.9, 0.3, 0.7, 0.9)
o6.dynScale(0.8 + sin(T * 3.0 + 5.236) * 0.3)

// ——— ОРБИТАЛЬНЫЕ ТОЧКИ КЛИКАБЕЛЬНЫ — каждая меняет скорость ———
o1.onClick({ setState("hue", getState("hue") + 1.5) })
o2.onClick({ setState("hue", getState("hue") + 1.5) })
o3.onClick({ setState("hue", getState("hue") + 1.5) })
o4.onClick({ setState("hue", getState("hue") + 1.5) })
o5.onClick({ setState("hue", getState("hue") + 1.5) })
o6.onClick({ setState("hue", getState("hue") + 1.5) })

// ——— КУРСОР — следует за мышью ———
cursor = Add(circle(0.025, 32))
cursor.dynPos(MX, MY)
cursor.dynColor(1.0, 1.0, 1.0, 0.7 + CLICK * 0.3)
cursor.dynScale(1.0 + CLICK * 0.5)

// ——— ВСПЫШКА ПРИ КЛИКЕ ———
flash = Add(circle(0.35, 64))
flash.dynPos(MX, MY)
flash.dynColor(
    0.5 + sin(T + getState("hue")) * 0.5,
    0.5 + cos(T + getState("hue")) * 0.5,
    0.8,
    CLICK * 0.25
)

// ——— СЧЁТЧИК КЛИКОВ — текст ———
counter = Add(text("click the orbs", 0.055))
counter.aMove([0.0, -0.82], start: 0, duration: 0)
counter.dynColor(
    0.5 + sin(T + getState("hue")) * 0.5,
    0.5 + sin(T + getState("hue") + 2.094) * 0.5,
    0.5 + sin(T + getState("hue") + 4.189) * 0.5,
    0.6 + sin(T * 2) * 0.2
)

// ——— СТРЕЛКА — всегда смотрит на курсор ———
ptr = Add(arrow(from: [0.0, 0.0], to: [0.1, 0.0]))
ptr.dynColor(1.0, 1.0, 0.4, 0.5)
ptr.dynRot(atan2(-MY, MX) * 57.2958)
ptr.dynScale(0.5 + CLICK * 0.8)

// ——— ВОЛНОВЫЕ КОЛЬЦА от центра ———
w1 = Add(circle(0.28, 64))
w1.aMove([0.0, 0.0], start: 0, duration: 0)
w1.dynColor(0.3, 0.5, 0.8, 0.15 + sin(T * 1.5) * 0.1)
w1.dynScale(1.0 + sin(T * 1.5) * 0.08)

w2 = Add(circle(0.38, 64))
w2.aMove([0.0, 0.0], start: 0, duration: 0)
w2.dynColor(0.2, 0.4, 0.7, 0.1 + sin(T * 1.5 + 1.0) * 0.08)
w2.dynScale(1.0 + sin(T * 1.5 + 1.0) * 0.06)

w3 = Add(circle(0.5, 64))
w3.aMove([0.0, 0.0], start: 0, duration: 0)
w3.dynColor(0.15, 0.3, 0.6, 0.07 + sin(T * 1.5 + 2.0) * 0.05)
w3.dynScale(1.0 + sin(T * 1.5 + 2.0) * 0.04)