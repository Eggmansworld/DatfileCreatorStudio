using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DatfileCreatorStudio.Views.Controls;

/// <summary>
/// A background raster of drifting "copper bars" — a nod to the Amiga
/// demoscene cracktros that scrolled bands of colour behind everything. It
/// sits at the very back of the main window and shows through the gaps between
/// the (opaque) panels, so the chrome stays readable while the margins come
/// alive. Purely an easter egg: only rendered while the Rainbow accent is
/// active, and it quietly freezes after a spell of no user interaction.
/// </summary>
public sealed class CopperBars : Control
{
    // ── Bindable knobs (all driven from the view model / Rainbow Controls) ──

    /// <summary>Master switch. When false nothing is drawn and the clock stops.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<CopperBars, bool>(nameof(IsActive));

    /// <summary>Vertical drift rate, 0 (still) .. 1 (brisk).</summary>
    public static readonly StyledProperty<double> SpeedProperty =
        AvaloniaProperty.Register<CopperBars, double>(nameof(Speed), 0.35);

    /// <summary>Thickness of each bar, in device-independent pixels.</summary>
    public static readonly StyledProperty<double> BarSizeProperty =
        AvaloniaProperty.Register<CopperBars, double>(nameof(BarSize), 28);

    /// <summary>Colour-cycling rate, 0 (frozen hue) .. 1 (fast).</summary>
    public static readonly StyledProperty<double> CycleSpeedProperty =
        AvaloniaProperty.Register<CopperBars, double>(nameof(CycleSpeed), 0.25);

    /// <summary>Amount of organic left-right/up-down sway, 0 .. 1.</summary>
    public static readonly StyledProperty<double> WiggleProperty =
        AvaloniaProperty.Register<CopperBars, double>(nameof(Wiggle), 0.35);

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Speed { get => GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }
    public double BarSize { get => GetValue(BarSizeProperty); set => SetValue(BarSizeProperty, value); }
    public double CycleSpeed { get => GetValue(CycleSpeedProperty); set => SetValue(CycleSpeedProperty, value); }
    public double Wiggle { get => GetValue(WiggleProperty); set => SetValue(WiggleProperty, value); }

    // ── Clock & idle handling ───────────────────────────────────────────────

    /// <summary>Freeze the animation after this many seconds of no interaction.</summary>
    private const double IdleSeconds = 300;

    private readonly DispatcherTimer _timer;
    private double _time;          // accumulated animation seconds
    private long _lastTs;          // Stopwatch ticks at the previous frame
    private long _lastActivityTs;  // Stopwatch ticks at the last user activity

    static CopperBars()
    {
        // A knob change should repaint even while idle-frozen.
        AffectsRender<CopperBars>(
            IsActiveProperty, SpeedProperty, BarSizeProperty, CycleSpeedProperty, WiggleProperty);
    }

    public CopperBars()
    {
        IsHitTestVisible = false; // never intercept clicks meant for the UI
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _lastActivityTs = Stopwatch.GetTimestamp();
    }

    /// <summary>Called by the window on any pointer/keyboard activity to keep the animation awake.</summary>
    public void RegisterActivity() => _lastActivityTs = Stopwatch.GetTimestamp();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastTs = Stopwatch.GetTimestamp();
        _lastActivityTs = _lastTs;
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsActiveProperty)
            UpdateRunning();
    }

    private void UpdateRunning()
    {
        if (IsActive && this.GetVisualRoot() is not null)
        {
            _lastTs = Stopwatch.GetTimestamp();
            if (!_timer.IsEnabled)
                _timer.Start();
        }
        else
        {
            _timer.Stop();
            InvalidateVisual();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTs) / (double)Stopwatch.Frequency;
        _lastTs = now;
        if (dt > 0.2) dt = 0.2; // clamp after the machine was busy/suspended

        // Idle: hold the last frame rather than burning cycles.
        if ((now - _lastActivityTs) / (double)Stopwatch.Frequency > IdleSeconds)
            return;

        _time += dt;
        InvalidateVisual();
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        if (!IsActive)
            return;

        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        double bar = Math.Clamp(BarSize, 8, 200);
        double gap = bar * 1.15;
        double spacing = bar + gap;
        double drift = _time * (Speed * 40.0);         // px/second at Speed = 1 → 40
        double hueBase = _time * (CycleSpeed * 60.0);   // deg/second at CycleSpeed = 1 → 60
        double wiggleAmp = Wiggle * bar * 1.4;

        // Each bar carries a persistent absolute index k that scrolls with the
        // drift, so its hue and sway stay tied to *it* — no colour snap when the
        // pattern wraps. We only draw the handful of k's currently in view.
        int kFirst = (int)Math.Floor(drift / spacing) - 2;
        int kLast = (int)Math.Floor((drift + h) / spacing) + 2;

        for (int k = kFirst; k <= kLast; k++)
        {
            double centerY = k * spacing - drift + spacing * 0.5;
            centerY += wiggleAmp * Math.Sin(_time * 0.9 + k * 0.7); // organic sway
            if (centerY + bar < 0 || centerY - bar > h)
                continue;

            double hue = Mod360(hueBase + k * 26.0);
            DrawBand(ctx, w, centerY, bar, hue);
        }
    }

    /// <summary>One copper bar: horizontal strips forming a dark→bright→dark metallic sheen.</summary>
    private static void DrawBand(DrawingContext ctx, double w, double centerY, double bar, double hue)
    {
        double top = centerY - bar / 2.0;
        int strips = Math.Max(4, (int)(bar / 3.0));
        double sh = bar / strips;
        for (int s = 0; s < strips; s++)
        {
            double u = (s + 0.5) / strips * 2.0 - 1.0; // -1 .. 1 across the bar
            double bright = 1.0 - u * u;                 // parabolic — brightest at centre
            var brush = new ImmutableSolidColorBrush(Copper(hue, bright));
            ctx.DrawRectangle(brush, null, new Rect(0, top + s * sh, w, sh + 0.75));
        }
    }

    /// <summary>Copper tube colour: saturated hue, brighter toward the centre with a white-hot glint.</summary>
    private static Color Copper(double hue, double bright)
    {
        var (r, g, b) = HsvToRgb(hue, 0.82, 0.30 + 0.60 * bright);
        double glint = Math.Pow(bright, 5) * 0.85; // narrow specular highlight
        byte Mix(double c) => (byte)Math.Clamp(c + (255 - c) * glint, 0, 255);
        return Color.FromArgb(255, Mix(r), Mix(g), Mix(b));
    }

    private static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        h = Mod360(h) / 60.0;
        int i = (int)Math.Floor(h) % 6;
        double f = h - Math.Floor(h);
        double p = v * (1 - s);
        double q = v * (1 - s * f);
        double t = v * (1 - s * (1 - f));
        (double r, double g, double b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return (r * 255, g * 255, b * 255);
    }

    private static double Mod360(double x) => ((x % 360) + 360) % 360;
}
