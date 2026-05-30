using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Insomnia.Services;

public sealed unsafe class ShutdownTrackerService : IShutdownTrackerService
{
    private const int ErrorClassAlreadyExists = 1410;
    private const uint WmClose = 0x0010;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmDestroy = 0x0002;

    private readonly Lock _syncRoot = new();
    private readonly string _className = $"InsomniaShutdownTracker-{Environment.ProcessId}";
    private readonly WNDPROC _wndProc;
    private ManualResetEventSlim? _started;
    private ManualResetEventSlim? _stopped;
    private Thread? _messageThread;
    private HWND _windowHandle;
    private Exception? _startupException;
    private bool _isRunning;
    private bool _isDisposed;

    public ShutdownTrackerService()
    {
        _wndProc = WndProc;
    }

    public void Start()
    {
        ManualResetEventSlim started;
        
        using (_syncRoot.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isRunning)
                return;

            _windowHandle = HWND.Null;
            _startupException = null;
            _started = new ManualResetEventSlim(false);
            _stopped = new ManualResetEventSlim(false);
            started = _started;

            _messageThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "Insomnia shutdown tracker"
            };

            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
        }

        started.Wait();

        if (_startupException is not null)
        {
            var exception = _startupException;
            Stop();
            throw new InvalidOperationException("Failed to start shutdown tracker.", exception);
        }

        using (_syncRoot.EnterScope())
        {
            _isRunning = true;
        }
    }

    public void Stop()
    {
        ManualResetEventSlim? stopped;
        HWND windowHandle;

        using (_syncRoot.EnterScope())
        {
            if (!_isRunning && _messageThread is null)
                return;

            stopped = _stopped;
            windowHandle = _windowHandle;
        }

        if (windowHandle != HWND.Null)
            PInvoke.PostMessage(windowHandle, WmClose, new WPARAM(0), new LPARAM(0));

        stopped?.Wait(TimeSpan.FromSeconds(3));

        using (_syncRoot.EnterScope())
        {
            _isRunning = false;
            _messageThread = null;
            _windowHandle = HWND.Null;
            _startupException = null;

            _started?.Dispose();
            _stopped?.Dispose();
            _started = null;
            _stopped = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _isDisposed = true;
    }

    private void RunMessageLoop()
    {
        try
        {
            fixed (char* blockReason = "Insomnia is keeping the computer awake and blocked this shutdown request.")
            {
                RegisterWindowClass();
                _windowHandle = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                    _className,
                    "Insomnia shutdown tracker",
                    WINDOW_STYLE.WS_OVERLAPPED,
                    0,
                    0,
                    0,
                    0,
                    HWND.Null,
                    null,
                    PInvoke.GetModuleHandle((string?)null),
                    null);

                if (_windowHandle == HWND.Null)
                    throw new InvalidOperationException($"CreateWindowEx failed. Win32 error: {Marshal.GetLastWin32Error()}.");

                var reasonCreated = PInvoke.ShutdownBlockReasonCreate(_windowHandle, blockReason);
                if (!reasonCreated)
                    throw new InvalidOperationException($"ShutdownBlockReasonCreate failed. Win32 error: {Marshal.GetLastWin32Error()}.");

                SignalStarted();

                while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0) > 0)
                {
                    PInvoke.TranslateMessage(message);
                    PInvoke.DispatchMessage(message);
                }
            }
        }
        catch (Exception exception)
        {
            _startupException = exception;
            SignalStarted();
        }
        finally
        {
            SignalStopped();
        }
    }

    private void SignalStarted()
    {
        using (_syncRoot.EnterScope())
        {
            _started?.Set();
        }
    }

    private void SignalStopped()
    {
        using (_syncRoot.EnterScope())
        {
            _stopped?.Set();
        }
    }

    private void RegisterWindowClass()
    {
        fixed (char* className = _className)
        {
            var windowClass = new WNDCLASSW
            {
                lpfnWndProc = _wndProc,
                hInstance = GetCurrentModuleHandle(),
                lpszClassName = className
            };

            var atom = PInvoke.RegisterClass(windowClass);
            var error = Marshal.GetLastWin32Error();
            if (atom == 0 && error != ErrorClassAlreadyExists)
                throw new InvalidOperationException($"RegisterClass failed. Win32 error: {error}.");
        }
    }

    private static HINSTANCE GetCurrentModuleHandle()
    {
        return new HINSTANCE(PInvoke.GetModuleHandle(null).DangerousGetHandle());
    }

    private LRESULT WndProc(HWND hWnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WmQueryEndSession:
                return new LRESULT(0);

            case WmClose:
                PInvoke.ShutdownBlockReasonDestroy(hWnd);
                PInvoke.DestroyWindow(hWnd);
                return new LRESULT(0);

            case WmDestroy:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);

            default:
                return PInvoke.DefWindowProc(hWnd, message, wParam, lParam);
        }
    }
}
