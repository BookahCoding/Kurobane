namespace Kurobane.Core;

/// <summary>Everything needed to reach one ZeroClaw gateway. The token lives only in the secret store.</summary>
public sealed class KurobaneConnection
{
    public string Name { get; set; } = "My ZeroClaw";
    public string BaseUrl { get; set; } = "";
    public string AgentAlias { get; set; } = "default";
    public string DeviceName { get; set; } = "Kurobane on Windows";
    /// <summary>Secret-store key holding the bearer token, e.g. "server:abc123:token".</summary>
    public string TokenKey { get; set; } = "";

    public Uri BaseUri => string.IsNullOrWhiteSpace(BaseUrl)
        ? throw new InvalidOperationException("Gateway address is empty — set it in Settings.")
        : ZeroClawClient.NormalizeBaseUri(new Uri(BaseUrl.TrimEnd('/') + "/"));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(TokenKey);

    public ZeroClawClient CreateClient(ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        var client = new ZeroClawClient(BaseUri, handler);
        client.SetToken(secrets.Get(TokenKey));
        return client;
    }

    public async Task<string> PairAsync(ISecretStore secrets, string pairingCode, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var client = new ZeroClawClient(BaseUri, handler);
        var token = await client.PairAsync(pairingCode, DeviceName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(TokenKey))
            TokenKey = $"server:{Guid.NewGuid():N}:token";
        secrets.Set(TokenKey, token);
        return token;
    }
}
