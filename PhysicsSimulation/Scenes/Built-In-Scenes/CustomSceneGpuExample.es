name = "CustomSceneGpuExample"

Add(plot(
    func: x => sin(x * 5) * 0.3,
    xmin: -1,
    xmax: 1
)).aColor(to: [0, 1, 1, 1], from: [1,1,1,1], duration: 1, ease: inOut)

Add(plot(
    func: x => x*x*T - 0.5,
    xmin: -0.5,
    xmax: 0.5,
    dynamic: true
)).aColor(to: [1, 0.7, 0, 1], duration: 1, ease: inOut)

Add(plot(
    func: x => pow(abs(x), 2/3) + 0.9*sqrt(max(0, 1-x*x))*sin(20*PI*x + T*2),
    xmin: -1,
    xmax: 1,
    dynamic: true
)).aColor(to: [1, 0.1, 0.3, 1])

Add(plot(
    func: x => sin(x*10 + T*4)*0.22 + cos(x*23 + T*2.7)*0.06,
    xmin: -1,
    xmax: 1,
    dynamic: true
)).aColor(to: [0.2, 1, 0.6, 1])

bgColor([0.08, 0.05, 0.15], duration: 4)
bgColor([0.15, 0.05, 0.08], duration: 4)
