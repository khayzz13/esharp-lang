using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class EmitterStressTests
{
    static int _asmCounter;

    static void AssertEmits(string source)
    {
        var asmName = $"StressTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        EsHarness.EmitBoundToFile(binder, bound, asmName, path);
    }

    [Fact]
    public void PositionalDataWithListLiterals()
    {
        AssertEmits("""
namespace T

struct Config(name: string, url: string, category: string)

func defaults() -> List<Config> {
    return [
        Config("A", "http://a.com", "cat1"),
        Config("B", "http://b.com", "cat2"),
    ]
}
""");
    }

    [Fact]
    public void TryCatchWithTypedBinding()
    {
        AssertEmits("""
namespace T

func run() -> string {
    try {
        let x = 1 / 0
        return "ok"
    } catch (ex: Exception) {
        return ex.Message
    }
}
""");
    }

    [Fact]
    public void AsyncWithTupleReturnAndForDestructuring()
    {
        AssertEmits("""
namespace T

func work() -> (List<string>, List<string>) {
    var items = List<string>()
    var errors = List<string>()
    return (items, errors)
}

func run() {
    var tasks = List<Task<(List<string>, List<string>)>>()
    tasks.Add(Task.Run(func() -> (List<string>, List<string>) { return work() }))
    let results = await Task.WhenAll(tasks.ToArray())
    for (items, errors) in results {
        let n = items.Count + errors.Count
    }
}
""");
    }

    [Fact]
    public void RefDataWithAsyncMethodAndTryCatch()
    {
        AssertEmits("""
namespace T

struct Item(title: string, source: string)

func fetchItems(client: HttpClient, url: string) -> (List<Item>, List<string>) {
    var items = List<Item>()
    var errors = List<string>()
    try {
        let stream = await client.GetStreamAsync(url)
        items.Add(Item("title", "src"))
    } catch (ex: Exception) {
        errors.Add(ex.Message)
    }
    return (items, errors)
}
""");
    }

    [Fact]
    public void RefDataWithMultipleMethodsAndExpressionBodies()
    {
        AssertEmits("""
namespace T

struct Config(name: string, url: string)
struct Item(title: string, source: string)
struct Status(name: string, itemCount: int)

class Svc {
    configs: List<Config>
    items: List<Item>
    statuses: List<Status>
    client: HttpClient
    maxItems: int

    init() {
        self.configs = List<Config>()
        self.items = List<Item>()
        self.statuses = List<Status>()
        self.client = HttpClient()
        self.maxItems = 500
    }

    func poll() {
        var tasks = List<Task<(List<Item>, List<string>)>>()
        for cfg in self.configs {
            let c = self.client
            let f = cfg
            tasks.Add(Task.Run(func() -> (List<Item>, List<string>) { return (List<Item>(), List<string>()) }))
        }
        let results = await Task.WhenAll(tasks.ToArray())
        var all = List<Item>()
        var statuses = List<Status>()
        var i = 0
        for (items, errors) in results {
            let cfg = self.configs[i]
            i += 1
            let err = errors.Count > 0 ? errors[0] : ""
            statuses.Add(Status(cfg.name, items.Count))
            all.AddRange(items)
        }
        if all.Count > self.maxItems {
            all.RemoveRange(self.maxItems, all.Count - self.maxItems)
        }
        self.items = all
        self.statuses = statuses
    }

    func count() -> int = self.items.Count
    func addConfig(name: string, url: string) = self.configs.Add(Config(name, url))
}
""");
    }

    [Fact]
    public void InterfaceImplWithLambdaArgs()
    {
        AssertEmits("""
namespace T

class Plugin {
    name: string
    init() { self.name = "test" }
    pub func Name() -> string = self.name
    pub func Run(app: WebApplication) {
        app.MapGet("/api/test", func(q: string) -> IResult {
            return Results.Json(q ?? "default")
        })
        app.MapGet("/api/ping", func() -> IResult = Results.Json("pong"))
    }
}
""");
    }

    [Fact]
    public void Parse_KeywordAsVariableName_ReportsError()
    {
        var parser = new Parser("""
namespace T
func run() -> int {
    let pub = 42
    return pub
}
""", "test.es");
        parser.ParseCompilationUnit();
        Assert.Contains(parser.Diagnostics, d => d.Message.Contains("keyword"));
    }

    [Fact]
    public void Parse_ConstructorCallWith6ArgsInForLoop()
    {
        // Reproduces infinite recursion: ParseExpression → ParseArgumentList → ParsePostfixExpression
        var parser = new Parser("""
namespace T

struct Item(a: string, b: string, c: string, d: DateTimeOffset, e: string, f: string)

func run(synd: object, feed: object) {
    var items = List<Item>()
    for entry in synd.Items {
        let link = entry.Links.Count > 0 ? entry.Links[0].Uri.ToString() : ""
        let summary = entry.Summary?.Text ?? ""
        let pub = entry.PublishDate != DateTimeOffset.MinValue ? entry.PublishDate : entry.LastUpdatedTime
        items.Add(Item(entry.Title.Text, link, feed.name, pub, summary, feed.category))
    }
}
""", "test.es");
        parser.ParseCompilationUnit();
    }

    [Fact]
    public void NullConditionalChainWithCoalesce()
    {
        AssertEmits("""
namespace T
func safe(s: string) -> int {
    return s?.Length ?? 0
}
func ternaryChain(x: int) -> string {
    let label = x > 100 ? "high" : x > 50 ? "mid" : "low"
    return label
}
""");
    }
}
