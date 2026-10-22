using System.Security.Cryptography;
using System.Text;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Security;

/// <summary>可替換的加解密介面；正式程式使用 Windows DPAPI。</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>使用 Windows DPAPI 的 CurrentUser 範圍；只有同一個 Windows 使用者能解密。</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GpsTrackerPc.GitHubToken.v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// GitHub Token 的本機儲存。檔案內容是 DPAPI 加密後的位元組，不是明文；
/// 畫面上只判斷有沒有設定，不會把內容讀出來顯示。
/// </summary>
public sealed class TokenStore
{
    private readonly string _filePath;
    private readonly ISecretProtector _protector;

    public TokenStore(string filePath, ISecretProtector? protector = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Token 檔案路徑不能是空白", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        _protector = protector ?? new DpapiSecretProtector();
    }

    public string FilePath => _filePath;

    /// <summary>檔案存在且不是空的就視為「已設定」；內容是否可解密由 <see cref="TryLoad"/> 判斷。</summary>
    public bool HasToken
    {
        get
        {
            try
            {
                return File.Exists(_filePath) && new FileInfo(_filePath).Length > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token 不能是空白", nameof(token));
        }

        var plaintext = Encoding.UTF8.GetBytes(token.Trim());
        byte[]? ciphertext = null;
        try
        {
            ciphertext = _protector.Protect(plaintext);
            AtomicFile.WriteAllBytes(_filePath, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext != null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
    }

    public string? Load() => TryLoad(out var token) ? token : null;

    public bool TryLoad(out string? token)
    {
        token = null;
        if (!File.Exists(_filePath))
        {
            return false;
        }

        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            ciphertext = File.ReadAllBytes(_filePath);
            plaintext = _protector.Unprotect(ciphertext);
            token = Encoding.UTF8.GetString(plaintext);
            return !string.IsNullOrWhiteSpace(token);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or
                                   UnauthorizedAccessException or NotSupportedException)
        {
            token = null;
            return false;
        }
        finally
        {
            if (plaintext != null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (ciphertext != null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
