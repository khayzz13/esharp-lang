module Spike

data Vec2 {
    x: int
    y: int
}

func addVecs(a: Vec2, b: Vec2) -> Vec2 {
    return Vec2 { x: a.x + b.x, y: a.y + b.y }
}

func dot(a: Vec2, b: Vec2) -> int {
    return a.x * b.x + a.y * b.y
}

func sumTo(n: int) -> int {
    var total = 0
    var i = 0
    while i <= n {
        total += i
        i += 1
    }
    return total
}

func abs(x: int) -> int {
    if x < 0 {
        return 0 - x
    }
    return x
}
