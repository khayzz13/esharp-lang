using Esharp.Generated;

var client = new Client
{
    state = ConnectionState.disconnected(),
    name = "test-client"
};

Console.WriteLine($"initial:   {client.describe()}");

client.startConnect();
Console.WriteLine($"after startConnect: {client.describe()}");

client.markConnected();
Console.WriteLine($"after markConnected: {client.describe()}");

client.markFailed("timeout");
Console.WriteLine($"after markFailed: {client.describe()}");

Console.WriteLine(client.greet());
