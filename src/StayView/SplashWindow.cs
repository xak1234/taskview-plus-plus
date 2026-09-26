using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StayView.Core;

namespace StayView;

// Launch splash: "Taskview++" centred on the primary monitor. The whole panel fades in,
// the title holds, then the panel fades out. Window-level alpha so the background fades too.
sealed class SplashWindow : Window
{
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    const uint LWA_ALPHA = 2;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;

    readonly nint handle;
    readonly FrameTimer fadeTimer = new();
    const int FadeInMs = 500, HoldMs = 1400, FadeOutMs = 650;
    long shownAt, fadeOutAt;
    bool fadeOutRequested;
    Action? faded;

    public SplashWindow()
    {
        Title = "Taskview++";
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        Native.DisableDwmBorder(handle);
        // Layered for the alpha fade, tool window (no taskbar button), click-through.
        long ex = Native.GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(handle, GWL_EXSTYLE, (nint)(ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
        SetLayeredWindowAttributes(handle, 0, 0, LWA_ALPHA);
        Activated += (_, _) => Begin();

        var title = new TextBlock
        {
            Text = "Taskview++",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiLight,
            FontSize = 64,
            CharacterSpacing = 40,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 246, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var accent = new Border
        {
            Height = 2, Width = 180, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, 110, 200, 255), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 110, 200, 255), Offset = .5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, 110, 200, 255), Offset = 1 }
                }
            }
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(accent);
        Content = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 16, 22, 38), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 8, 11, 20), Offset = 1 }
                }
            },
            Children = { stack }
        };

        // Centre on the primary monitor's work area.
        var origin = new Native.RECT(0, 0, 1, 1);
        var monitor = Native.MonitorFromRect(ref origin, 1);
        var work = Native.WorkArea(monitor);
        double scale = Native.MonitorScale(monitor);
        int width = (int)Math.Round(560 * scale), height = (int)Math.Round(220 * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height));
        fadeTimer.Tick += (_, _) => FadeTick();
    }

    void Begin()
    {
        if (shownAt == 0) shownAt = Environment.TickCount64;
        if (!fadeTimer.IsEnabled) fadeTimer.Start();
    }

    void SetAlpha(double opacity)
        => SetLayeredWindowAttributes(handle, 0, (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), LWA_ALPHA);

    // Show without taking keyboard focus (used when closing the overview with Esc, so the
    // window the user returns to keeps focus), then fade.
    public void ShowPassiveAndFade()
    {
        AppWindow.Show(false);
        Begin();
        FadeOut();
    }

    // Keep above the overview that opens underneath while loading.
    public void KeepOnTop() => Native.SetWindowPos(handle, -1, 0, 0, 0, 0, 0x13 | 0x10); // TOPMOST, NOMOVE|NOSIZE|NOACTIVATE

    // Fade out after the title has faded in and held. onFaded runs once the banner
    // has faded and closed (used to quit after the exit banner).
    public void FadeOut(Action? onFaded = null)
    {
        faded = onFaded ?? faded;
        fadeOutRequested = true;
        Begin();
        KeepOnTop();
    }

    void FadeTick()
    {
        long now = Environment.TickCount64;
        if (shownAt == 0) shownAt = now;
        long elapsed = now - shownAt;
        KeepOnTop();   // the overview underneath re-asserts topmost while it settles
        if (elapsed < FadeInMs)
        {
            double t = elapsed / (double)FadeInMs;
            SetAlpha(t * t * (3 - 2 * t));
            return;
        }
        if (!fadeOutRequested || now < shownAt + FadeInMs + HoldMs)
        {
            SetAlpha(1);
            return;
        }
        if (fadeOutAt == 0) fadeOutAt = now;
        double outT = Math.Clamp((now - fadeOutAt) / (double)FadeOutMs, 0, 1);
        double eased = outT * outT * (3 - 2 * outT);
        SetAlpha(1 - eased);
        if (outT < 1) return;
        fadeTimer.Stop();
        try { Close(); }
        catch (Exception ex) { Log.Write("Splash close: " + ex.Message); }
        finally { var done = faded; faded = null; done?.Invoke(); }
    }
}
