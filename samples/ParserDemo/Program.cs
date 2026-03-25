using Esharp.Generated;

var sum = Worker.sumTo(5);
var total = Worker.sumAll(new List<int> { 2, 4, 6, 8 });
var job = Worker.start(new List<string> { "alpha", "beta", "gamma" });
job.Join();

Console.WriteLine($"sumTo(5) = {sum}");
Console.WriteLine($"sumAll = {total}");
