namespace Insomnia.Services;

public interface ISleepTrackerService : ITracker
{
    public bool IsKeepDisplayAwake { get; set; }
}