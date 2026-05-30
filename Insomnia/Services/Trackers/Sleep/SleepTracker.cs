using Windows.Win32;
using Windows.Win32.System.Power;

namespace Insomnia.Services;

public sealed class SleepTrackerService : ISleepTrackerService
{
    private readonly Lock _syncRoot = new();
    private bool _isRunning;
    private bool _isDisposed;
    
    public bool IsKeepDisplayAwake { get; set; } = false;

    public void Start()
    {
        using (_syncRoot.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isRunning)
                return;

            var state = EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED;

            if (IsKeepDisplayAwake)
                state |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;

            var result = PInvoke.SetThreadExecutionState(state);
            if (result == 0)
                throw new InvalidOperationException("Failed to prevent system sleep.");

            _isRunning = true;
        }
    }
    
    public void Stop()
    {
        using (_syncRoot.EnterScope())
        {
            if (!_isRunning)
                return;

            PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            _isRunning = false;
        }
    }

    public void Dispose()
    {
        Stop();
        _isDisposed = true;
    }
}
