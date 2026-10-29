using System.IO;
using System.Security.Cryptography;
using System.Text;
using Kurobane.Core;

namespace Kurobane.App;

/// <summary>
/// Secrets are encrypted with Windows DPAPI (current-user scope) before touching disk, in files
/// under %APPDATA%\Kurobane\secrets. Never stored in settings JSON or source.
/// </summary>
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private string PathFor(string name) => Path.Combine(directory, Convert.ToHexString(Encoding.UTF8.GetBytes(name)) + ".sec");

    public string? Get(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public void Set(string name, string value)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(PathFor(name), ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public void Remove(string name)
    {
        var p = PathFor(name);
        if (File.Exists(p)) File.Delete(p);
    }
}
