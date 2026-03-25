module Worker

func sumTo(limit: int) -> int {
    var i = 0
    var total = 0

    while i <= limit {
        total = total + i
        i = i + 1
    }

    return total
}

func sumAll(values: List<int>) -> int {
    var total = 0

    for value in values {
        total = total + value
    }

    return total
}

func start(values: List<string>) -> Job {
    return spawn {
        for value in values {
            Console.WriteLine(value)
        }
    }
}
