name = "ExtendedGpuSceneExample"

bgColor([0.05, 0.03, 0.08], duration: 3)
bgColor([0.08, 0.04, 0.12], duration: 3)

Add(
    plot(
        func: x => sin(x * 4) * 0.25,
        xmin: -1,
        xmax: 1
        dynamic: "true"
    )
).aColor(
    to: [0.6, 0.8, 1, 1],
    duration: 1,
    ease: "inout"
)

Add(
    plot(
        func: x => sin(x * 7 + T * 2) * 0.15,
        xmin: -1,
        xmax: 1,
        dynamic: "true"
    )
).aColor(
    to: [0.3, 1, 0.7, 1],
    duration: 1,
    ease: "inout"
)

Add(
    rect(
        width: 0.5,
        height: 0.5,
        isDynamic: "true"
    )
).aColor(to: [1, 0, 0, 1], duration: 5)


Add(
    plot(
        func: x =>
            pow(abs(x), 2/3)
            + 0.8 * sqrt(max(0, 1 - x*x))
            * sin(18 * PI * x + T * 1.5),
        xmin: -1,
        xmax: 1,
        dynamic: "true"
    )
).aColor(
    to: [1, 0.2, 0.4, 1],
    duration: 1,
    ease: "inout"
)

Add(
    plot(
        func: x =>
            sin(x * 12 + T * 3) * 0.12
            + cos(x * 25 + T * 4) * 0.05,
        xmin: -1,
        xmax: 1,
        dynamic: "true"
    )
).aColor(
    to: [0.2, 0.6, 1, 1],
    duration: 1
)

bgColor([0.00, 0.00, 0.00], duration: 5)
