using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using HelmetSwap.Core;

namespace HelmetSwap.App;

/// <summary>A driver chip: team colour bar, name and helmet make.</summary>
public sealed class DriverChip : INotifyPropertyChanged
{
    bool _enabled = true, _showYear;
    string? _tooltip;
    Driver _driver = null!;
    /// <summary>The helmet; for the one "Career Design" chip, the in-game design slot picked under the list (changes).</summary>
    public required Driver Driver
    {
        get => _driver;
        set { _driver = value; foreach (var p in new[] { nameof(Id), nameof(Name), nameof(Make), nameof(Label), nameof(Tooltip) }) Changed(p); }
    }
    /// <summary>The chip standing for every standard career design (one chip; the slot is picked apart).</summary>
    public bool IsDesigns => Driver.IsCareerDesign;
    string Title => IsDesigns ? "Career Design" : Driver.Name + (Driver.Variant is null ? "" : " · " + Driver.Variant);
    public string Id => Driver.Team.ColorKey;                   // team colour (TeamBrush)
    public string Name => Title + (_showYear && !Driver.IsCareer ? " · " + Driver.Year : "");   // "Max Verstappen · Japan"
    /// <summary>Add the season to the name (a search or filter lists every season at once).</summary>
    public bool ShowYear { get => _showYear; set { if (_showYear == value) return; _showYear = value; Changed(nameof(Name)); } }
    public string Make => Driver.Make + (Driver.Spoiler is null ? "" : " · " + Driver.Spoiler) + (Driver.HasStrip ? "" : " · no strip");   // "bell · clear wing"
    public string Label => $"{Title}, {Driver.Team.Name}, {Driver.Year}, {Driver.ShellName}{(Driver.Spoiler is null ? "" : ", " + Driver.Spoiler)}";
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(nameof(Enabled)); } }
    bool _used;
    /// <summary>Some part comes from this helmet (marked in the list when parts come from several helmets).</summary>
    public bool Used { get => _used; set { if (_used == value) return; _used = value; Changed(nameof(Used)); } }
    public string? Tooltip { get => _tooltip ?? (Driver.IsCareer ? $"{Driver.Team.Name} · {Driver.ShellName}" : $"{Driver.Team.Name} {Driver.Year} · {Driver.ShellName}"); set { _tooltip = value; Changed(nameof(Tooltip)); Changed(nameof(Reason)); } }
    /// <summary>The chip's own note (why it can't be picked, or what its helmet brings), without the default words.</summary>
    public string? Reason => _tooltip;
    void Changed(string p) => PropertyChanged?.Invoke(this, new(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record LogLine(string Text, Brush Brush);

sealed class Settings
{
    public string? GameDir { get; set; }
    public string? Target { get; set; }
    public string? Donor { get; set; }
    public string? OutDir { get; set; }
    /// <summary>Width of the picks column (the preview takes the rest), as dragged.</summary>
    public double? FormWidth { get; set; }
    /// <summary>The in-game career design slot the "Career Design" chip replaces (a design folder, e.g. tier01_livery52).</summary>
    public string? DesignSlot { get; set; }
    /// <summary>The hover previews of the lists (made in the background the first time); off on a slower PC.</summary>
    public bool HoverPictures { get; set; } = true;

    static string PathOf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HelmetSwapper", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new(); } catch { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!); File.WriteAllText(PathOf, JsonSerializer.Serialize(this)); } catch { }
    }
}

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly ObservableCollection<LogLine> _log = [];
    List<DriverChip> _targets = [], _donors = [];
    List<Driver> _designs = [];
    bool _settingSlot;

    /// <summary>A career design slot in the picker: "Career Design 52 · Tier 1 (Arai GP-6)".</summary>
    sealed record SlotItem(Driver Driver)
    {
        public override string ToString() => $"{Driver.Name} · {Driver.Variant}" + (Driver.MakeName == "Arai" ? "" : $" ({Driver.ShellName})");
    }
    DriverChip? _target, _donor;      // the picks, kept while the other series' tab is shown (_donor: the helmet picked last)

    // ---- the part slots: whose each part is (PartPicks.Each order); off = switched off in the preview (the slot keeps its
    // helmet); aimed = the slots the next helmet picked goes to (none = every part)
    readonly Driver?[] _src = new Driver?[5];
    readonly bool[] _off = new bool[5];
    readonly System.Windows.Controls.Primitives.ToggleButton[] _slotBtn = new System.Windows.Controls.Primitives.ToggleButton[5];
    readonly System.Windows.Controls.TextBlock[] _slotWho = new System.Windows.Controls.TextBlock[5];
    readonly System.Windows.Controls.Button[] _slotClear = new System.Windows.Controls.Button[5];
    static readonly string[] SlotNames = ["Shell", "Visor", "Visor strip", "Wing", "Attachments"];
    static readonly string[] SlotTips =
    [
        "The helmet's shell: its paint (design and textures) and, from another make, the shell itself",
        "The visor: its tint and the tear-offs",
        "The sponsor strip above the visor",
        "The rear wing on top: solid, clear or none",
        "The clear vents and chin strip, and the front vents some helmets have (every Schuberth)",
    ];
    bool _refreshing;
    GameInstall? _game;
    string? _loadedDir, _lastOut;
    bool _outEdited;

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>The app version (from the project's Version), e.g. "1.1.0" -> shown as "v1.1".</summary>
    static string AppVersion
    {
        get
        {
            var v = (System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?").Split('+')[0];
            return v.EndsWith(".0") && v.Count(c => c == '.') == 2 ? v[..^2] : v;
        }
    }

    static string DefaultOutDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Helmet Swapper");

    public MainWindow()
    {
        InitializeComponent();
        Studio.Ui.StudioChrome.Apply(this, 64);   // borderless: the brand band is the title bar
        // use the screen's height when there is room (the lists, parts row and log all fit without scrolling the log)
        var room = SystemParameters.WorkArea.Height - 8;
        Height = Math.Max(MinHeight, Math.Min(1180, room));
        // shorter screens: fewer chip rows before the lists scroll, so the log keeps its room
        if (room < 1250) TargetScroll.MaxHeight = DonorScroll.MaxHeight = room < 1000 ? 108 : 144;
        VersionText.Text = "v" + AppVersion;
        LogList.ItemsSource = _log;
        GameBox.Text = _settings.GameDir ?? GameInstall.Detect() ?? "";
        OutBox.PreviewKeyDown += (_, _) => _outEdited = true;
        Log("Pick the driver to change and whose helmet to give them, then Build.", Res("LogText"));
        Loaded += (_, _) => LoadDrivers();
        // the 3D preview column beside the picks when the window is wide enough; it can also pop out into its own window
        var work = SystemParameters.WorkArea;
        if (work.Width >= ColumnMinWindow + 50) Width = Math.Min(work.Width - 8, 1720);
        Loaded += (_, _) => { ApplyPreviewLayout(); ApplyFormLayout(); };
        SizeChanged += (_, _) => { if (IsLoaded) { ApplyPreviewLayout(); ApplyFormLayout(); } };
        // the picks fill the window's height and the log takes the rest (it only sets a new height when one is needed)
        FormGrid.LayoutUpdated += (_, _) => FitForm();
        BuildSlots();
        TargetBox.ItemContainerGenerator.StatusChanged += (_, _) => AttachTips(TargetBox);
        DonorBox.ItemContainerGenerator.StatusChanged += (_, _) => AttachTips(DonorBox);
        PicturesBox.IsChecked = _settings.HoverPictures;
        Closed += (_, _) => _pictures.Close();   // the hidden viewer of the hover pictures goes with the app
        PreviewMouseDown += ClosePreviewsNote;
        Deactivated += (_, _) => { _previewsPinned = false; NoteShown(false); };
        LocationChanged += (_, _) => { _previewsPinned = false; NoteShown(false, now: true); };
    }

    // ---------------------------------------------------------------- hover previews (as Swapper's)

    readonly HelmetPictures _pictures = new();

    /// <summary>Every helmet's hover picture made in the background (when they are on), so a hover shows one at once.
    /// Pictures already made are skipped, so after the first time this costs nothing.</summary>
    void PrewarmPictures()
    {
        if (_game is not { } game || PicturesBox.IsChecked != true) return;
        int run = ++_prewarmRun;   // a newer run (the box turned off and on again) takes over from this one
        _ = _pictures.Prewarm(game, _targets.Concat(_donors).Select(c => c.Driver), () => PicturesBox.IsChecked == true && run == _prewarmRun,
            p => { if (run == _prewarmRun) ShowPreviewsProgress(p); });
    }

    int _prewarmRun;

    // the line next to Hover previews while they are being made (gone when they are all made, or turned off); the countdown
    // ticks down each second and eases toward the latest measured time left (no jumps), never below 0:01 while previews
    // are still being made
    PrewarmProgress? _previewsNow;
    TimeSpan _previewsEstimate;
    DateTime _previewsEstimateAt;
    double _previewsShown = -1;
    readonly System.Windows.Threading.DispatcherTimer _previewsTick = new() { Interval = TimeSpan.FromSeconds(1) };

    void ShowPreviewsProgress(PrewarmProgress? p)
    {
        _previewsNow = p;
        if (p is null || PicturesBox.IsChecked != true)
        {
            _previewsTick.Stop();
            _previewsShown = -1;
            PreviewsProgress.Text = "";
            PreviewsProgress.Visibility = Visibility.Collapsed;
            return;
        }
        if (p.Left is { } left)
        {
            _previewsEstimate = left;
            _previewsEstimateAt = DateTime.UtcNow;
            if (_previewsShown < 0) _previewsShown = left.TotalSeconds;
        }
        if (!_previewsTick.IsEnabled)
        {
            _previewsTick.Tick -= PreviewsTick;
            _previewsTick.Tick += PreviewsTick;
            _previewsTick.Start();
        }
        PreviewsProgress.Visibility = Visibility.Visible;
        WritePreviewsProgress();
    }

    void PreviewsTick(object? sender, EventArgs e)
    {
        if (_previewsShown >= 0)
        {
            var estimate = (_previewsEstimate - (DateTime.UtcNow - _previewsEstimateAt)).TotalSeconds;
            _previewsShown = Math.Max(1, _previewsShown - 1 + (estimate - (_previewsShown - 1)) * 0.3);
        }
        WritePreviewsProgress();
    }

    void WritePreviewsProgress()
    {
        if (_previewsNow is not { } p) return;
        int pct = p.Total == 0 ? 100 : p.Done * 100 / p.Total;
        var secs = (int)Math.Round(_previewsShown);
        PreviewsProgress.Text = $"making previews · {pct}%" + (_previewsShown >= 0 ? $" · {secs / 60}:{secs % 60:00} left" : "");
    }

    void Pictures_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.HoverPictures = PicturesBox.IsChecked == true;
        _settings.Save();
        if (PicturesBox.IsChecked == true) { AttachTips(TargetBox); AttachTips(DonorBox); } else DetachTips();
        PrewarmPictures();
    }

    // the hover previews' note: open while the "i" is hovered, pinned by a click (or Enter / Space) until a click elsewhere
    bool _previewsPinned;
    void PreviewsInfo_Enter(object sender, System.Windows.Input.MouseEventArgs e) => NoteShown(true);
    void PreviewsInfo_Leave(object sender, System.Windows.Input.MouseEventArgs e) { if (!_previewsPinned) NoteShown(false); }
    void PreviewsInfo_Click(object sender, RoutedEventArgs e)
    {
        _previewsPinned = !_previewsPinned;
        NoteShown(_previewsPinned || PreviewsInfo.IsMouseOver);
    }
    void ClosePreviewsNote(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_previewsPinned || PreviewsInfo.IsMouseOver) return;
        _previewsPinned = false;
        NoteShown(false);
    }

    /// <summary>The note fades in and out (ease in / out), as the tooltips do.</summary>
    void NoteShown(bool show, bool now = false)
    {
        var note = (UIElement)PreviewsPop.Child;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        if (show)
        {
            if (!PreviewsPop.IsOpen) { note.Opacity = 0; PreviewsPop.IsOpen = true; }
            note.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            return;
        }
        if (!PreviewsPop.IsOpen) return;
        if (now) { note.BeginAnimation(OpacityProperty, null); PreviewsPop.IsOpen = false; return; }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (note.Opacity < 0.01) PreviewsPop.IsOpen = false; };
        note.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The chips given a hover preview, with the handler that fills it (taken off again when previews are turned off).</summary>
    readonly Dictionary<System.Windows.Controls.ListBoxItem, System.Windows.Controls.ToolTipEventHandler> _previewTips = [];

    /// <summary>Hover previews off: every chip goes back to its plain tip (its words only, from the list's style).</summary>
    void DetachTips()
    {
        foreach (var (item, handler) in _previewTips)
        {
            item.ToolTipOpening -= handler;
            item.ClearValue(ToolTipProperty);
            item.ClearValue(System.Windows.Controls.ToolTipService.InitialShowDelayProperty);
            item.ClearValue(System.Windows.Controls.ToolTipService.ShowDurationProperty);
            item.ClearValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty);
        }
        _previewTips.Clear();
    }

    /// <summary>A hover tip per chip: the helmet in the 3/4 view, the driver, what it is, and the chip's note (why it can't
    /// be picked) when it has one.</summary>
    void AttachTips(System.Windows.Controls.ListBox box)
    {
        if (box.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
        if (PicturesBox.IsChecked != true) return;   // previews off: the chips keep their plain tips
        foreach (var gone in _previewTips.Keys.Where(i => !TargetBox.IsAncestorOf(i) && !DonorBox.IsAncestorOf(i)).ToList()) _previewTips.Remove(gone);
        foreach (var chip in box.Items.OfType<DriverChip>())
        {
            if (box.ItemContainerGenerator.ContainerFromItem(chip) is not System.Windows.Controls.ListBoxItem item || item.ToolTip is System.Windows.Controls.ToolTip) continue;
            var image = new System.Windows.Controls.Image { Width = HelmetPictures.W / 2, Height = HelmetPictures.H / 2, Stretch = Stretch.Uniform };
            var title = new System.Windows.Controls.TextBlock
            {
                FontFamily = (FontFamily)Application.Current.Resources["Display"], FontWeight = FontWeights.Bold,
                FontSize = 10.5, Foreground = Res("Ink"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            };
            title.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DriverChip.Name)) { Source = chip, Converter = UpperCase.Instance });
            var note = new System.Windows.Controls.TextBlock
            {
                Text = "rendering…", FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontWeight = FontWeights.Bold, FontSize = 10.5,
                Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = image.Width, Margin = new Thickness(0, 4, 0, 0),
            };
            // the chip's own words (why it is greyed, or what its helmet brings) under the picture
            var reason = new System.Windows.Controls.TextBlock
            {
                FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 10.5, Foreground = Res("Purple"), TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, MaxWidth = image.Width, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed,
            };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(image); panel.Children.Add(title); panel.Children.Add(note); panel.Children.Add(reason);
            var tip = new System.Windows.Controls.ToolTip
            {
                Content = panel, Background = Res("Panel"), BorderBrush = Res("Purple"), BorderThickness = new Thickness(1.5), Padding = new Thickness(12, 10, 12, 8),
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top, HasDropShadow = true,
            };
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(item, 250);
            System.Windows.Controls.ToolTipService.SetShowDuration(item, 60000);
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(item, true);
            item.ToolTip = tip;
            Driver? shown = null;   // the helmet pictured (the Career Design chip changes helmet with its slot)
            System.Windows.Controls.ToolTipEventHandler fill = async (_, _) =>
            {
                var d = chip.Driver;
                // (the shell's name is in the line above: "Bell HP7 · their shell brings the Bell HP7" says it once)
                var why = chip.Reason is { } r && r.StartsWith(d.ShellName + " · ", StringComparison.Ordinal) ? r[(d.ShellName.Length + 3)..] : chip.Reason;
                reason.Text = why ?? "";
                reason.Visibility = string.IsNullOrEmpty(why) ? Visibility.Collapsed : Visibility.Visible;
                string what = (d.IsCareerDesign ? $"{d.Name} · {d.Variant} · {d.ShellName}" : d.IsCareer ? $"career · {d.ShellName}" : $"{d.Team.Name} {d.Year} · {d.ShellName}").ToLowerInvariant();
                if (shown == d && image.Source is not null) { note.Text = what; return; }
                image.Source = null; note.Text = "rendering…";
                try
                {
                    var game = _game ?? throw new InvalidOperationException("no game folder");
                    var path = await _pictures.For(game, d);
                    if (chip.Driver != d) return;   // the slot changed meanwhile: the next hover shows the new one
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit(); bmp.Freeze();
                    image.Source = bmp; shown = d;
                    note.Text = what;
                }
                catch (Exception) { note.Text = _game is null ? "set the F1 25 game folder to see this" : "the picture could not be made"; }
            };
            item.ToolTipOpening += fill;
            _previewTips[item] = fill;
            // scripted checks: HELMETSWAP_TEST_TIP=<helmet folder> opens that chip's hover preview without the mouse
            if (Environment.GetEnvironmentVariable("HELMETSWAP_TEST_TIP") is { Length: > 0 } test && test == chip.Driver.Folder)
                Dispatcher.BeginInvoke(() => { tip.PlacementTarget = item; tip.IsOpen = true; fill(item, null!); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    sealed class UpperCase : System.Windows.Data.IValueConverter
    {
        public static readonly UpperCase Instance = new();
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => (v as string)?.ToUpperInvariant() ?? "";
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    sealed class LowerCase : System.Windows.Data.IValueConverter
    {
        public static readonly LowerCase Instance = new();
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => (v as string)?.ToLowerInvariant() ?? "";
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    void Log(string s, Brush b)
    {
        _log.Add(new LogLine(s, b));
        LogList.ScrollIntoView(_log[^1]);
    }

    void Detect_Click(object sender, RoutedEventArgs e)
    {
        var d = GameInstall.Detect();
        if (d is null) Log("Could not find F1 25 in your Steam libraries. Use Browse… to pick the game folder.", Res("LogWarn"));
        else { GameBox.Text = d; Log($"Found F1 25: {d}", Res("LogText")); LoadDrivers(); }
    }

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the F1 25 game folder" };
        if (dlg.ShowDialog(this) == true) { GameBox.Text = dlg.FolderName; LoadDrivers(); }
    }

    void GameBox_LostFocus(object sender, RoutedEventArgs e) => LoadDrivers();

    async void LoadDrivers()
    {
        var dir = GameBox.Text.Trim();
        if (dir.Length == 0 || dir == _loadedDir) return;
        _loadedDir = dir;
        try
        {
            var game = new GameInstall(dir);
            SetBusy(true);
            var drivers = await Task.Run(() => Drivers.Find(game, olderHelmets: true, career: true));
            if (!drivers.Any(d => d.Year == Drivers.Season)) throw new InvalidDataException("No 2026 drivers were found in this game folder.");
            _game = game;
            // team order (F1 then F2), as the game lists them; 2026 drivers can be changed, any season's helmet can be given
            // the standard career designs are one helmet in different in-game slots: one chip, the slot picked under the list
            _designs = drivers.Where(d => d.IsCareerDesign).ToList();
            var slot = _designs.FirstOrDefault(d => d.Folder == _settings.DesignSlot) ?? _designs.FirstOrDefault(d => d.Folder == "tier01_livery52") ?? _designs.FirstOrDefault();
            _targets = drivers.Where(d => d.Changeable && (!d.IsCareerDesign || d == slot)).Select(d => new DriverChip { Driver = d }).ToList();
            _settingSlot = true;
            DesignSlot.ItemsSource = _designs.Select(d => new SlotItem(d)).ToList();
            DesignSlot.SelectedIndex = slot is null ? -1 : _designs.IndexOf(slot);
            _settingSlot = false;
            // (a standard career design's colours are picked in game: it can be changed, not given)
            _donors = drivers.Where(d => !d.IsCopy && !d.IsCareerDesign).Select(d => new DriverChip { Driver = d }).ToList();
            _target = _targets.FirstOrDefault(c => c.Driver.Folder == _settings.Target || c.IsDesigns && _designs.Any(d => d.Folder == _settings.Target));
            ShowSlotRow();
            _donor = _donors.FirstOrDefault(c => c.Driver.Folder == _settings.Donor);
            // the last helmet given fills every part, as a whole-helmet swap
            for (int i = 0; i < 5; i++) { _src[i] = _donor?.Driver; _off[i] = false; }
            if (_target is not null)
            {
                (_target.Driver.Series switch { "F2" => TargetF2, "Career" => TargetCareer, _ => TargetF1 }).IsChecked = true;
                (_target.Driver.Year == 2025 ? TargetY2025 : TargetY2026).IsChecked = true;
            }
            if (_donor is not null)
            {
                (_donor.Driver.Series switch { "F2" => DonorF2, "Career" => DonorCareer, _ => DonorF1 }).IsChecked = true;
                (_donor.Driver.Year switch { 2025 => DonorY2025, 2024 => DonorY2024, _ => DonorY2026 }).IsChecked = true;
            }
            TargetTeam.ItemsSource = TeamsOf(_targets); TargetTeam.SelectedIndex = 0;
            DonorTeam.ItemsSource = TeamsOf(_donors); DonorTeam.SelectedIndex = 0;
            UpdateDonors();
            ShowChips();
            RefreshSlots();
            UpdateParts();
            SuggestOutput();
            UpdatePreview();
            int unknown = drivers.Count(d => d.Shell is null);
            static bool Real(DriverChip c, int y) => c.Driver.Year == y && !c.Driver.IsCareer;
            Log($"{_targets.Count(c => Real(c, Drivers.Season))} 2026 and {_targets.Count(c => Real(c, 2025))} 2025 drivers to change, plus {_targets.Count(c => c.Driver.IsCareer && !c.IsDesigns)} career replicas and the career designs ({_designs.Count} in-game slots); " +
                $"helmets from {_donors.Count(c => Real(c, Drivers.Season))} 2026, {_donors.Count(c => Real(c, 2025))} 2025 and {_donors.Count(c => Real(c, 2024))} 2024 drivers and {_donors.Count(c => c.Driver.IsCareer)} career replicas."
                + (unknown > 0 ? $" {unknown} have a helmet shell the app doesn't recognise." : ""), Res("LogText"));
            PrewarmPictures();
        }
        catch (Exception ex)
        {
            _game = null; _loadedDir = null;
            TargetBox.ItemsSource = DonorBox.ItemsSource = null;
            Log(ex.Message, Res("LogError"));
        }
        finally { SetBusy(false); }
    }


    // null = all seasons / both series
    int? TargetYear => TargetYAll.IsChecked == true ? null : TargetY2025.IsChecked == true ? 2025 : Drivers.Season;
    int? DonorYear => DonorYAll.IsChecked == true ? null : DonorY2025.IsChecked == true ? 2025 : DonorY2024.IsChecked == true ? 2024 : Drivers.Season;
    string? TargetSeries => TargetSAll.IsChecked == true ? null : TargetF2.IsChecked == true ? "F2" : TargetCareer.IsChecked == true ? "Career" : "F1";
    string? DonorSeries => DonorSAll.IsChecked == true ? null : DonorF2.IsChecked == true ? "F2" : DonorCareer.IsChecked == true ? "Career" : "F1";

    /// <summary>Each list shows its tab's series (F1 / F2) and, for helmets, season; a pick on another tab stays picked.</summary>
    void ShowChips()
    {
        _refreshing = true;
        try
        {
            Show(TargetBox, _targets, FilterOf("Target"), TargetYear, TargetSeries, _target);
            Show(DonorBox, _donors, FilterOf("Donor"), DonorYear, DonorSeries, _donor);
        }
        finally { _refreshing = false; }
        // an empty list says so (instead of vanishing)
        foreach (var (box, empty) in new[] { (TargetBox, TargetEmpty), (DonorBox, DonorEmpty) })
        {
            bool none = box.Items.Count == 0;
            empty.Text = none ? "no helmets match these tabs and filters: try ALL on the season or series tab, or clear the search" : "";
            empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>A list's search / make / team filter (empty = browse by the tabs).</summary>
    sealed record ChipFilter(string Text, string? Make, TeamItem? Team)
    {
        public bool Active => Text.Length > 0 || Make is not null || Team is not null;
        public bool Matches(Driver d)
        {
            if (Make is not null && !d.MakeName.Equals(Make, StringComparison.OrdinalIgnoreCase)) return false;
            if (Team is not null && (d.Team.Name != Team.Name || d.Series != Team.Series)) return false;
            var hay = $"{d.Name} {d.Variant} {d.Team.Name} {d.ShellName} {d.Year} {d.Series} {d.Spoiler}".ToLowerInvariant();
            return Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(hay.Contains);
        }
    }

    /// <summary>A team in the team filter: F1 teams first, then F2.</summary>
    public sealed record TeamItem(string Name, string Series)
    {
        public override string ToString() => Series is "F2" or "Career" && !Name.StartsWith("Career", StringComparison.Ordinal) ? $"{Name} · {Series}" : Name;
    }

    static List<object> TeamsOf(IEnumerable<DriverChip> chips) =>
        ["All teams", .. chips.Select(c => new TeamItem(c.Driver.Team.Name, c.Driver.Series)).Distinct()
                              .OrderBy(t => t.Series switch { "F2" => 1, "Career" => 2, _ => 0 }).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];

    ChipFilter FilterOf(string side)
    {
        var text = ((System.Windows.Controls.TextBox)FindName(side + "Search")).Text.Trim();
        var make = new[] { "Arai", "Bell", "Schuberth", "Stilo" }
            .FirstOrDefault(m => ((System.Windows.Controls.RadioButton)FindName(side + "Make" + m)).IsChecked == true);
        var team = ((System.Windows.Controls.ComboBox)FindName(side + "Team")).SelectedItem as TeamItem;
        return new(text, make, team);
    }

    /// <summary>Fill a chip list: the season and series tabs (each can be "All") and the search / make / team filter;
    /// with all seasons, newest first and the season on each chip. Career helmets have no season: the Career tab
    /// shows them whatever the season tab, and they're listed with the current season.</summary>
    static void Show(System.Windows.Controls.ListBox box, List<DriverChip> all, ChipFilter filter, int? year, string? series, DriverChip? pick)
    {
        var list = all.Where(c => (year is null || c.Driver.Year == year || series == "Career") && (series is null || c.Driver.Series == series) && filter.Matches(c.Driver)).ToList();
        if (year is null) list = list.OrderByDescending(c => c.Driver.Year).ThenBy(c => c.Driver.Series).ToList();
        foreach (var c in all) c.ShowYear = year is null;
        box.ItemsSource = list;
        box.SelectedItem = pick is not null && list.Contains(pick) ? pick : null;
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && _game is not null) ShowChips();
    }

    void Series_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _game is null) return;
        // a series tab that the picked team isn't in: the team filter goes back to all teams
        foreach (var (team, series) in new[] { (TargetTeam, TargetSeries), (DonorTeam, DonorSeries) })
            if (team.SelectedItem is TeamItem t && series is not null && t.Series != series) { _teamSync = true; team.SelectedIndex = 0; _teamSync = false; }
        ShowChips();
    }

    bool _teamSync;

    /// <summary>A team picked: its series tab is selected unless "All" is (Career for the career helmets), and the season tab goes to
    /// All when that team has no helmet in the selected season, so the team's helmets always show.</summary>
    void Team_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _game is null || _teamSync) return;
        var combo = (System.Windows.Controls.ComboBox)sender;
        bool target = combo == TargetTeam;
        if (combo.SelectedItem is TeamItem t)
        {
            _teamSync = true;
            try
            {
                var series = target ? new { All = TargetSAll, F1 = TargetF1, F2 = TargetF2, Career = TargetCareer } : new { All = DonorSAll, F1 = DonorF1, F2 = DonorF2, Career = DonorCareer };
                // (on "All" series the team's helmets already show)
                if ((target ? TargetSeries : DonorSeries) is { } cur && cur != t.Series)
                    (t.Series switch { "F2" => series.F2, "Career" => series.Career, _ => series.F1 }).IsChecked = true;
                int? year = target ? TargetYear : DonorYear;
                var chips = target ? _targets : _donors;
                if (year is not null && t.Series != "Career" && !chips.Any(c => c.Driver.Team.Name == t.Name && c.Driver.Series == t.Series && c.Driver.Year == year))
                    (target ? TargetYAll : DonorYAll).IsChecked = true;
            }
            finally { _teamSync = false; }
        }
        ShowChips();
    }

    void Target_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshing || TargetBox.SelectedItem is not DriverChip t) return;
        _target = t;
        ShowSlotRow();
        UpdateDonors();
        UpdateParts();
        SuggestOutput();
        UpdatePreview();
    }

    /// <summary>The design slot picker shows while the "Career Design" chip is the driver to change.</summary>
    void ShowSlotRow() => SlotRow.Visibility = _target is { IsDesigns: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Another in-game design slot: the "Career Design" chip now stands for it (same helmet, other slot).</summary>
    void DesignSlot_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingSlot || DesignSlot.SelectedItem is not SlotItem s) return;
        var chip = _targets.FirstOrDefault(c => c.IsDesigns);
        if (chip is null) return;
        chip.Driver = s.Driver;
        _settings.DesignSlot = s.Driver.Folder; _settings.Save();
        if (_target != chip) return;
        UpdateDonors();
        UpdateParts();
        SuggestOutput();
        UpdatePreview();
    }

    void Donor_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshing || DonorBox.SelectedItem is not DriverChip d) return;
        Assign(d);
    }

    /// <summary>A click on the helmet already picked (no selection change): it still goes to the slots aimed at.</summary>
    void DonorBox_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_refreshing || e.OriginalSource is not DependencyObject o) return;
        if (DonorBox.ContainerFromElement(o) is System.Windows.Controls.ListBoxItem { DataContext: DriverChip d } && d == DonorBox.SelectedItem && Aimed().Count > 0)
            Assign(d);
    }

    // ---------------------------------------------------------------- part slots

    void BuildSlots()
    {
        for (int i = 0; i < 5; i++)
        {
            int k = i;
            var name = new Tracked { Text = SlotNames[i], FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.Bold, FontSize = 10.5, Tracking = 0.14, Foreground = Res("Ink") };
            _slotWho[i] = new System.Windows.Controls.TextBlock { FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0), Foreground = Res("Muted") };
            var body = new System.Windows.Controls.StackPanel(); body.Children.Add(name); body.Children.Add(_slotWho[i]);
            _slotBtn[i] = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)FindResource("PartSlot"), Content = body, ToolTip = SlotTips[i] + " · click to send the next helmet you pick to this part" };
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(_slotBtn[i], true);
            System.Windows.Automation.AutomationProperties.SetName(_slotBtn[i], SlotNames[i] + " slot");
            _slotBtn[i].Checked += (_, _) => UpdateParts();
            _slotBtn[i].Unchecked += (_, _) => UpdateParts();
            _slotClear[i] = new System.Windows.Controls.Button { Style = (Style)FindResource("SlotClear"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                                         Margin = new Thickness(0, 3, 3, 0), ToolTip = "Back to the driver's own" };
            System.Windows.Automation.AutomationProperties.SetName(_slotClear[i], SlotNames[i] + ": back to own");
            _slotClear[i].Click += (_, _) => { _src[k] = null; _off[k] = false; PicksChanged(); };
            var cell = new System.Windows.Controls.Grid { Margin = new Thickness(0, 3, 8, 3) };
            cell.Children.Add(_slotBtn[i]); cell.Children.Add(_slotClear[i]);
            SlotPanel.Children.Add(cell);
        }
        RefreshSlots();
    }

    /// <summary>A part slot that can't change for this driver (a career design sharing the My Team parts: strip, wing, attachments).</summary>
    bool SlotLocked(int i) => _partsLocked && PartPicks.Each[i] is SwapParts.VisorStrip or SwapParts.Wing or SwapParts.Attachments;

    List<int> Aimed() => Enumerable.Range(0, 5).Where(i => _slotBtn[i]?.IsChecked == true && !SlotLocked(i)).ToList();

    /// <summary>Every part slot with a helmet (switched on or off in the preview), the locked ones left out: what the
    /// preview loads.</summary>
    PartPicks AllPicks
    {
        get
        {
            var p = new PartPicks();
            for (int i = 0; i < 5; i++) if (_src[i] is { } d && !SlotLocked(i)) p[PartPicks.Each[i]] = d;
            return p;
        }
    }

    /// <summary>The parts the swap takes, each from its helmet: the slots with a helmet, switched on.</summary>
    PartPicks Picks
    {
        get
        {
            var p = new PartPicks();
            for (int i = 0; i < 5; i++) if (_src[i] is { } d && !_off[i] && !SlotLocked(i)) p[PartPicks.Each[i]] = d;
            return p;
        }
    }

    SwapParts Parts => Picks.Parts;

    /// <summary>A helmet picked: it goes to the slots aimed at, or (none aimed) to every part, as a whole-helmet swap.</summary>
    void Assign(DriverChip d)
    {
        _donor = d;
        var to = Aimed();
        if (to.Count == 0) to = [.. Enumerable.Range(0, 5).Where(i => !SlotLocked(i))];
        foreach (var i in to) { _src[i] = d.Driver; _off[i] = false; }
        PicksChanged();
    }

    void PicksChanged()
    {
        RefreshSlots();
        UpdateParts();
        SuggestOutput();
        UpdatePreview();
    }

    static string Short(Driver d) => d.ShortName + (d.YearTag == "" ? "" : " " + d.YearTag);

    /// <summary>Each slot says whose the part is ("own", a helmet, switched off, locked); the helmets in use are marked
    /// in the list when they're more than one.</summary>
    void RefreshSlots()
    {
        if (_slotBtn[0] is null) return;
        for (int i = 0; i < 5; i++)
        {
            bool locked = SlotLocked(i);
            _slotBtn[i].IsEnabled = !locked;
            if (locked) _slotBtn[i].IsChecked = false;
            _slotWho[i].Text = locked ? "locked" : _src[i] is { } d ? Short(d) + (_off[i] ? " · off" : "") : "own";
            _slotWho[i].Foreground = !locked && _src[i] is not null && !_off[i] ? Res("Purple") : Res("Muted");
            _slotWho[i].FontWeight = !locked && _src[i] is not null && !_off[i] ? FontWeights.Bold : FontWeights.Normal;
            _slotClear[i].Visibility = !locked && _src[i] is not null ? Visibility.Visible : Visibility.Collapsed;
            _slotBtn[i].ToolTip = locked ? "This career design shares its helmet parts with the other designs: only the shell and the visor can change"
                                         : SlotTips[i] + " · click to send the next helmet you pick to this part";
        }
        var used = AllPicks.Donors;
        foreach (var c in _donors) c.Used = used.Count > 1 && used.Contains(c.Driver);
    }

    /// <summary>Any other driver can give their helmet; a different make (shell) is swapped too.</summary>
    void UpdateDonors()
    {
        var t = _target?.Driver;
        foreach (var c in _donors)
        {
            var d = c.Driver;
            if (t is null) { c.Enabled = true; c.Tooltip = null; }
            else if (d.Folder == t.Folder) { c.Enabled = false; c.Tooltip = "the driver being changed"; }
            else if (d.Shell is null) { c.Enabled = false; c.Tooltip = "helmet shell not recognised"; }
            else if (d.Shell != t.Shell && t.IsCareerDesign)
            {
                c.Enabled = true;
                c.Tooltip = $"{d.ShellName} · career designs wear an {t.ShellName}: the visor can go on, not the shell";
            }
            else if (d.Shell != t.Shell && !t.OwnShellBinding)
            {
                c.Enabled = true;
                c.Tooltip = $"{d.ShellName} · {t.Name}'s files share their shell binding with the whole team: the visor, wing or attachments can go on, not the shell";
            }
            else if (d.Shell != t.Shell) { c.Enabled = true; c.Tooltip = $"{d.ShellName} · their shell brings the {d.ShellName}"; }
            else { c.Enabled = true; c.Tooltip = null; }
        }
        if (_donor is { Enabled: false }) { _donor = null; DonorBox.SelectedItem = null; }
        for (int i = 0; i < 5; i++) if (_src[i] is { } s && t is not null && s.Folder == t.Folder) { _src[i] = null; _off[i] = false; }
        DonorHint.Text = t is null ? "" : $"{t.Label.ToLowerInvariant()} wears {A(t.ShellName)} {t.ShellName.ToLowerInvariant()} · "
                         + (t.IsCareerDesign ? $"shell from {A(t.MakeName)} {t.MakeName.ToLowerInvariant()}, visor from any"
                            : t.OwnShellBinding ? "another make's shell can go on" : "another make: visor, wing or attachments only");
        LockParts(t);
    }

    static string A(string word) => "AEIOU".Contains(char.ToUpperInvariant(word[0])) ? "an" : "a";

    bool _partsLocked;

    /// <summary>A career design sharing the My Team helmet parts model: only its design and visor can change, so the
    /// visor strip, wing and attachments slots are locked (their helmets come back when another driver is picked).</summary>
    void LockParts(Driver? t)
    {
        _partsLocked = t is { IsCareerDesign: true, AttachmentsRoot: not null };
        RefreshSlots();
    }

    /// <summary>The note under the parts: a problem (red), a different-make fit warning (amber), or where the next helmet
    /// goes and what the driver keeps.</summary>
    void UpdateParts()
    {
        Driver? t = _target?.Driver;
        var picks = Picks;
        var parts = picks.Parts;
        string? problem = t is null || picks.IsEmpty ? null : HelmetSwapper.PartsProblem(t, picks);
        string? warning = t is null || picks.IsEmpty ? null : HelmetSwapper.PartsWarning(t, picks);
        var aimed = Aimed();
        string next = aimed.Count == 0 ? "" : $"the next helmet you pick goes to: {string.Join(" + ", aimed.Select(i => SlotNames[i].ToLowerInvariant()))}";
        string info = next != "" ? next
            : picks.IsEmpty ? "pick a helmet below for the whole helmet · or click parts first to take just those from it"
            : parts == SwapParts.All ? "click parts, then another helmet, to take just those parts from it"
            : $"{t?.Label ?? "the driver"} keeps their own {HelmetSwapper.PartsLabel(SwapParts.All & ~parts).ToLowerInvariant()}";
        PartsHint.Text = (problem ?? warning ?? info).ToLowerInvariant();
        PartsHint.Foreground = problem is not null ? Res("Red") : warning is not null ? WarnBrush : Res("Purple");
        PartsHint.ToolTip = problem ?? warning;
    }

    static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x5E, 0x00));

    void SuggestOutput()
    {
        if (_outEdited || _target is not { } t || Picks is not { IsEmpty: false } picks) return;
        OutBox.Text = Path.Combine(_settings.OutDir ?? DefaultOutDir, HelmetSwapper.OutputName(t.Driver, picks));
    }

    void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "SERPs template (*.serps)|*.serps",
            FileName = Path.GetFileName(OutBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(OutBox.Text)) ? Path.GetDirectoryName(OutBox.Text) : "",
        };
        if (dlg.ShowDialog(this) == true) { OutBox.Text = dlg.FileName; _outEdited = true; }
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) { Log("Pick the F1 25 game folder first.", Res("LogWarn")); return; }
        if (_target is not { } t) { Log("Pick the driver to change.", Res("LogWarn")); return; }
        var picks = Picks;
        if (picks.IsEmpty) { Log("Pick whose helmet to use.", Res("LogWarn")); return; }
        if (HelmetSwapper.PartsProblem(t.Driver, picks) is { } problem) { Log(problem, Res("LogWarn")); return; }
        var outPath = OutBox.Text.Trim();
        if (outPath.Length == 0) { Log("Choose where to save the new .serps.", Res("LogWarn")); return; }
        if (!outPath.EndsWith(".serps", StringComparison.OrdinalIgnoreCase)) outPath += ".serps";
        if (File.Exists(outPath) && MessageBox.Show(this, $"{Path.GetFileName(outPath)} already exists. Replace it?", "Helmet Swapper",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var game = _game; Driver target = t.Driver, donor = picks.Primary!;
        _settings.GameDir = game.GameDir; _settings.Target = target.Folder; _settings.Donor = (_donor?.Driver ?? donor).Folder;
        _settings.OutDir = Path.GetDirectoryName(Path.GetFullPath(outPath)); _settings.Save();
        _log.Clear();
        string To = $"{target.Name}{(target.YearTag == "" ? "" : " " + target.YearTag)} ({target.Series}, {target.ShellName})";
        Log(picks.Single is { } one ? $"{one.Name}'s {(one.YearTag == "" ? "" : one.YearTag + " ")}{one.ShellName} helmet -> {To}"
            : $"{string.Join(", ", PartPicks.Each.Where(p => picks[p] is not null).Select(p => $"{HelmetSwapper.PartsLabel(p).ToLowerInvariant()} from {Short(picks[p]!)}"))} -> {To}", Res("LogText"));
        var log = new SwapLog();
        log.Line += (s, w) => Dispatcher.Invoke(() => Log((w ? "Warning: " : "") + s, w ? Res("LogWarn") : Res("LogMuted")));
        SetBusy(true);
        ShowButton.Visibility = Visibility.Collapsed;
        StatusPill.Visibility = Visibility.Collapsed;
        try
        {
            var checks = await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                return HelmetSwapper.BuildAndSave(game, target, picks, outPath, log);
            });
            _lastOut = outPath;
            Log($"Done. Saved {Path.GetFileName(outPath)}. Open it in SERPs Studio like any template.", Res("LogOk"));
            Status($"Verified · {checks} checks", "Teal");
            ShowButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { Log("Failed: " + ex.Message, Res("LogError")); Status("Failed · nothing written", "Red"); }
        finally { SetBusy(false); }
    }

    // ---------------------------------------------------------------- the 3D preview (column or its own window)

    /// <summary>The app's embedded fonts as @font-face rules (data URIs) for the preview pages: they show Presicav and
    /// Roboto Mono on any PC, installed or not (the browser page is a single file, so the fonts travel inside it).</summary>
    static readonly Lazy<string> FontCss = new(() =>
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (file, family, weight) in new[] { ("Presicav-Regular.ttf", "Presicav", 400), ("Presicav-Bold.ttf", "Presicav", 700), ("Presicav-Heavy.ttf", "Presicav", 900),
                                                       ("RobotoMono-Regular.ttf", "Roboto Mono", 400), ("RobotoMono-Bold.ttf", "Roboto Mono", 700) })
        {
            try
            {
                using var s = Application.GetResourceStream(new Uri($"pack://application:,,,/Fonts/{file}"))!.Stream;
                using var ms = new MemoryStream(); s.CopyTo(ms);
                sb.Append($"@font-face{{font-family:\"{family}\";font-weight:{weight};font-style:normal;src:url(data:font/ttf;base64,{Convert.ToBase64String(ms.ToArray())}) format(\"truetype\")}}").Append('\n');
            }
            catch (Exception) { }   // a missing font: the page falls back to its next font, as before
        }
        return sb.ToString();
    });

    const double ColumnMinWindow = 1150;      // narrower: the column hides (the preview can still pop out)
    PreviewHost? _pv, _dockHost;
    PreviewWindow? _popWin;
    bool _dockFailed, _popReady;
    double _dockedWidth;
    int _pvRequest;
    string? _pvShown = "";

    /// <summary>Column or window: show the column when the window is wide enough and the preview isn't popped out, and
    /// point the preview at whichever is shown.</summary>
    void ApplyPreviewLayout()
    {
        bool column = _popWin is null && ActualWidth >= ColumnMinWindow;
        PreviewCol.MinWidth = column ? 420 : 0;
        PreviewCol.Width = column ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitCol.Width = new GridLength(column ? 12 : 0);
        PreviewPanel.Visibility = Splitter.Visibility = column ? Visibility.Visible : Visibility.Collapsed;
        // the picks keep the width they were dragged to, never so wide that the preview loses its room; without the
        // preview they just fill the window
        FormCol.MaxWidth = column ? FormMax : double.PositiveInfinity;
        FormCol.Width = column ? new GridLength(Math.Clamp(_settings.FormWidth ?? FormDefault, FormMin, FormMax)) : new GridLength(1, GridUnitType.Star);
        PopButton.Content = _popWin is not null ? "Dock preview" : column ? "Pop out preview" : "Preview window";
        if (column && _dockHost is null && !_dockFailed) { InitDock(); return; }
        var host = _popWin is not null ? (_popReady ? _popWin.Host : null) : column ? _dockHost : null;
        if (host == _pv) return;
        if (_pv == _dockHost) _dockHost?.ShowMessage("");      // the hidden column lets its scene go
        _pv = host;
        _pvShown = "";
        UpdatePreview();
    }

    async void InitDock()
    {
        try
        {
            var host = new PreviewHost(PreviewView);
            await host.InitAsync();
            Hook(host);
            _dockHost = host;
        }
        catch (Exception)
        {
            _dockFailed = true;
            PreviewView.Visibility = Visibility.Collapsed;
            PreviewNote.Visibility = Visibility.Visible;
            PreviewNote.Text = "the 3d preview needs microsoft edge webview2 (part of windows 10 and 11). open in browser still works.";
        }
        ApplyPreviewLayout();
    }

    void Hook(PreviewHost host)
    {
        host.Ready += () => { if (host == _pv) SetPreviewStatus(_pvLabel); };
        host.PartsPicked += PartsFromPage;
    }

    void SetPreviewStatus(string text)
    {
        PreviewStatus.Text = text;
        if (_popWin is not null) _popWin.Status = text;
    }

    /// <summary>A part button clicked in the preview switches that slot on or off here (it keeps its helmet).</summary>
    void PartsFromPage(int p)
    {
        for (int i = 0; i < 5; i++) if (_src[i] is not null) _off[i] = (p & (int)PartPicks.Each[i]) == 0;
        RefreshSlots();
        UpdateParts();
        SuggestOutput();
    }

    // the divider: the picks between FormMin and whatever leaves the preview PreviewMin; it can't push the preview out
    const double FormMin = 660, FormDefault = 940, PreviewMin = 420;

    /// <summary>A narrow picks column: each filter bar's makes and team go under its search box (and wrap).</summary>
    void ApplyFormLayout()
    {
        bool narrow = FormCol.ActualWidth > 0 && FormCol.ActualWidth < 900;
        foreach (var right in new[] { TargetFilterRight, DonorFilterRight })
        {
            System.Windows.Controls.Grid.SetColumn(right, narrow ? 0 : 1);
            System.Windows.Controls.Grid.SetRow(right, narrow ? 1 : 0);
            System.Windows.Controls.Grid.SetColumnSpan(right, narrow ? 2 : 1);
            right.Margin = narrow ? new Thickness(-10, 8, 0, 0) : new Thickness(0);
        }
    }

    /// <summary>The picks fill the window's height: the log takes whatever is left, shrinking down to a few lines (it
    /// scrolls inside) before the picks themselves have to scroll, so it never runs past the bottom of the window.</summary>
    void FitForm()
    {
        var rows = FormGrid.RowDefinitions;
        double need = 0;
        for (int i = 0; i < rows.Count - 1; i++) need += rows[i].ActualHeight;
        need += rows[^1].MinHeight;
        double fit = Math.Max(need, FormScroll.ViewportHeight - FormGrid.Margin.Top - FormGrid.Margin.Bottom);
        if (double.IsNaN(FormGrid.Height) || Math.Abs(FormGrid.Height - fit) > 0.5) FormGrid.Height = fit;
    }
    double FormMax => Math.Max(FormMin, (Body.ActualWidth > 0 ? Body.ActualWidth : ActualWidth - 16) - 12 - PreviewMin - 30);

    void Splitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // keep both sides in range while dragging (and the preview a star column, so it follows window resizes)
        if (FormCol.ActualWidth > FormMax || FormCol.ActualWidth < FormMin)
            FormCol.Width = new GridLength(Math.Clamp(FormCol.ActualWidth, FormMin, FormMax));
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
    }

    void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var w = Math.Clamp(Math.Round(FormCol.ActualWidth), FormMin, FormMax);
        FormCol.Width = new GridLength(w);
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
        _settings.FormWidth = w;
        _settings.Save();
    }

    /// <summary>Double-click the divider: back to the standard split.</summary>
    void Splitter_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _settings.FormWidth = null;
        _settings.Save();
        FormCol.Width = new GridLength(Math.Clamp(FormDefault, FormMin, FormMax));
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
    }

    /// <summary>Pop the preview out into its own (resizable) window, or put it back.</summary>
    async void Pop_Click(object sender, RoutedEventArgs e)
    {
        if (_popWin is not null) { _popWin.Close(); return; }
        var w = new PreviewWindow();
        bool wasColumn = PreviewPanel.Visibility == Visibility.Visible;
        _popWin = w;
        _popReady = false;
        w.Closed += (_, _) =>
        {
            if (_popWin != w) return;
            _popWin = null;
            _popReady = false;
            if (wasColumn && WindowState == WindowState.Normal) Width = Math.Max(Width, _dockedWidth);
            ApplyPreviewLayout();
        };
        if (wasColumn && WindowState == WindowState.Normal)
        {
            // the picks keep their side; the preview window goes beside them when there is room
            _dockedWidth = ActualWidth;
            Width = MinWidth;
        }
        var work = SystemParameters.WorkArea;
        if (Left + Width + 10 + 700 <= work.Right) { w.Left = Left + Width + 10; w.Top = Top; w.Width = Math.Min(1100, work.Right - w.Left - 8); w.Height = Height; }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyPreviewLayout();
        w.Show();
        try { await w.Host.InitAsync(); }
        catch (Exception ex) { Log("The preview window could not be opened: " + ex.Message, Res("LogError")); w.Close(); return; }
        if (_popWin != w) return;
        Hook(w.Host);
        _popReady = true;
        ApplyPreviewLayout();
    }

    string _pvLabel = "";

    /// <summary>Show the picks: the swap (before / after), or one driver's own helmet; a parts change is sent to the page
    /// shown (no rebuild).</summary>
    async void UpdatePreview()
    {
        if (_pv is null || _game is null) return;
        Driver? t = _target?.Driver, d = _donor?.Driver;
        // the page holds every slot's helmet; switching a part on or off is a message, another helmet a new page
        var all = AllPicks;
        var key = t is null ? $"own|{d?.Folder}" : $"{t.Folder}|{string.Join(",", PartPicks.Each.Select(p => all[p]?.Folder ?? "-"))}";
        var parts = Parts;
        if (key == _pvShown) { _pv.SetParts((int)parts); return; }
        _pvShown = key;
        int request = ++_pvRequest;
        if (t is null && d is null)
        {
            _pvLabel = ""; SetPreviewStatus("");
            _pv.ShowMessage("pick a driver to change and whose helmet to give them");
            return;
        }
        var game = _game;
        string Who(Driver x) => (x.Name + (x.YearTag == "" ? "" : " " + x.YearTag)).ToLowerInvariant();
        _pvLabel = t is null ? $"{Who(d!)}'s own helmet"
            : all.IsEmpty ? $"{Who(t)}'s own helmet"
            : all.Single is { } one ? $"{Who(one)}'s helmet on {Who(t)}"
            : $"helmet parts from {string.Join(" + ", all.Donors.Select(x => Short(x).ToLowerInvariant()))} on {Who(t)}";
        SetPreviewStatus("loading " + _pvLabel + "…");
        _pv.ShowBusy("building the preview…");
        try
        {
            var html = await Task.Run(() => t is null ? HelmetPage.Page(game, d!, (PartPicks?)null, 0, embedded: true, fontCss: FontCss.Value)
                                                      : HelmetPage.Page(game, t, all.IsEmpty ? null : all, parts, embedded: true, fontCss: FontCss.Value));
            if (request != _pvRequest) return;      // the picks changed meanwhile
            _pv.ShowHtml(html, busy: "building the preview…");
            _pv.SetParts((int)Parts);
        }
        catch (Exception ex)
        {
            if (request != _pvRequest) return;
            SetPreviewStatus("");
            _pv.HideBusy();
            _pv.ShowMessage("the preview could not be made: " + ex.Message);
        }
    }

    /// <summary>The interactive 3D page (before / after / side by side, the parts switchable on the page), in the browser.</summary>
    async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) { Log("Pick the F1 25 game folder first.", Res("LogWarn")); return; }
        if (_target is not { } t) { Log("Pick the driver to change.", Res("LogWarn")); return; }
        var all = AllPicks;
        if (all.IsEmpty) { Log("Pick whose helmet to use.", Res("LogWarn")); return; }
        var game = _game; Driver target = t.Driver; var parts = Parts;
        Log($"Preview: {(all.Single is { } one ? one.Name + "'s helmet" : string.Join(" + ", all.Donors.Select(Short)))} -> {target.Name} (nothing is saved)", Res("LogText"));
        PreviewButton.IsEnabled = false;
        try
        {
            var path = await Task.Run(() =>
            {
                var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "HelmetSwapper")).FullName;
                var name = string.Concat(HelmetSwapper.SwapName(target, all).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var file = Path.Combine(dir, $"Preview - {name}.html");
                File.WriteAllText(file, HelmetPage.Page(game, target, all, parts, fontCss: FontCss.Value), new System.Text.UTF8Encoding(false));
                return file;
            });
            Log("Preview opened in your browser. Build to save it.", Res("LogOk"));
            if (Environment.GetEnvironmentVariable("HELMETSWAP_NO_BROWSER") != "1")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("Preview failed: " + ex.Message, Res("LogError")); }
        finally { PreviewButton.IsEnabled = true; }
    }

    /// <summary>The helmet as a 3D model: what the after view shows (the driver's own helmet when no helmet is picked to
    /// give), FBX + OBJ with full-resolution textures and UV layouts, into "&lt;name&gt; - 3D Model" in a folder the user picks.</summary>
    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) { Log("Pick the F1 25 game folder first.", Res("LogWarn")); return; }
        if (_target is not { } t) { Log("Pick the driver whose helmet to export.", Res("LogWarn")); return; }
        Driver target = t.Driver;
        PartPicks? picks = Picks;
        if (picks.IsEmpty || HelmetSwapper.PartsProblem(target, picks) is not null) picks = null;   // nothing valid picked: their own
        var name = picks is null ? $"{target.Label}{(target.YearTag == "" ? "" : " " + target.YearTag)}{(target.IsCareerDesign ? "" : " Helmet")}" : HelmetSwapper.SwapName(target, picks);
        var dlg = new OpenFolderDialog
        {
            Title = "Where to save the 3D model (a folder is made inside)",
            InitialDirectory = Directory.Exists(_settings.OutDir) ? _settings.OutDir : DefaultOutDir,
        };
        // (scripted tests name the folder instead of the dialog)
        var parent = Environment.GetEnvironmentVariable("HELMETSWAP_EXPORT_DIR");
        if (string.IsNullOrEmpty(parent)) { if (dlg.ShowDialog(this) != true) return; parent = dlg.FolderName; }
        var dir = Path.Combine(parent, $"{name} - 3D Model");
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HELMETSWAP_EXPORT_DIR")) &&
            MessageBox.Show(this, $"\"{Path.GetFileName(dir)}\" already exists. Replace its files?", "Helmet Swapper", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var game = _game;
        _log.Clear();
        Log($"3D model: {(picks is null ? $"{target.Name}'s own helmet" : $"{target.Name} with {string.Join(", ", PartPicks.Each.Where(p => picks[p] is not null).Select(p => $"{HelmetSwapper.PartsLabel(p).ToLowerInvariant()} from {picks[p]!.Name}"))}")}", Res("LogText"));
        var log = new SwapLog();
        log.Line += (s, w) => Dispatcher.Invoke(() => Log((w ? "Warning: " : "") + s, w ? Res("LogWarn") : Res("LogMuted")));
        SetBusy(true);
        ShowButton.Visibility = Visibility.Collapsed;
        StatusPill.Visibility = Visibility.Collapsed;
        try
        {
            var files = await Task.Run(() => HelmetExport.Write(game, target, picks, dir, log));
            _lastOut = files.FirstOrDefault(f => f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
            Log($"Done. Saved {files.Count} files in \"{Path.GetFileName(dir)}\": {HelmetExport.Stem(target)}_helmet.fbx and .obj, textures/ (colour, normal and surface maps) and UVs/ (the UV layouts).", Res("LogOk"));
            Log("Each part is its own material (a texture set in Painter) with the same name on every helmet (helmet, helmet_wing, visor, visor_attachments...), so a Painter project can swap to another exported helmet and keep its layers. The normal maps are DirectX style (green = +v).", Res("LogMuted"));
            Status($"Exported · {files.Count} files", "Teal");
            ShowButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { Log("Export failed: " + ex.Message, Res("LogError")); Status("Export failed", "Red"); }
        finally { SetBusy(false); }
    }

    void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOut is null || !File.Exists(_lastOut)) { Log("The file is no longer there.", Res("LogWarn")); return; }
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastOut}\""); }
        catch (Exception ex) { Log("Could not open the folder: " + ex.Message, Res("LogWarn")); }
    }

    void Status(string text, string brush)
    {
        StatusText.Text = text;
        StatusPill.Background = Res(brush);
        StatusText.Foreground = brush == "Red" ? Res("Paper") : Res("Ink");
        StatusPill.Visibility = Visibility.Visible;
    }

    void SetBusy(bool b)
    {
        BuildButton.IsEnabled = !b;
        ExportButton.IsEnabled = !b;
        Busy.Visibility = b ? Visibility.Visible : Visibility.Collapsed;
        TargetBox.IsEnabled = !b;
        DonorBox.IsEnabled = !b;
        WaitCursor(b);
    }

    static void WaitCursor(bool wait) => System.Windows.Input.Mouse.OverrideCursor = wait ? System.Windows.Input.Cursors.Wait : null;
}
