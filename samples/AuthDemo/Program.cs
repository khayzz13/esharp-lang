using Esharp.Generated;

namespace AuthDemo;

public sealed record UserRecord(Guid id, string email, string passwordHash, bool disabled);

public interface IUserStore
{
    UserRecord? findByEmail(string email);
}

public interface IPasswordHasher
{
    bool verify(string password, string hash);
}

public interface ISessionTokens
{
    string issue(Guid userId);
}

sealed class InMemoryUserStore : IUserStore
{
    readonly Dictionary<string, UserRecord> _users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alice@example.com"] = new UserRecord(Guid.Parse("11111111-1111-1111-1111-111111111111"), "alice@example.com", "secret", disabled: false),
        ["disabled@example.com"] = new UserRecord(Guid.Parse("22222222-2222-2222-2222-222222222222"), "disabled@example.com", "secret", disabled: true)
    };

    public UserRecord? findByEmail(string email) =>
        _users.TryGetValue(email, out var user) ? user : null;
}

sealed class PlaintextHasher : IPasswordHasher
{
    public bool verify(string password, string hash) => password == hash;
}

sealed class SessionTokens : ISessionTokens
{
    public string issue(Guid userId) => $"session-{userId:N}";
}

public static class Program
{
    public static void Main()
    {
        var users = new InMemoryUserStore();
        var hasher = new PlaintextHasher();
        var tokens = new SessionTokens();

        var alice = new LoginRequest
        {
            email = "alice@example.com",
            password = "secret",
            ip = "127.0.0.1"
        };

        var bad = new LoginRequest
        {
            email = "alice@example.com",
            password = "wrong",
            ip = "127.0.0.1"
        };

        var okResult = alice.login(users, hasher, tokens);
        var badResult = bad.login(users, hasher, tokens);

        if (okResult.IsOk)
        {
            Console.WriteLine($"login ok: {okResult.Value.token} / {okResult.Value.userId}");
        }

        if (badResult.IsError)
        {
            Console.WriteLine($"login error: {badResult.ErrorValue.Tag}");
        }
    }
}
