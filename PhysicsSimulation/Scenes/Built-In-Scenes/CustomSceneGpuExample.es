name = "EpicTimedShowcase"

r = Add(rect(0.6, 0.4, "true"))

// ——— ЦВЕТ ———
// Плавное появление из прозрачного
r.aColor(to: [1, 0.3, 0.5, 1], start: 0.5, duration: 2,   ease: "out")

// Внезапный флэш в бирюзовый
r.aColor(to: [0.2, 1, 1, 1],   start: 3.5, duration: 0.8, ease: "inout")

// Медленный переход в жёлтый
r.aColor(to: [1, 1, 0.3, 1],   start: 5,   duration: 4,   ease: "in")

// Финальный мягкий блик
r.aColor(to: [1, 0.8, 1, 1],   start: 10,  duration: 3,   ease: "out")


// ——— МАСШТАБ (пульсация + взрыв) ———
r.aScale(1.6, start: 1,    duration: 1.2, ease: "out")
r.aScale(0.8, start: 2.8,  duration: 0.6, ease: "inout")
r.aScale(2.2, start: 4,    duration: 1.5, ease: "out")
r.aScale(1.0, start: 6.5,  duration: 2,   ease: "inout")
r.aScale(1.4, start: 9,    duration: 1,   ease: "out")
r.aScale(1.0, start: 11,   duration: 2,   ease: "inout")


// ——— ДВИЖЕНИЕ ПО КРУГУ + ВОЛНА ———
r.aMove([ 0.6,  0.6], start: 0,   duration: 4,  ease: "linear")
r.aMove([ 0.6, -0.6], start: 4,   duration: 4,  ease: "linear")
r.aMove([-0.6, -0.6], start: 8,   duration: 4,  ease: "linear")
r.aMove([-0.6,  0.6], start: 12,  duration: 4,  ease: "linear")
r.aMove([ 0.0,  0.0], start: 16,  duration: 3,  ease: "inout")


// ——— ВРАЩЕНИЕ ———
r.aRot(0,      start: 0,    duration: 0) 
r.aRot(720,    start: 1,    duration: 6,   ease: "linear")
r.aRot(360,     start: 8,    duration: 4,   ease: "out") 
r.aRot(1080,   start: 13,   duration: 5,   ease: "linear")


// ——— БОНУС: лёгкое "дыхание" в конце ———
r.aScale(1.3, start: 18, duration: 1.5, ease: "out")
r.aScale(1.0, start: 20, duration: 2,   ease: "inout")
r.aColor(to: [1, 1, 1, 1], start: 21, duration: 3, ease: "out")