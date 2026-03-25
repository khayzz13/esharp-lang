using Esharp.Generated;

var alice = new User { name = "Alice", age = 25 };
Console.WriteLine(alice.greet());
Console.WriteLine($"Adult: {alice.isAdult()}");

var pair = Ops.makePair(1, "hello");
Console.WriteLine($"Pair: ({pair.first}, {pair.second})");

var opt = Ops.wrapSome(42);
Console.WriteLine($"Option tag: {opt.Tag}, value: {opt._some_value}");
