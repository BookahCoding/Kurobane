namespace Kurobane.Core;

/// <summary>Named secret storage. Implementations must keep values local to the current user/machine.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Remove(string name);
}

/// <summary>Plain in-memory store (used by tests and by --demo).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public void Set(string name, string value) => _values[name] = value;
    public void Remove(string name) => _values.Remove(name);
}
