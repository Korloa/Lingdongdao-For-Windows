using System.Security.Cryptography;
using System.Text;

namespace LingDongDao.Perception;

// The file contains only a Windows CurrentUser DPAPI blob, never plaintext.
public static class AgentCredentialStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("LingDongDao.DeepSeek.ApiKey.v1");

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingDongDao", "agent-key.dpapi");

    public static void Save(string key, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("API key is empty", nameof(key));
        string target = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] plain = Encoding.UTF8.GetBytes(key.Trim());
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, target, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string? Load(string? path = null)
    {
        string target = path ?? DefaultPath;
        if (!File.Exists(target)) return null;
        try
        {
            byte[] encrypted = File.ReadAllBytes(target);
            byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException) { return null; }
    }

    public static void Delete(string? path = null)
    {
        string target = path ?? DefaultPath;
        if (File.Exists(target)) File.Delete(target);
    }
}
