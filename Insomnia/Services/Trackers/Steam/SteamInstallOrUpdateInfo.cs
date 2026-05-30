namespace Insomnia.Services;

public sealed record SteamInstallOrUpdateInfo(
    uint AppId,
    string? Name,
    string LibraryPath,
    long BytesDownloaded,
    long BytesToDownload,
    long BytesStaged,
    long BytesToStage,
    uint StateFlags);
