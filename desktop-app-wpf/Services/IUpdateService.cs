using PdfStampNgrokDesktop.Core;
using PdfStampNgrokDesktop.Models;

namespace PdfStampNgrokDesktop.Services;

public interface IUpdateService
{
    Task<Result<UpdateManifest?>> CheckForUpdatesAsync(UpdateConfig updateConfig, string currentVersion, CancellationToken cancellationToken = default);
}
