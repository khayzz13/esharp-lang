using System.Reflection;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: run_dll <path.dll> <Type> <method>");
    return 1;
}
try
{
    var asm = Assembly.LoadFrom(args[0]);
    var type = asm.GetType(args[1]) ?? throw new Exception($"type {args[1]} not found");
    var method = type.GetMethod(args[2], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new Exception($"method {args[2]} not found");
    var callArgs = method.GetParameters().Zip(args.Skip(3), (p, a) =>
    {
        object converted = p.ParameterType switch
        {
            { } t when t == typeof(int) => int.Parse(a),
            { } t when t == typeof(long) => long.Parse(a),
            { } t when t == typeof(double) => double.Parse(a),
            { } t when t == typeof(bool) => bool.Parse(a),
            _ => a,
        };
        return converted;
    }).ToArray();
    var result = method.Invoke(null, callArgs.Length == 0 ? null : callArgs);
    if (result is null) { Console.WriteLine("(void)"); return 0; }
    var t = result.GetType();
    if (result is System.Threading.Tasks.Task task) { task.GetAwaiter().GetResult(); Console.WriteLine("(task void)"); return 0; }
    if (result is System.Threading.Tasks.ValueTask vt) { vt.GetAwaiter().GetResult(); Console.WriteLine("(valuetask void)"); return 0; }
    if (t.IsGenericType)
    {
        var def = t.GetGenericTypeDefinition();
        if (def == typeof(System.Threading.Tasks.Task<>) || def == typeof(System.Threading.Tasks.ValueTask<>))
        {
            var awaiter = t.GetMethod("GetAwaiter")!.Invoke(result, null)!;
            var v = awaiter.GetType().GetMethod("GetResult")!.Invoke(awaiter, null);
            Console.WriteLine(v);
            return 0;
        }
    }
    Console.WriteLine(result);
    return 0;
}
catch (Exception e)
{
    while (e is TargetInvocationException tie && tie.InnerException is not null) e = tie.InnerException;
    Console.Error.WriteLine($"{e.GetType().FullName}: {e.Message}");
    Console.Error.WriteLine(e.StackTrace);
    return 1;
}
