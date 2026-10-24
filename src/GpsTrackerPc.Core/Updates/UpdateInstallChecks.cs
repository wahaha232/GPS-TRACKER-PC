using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GpsTrackerPc.Core.Updates;

/// <summary>更新前的本機檢查：磁碟空間與 EXE 數位簽章。</summary>
public static class UpdateInstallChecks
{
    public static bool HasFreeSpace(string directoryPath, long requiredBytes, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(directoryPath));
            while (!directory.Exists && directory.Parent != null)
            {
                directory = directory.Parent;
            }

            var root = Path.GetPathRoot(directory.FullName);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            availableBytes = drive.AvailableFreeSpace;
            return availableBytes >= requiredBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

public enum UpdateSignatureStatus
{
    Unsigned,
    Valid,
    Invalid,
}

public sealed record UpdateSignatureResult(UpdateSignatureStatus Status, string? Subject, string? Error)
{
    public static UpdateSignatureResult Unsigned { get; } =
        new(UpdateSignatureStatus.Unsigned, null, "檔案沒有數位簽章");
}

/// <summary>
/// 讀取 Authenticode 簽章。專案目前沒有簽章憑證，所以未簽章只警告、不阻擋；
/// 有簽章但鏈結驗證失敗則視為不合法。
/// </summary>
public static class UpdateSignature
{
    public static UpdateSignatureResult Inspect(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057 // 讀取已簽章 PE 的 Authenticode 憑證目前只有這個 API。
            using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var certificate2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                },
            };

            var valid = chain.Build(certificate2);
            var subject = certificate2.Subject;
            return valid
                ? new UpdateSignatureResult(UpdateSignatureStatus.Valid, subject, null)
                : new UpdateSignatureResult(
                    UpdateSignatureStatus.Invalid,
                    subject,
                    "數位簽章鏈結驗證失敗");
        }
        catch (CryptographicException)
        {
            return UpdateSignatureResult.Unsigned;
        }
        catch (Exception ex)
        {
            return new UpdateSignatureResult(UpdateSignatureStatus.Invalid, null, ex.Message);
        }
    }
}
