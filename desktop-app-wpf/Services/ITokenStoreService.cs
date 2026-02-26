using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Models;

namespace PdfStampNgrokDesktop.Services;

public interface ITokenStoreService
{
    string ConfigPath { get; }

    string BackupDirectoryPath { get; }

    Task<Result<AppConfig>> LoadAsync(CancellationToken cancellationToken = default);

    Task<Result> SaveAsync(AppConfig config, CancellationToken cancellationToken = default);

    Task<Result<AppConfig>> RestoreLatestBackupAsync(CancellationToken cancellationToken = default);

    Result<string> ProtectToken(string plainToken);

    Result<string> UnprotectToken(string encryptedToken);
}
