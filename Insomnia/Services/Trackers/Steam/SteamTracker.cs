using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ObservableCollections;
using R3;

namespace Insomnia.Services;

public sealed partial class SteamTrackerService : ISteamTrackerService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _syncRoot = new();
    private readonly TimeProvider _timeProvider;
    private readonly ObservableList<SteamInstallOrUpdateInfo> _activeInstallOrUpdates = [];
    private readonly Subject<bool> _hasActiveInstallOrUpdateChanged = new();
    private readonly Subject<IReadOnlyList<SteamInstallOrUpdateInfo>> _activeInstallOrUpdatesChanged = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;
    private bool _hasActiveInstallOrUpdate;
    private bool _isRunning;
    private bool _isDisposed;

    public SteamTrackerService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool HasActiveInstallOrUpdate
    {
        get
        {
            using (_syncRoot.EnterScope())
            {
                return _hasActiveInstallOrUpdate;
            }
        }
    }

    public Observable<bool> HasActiveInstallOrUpdateChanged => _hasActiveInstallOrUpdateChanged;

    public IReadOnlyObservableList<SteamInstallOrUpdateInfo> ActiveInstallOrUpdates => _activeInstallOrUpdates;

    public Observable<IReadOnlyList<SteamInstallOrUpdateInfo>> ActiveInstallOrUpdatesChanged => _activeInstallOrUpdatesChanged;

    public void Start()
    {
        using (_syncRoot.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isRunning)
                return;

            _cancellationTokenSource = new CancellationTokenSource();
            _monitoringTask = MonitorSteamAsync(_cancellationTokenSource.Token);
            _isRunning = true;
        }
    }

    public void Stop()
    {
        Task? monitoringTask;
        CancellationTokenSource? cancellationTokenSource;

        using (_syncRoot.EnterScope())
        {
            if (!_isRunning)
                return;

            monitoringTask = _monitoringTask;
            cancellationTokenSource = _cancellationTokenSource;
            _monitoringTask = null;
            _cancellationTokenSource = null;
            _isRunning = false;
        }

        cancellationTokenSource?.Cancel();

        try
        {
            monitoringTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellationTokenSource?.Dispose();
        }

        SetActiveInstallOrUpdates([]);
    }

    public void Dispose()
    {
        Stop();

        _hasActiveInstallOrUpdateChanged.Dispose();
        _activeInstallOrUpdatesChanged.Dispose();
        _isDisposed = true;
    }

    private async Task MonitorSteamAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);

        using var timer = new PeriodicTimer(DefaultPollInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await RefreshAsync(cancellationToken);
    }

    private Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetActiveInstallOrUpdates(FindActiveInstallOrUpdates());

        return Task.CompletedTask;
    }

    private void SetActiveInstallOrUpdates(IReadOnlyList<SteamInstallOrUpdateInfo> activeInstallOrUpdates)
    {
        bool shouldPublishCollection;
        bool shouldPublishHasActive;
        bool hasActiveInstallOrUpdate;

        using (_syncRoot.EnterScope())
        {
            shouldPublishCollection = !AreSame(_activeInstallOrUpdates, activeInstallOrUpdates);
            if (!shouldPublishCollection)
                return;

            _activeInstallOrUpdates.Clear();
            foreach (var item in activeInstallOrUpdates)
                _activeInstallOrUpdates.Add(item);

            hasActiveInstallOrUpdate = _activeInstallOrUpdates.Count > 0;
            shouldPublishHasActive = _hasActiveInstallOrUpdate != hasActiveInstallOrUpdate;
            _hasActiveInstallOrUpdate = hasActiveInstallOrUpdate;
        }

        var snapshot = activeInstallOrUpdates.ToArray();
        _activeInstallOrUpdatesChanged.OnNext(snapshot);

        if (shouldPublishHasActive)
            _hasActiveInstallOrUpdateChanged.OnNext(hasActiveInstallOrUpdate);
    }

    private static IReadOnlyList<SteamInstallOrUpdateInfo> FindActiveInstallOrUpdates()
    {
        var steamPath = TryGetSteamPath();
        if (steamPath is null)
            return [];

        var activeInstallOrUpdates = new List<SteamInstallOrUpdateInfo>();
        foreach (var libraryPath in GetLibraryPaths(steamPath))
        {
            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamAppsPath))
                continue;

            foreach (var manifestPath in SafeEnumerateFiles(steamAppsPath, "appmanifest_*.acf"))
            {
                var info = TryReadActiveInstallOrUpdate(libraryPath, steamAppsPath, manifestPath);
                if (info is not null)
                    activeInstallOrUpdates.Add(info);
            }
        }

        return activeInstallOrUpdates;
    }

    private static SteamInstallOrUpdateInfo? TryReadActiveInstallOrUpdate(
        string libraryPath,
        string steamAppsPath,
        string manifestPath)
    {
        var values = ReadKeyValues(manifestPath);
        if (!TryGetAppId(manifestPath, values, out var appId))
            return null;

        var name = values.GetValueOrDefault("name");
        var stateFlags = GetUInt64(values, "StateFlags");
        var bytesDownloaded = GetInt64(values, "BytesDownloaded");
        var bytesToDownload = GetInt64(values, "BytesToDownload");
        var bytesStaged = GetInt64(values, "BytesStaged");
        var bytesToStage = GetInt64(values, "BytesToStage");
        var hasDownloadProgress = bytesToDownload > 0 && bytesDownloaded < bytesToDownload;
        var hasStagingProgress = bytesToStage > 0 && bytesStaged < bytesToStage;
        var hasDownloadingDirectory = DirectoryHasContent(Path.Combine(steamAppsPath, "downloading", appId.ToString()));

        if (!hasDownloadProgress && !hasStagingProgress && !hasDownloadingDirectory)
            return null;

        return new SteamInstallOrUpdateInfo(
            appId,
            name,
            libraryPath,
            bytesDownloaded,
            bytesToDownload,
            bytesStaged,
            bytesToStage,
            (uint)stateFlags);
    }

    private static IReadOnlyList<string> GetLibraryPaths(string steamPath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(Path.Combine(steamPath, "steamapps")))
            paths.Add(steamPath);

        var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        foreach (var path in ReadPathValues(libraryFoldersPath))
        {
            if (Directory.Exists(Path.Combine(path, "steamapps")))
                paths.Add(path);
        }

        return paths.ToArray();
    }

    private static string? TryGetSteamPath()
    {
        var registryPath = TryGetSteamPathFromRegistry();
        if (registryPath is not null)
            return registryPath;

        var processPath = TryGetSteamPathFromProcess();
        if (processPath is not null)
            return processPath;

        const string defaultPath = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(defaultPath) ? defaultPath : null;
    }

    private static string? TryGetSteamPathFromRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steamPath = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;

        return Directory.Exists(steamPath) ? steamPath : null;
    }

    private static string? TryGetSteamPathFromProcess()
    {
        try
        {
            using var process = Process.GetProcessesByName("steam").FirstOrDefault();
            var fileName = process?.MainModule?.FileName;

            return fileName is null ? null : Path.GetDirectoryName(fileName);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadKeyValues(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SafeReadLines(path))
        {
            var match = VdfKeyValueRegex().Match(line);
            if (match.Success)
                values[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        return values;
    }

    private static IEnumerable<string> ReadPathValues(string path)
    {
        if (!File.Exists(path))
            yield break;

        foreach (var line in SafeReadLines(path))
        {
            var match = VdfKeyValueRegex().Match(line);
            if (match.Success && string.Equals(match.Groups["key"].Value, "path", StringComparison.OrdinalIgnoreCase))
                yield return match.Groups["value"].Value.Replace(@"\\", @"\");
        }
    }

    private static bool TryGetAppId(
        string manifestPath,
        IReadOnlyDictionary<string, string> values,
        out uint appId)
    {
        if (uint.TryParse(values.GetValueOrDefault("appid"), out appId))
            return true;

        var fileName = Path.GetFileNameWithoutExtension(manifestPath);
        return uint.TryParse(fileName["appmanifest_".Length..], out appId);
    }

    private static bool DirectoryHasContent(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string searchPattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, searchPattern).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try
        {
            return File.ReadLines(path).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long GetInt64(IReadOnlyDictionary<string, string> values, string key)
    {
        return long.TryParse(values.GetValueOrDefault(key), out var value) ? value : 0;
    }

    private static ulong GetUInt64(IReadOnlyDictionary<string, string> values, string key)
    {
        return ulong.TryParse(values.GetValueOrDefault(key), out var value) ? value : 0;
    }

    private static bool AreSame(
        IReadOnlyList<SteamInstallOrUpdateInfo> left,
        IReadOnlyList<SteamInstallOrUpdateInfo> right)
    {
        return left.Count == right.Count &&
               left.OrderBy(static item => item.AppId).SequenceEqual(right.OrderBy(static item => item.AppId));
    }

    [GeneratedRegex("\"(?<key>[^\"]+)\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex VdfKeyValueRegex();
}
