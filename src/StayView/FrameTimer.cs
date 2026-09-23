using Microsoft.UI.Xaml.Media;

namespace StayView;

// A DispatcherTimer-shaped clock that ticks once per rendered frame (CompositionTarget.Rendering),
// i.e. in step with the display refresh. A 16 ms DispatcherTimer rides the ~15.6 ms system
// timer and lands frames unevenly (16/31/16 ms), which is visible as judder in animations
// that Windows' own Task View does not have. Only subscribed while running, so an idle
// overview does not force continuous frames.
sealed class FrameTimer
{
    public event EventHandler<object>? Tick;
    public bool IsEnabled { get; private set; }
    public void Start()
    {
        if (IsEnabled) return;
        IsEnabled = true;
        CompositionTarget.Rendering += OnRendering;
    }
    public void Stop()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        CompositionTarget.Rendering -= OnRendering;
    }
    void OnRendering(object? sender, object e) => Tick?.Invoke(this, e);
}
