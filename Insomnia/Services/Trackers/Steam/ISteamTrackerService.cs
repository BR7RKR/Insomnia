using ObservableCollections;
using R3;

namespace Insomnia.Services;

public interface ISteamTrackerService : ITracker
{
    public bool HasActiveInstallOrUpdate { get; }
    public Observable<bool> HasActiveInstallOrUpdateChanged { get; }
    public IReadOnlyObservableList<SteamInstallOrUpdateInfo> ActiveInstallOrUpdates { get; }
    public Observable<IReadOnlyList<SteamInstallOrUpdateInfo>> ActiveInstallOrUpdatesChanged { get; }
}
