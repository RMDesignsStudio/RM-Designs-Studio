using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Studio.Ui;

/// <summary>
/// Minimise / maximise (restore) / close for a <see cref="StudioChrome"/> window, drawn in the RM Designs style: white
/// line glyphs on the brand band, a light highlight on hover and the Windows red for close. Put it at the right end of
/// the band; it stretches to the band's height. Close goes through the normal window close, so an app's own
/// "are you sure" checks still run.
/// </summary>
public sealed class CaptionButtons : StackPanel
{
    public CaptionButton MinimiseButton { get; }
    public CaptionButton MaximiseButton { get; }
    public CaptionButton CloseButton { get; }

    static readonly Brush Hover = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), Down = Frozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    static readonly Brush CloseHover = Frozen(Color.FromRgb(0xE8, 0x11, 0x23)), CloseDown = Frozen(Color.FromRgb(0xC5, 0x0F, 0x1F));

    // 10 x 10 line glyphs, as Windows draws its own
    const string MinGlyph = "M0,5.5 H10", MaxGlyph = "M0.5,0.5 H9.5 V9.5 H0.5 Z",
                 RestoreGlyph = "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z", CloseGlyph = "M0,0 L10,10 M10,0 L0,10";

    Window? _window;

    public CaptionButtons()
    {
        Orientation = Orientation.Horizontal;
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
        MinimiseButton = Make("StudioMinimise", "Minimise", MinGlyph, Hover, Down, () => { if (_window is not null) SystemCommands.MinimizeWindow(_window); });
        MaximiseButton = Make("StudioMaximise", "Maximise", MaxGlyph, Hover, Down, ToggleMaximise);
        CloseButton = Make("StudioClose", "Close", CloseGlyph, CloseHover, CloseDown, () => { if (_window is not null) SystemCommands.CloseWindow(_window); });
        Loaded += (_, _) =>
        {
            if (_window is not null) return;
            _window = Window.GetWindow(this);
            if (_window is null) return;
            StudioChrome.Register(_window, this);
            _window.StateChanged += (_, _) => ShowState();
            ShowState();
        };
    }

    /// <summary>Maximise, or restore a maximised window.</summary>
    public void ToggleMaximise()
    {
        if (_window is null) return;
        if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
        else SystemCommands.MaximizeWindow(_window);
    }

    void ShowState()
    {
        if (_window is null) return;
        bool max = _window.WindowState == WindowState.Maximized;
        ((Path)MaximiseButton.Content).Data = Geometry.Parse(max ? RestoreGlyph : MaxGlyph);
        AutomationProperties.SetName(MaximiseButton, max ? "Restore" : "Maximise");
        bool resizable = _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        MaximiseButton.Visibility = resizable ? Visibility.Visible : Visibility.Collapsed;
        MinimiseButton.Visibility = _window.ResizeMode == ResizeMode.NoResize ? Visibility.Collapsed : Visibility.Visible;
        MaximiseButton.IsHot = MaximiseButton.IsDown = false;
    }

    CaptionButton Make(string id, string name, string glyph, Brush hover, Brush down, Action click)
    {
        var b = new CaptionButton(hover, down)
        {
            Content = new Path
            {
                Data = Geometry.Parse(glyph), Stroke = Brushes.White, StrokeThickness = 1, Width = 10, Height = 10,
                SnapsToDevicePixels = true, IsHitTestVisible = false,
            },
        };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        Children.Add(b);
        return b;
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>One caption button. <see cref="IsHot"/> / <see cref="IsDown"/> are also set from outside: over the
/// maximise button Windows itself tracks the pointer (for its snap layouts), so WPF never sees it there.</summary>
public sealed class CaptionButton : Button
{
    readonly Brush _hover, _down;
    bool _hot, _downState;

    static readonly ControlTemplate Plain = MakeTemplate();

    internal CaptionButton(Brush hover, Brush down)
    {
        _hover = hover;
        _down = down;
        Template = Plain;
        Width = 46;
        Focusable = false;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        Cursor = System.Windows.Input.Cursors.Arrow;
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
        MouseEnter += (_, _) => Paint();
        MouseLeave += (_, _) => Paint();
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        Paint();
    }

    public bool IsHot { get => _hot; set { if (_hot != value) { _hot = value; Paint(); } } }
    public bool IsDown { get => _downState; set { if (_downState != value) { _downState = value; Paint(); } } }

    void Paint() =>
        Background = IsPressed || _downState ? _down : IsMouseOver || _hot ? _hover : Brushes.Transparent;

    static ControlTemplate MakeTemplate()
    {
        var t = new ControlTemplate(typeof(CaptionButton));
        var bg = new FrameworkElementFactory(typeof(Border));
        bg.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bg.AppendChild(content);
        t.VisualTree = bg;
        t.Seal();
        return t;
    }
}
