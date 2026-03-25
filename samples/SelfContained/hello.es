module Hello

func main() {
    Console.WriteLine("hello from E#")
    Console.WriteLine("no C# glue code, no Program.cs")

    let sum = sumTo(10)
    Console.WriteLine("computed sum")
}

func sumTo(n: int) -> int {
    var total = 0
    var i = 1
    while i <= n {
        total += i
        i += 1
    }
    return total
}
