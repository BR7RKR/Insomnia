namespace Insomnia.Services;

public interface ITracker : IDisposable
{
    public void Start();
    public void Stop();
}