using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Swapper.Core;
using Swapper.Core.TeamSimplified;
using WheelSwap.Core;

namespace Swapper.App;

public sealed record EntryTag(string Text, Brush Brush);

/// <summary>A chassis option of the template (ticked = it gets the new steering wheel).</summary>
public sealed class EntryItem : INotifyPropertyChanged
{
    bool _checked;
    public required CarEntry Entry { get; init; }
    /// <summary>The Simplified Studio project the chassis is in (null for a .serps).</summary>
    public SimplifiedProject? Project { get; init; }
    public string Label => Entry.Describe + (Entry.Problem is null ? "" : "  (not supported)");
    public string Title => Entry.Title;
    public string FileName => Entry.FileName;
    public IEnumerable<EntryTag> Tags
    {
        get
        {
            if (Entry.Swapped) yield return new("Swapped chassis", MainWindow.Res("Magenta"));
            if (Entry.PreviousSwap is not null) yield return new("Wheel already swapped", MainWindow.Res("Amber"));
            if (Entry.Problem is not null) yield return new("Not supported", MainWindow.Res("Red"));
            if (Project is not null && SimplifiedProject.LegacyFolder(Entry.ZipPath) is { } f) yield return new(f, MainWindow.Res("LineSoft"));
            else if (Project is not null && !Project.IsOn(Entry.ZipPath)) yield return new("Switched off", MainWindow.Res("LineSoft"));
        }
    }
    public string? Tooltip => Entry.Problem ?? Entry.ZipPath;
    public bool IsEnabled => Entry.Problem is null;
    public bool IsChecked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Title;
}

/// <summary>The preview list's first line while a new chassis is picked: that option as it will be.</summary>
public sealed record NewOptionItem(string Title)
{
    public override string ToString() => Title;
}

public sealed record LogLine(string Text, Brush Brush);

/// <summary>The three things a template can be given.</summary>
public enum Pick { Chassis, Wheel, Suits, Rims, Engine }

/// <summary>A donor chip: team colour bar, name and a lowercase note (season, special livery).</summary>
public sealed class Chip : INotifyPropertyChanged
{
    bool _enabled = true, _showYear, _used;
    string? _tooltip;
    public required Pick Pick { get; init; }
    /// <summary>What the pick is: a car file stem, a wheel donor id, a suit set name.</summary>
    public required string Key { get; init; }
    /// <summary>The team colour key (TeamBrush).</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string TeamName { get; init; }
    public required int Year { get; init; }
    public string Series { get; init; } = "F1";
    public string Note { get; init; } = "";
    public object? Source { get; init; }
    public string Name => Title + (_showYear ? " · " + Year : "");
    public string Make => Note;
    public string Label => $"{Title}, {Year}{(Series == "F1" ? "" : ", " + Series)}{(Pick == Pick.Engine ? ", " + Note : "")}";   // an engine: the car it's from
    public bool ShowYear { get => _showYear; set { if (_showYear == value) return; _showYear = value; Changed(nameof(Name)); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(nameof(Enabled)); } }
    public bool Used { get => _used; set { if (_used == value) return; _used = value; Changed(nameof(Used)); } }
    public string? Tooltip { get => _tooltip; set { _tooltip = value; Changed(nameof(Tooltip)); } }
    public string Haystack => $"{Title} {TeamName} {Year} {Series} {Note}".ToLowerInvariant();
    void Changed(string p) => PropertyChanged?.Invoke(this, new(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

sealed class Settings
{
    public string? GameDir { get; set; }
    public string? LastTemplateDir { get; set; }
    public string? Chassis { get; set; }
    public string? Wheel { get; set; }
    public string? Suits { get; set; }
    public string? Rims { get; set; }
    public string? Engine { get; set; }
    public bool SaveReport { get; set; }
    /// <summary>Hover previews in the lists (made in the background the first time); on unless turned off.</summary>
    public bool HoverPictures { get; set; } = true;
    /// <summary>Width of the picks column (the preview takes the rest), as dragged.</summary>
    public double? FormWidth { get; set; }

    // (scripted tests point SWAPPER_SETTINGS at a file of their own, so they never touch the user's)
    static string PathOf => Environment.GetEnvironmentVariable("SWAPPER_SETTINGS") is { Length: > 0 } test ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RM Designs Studio", "Swapper", "settings.json");

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
    readonly ObservableCollection<EntryItem> _entries = [];
    readonly ObservableCollection<LogLine> _log = [];
    SerpsTemplate? _template;
    SimplifiedProject? _project;
    TemplateTeams.Team? _team;
    /// <summary>A My Team template's own driverwear part (the new suits are built onto it), or null.</summary>
    string? _myTeamWear;
    GameData? _game;
    string? _loadedDir, _lastOut, _reportPath;
    bool _outEdited, _refreshing;

    // the picks: one donor per slot (null = the template's own); the slot aimed at is the one the list picks for
    readonly Chip?[] _pick = new Chip?[5];
    Pick _aim = Pick.Chassis;
    readonly List<Chip>[] _chips = [[], [], [], [], []];
    readonly ToggleButton[] _slotBtn = new ToggleButton[5];
    readonly TextBlock[] _slotWho = new TextBlock[5];
    readonly Tracked[] _slotName = new Tracked[5];
    readonly Button[] _slotClear = new Button[5];
    static readonly string[] SlotNames = ["Chassis", "Steering wheel", "Suits", "Rims", "Engine sound"];
    static readonly string[] ListNames = ["Chassis from", "Steering wheel from", "Suits from", "Rims from", "Engine sound from"];
    static readonly string[] SlotTips =
    [
        "The car itself: another team's 2026 car or a special livery, as a new chassis option in the template",
        "The steering wheel, paddles and the driver's hand animation, onto the ticked chassis options",
        "The drivers' suits, gloves, boots and caps: any team's, from 2024 to 2026, F1 or F2",
        "The road wheels' rims: any car's of the same season (special liveries too), onto the new chassis or the ticked chassis options",
        "The engine sound: any power unit of the 2026 or 2025 cars (each car's sounds a little different), onto the new chassis or the ticked chassis options",
    ];
    /// <summary>The slots in the order they are shown.</summary>
    static readonly Pick[] SlotOrder = [Pick.Chassis, Pick.Wheel, Pick.Rims, Pick.Engine, Pick.Suits];

    // each list keeps its own tabs and filter
    sealed class ListState { public int? Year = GameData.CurrentSeason; public string? Series = "F1"; public string Text = ""; public string? Team; }
    readonly ListState[] _state = [new(), new(), new(), new(), new()];

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>The app version (from the project's Version), e.g. "0.1.0" -> "v0.1".</summary>
    internal static string AppVersion
    {
        get
        {
            var v = (System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?").Split('+')[0];
            return v.EndsWith(".0") && v.Count(c => c == '.') == 2 ? v[..^2] : v;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        Studio.Ui.StudioChrome.Apply(this, 64);   // borderless: the brand band is the title bar
        var room = SystemParameters.WorkArea.Height - 8;
        Height = Math.Max(MinHeight, Math.Min(1180, room));
        if (room < 1250) DonorScroll.MaxHeight = room < 1000 ? 108 : 144;
        VersionText.Text = "v" + AppVersion;
        EntryList.ItemsSource = _entries;
        LogList.ItemsSource = _log;
        // the saved folder while it is still there, else found (Team Simplified's detector: Steam libraries from the
        // registry, EA / Origin installs), else the older search
        GameBox.Text = Swapper.Core.TeamSimplified.GameDirDetector.DetectIfUnset(_settings.GameDir) ?? _settings.GameDir ?? GameData.DetectGameDir() ?? "";
        ReportBox.IsChecked = _settings.SaveReport;
        PicturesBox.IsChecked = _settings.HoverPictures;
        OutBox.PreviewKeyDown += (_, _) => _outEdited = true;
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(IsTemplateFile) is { } f)
                LoadTemplate(f);
        };
        Log("Open a .serps template, then pick its new chassis, steering wheel, rims, engine sound or suits (any of the five) and Build.", Res("LogText"));
        var work = SystemParameters.WorkArea;
        if (work.Width >= ColumnMinWindow + 50) Width = Math.Min(work.Width - 8, 1720);
        // scripted tests (tests/gui_smoke.ps1): the window opens behind every other window and never takes the focus, so a
        // test can run (and capture it) while the user works
        if (Environment.GetEnvironmentVariable("SWAPPER_TEST_BEHIND") == "1")
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = work.Left; Top = work.Top; ShowActivated = false;
            SourceInitialized += (_, _) => SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, 1 /* HWND_BOTTOM */, 0, 0, 0, 0,
                                                        0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */);
        }
        Loaded += (_, _) => { ApplyPreviewLayout(); ApplyFormLayout(); LoadGame(); };
        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(IsTemplateFile);
        if (arg is not null && File.Exists(arg)) Loaded += (_, _) => LoadTemplate(arg);
        SizeChanged += (_, _) => { if (IsLoaded) { ApplyPreviewLayout(); ApplyFormLayout(); } };
        FormGrid.LayoutUpdated += (_, _) => FitForm();

        BuildSlots();
        DonorBox.ItemContainerGenerator.StatusChanged += (_, _) => AttachTips();
        Closed += (_, _) => _pictures.Close();   // the hidden viewer of the hover pictures goes with the app
        // a pinned hover-previews note closes on a click elsewhere, and goes when the window moves or loses focus
        PreviewMouseDown += ClosePreviewsNote;
        Deactivated += (_, _) => { _previewsPinned = false; NoteShown(false); };
        LocationChanged += (_, _) => { _previewsPinned = false; NoteShown(false, now: true); };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    void Log(string s, Brush b)
    {
        _log.Add(new LogLine(s, b));
        LogList.ScrollIntoView(_log[^1]);
    }

    // ---------------------------------------------------------------- game folder and what it offers

    void Detect_Click(object sender, RoutedEventArgs e)
    {
        var found = Swapper.Core.TeamSimplified.GameDirDetector.Detect();
        var d = found?.Path ?? GameData.DetectGameDir();
        if (d is null) Log("Could not find F1 25 in your Steam libraries or EA installs. Use Browse… to pick the game folder.", Res("LogWarn"));
        else { GameBox.Text = d; Log($"Found F1 25{(found is null ? "" : $" ({found.Source})")}: {d}", Res("LogText")); LoadGame(); }
    }

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the F1 25 game folder" };
        if (dlg.ShowDialog(this) == true) { GameBox.Text = dlg.FolderName; LoadGame(); }
    }

    void GameBox_LostFocus(object sender, RoutedEventArgs e) => LoadGame();

    /// <summary>Read what the game offers: its cars (with the special liveries), the wheels and the suit sets.</summary>
    async void LoadGame()
    {
        var dir = GameBox.Text.Trim();
        if (dir.Length == 0 || dir == _loadedDir) return;
        _loadedDir = dir;
        try
        {
            SetBusy(true);
            var game = new GameData(dir);
            var (cars, suits, engines) = await Task.Run(() => (Catalog.Chassis(game), Catalog.Suits(game), EngineSwap.Catalog(game)));
            if (cars.Count == 0) throw new InvalidDataException("No 2026 cars were found in this game folder.");
            _game = game;
            _chips[(int)Pick.Chassis] = cars.Select(c => new Chip
            {
                Pick = Pick.Chassis, Key = c.Id, Id = c.ColorKey, Title = c.Label, TeamName = c.TeamName, Year = c.Season,
                Note = c.Livery is null ? "" : "special", Source = c,
            }).ToList();
            _chips[(int)Pick.Wheel] = Catalog.Wheels.Select(t => new Chip
            {
                Pick = Pick.Wheel, Key = t.Id, Id = t.Id, Title = t.Name, TeamName = t.Name, Year = t.Season, Source = t,
            }).ToList();
            _chips[(int)Pick.Rims] = cars.Select(c => new Chip
            {
                Pick = Pick.Rims, Key = c.Id, Id = c.ColorKey, Title = c.Label, TeamName = c.TeamName, Year = c.Season,
                Note = c.Livery is null ? "" : "special", Source = c,
            }).ToList();
            // engine sounds: the maker's name, the car it comes from as the note (each car's sounds a little different)
            _chips[(int)Pick.Engine] = engines.Select(en => new Chip
            {
                Pick = Pick.Engine, Key = en.Id, Id = en.ColorKey, Title = en.Maker, TeamName = en.Maker, Year = en.Season,
                Note = en.Source.ToLowerInvariant(), Source = en,
            }).ToList();
            _chips[(int)Pick.Suits] = suits.Select(s => new Chip
            {
                Pick = Pick.Suits, Key = s.Name, Id = s.ColorKey, Title = s.Label, TeamName = s.Team, Year = s.Year, Series = s.Series,
                Note = s.Maker?.ToLowerInvariant() ?? (s.Special is null ? "" : "special"), Source = s,
            }).ToList();
            // (the app opens with nothing picked: every slot on the template's own)
            UpdateDonors();
            RefreshSlots();
            ShowList(resetTeam: true);
            PrewarmPictures();
            // scripted tests: render a few hover pictures and log where they are (a tooltip can't be hovered behind other windows)
            if (Environment.GetEnvironmentVariable("SWAPPER_TEST_PICTURES") is { Length: > 0 } ids)
                foreach (var part in ids.Split(';'))
                {
                    var (kind, key) = (Enum.Parse<Pick>(part.Split(':')[0]), part.Split(':')[1]);
                    try { Log($"Picture {kind} {key}: {await _pictures.For(game, kind, _chips[(int)kind].First(c => c.Key == key).Source!)}", Res("LogMuted")); }
                    catch (Exception ex) { Log($"Picture {kind} {key} failed: {ex.Message}", Res("LogWarn")); }
                }
            Log($"F1 25: {cars.Count} cars (with {cars.Count(c => c.Livery is not null)} special liveries), {Catalog.Wheels.Count} steering wheels, {engines.Count} engine sounds and {suits.Count} suit sets.", Res("LogText"));
            SuggestOutput();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            _game = null; _loadedDir = null;
            foreach (var l in _chips) l.Clear();
            DonorBox.ItemsSource = null;
            Log(ex.Message, Res("LogError"));
        }
        finally { SetBusy(false); }
    }

    // ---------------------------------------------------------------- the template

    static bool IsTemplateFile(string f) => f.EndsWith(".serps", StringComparison.OrdinalIgnoreCase) || SimplifiedProject.IsProject(f);   // a project is answered with a note

    void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SERPs template (*.serps)|*.serps",
            InitialDirectory = _settings.LastTemplateDir ?? "",
        };
        if (dlg.ShowDialog(this) == true) LoadTemplate(dlg.FileName);
    }

    void LoadTemplate(string path)
    {
        try
        {
            WaitCursor(true);
            // Simplified Studio projects stay Simplified Studio's (Team Simplified's wish): it exports them as a .serps
            if (SimplifiedProject.IsProject(path)) { Log(TemplateOutput.ProjectNote, Res("LogWarn")); return; }
            SimplifiedProject? project = null;
            var tpl = new SerpsTemplate(path);
            WheelBuild.MarkUnsupported(tpl, anySeason: true);
            _template = tpl;
            _project = project;
            _team = project is null ? TemplateTeams.Of(tpl) : TemplateTeams.Of(project);
            _myTeamWear = _team is { SuitSet: null } ? MyTeamSuitSwap.TemplatePart(tpl) : null;
            TemplateBox.Text = path;
            _settings.LastTemplateDir = Path.GetDirectoryName(path);
            _settings.Save();
            _entries.Clear();
            // ticked: the chassis options in use (a project's switched-on ones; all of a template's)
            bool anyOn = project is not null && tpl.CarEntries.Any(c => c.Problem is null && project.IsOn(c.ZipPath));
            foreach (var c in tpl.CarEntries)
                _entries.Add(new EntryItem { Entry = c, Project = project, IsChecked = c.Problem is null && (!anyOn || project!.IsOn(c.ZipPath)) });
            EntryHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EntryHint.Text = "no chassis options with a steering wheel were found in this template.";
            var kind = project is null ? "template" : "project";
            TemplateHint.Text = _team is null ? "team not recognised (no known shader package): only the steering wheel can be swapped"
                : $"{_team.Name.ToLowerInvariant()} {kind}" + (_team.SuitSet is { } wear ? $" · its drivers wear {wear}" : _myTeamWear is not null ? " · its drivers wear the my team suit" : "");
            if (project is not null && project.Name.Length > 0)
                TemplateHint.Text = $"{project.Name.ToLowerInvariant()}{(project.Author.Length > 0 ? " by " + project.Author.ToLowerInvariant() : "")} · " + TemplateHint.Text;
            _settingEntry = true;
            PreviewEntry.ItemsSource = _entries.ToList();
            PreviewEntry.SelectedIndex = _entries.Count == 0 ? -1 : project is null ? 0 : Math.Max(0, project.Preferred(_entries.Select(x => x.Entry.ZipPath).ToList()));
            _previewListFor = null;
            _settingEntry = false;
            Log($"{(project is null ? "Template" : "Simplified project")} {tpl.Name}: {(_team?.Name ?? "unknown team")}, {_entries.Count} chassis option{(_entries.Count == 1 ? "" : "s")}" +
                (project is null ? "." : $", {_entries.Count(x => project.IsOn(x.Entry.ZipPath))} switched on. It is saved as a project again (.simplified)."), Res("LogText"));
            _outEdited = false;
            _state[(int)Pick.Chassis].Year = TemplateSeason;
            UpdateDonors();
            RefreshSlots();
            ShowList();
            SuggestOutput();
            UpdatePreview();
        }
        catch (Exception ex) { Log("Could not read the template: " + ex.Message, Res("LogError")); }
        finally { WaitCursor(false); }
    }

    bool _settingEntry;

    void PreviewEntry_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingEntry) UpdatePreview();
    }

    // ---------------------------------------------------------------- the three slots

    void BuildSlots()
    {
        foreach (var slot in SlotOrder)
        {
            int i = (int)slot, k = i;
            var name = _slotName[i] = new Tracked { Text = SlotNames[i], FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.Bold, FontSize = 10.5, Tracking = 0.14, Foreground = Res("Ink") };
            _slotWho[i] = new TextBlock { FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0), Foreground = Res("Muted"),
                                          TextTrimming = TextTrimming.CharacterEllipsis };   // a long pick shortens, the slot keeps its width
            var body = new StackPanel(); body.Children.Add(name); body.Children.Add(_slotWho[i]);
            _slotBtn[i] = new ToggleButton { Style = (Style)FindResource("PartSlot"), Content = body };
            ToolTipService.SetShowOnDisabled(_slotBtn[i], true);
            System.Windows.Automation.AutomationProperties.SetName(_slotBtn[i], SlotNames[i] + " slot");
            // a slot is aimed like a tab: checking one (click, keyboard or UI Automation) aims it; the aimed one stays checked
            _slotBtn[i].Checked += (_, _) => { if (!_settingSlots) Aim((Pick)k); };
            _slotBtn[i].Unchecked += (_, _) => { if (!_settingSlots && _aim == (Pick)k) _slotBtn[k].IsChecked = true; };
            _slotClear[i] = new Button { Style = (Style)FindResource("SlotClear"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                                         Margin = new Thickness(0, 3, 3, 0), ToolTip = "Back to the template's own" };
            System.Windows.Automation.AutomationProperties.SetName(_slotClear[i], SlotNames[i] + ": back to the template's own");
            _slotClear[i].Click += (_, _) => { _pick[k] = null; Remember(); PicksChanged(); };
            var cell = new Grid { Margin = new Thickness(0, 3, 8, 3), MinWidth = 0 };
            cell.Children.Add(_slotBtn[i]); cell.Children.Add(_slotClear[i]);
            SlotPanel.Children.Add(cell);
        }
        // every slot as wide as the longest title needs (beside the room its × keeps), so they line up as they wrap
        double title = 0;
        foreach (var n in _slotName) { n.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); title = Math.Max(title, n.DesiredSize.Width); }
        SlotPanel.ItemWidth = Math.Max(150, Math.Ceiling(title + 11 + 26 + 2 + 8));
        RefreshSlots();
    }

    /// <summary>Why a slot can't be picked for this template (null = it can).</summary>
    string? Locked(Pick p) => p switch
    {
        Pick.Chassis when _template is not null && _team is null => "this template's team isn't recognised, so there is no slot to put a chassis in",
        Pick.Chassis when _team is { ChassisSwapWorks: false } => $"not for {_team.Name}: the game doesn't load a swapped chassis in its slots",
        Pick.Suits when _template is not null && _team is null => "this template's team isn't recognised, so it isn't known whose suits to replace",
        Pick.Suits when _team is { SuitSwapWorks: false } => $"not for {_team.Name}: its drivers wear the suit picked in game",
        Pick.Suits when _team is { SuitSet: null } && _myTeamWear is null => "this my team template has no my team driverwear part to dress in new suits",
        Pick.Engine when _team is { IsMyTeam: true } => $"not for {_team.Name}: its engine sound comes with the engine supplier picked in game",
        _ => null,
    };

    void Aim(Pick p)
    {
        _aim = p;
        RefreshSlots();
        ShowList(resetTeam: true);
        UpdatePreview();
    }

    bool _settingSlots;

    void RefreshSlots()
    {
        if (_slotBtn[0] is null) return;
        _settingSlots = true;
        try { ShowSlots(); }
        finally { _settingSlots = false; }
    }

    void ShowSlots()
    {
        for (int i = 0; i < _pick.Length; i++)
        {
            var locked = Locked((Pick)i);
            if (locked is not null) _pick[i] = null;
            _slotBtn[i].IsChecked = (int)_aim == i;
            _slotBtn[i].IsEnabled = locked is null;
            _slotWho[i].Text = locked is not null ? "not for this template" : _pick[i] is { } c ? (i == (int)Pick.Chassis ? "+ " : "") + Short(c) : _template is null ? "nothing picked" : "template's own";
            bool on = locked is null && _pick[i] is not null;
            _slotWho[i].Foreground = on ? Res("Purple") : Res("Muted");
            _slotWho[i].FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
            _slotClear[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _slotBtn[i].ToolTip = locked ?? SlotTips[i] + " · click to pick it in the list below";
            // a new chassis no steering wheel fits (another season, or the older rig of the Cadillac Miami / Silverstone 26):
            // the wheel slot says so, in red
            if (i == (int)Pick.Wheel && locked is null && _pick[(int)Pick.Chassis]?.Source is ChassisDonor newCar && ChassisView.KnownNoWheelReason(newCar) is { } why)
            {
                _slotWho[i].Text = "can't go on this car";
                _slotWho[i].Foreground = Res("Red");
                _slotWho[i].FontWeight = FontWeights.Bold;
                _slotBtn[i].ToolTip = $"{why[0].ToString().ToUpperInvariant()}{why[1..]}. The new chassis keeps its own wheel" +
                                      (_pick[i] is null ? "." : $"; clear the {Short(_pick[i]!)} wheel (×) to build it.");
            }
        }
        if (Locked(_aim) is not null) { _aim = Pick.Wheel; for (int i = 0; i < _pick.Length; i++) _slotBtn[i].IsChecked = i == (int)Pick.Wheel; }
        SlotHint.Text = _pick.All(c => c is null)
            ? "click a slot, then pick from the list below · what you leave on template's own stays as the template has it"
            : "the new template gets: " + string.Join(" · ", SlotOrder.Select(x => (int)x).Where(i => _pick[i] is not null)
                                                                     .Select(i => i == (int)Pick.Chassis
                                                                         ? $"a new chassis option ({Short(_pick[i]!).ToLowerInvariant()}), beside yours"
                                                                         : i == (int)Pick.Wheel && _pick[(int)Pick.Chassis]?.Source is ChassisDonor hc && ChassisView.KnownNoWheelReason(hc) is not null
                                                                         ? $"no new steering wheel ({Short(_pick[i]!).ToLowerInvariant()} can't go on this car)"
                                                                         : $"{SlotNames[i].ToLowerInvariant()} from {Short(_pick[i]!).ToLowerInvariant()}"));
        // the chassis picked shows as the option it will be, under the template's own
        NewOptionRow.Visibility = _pick[(int)Pick.Chassis] is null ? Visibility.Collapsed : Visibility.Visible;
        bool newChassis = _pick[(int)Pick.Chassis] is not null;
        EntryList.IsEnabled = !newChassis;
        EntryList.Opacity = newChassis ? 0.45 : 1;
        RefreshPreviewList();
        var rigBad = _pick[(int)Pick.Chassis]?.Source is ChassisDonor rc && _pick[(int)Pick.Wheel] is { } rw && ChassisView.KnownWheelProblem(rc, rw.Key) is not null;
        var onParts = new[] { _pick[(int)Pick.Wheel] is null ? null : "wheel", _pick[(int)Pick.Rims] is null ? null : "rims",
                              _pick[(int)Pick.Engine] is null ? null : "engine sound" }.OfType<string>().ToList();
        var onCar = onParts.Count <= 1 ? string.Concat(onParts) : string.Join(", ", onParts[..^1]) + " and " + onParts[^1];
        bool one = onParts.Count == 1 && onParts[0] != "rims";
        EntryNote.Text = newChassis ? (onCar.Length == 0 ? "the new chassis is added as one more option · these are kept as they are"
                                       : rigBad ? "this car's older steering rig can't take the new wheel · clear the wheel (×) to build the chassis"
                                       : $"the new {onCar} go{(one ? "es" : "")} on the new chassis option · these are kept as they are")
                                    : onCar.Length == 0 ? "tick the options a new wheel, rims or engine sound go on · the options themselves are kept as they are"
                                    : $"each ticked option gets a new section with the new {onCar} · the options themselves are kept as they are";
        if (_pick[(int)Pick.Chassis] is { } nc)
            NewOptionTitle.Text = $"Livery - {nc.Title.Replace(" · ", " ")} Chassis" + (_pick[(int)Pick.Wheel] is { } nw && !rigBad ? " - " + GameData.WheelSuffix(nw.Key) : "")
                                  + (_pick[(int)Pick.Rims]?.Source is ChassisDonor nr ? " - " + RimSwap.Suffix(nr) : "")
                                  + (_pick[(int)Pick.Engine]?.Source is EngineSound ne ? " - " + EngineSwap.Suffix(ne) : "");
        SlotHint.Foreground = Res("Purple");
    }

    string? _previewListFor;

    /// <summary>The preview list: the template's chassis options, with the new chassis option first (and shown when it is
    /// picked or changes) while a chassis is picked.</summary>
    void RefreshPreviewList()
    {
        var nc = _pick[(int)Pick.Chassis];
        var key = nc?.Key;
        if (key == _previewListFor && (nc is null) == (PreviewEntry.Items.Count == 0 || PreviewEntry.Items[0] is not NewOptionItem)) return;
        _previewListFor = key;
        _settingEntry = true;
        try
        {
            var selected = PreviewEntry.SelectedItem as EntryItem;
            var items = new List<object>();
            if (nc is not null) items.Add(new NewOptionItem("New · " + nc.Title));
            items.AddRange(_entries);
            PreviewEntry.ItemsSource = items;
            PreviewEntry.SelectedItem = nc is not null ? items[0] : selected is not null && _entries.Contains(selected) ? selected : _entries.FirstOrDefault();
        }
        finally { _settingEntry = false; }
    }

    /// <summary>The Car-mode captions: the car shown, and the wheel when it is the new one.</summary>
    void Captions(Dictionary<string, object?> car, string name, bool newWheel)
    {
        var wheel = _pick[(int)Pick.Wheel];
        car["afterCar"] = newWheel && wheel is not null ? $"{name} · {Short(wheel)} wheel" : name;
        car["beforeCar"] = $"{name} · its own wheel";
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, float[]>?> _cams = new();

    /// <summary>Give a preview car its Onboard view: the game's own camera place from the car's camera layer (a new
    /// chassis: its car file; a template option: its ERP, else its team's car file, e.g. for a My Team livery).</summary>
    static Dictionary<string, object?> WithCams(Dictionary<string, object?> car, GameData game, SerpsTemplate? tpl, CarEntry? entry,
                                                ChassisDonor? donor, TemplateTeams.Team? team)
    {
        string key = donor is not null ? donor.ErpPath : $"{tpl?.SourcePath}|{entry?.ZipPath}";
        var cams = _cams.GetOrAdd(key, _ =>
        {
            EgoEngineLibrary.Archive.Erp.ErpFile? Load(Func<byte[]> read) { try { return ErpX.Load(read()); } catch (Exception) { return null; } }
            if (donor is not null) return CarCams.Of(Load(() => File.ReadAllBytes(donor.ErpPath)));
            if (tpl is null || entry is null) return null;
            return CarCams.Of(Load(() => tpl.Get(entry.ZipPath)))
                   ?? (team?.Folders.FirstOrDefault() is { } slot ? CarCams.Of(Load(() => File.ReadAllBytes(game.TeamErp(slot, entry.Season)))) : null);
        });
        if (cams is not null) car["cams"] = cams;
        return car;
    }

    /// <summary>The build (<paramref name="after"/>) with the template's own car as it is (<paramref name="before"/>, built in
    /// the same scene) on its Before side: the viewer swaps the whole car (body, framing, wheel place, cameras).</summary>
    static Dictionary<string, object?> BeforeAfter(Dictionary<string, object?> before, Dictionary<string, object?> after, string beforeName)
    {
        var side = new Dictionary<string, object?>();
        foreach (var k in new[] { "body", "box", "wbox", "toWheel", "cams" })
            if (before.GetValueOrDefault(k) is { } v) side[k] = v;
        after["per"] = new Dictionary<string, object?> { ["before"] = side };
        after["before"] = before["before"];
        after["beforeCar"] = after["beforeName"] = $"{beforeName} · the template as it is";
        after["sides"] = true;
        return after;
    }

    /// <summary>A pick in a few words: "26 Red Bull" (wheel), "Racing Bulls · Suzuka 26" (chassis), "25 McLaren · Silverstone" (suits).</summary>
    static string Short(Chip c) => c.Pick switch
    {
        Pick.Wheel => GameData.WheelSuffix(c.Key).Replace(" Wheel", ""),
        Pick.Chassis or Pick.Rims => c.Year == GameData.CurrentSeason ? c.Title : $"{c.Title} ({c.Year})",
        Pick.Engine => EngineSwap.Suffix((EngineSound)c.Source!).Replace(" Engine", ""),
        _ => $"{c.Year % 100:00} {c.Title}",
    };

    /// <summary>The same, fit for a file name.</summary>
    static string FileShort(Chip c) => Short(c).Replace(" · ", " ");

    /// <summary>Which donors make sense for this template: its own car and suits are what it already has.</summary>
    /// <summary>The season of the open template's cars (2026, or 2025 for an F1 25 template), or null with none open.</summary>
    int? TemplateSeason => _template is null ? null
        : _template.CarEntries.GroupBy(c => c.Season).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault() ?? GameData.CurrentSeason;

    void UpdateDonors()
    {
        // (My Team's own car is the FOM car: its slot is the one the swapped car goes into)
        var own = _team is { IsMyTeam: true } ? MyTeamChassis.Slot : _team?.Folders.FirstOrDefault();
        var season = TemplateSeason;
        foreach (var c in _chips[(int)Pick.Chassis])
        {
            bool mine = own is not null && c.Key == own;   // (the template's own 2026 car; its 2025 car is another car)
            // a chassis goes into a template of its own season only (a 2026 template's slots are 2026 cars, and so on)
            bool otherSeason = season is not null && c.Year != season;
            c.Enabled = !mine && !otherSeason;
            c.Tooltip = mine ? "the template's own car"
                : otherSeason ? $"a {c.Year} car: this is a {season} template, so only {season} cars can be its chassis"
                : ((ChassisDonor)c.Source!).Livery is { } l ? $"{c.TeamName}'s {l} special livery ({c.Year}), a whole car of its own" : $"{c.TeamName}'s {c.Year} car";
        }
        foreach (var c in _chips[(int)Pick.Suits])
        {
            bool mine = _team?.SuitSet == c.Key;
            c.Enabled = !mine;
            var set = (SuitSet)c.Source!;
            c.Tooltip = mine ? "the suits this template's drivers already wear" : $"{c.TeamName} {c.Year}" + (c.Series == "F2" ? " · F2" : "") + (set.Special is { } s ? $" · {s} special" : "")
                                                                                    + (set.Maker is { } mk ? $" · {mk} suit" : "");
        }
        foreach (var c in _chips[(int)Pick.Wheel]) c.Tooltip = null;
        // rims fit cars of their own season: the new chassis', else the template's
        var rimSeason = (_pick[(int)Pick.Chassis]?.Source as ChassisDonor)?.Season ?? season;
        foreach (var c in _chips[(int)Pick.Rims])
        {
            bool otherSeason = rimSeason is not null && c.Year != rimSeason;
            c.Enabled = !otherSeason;
            c.Tooltip = otherSeason ? $"{c.Year} rims: only {rimSeason} cars' rims fit this car"
                : ((ChassisDonor)c.Source!).Livery is { } l ? $"the rims of {c.TeamName}'s {l} special livery ({c.Year})" : $"{c.TeamName}'s {c.Year} rims";
        }
        foreach (var c in _chips[(int)Pick.Engine])
        {
            var en = (EngineSound)c.Source!;
            bool mine = own is not null && en.Token == own && en.Season == season;   // the sound the template's car already has
            c.Enabled = !mine;
            c.Tooltip = mine ? "the engine sound this template's car already has"
                : $"{en.Maker} power unit, as it sounds in the {en.Source} {en.Season} car ({en.Switch})";
        }
        for (int i = 0; i < _pick.Length; i++) if (_pick[i] is { Enabled: false }) _pick[i] = null;
    }

    // ---------------------------------------------------------------- the donor list

    /// <summary>Fill the list for the slot aimed at: its season / series tabs, the search and the team filter.</summary>
    const string AllTeams = "All teams", AllMakers = "All makers";

    void ShowList(bool resetTeam = false)
    {
        var st = _state[(int)_aim];
        var all = _chips[(int)_aim];
        DonorLabel.Text = ListNames[(int)_aim];
        DonorHint.Text = _aim switch
        {
            Pick.Chassis => "2026 and 2025 cars and their special liveries",
            Pick.Wheel => "the wheel goes on the ticked chassis options",
            Pick.Rims => "the road wheels' rims, special liveries' too",
            Pick.Engine => "each power unit, by the car it sounds like",
            _ => "suits, gloves, boots and caps",
        };
        _refreshing = true;
        try
        {
            // the tabs this list has: wheels come from 2026 and 2025 cars, suits from 2024 to 2026 and F2 too, chassis are 2026
            YearTabs.Visibility = Visibility.Visible;
            Y2024.Visibility = _aim == Pick.Suits ? Visibility.Visible : Visibility.Collapsed;
            SeriesTabs.Visibility = _aim == Pick.Suits ? Visibility.Visible : Visibility.Collapsed;
            (st.Year switch { null => YAll, 2025 => Y2025, 2024 => Y2024, _ => Y2026 }).IsChecked = true;
            (st.Series switch { null => SAll, "F2" => SF2, _ => SF1 }).IsChecked = true;
            if (DonorSearch.Text != st.Text) DonorSearch.Text = st.Text;
            if (resetTeam)
            {
                var teams = all.Select(c => c.TeamName + (c.Series == "F2" ? " · F2" : "")).Distinct().OrderBy(t => t.Contains(" · F2") ? 1 : 0).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
                var allLabel = _aim == Pick.Engine ? AllMakers : AllTeams;   // the engine list filters by power unit maker
                DonorTeam.ItemsSource = new[] { allLabel }.Concat(teams).ToList();
                DonorTeam.SelectedItem = st.Team is not null && teams.Contains(st.Team) ? st.Team : allLabel;
            }
            var words = st.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = all.Where(c => (st.Year is null || c.Year == st.Year)
                                      && (_aim != Pick.Suits || st.Series is null || c.Series == st.Series)
                                      && (st.Team is null || c.TeamName + (c.Series == "F2" ? " · F2" : "") == st.Team)
                                      && words.All(c.Haystack.Contains)).ToList();
            foreach (var c in all) c.ShowYear = st.Year is null;
            DonorBox.ItemsSource = list;
            DonorBox.SelectedItem = _pick[(int)_aim] is { } p && list.Contains(p) ? p : null;
            DonorScroll.ScrollToTop();
            if (DonorBox.SelectedItem is { } sel)
                Dispatcher.BeginInvoke(() => (DonorBox.ItemContainerGenerator.ContainerFromItem(sel) as FrameworkElement)?.BringIntoView(),
                                       System.Windows.Threading.DispatcherPriority.Loaded);
        }
        finally { _refreshing = false; }
        bool none = DonorBox.Items.Count == 0 && _game is not null;
        DonorEmpty.Text = none ? "nothing matches these tabs and filters: try ALL on the tabs, or clear the search" : _game is null ? "set the F1 25 game folder to list what it has." : "";
        DonorEmpty.Visibility = none || _game is null ? Visibility.Visible : Visibility.Collapsed;
    }

    void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _refreshing) return;
        var st = _state[(int)_aim];
        st.Year = YAll.IsChecked == true ? null : Y2025.IsChecked == true ? 2025 : Y2024.IsChecked == true ? 2024 : GameData.CurrentSeason;
        st.Series = SAll.IsChecked == true ? null : SF2.IsChecked == true ? "F2" : "F1";
        ShowList(resetTeam: true);
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _refreshing) return;
        _state[(int)_aim].Text = DonorSearch.Text.Trim();
        ShowList();
    }

    /// <summary>A team picked: the tabs open up so all of that team's donors show.</summary>
    void Team_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _refreshing) return;
        var st = _state[(int)_aim];
        st.Team = DonorTeam.SelectedItem as string is { } t && t != AllTeams && t != AllMakers ? t : null;
        if (st.Team is not null)
        {
            var chips = _chips[(int)_aim].Where(c => c.TeamName + (c.Series == "F2" ? " · F2" : "") == st.Team).ToList();
            if (st.Year is not null && !chips.Any(c => c.Year == st.Year)) st.Year = null;
            if (_aim == Pick.Suits && st.Series is not null && !chips.Any(c => c.Series == st.Series)) st.Series = null;
        }
        ShowList();
    }

    void Donor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || DonorBox.SelectedItem is not Chip c) return;
        _pick[(int)c.Pick] = c;
        Remember();
        PicksChanged();
    }

    void Remember()
    {
        _settings.Chassis = _pick[(int)Pick.Chassis]?.Key;
        _settings.Wheel = _pick[(int)Pick.Wheel]?.Key;
        _settings.Suits = _pick[(int)Pick.Suits]?.Key;
        _settings.Rims = _pick[(int)Pick.Rims]?.Key;
        _settings.Engine = _pick[(int)Pick.Engine]?.Key;
        _settings.Save();
    }

    void PicksChanged()
    {
        RefreshSlots();
        SuggestOutput();
        UpdatePreview();
        CheckChassisRig();
    }

    /// <summary>Whether steering wheels fit the new chassis: its rig is read once in the background, then the slots say so.</summary>
    async void CheckChassisRig()
    {
        if (_game is not { } game || _pick[(int)Pick.Chassis]?.Source is not ChassisDonor car || ChassisView.KnownNoWheelReason(car) is not null) return;
        try { await Task.Run(() => ChassisView.NoWheelReason(game, car)); }
        catch (Exception) { return; }
        RefreshSlots();
    }

    // ---------------------------------------------------------------- wheel pictures on hover


    /// <summary>Every hover picture made in the background (when they are on), so a hover shows one at once: each car gives
    /// its chassis, rims and steering wheel pictures; then the wheels of cars that aren't chassis picks (the FOM car), then
    /// the suits. Pictures already made are skipped, so after the first time this costs nothing.</summary>
    void PrewarmPictures()
    {
        if (_game is not { } game || PicturesBox.IsChecked != true) return;
        int run = ++_prewarmRun;   // a newer run (the box turned off and on again) takes over from this one
        _ = _pictures.Prewarm(game, _chips[(int)Pick.Chassis].Select(c => (Pick.Chassis, c.Source!))
            .Concat(_chips[(int)Pick.Wheel].Select(c => (Pick.Wheel, (object)WheelCar(game, c.Key))))
            .Concat(_chips[(int)Pick.Suits].Select(c => (Pick.Suits, c.Source!))), () => PicturesBox.IsChecked == true && run == _prewarmRun,
            p => { if (run == _prewarmRun) ShowPreviewsProgress(p); });
    }

    int _prewarmRun;

    /// <summary>The line next to Hover previews while they are being made (gone when they are all made, or turned off).</summary>
    // the countdown: it ticks down each second and eases toward the latest measured time left (no jumps), never below 0:01
    // while previews are still being made
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
        if (PicturesBox.IsChecked == true) AttachTips(); else DetachTips();
        PrewarmPictures();
    }

    // the hover previews' note: open while the "i" is hovered, pinned by a click (or Enter / Space) until a click elsewhere
    bool _previewsPinned;
    void PreviewsInfo_Enter(object sender, MouseEventArgs e) => NoteShown(true);
    void PreviewsInfo_Leave(object sender, MouseEventArgs e) { if (!_previewsPinned) NoteShown(false); }
    void PreviewsInfo_Click(object sender, RoutedEventArgs e)
    {
        _previewsPinned = !_previewsPinned;
        NoteShown(_previewsPinned || PreviewsInfo.IsMouseOver);
    }
    void ClosePreviewsNote(object sender, MouseButtonEventArgs e)
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

    /// <summary>The car a steering wheel is from (its picture comes from that car's scene).</summary>
    ChassisDonor WheelCar(GameData game, string id)
    {
        if (_chips[(int)Pick.Chassis].FirstOrDefault(c => c.Key == id)?.Source is ChassisDonor car) return car;
        var (token, season) = GameData.ParseDonor(id);
        return new ChassisDonor(token, token, game.TeamErp(token, season), null, season);   // e.g. the FOM car, not a chassis pick
    }

    readonly HoverPictures _pictures = new();

    /// <summary>A chip's hover tip: a picture of what it gives (the stock wheel, the whole car, its rims close up, the suits on a
    /// figure), rendered from the game files the first time and kept in the user's cache.</summary>
    /// <summary>The chips given a hover preview, with the handler that fills it (taken off again when previews are turned off).</summary>
    readonly Dictionary<ListBoxItem, ToolTipEventHandler> _previewTips = [];

    /// <summary>Hover previews off: every chip goes back to its plain tip (its words only, from the list's style).</summary>
    void DetachTips()
    {
        foreach (var (item, handler) in _previewTips)
        {
            item.ToolTipOpening -= handler;
            item.ClearValue(ToolTipProperty);
            item.ClearValue(ToolTipService.InitialShowDelayProperty);
            item.ClearValue(ToolTipService.ShowDurationProperty);
            item.ClearValue(ToolTipService.ShowOnDisabledProperty);
        }
        _previewTips.Clear();
    }

    void AttachTips()
    {
        if (DonorBox.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated) return;
        if (PicturesBox.IsChecked != true) return;   // previews off: the chips keep their plain tips
        foreach (var gone in _previewTips.Keys.Where(i => !DonorBox.IsAncestorOf(i)).ToList()) _previewTips.Remove(gone);
        foreach (var chip in DonorBox.Items.OfType<Chip>())
        {
            if (chip.Pick == Pick.Engine) continue;   // a sound: no picture, its plain tip
            if (DonorBox.ItemContainerGenerator.ContainerFromItem(chip) is not ListBoxItem item || item.ToolTip is ToolTip) continue;
            bool wheel = chip.Pick == Pick.Wheel;
            var image = new Image { Width = HoverPictures.W / 2, Height = HoverPictures.H / 2, Stretch = Stretch.Uniform };
            var title = new TextBlock
            {
                Text = (wheel ? GameData.WheelSuffix(chip.Key) : chip.Title.Replace(" · ", " ") + (chip.Pick == Pick.Suits ? $" {chip.Year}" : "")).ToUpperInvariant(),
                FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.Bold,
                FontSize = 10.5, Foreground = Res("Ink"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            };
            var note = new TextBlock
            {
                Text = "rendering…", FontFamily = (FontFamily)FindResource("Mono"), FontWeight = FontWeights.Bold, FontSize = 10.5,
                Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
            };
            // the chip's own words (why it is greyed, or what it is) under the picture
            var reason = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10.5, Foreground = Res("Purple"), TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, MaxWidth = image.Width, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed,
            };
            var panel = new StackPanel();
            panel.Children.Add(image); panel.Children.Add(title); panel.Children.Add(note); panel.Children.Add(reason);
            var tip = new ToolTip
            {
                Content = panel, Background = Res("Panel"), BorderBrush = Res("Purple"), BorderThickness = new Thickness(1.5), Padding = new Thickness(12, 10, 12, 8),
                Placement = PlacementMode.Top, HasDropShadow = true,
            };
            ToolTipService.SetInitialShowDelay(item, 250);
            ToolTipService.SetShowDuration(item, 60000);
            ToolTipService.SetShowOnDisabled(item, true);
            item.ToolTip = tip;
            ToolTipEventHandler fill = async (_, _) =>
            {
                reason.Text = chip.Tooltip ?? "";
                reason.Visibility = string.IsNullOrEmpty(chip.Tooltip) ? Visibility.Collapsed : Visibility.Visible;
                if (image.Source is not null) return;
                try
                {
                    var game = _game ?? throw new InvalidOperationException("no game folder");
                    var path = await _pictures.For(game, chip.Pick, wheel ? WheelCar(game, chip.Key) : chip.Source!);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit(); bmp.Freeze();
                    image.Source = bmp;
                    note.Text = chip.Pick switch
                    {
                        Pick.Wheel => "stock wheel · " + chip.Year + " car",
                        Pick.Chassis => chip.Note.Length > 0 ? $"special livery · {chip.Year} car" : $"{chip.Year} car",
                        Pick.Rims => $"its rims · {chip.Year} car",
                        _ => $"{(chip.Series == "F2" ? "F2" : "F1")} suits · {chip.Year}",
                    };
                }
                catch (Exception) { note.Text = _game is null ? "set the F1 25 game folder to see this" : "the picture could not be made"; }
            };
            item.ToolTipOpening += fill;
            _previewTips[item] = fill;
        }
    }

    // ---------------------------------------------------------------- output and build

    void SuggestOutput()
    {
        if (_template is null || _outEdited) return;
        var parts = new List<string>();
        if (_pick[(int)Pick.Chassis] is { } ch) parts.Add($"{FileShort(ch)} Chassis");
        if (_pick[(int)Pick.Wheel] is { } w) parts.Add(GameData.WheelSuffix(w.Key));
        if (_pick[(int)Pick.Rims] is { } r) parts.Add($"{FileShort(r)} Rims");
        if (_pick[(int)Pick.Engine] is { } en) parts.Add(EngineSwap.Suffix((EngineSound)en.Source!));
        if (_pick[(int)Pick.Suits] is { } s) parts.Add($"{FileShort(s)} Suits");
        if (parts.Count == 0) return;
        var ext = _outExt;
        OutBox.Text = Path.Combine(Path.GetDirectoryName(_template.SourcePath)!, $"{GameData.StripWheelSuffix(_template.Name)} - {string.Join(", ", parts)}{ext}");
    }

    /// <summary>What a template is saved as unless picked otherwise (a project is always saved as a project).</summary>
    string _outExt = ".serps";

    void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "SERPs template (*.serps)|*.serps",
            FileName = Path.GetFileNameWithoutExtension(OutBox.Text),
            InitialDirectory = Path.GetDirectoryName(OutBox.Text) ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;
        OutBox.Text = dlg.FileName;

        _outEdited = true;
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) { Log("Set the F1 25 game folder first.", Res("LogWarn")); return; }
        if (_template is not { } tpl) { Log("Open a template first.", Res("LogWarn")); return; }
        if (_pick.All(p => p is null)) { Log("Pick a new chassis, steering wheel, rims, engine sound or suits first.", Res("LogWarn")); return; }
        var game = _game;
        var project = _project;
        var team = _team;
        var car = _pick[(int)Pick.Chassis]?.Source as ChassisDonor;
        var wheel = _pick[(int)Pick.Wheel]?.Key;
        var suits = _pick[(int)Pick.Suits]?.Source as SuitSet;
        var rims = _pick[(int)Pick.Rims]?.Source as ChassisDonor;
        var engine = _pick[(int)Pick.Engine]?.Source as EngineSound;
        // with a new chassis the wheel, rims and engine sound go on it; else on the ticked options of the template
        var selected = car is null && (wheel is not null || rims is not null || engine is not null) ? _entries.Where(x => x.IsChecked && x.IsEnabled).Select(x => x.Entry).ToList() : null;
        if (selected is { Count: 0 }) { Log($"Tick at least one chassis option to give the new {(wheel is not null ? "wheel" : rims is not null ? "rims" : "engine sound")}.", Res("LogWarn")); return; }
        if (car is null && engine is not null && selected!.Select(EngineSwap.Problem).FirstOrDefault(x => x is not null) is { } ew) { Log($"The engine sound: {ew}.", Res("LogWarn")); return; }
        if (rims is not null && SwapBuild.RimsProblem(tpl, car, rims) is { } rimsWhy) { Log(rimsWhy, Res("LogWarn")); return; }
        if (car is not null && SwapBuild.ChassisProblem(tpl, project, team, car) is { } cp) { Log(cp, Res("LogWarn")); return; }
        if (car is not null && wheel is not null && ChassisView.WheelProblem(game, car, wheel) is { } wp)
        {
            Log($"The steering wheel: {wp}. Clear the wheel (×) to build the chassis without it.", Res("LogWarn"));
            return;
        }
        if (suits is not null && SwapBuild.SuitsProblem(tpl, project, team, suits) is { } sp) { Log(sp, Res("LogWarn")); return; }
        var outPath = OutBox.Text.Trim();
        if (outPath.Length == 0) { Log("Choose where to save it.", Res("LogWarn")); return; }
        // always a .serps (a typed .simplified becomes one: Swapper writes no projects)
        if (TemplateOutput.IsProject(outPath)) outPath = Path.ChangeExtension(outPath, ".serps");
        else if (!outPath.EndsWith(".serps", StringComparison.OrdinalIgnoreCase)) outPath += ".serps";
        if (TemplateOutput.Problem(tpl, outPath) is { } problem) { Log(problem, Res("LogWarn")); return; }
        if (File.Exists(outPath) && MessageBox.Show(this, $"{Path.GetFileName(outPath)} already exists. Replace it?", "Swapper",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        bool saveReport = ReportBox.IsChecked == true;
        _settings.GameDir = game.GameDir; _settings.SaveReport = saveReport; _settings.Save();
        _log.Clear();
        var what = new List<string>();
        if (car is not null) what.Add($"{car.Label} chassis as a new chassis option");
        if (wheel is not null) what.Add($"{GameData.DonorName(wheel)} steering wheel" + (car is not null ? " on it" : " -> " + string.Join(", ", selected!.Select(s => s.Title))));
        if (rims is not null) what.Add($"{rims.Label} rims" + (car is not null ? " on it" : " -> " + string.Join(", ", selected!.Select(s => s.Title))));
        if (engine is not null) what.Add($"{engine.Name} engine sound" + (car is not null ? " on it" : " -> " + string.Join(", ", selected!.Select(s => s.Title))));
        if (suits is not null) what.Add($"{suits.Label} {suits.Year} suits");
        Log(string.Join(" · ", what), Res("LogText"));
        var log = new SwapLog();
        log.Line += (s, w) => Dispatcher.Invoke(() => Log((w ? "Warning: " : "") + s, w ? Res("LogWarn") : Res("LogMuted")));
        SetBusy(true);
        ReportButton.Visibility = ShowButton.Visibility = StatusPill.Visibility = Visibility.Collapsed;
        try
        {
            var outcome = await Task.Run(() => SwapBuild.Run(game, tpl, project, team, new(car, wheel, selected, suits, rims, engine), outPath, AppVersion, log, saveReport));
            _lastOut = outPath;
            if (outcome.ReportPath is { } rp) { _reportPath = rp; ReportButton.Visibility = Visibility.Visible; Log($"Report: {Path.GetFileName(rp)}", Res("LogMuted")); }
            Log($"Done. Saved {Path.GetFileName(outPath)}" + (log.Warnings.Count > 0 ? $" ({log.Warnings.Count} warning(s) above)." : ".") +
                " Open it in SERPs like any template and switch the new sections on.", Res("LogOk"));
            Status($"Verified · {outcome.Checks} checks", "Teal");
            ShowButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { Log("Failed: " + ex.Message, Res("LogError")); Status("Failed · nothing written", "Red"); }
        finally { SetBusy(false); }
    }

    void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_reportPath is null || !File.Exists(_reportPath)) { Log("The report file is no longer there.", Res("LogWarn")); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_reportPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log("Could not open the report: " + ex.Message, Res("LogWarn")); }
    }

    void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOut is null || !File.Exists(_lastOut)) { Log("The file is no longer there.", Res("LogWarn")); return; }
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastOut}\""); }
        catch (Exception ex) { Log("Could not open the folder: " + ex.Message, Res("LogWarn")); }
    }

    void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        if (Environment.GetEnvironmentVariable("SWAPPER_TEST_BEHIND") == "1")
        {
            about.ShowActivated = false;
            about.SourceInitialized += (_, _) => SetWindowPos(new System.Windows.Interop.WindowInteropHelper(about).Handle, 1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        about.Show();
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
        Busy.Visibility = b ? Visibility.Visible : Visibility.Collapsed;
        EntryList.IsEnabled = !b && _pick[(int)Pick.Chassis] is null;
        DonorBox.IsEnabled = !b;
        WaitCursor(b);
    }

    static void WaitCursor(bool wait) => System.Windows.Input.Mouse.OverrideCursor = wait ? System.Windows.Input.Cursors.Wait : null;

    // ---------------------------------------------------------------- the 3D preview (column or its own window)

    const double ColumnMinWindow = 1150;      // narrower: the column hides (the preview can still pop out)
    const double FormMin = 660, FormDefault = 940, PreviewMin = 420;
    ViewerHost? _pv, _dockHost;
    PreviewWindow? _popWin;
    bool _dockFailed, _popReady;
    double _dockedWidth;
    int _pvRequest;
    string? _pvShown = "";
    string _pvLabel = "";

    void ApplyPreviewLayout()
    {
        bool column = _popWin is null && ActualWidth >= ColumnMinWindow;
        PreviewCol.MinWidth = column ? PreviewMin : 0;
        PreviewCol.Width = column ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitCol.Width = new GridLength(column ? 12 : 0);
        PreviewPanel.Visibility = Splitter.Visibility = column ? Visibility.Visible : Visibility.Collapsed;
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
            var host = new ViewerHost(PreviewView);
            await host.InitAsync();
            host.Ready += () => { if (host == _pv) SetPreviewStatus(_pvLabel); };
            _dockHost = host;
        }
        catch (Exception)
        {
            _dockFailed = true;
            PreviewView.Visibility = Visibility.Collapsed;
            PreviewNote.Visibility = Visibility.Visible;
            PreviewNote.Text = "the 3d preview needs microsoft edge webview2 (part of windows 10 and 11).";
        }
        ApplyPreviewLayout();
    }

    void SetPreviewStatus(string text)
    {
        PreviewStatus.Text = text;
        PreviewStatus.ToolTip = text.Length > 0 ? text : null;   // a long note is cut short on the line
        if (_popWin is not null) _popWin.Status = text;
    }

    /// <summary>A narrow picks column: the team filter goes under the search box.</summary>
    void ApplyFormLayout()
    {
        bool narrow = FormCol.ActualWidth > 0 && FormCol.ActualWidth < 820;
        Grid.SetColumn(DonorTeam, narrow ? 0 : 1);
        Grid.SetColumnSpan(DonorTeam, narrow ? 2 : 1);
        Grid.SetRow(DonorTeam, narrow ? 1 : 0);
        DonorTeam.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(10, 0, 0, 0);
        DonorTeam.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    /// <summary>The picks fill the window's height: the log takes whatever is left, down to a few lines (it scrolls inside)
    /// before the picks themselves have to scroll.</summary>
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

    void Splitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (FormCol.ActualWidth > FormMax || FormCol.ActualWidth < FormMin)
            FormCol.Width = new GridLength(Math.Clamp(FormCol.ActualWidth, FormMin, FormMax));
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
    }

    void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var w = Math.Clamp(Math.Round(FormCol.ActualWidth), FormMin, FormMax);
        FormCol.Width = new GridLength(w);
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
        _settings.FormWidth = w;
        _settings.Save();
    }

    void Splitter_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _settings.FormWidth = null;
        _settings.Save();
        FormCol.Width = new GridLength(Math.Clamp(FormDefault, FormMin, FormMax));
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
    }

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
        w.Host.Ready += () => { if (w.Host == _pv) SetPreviewStatus(_pvLabel); };
        _popReady = true;
        ApplyPreviewLayout();
    }

    /// <summary>Show the picks: the template's chassis option picked above the preview, with the new wheel built in (in
    /// memory, nothing saved) when one is picked, and the new chassis from the game beside it.</summary>
    async void UpdatePreview()
    {
        if (_pv is null) return;
        var game = _game;
        var tpl = _template;
        var entry = (PreviewEntry.SelectedItem as EntryItem)?.Entry;
        Chip? chassis = _pick[(int)Pick.Chassis], wheel = _pick[(int)Pick.Wheel], suits = _pick[(int)Pick.Suits], rimsChip = _pick[(int)Pick.Rims];
        // the preview shows what the slot being picked for is about: the suits for Suits; for Chassis and Steering wheel
        // the car (the new chassis when one is picked, else the template's option), wearing the new wheel when one is picked
        var ownSuits = _team?.SuitSet is { } own ? _chips[(int)Pick.Suits].FirstOrDefault(c => c.Key == own)?.Source as SuitSet : null;
        var suitShow = (suits?.Source as SuitSet) ?? ownSuits;
        bool showSuits = _aim == Pick.Suits && suitShow is not null;
        var newCar = PreviewEntry.SelectedItem is NewOptionItem ? chassis?.Source as ChassisDonor : null;
        if (newCar is not null) entry = null;
        // no template open (browsing what the game has): a wheel, rims or engine sound picked without a chassis is shown on
        // a car of its own (the wheel's or engine's team car, the rims' car), so everything can be looked at before a
        // template is opened; building still needs one
        var engineChip = _pick[(int)Pick.Engine];
        ChassisDonor? browse = null;
        if (tpl is null && newCar is null && !showSuits)
        {
            ChassisDonor? TeamCar(string token, int season) => _chips[(int)Pick.Chassis].Select(c => c.Source as ChassisDonor)
                .FirstOrDefault(d => d is not null && d.Team == token && d.Season == season && d.Livery is null);
            browse = wheel?.Source is Team wt ? TeamCar(wt.Token, wt.Season)
                : rimsChip?.Source as ChassisDonor
                ?? (engineChip?.Source is EngineSound es ? TeamCar(es.Token, es.Season) : null);
            newCar = browse;
        }
        bool browseOwnWheel = browse is not null && wheel?.Source is Team bt && bt.Token == browse.Team && bt.Season == browse.Season;
        bool browseOwnRims = browse is not null && rimsChip?.Source is ChassisDonor br && br.Id == browse.Id;
        // Before: the template's car as it is (the option shown; beside a new chassis, the template's first option)
        var ownEntry = entry is { Problem: null } ? entry : tpl?.CarEntries.FirstOrDefault(c => c.Problem is null);
        var team = _team;
        var key = showSuits ? $"{game?.GameDir}|suits|{suitShow!.Name}|{ownSuits?.Name}"
                            : $"{game?.GameDir}|car|{tpl?.SourcePath}|{(newCar is null ? entry?.ZipPath : "")}|{chassis?.Key}|{wheel?.Key}|{rimsChip?.Key}|{browse?.Id}";
        if (key == _pvShown) return;
        _pvShown = key;
        int request = ++_pvRequest;
        var pv = _pv;
        if (game is null) { _pvLabel = ""; SetPreviewStatus(""); pv.ShowMessage("set the f1 25 game folder"); return; }
        if (!showSuits && tpl is null && newCar is null)
        {
            _pvLabel = ""; SetPreviewStatus("");
            pv.ShowMessage(engineChip is not null ? "this engine sound has no car of its own to show: pick a chassis to see it on one"
                                                  : "pick a chassis, steering wheel, rims or suits to look at them here · open a serps template to build");
            return;
        }
        string? wheelNote = newCar is not null && wheel is not null && !browseOwnWheel ? ChassisView.WheelProblem(newCar) : null;
        // with a new chassis picked the wheel goes on it only: the template's own options are shown as they are
        bool wheelOn = !showSuits && wheel is not null && !browseOwnWheel && (newCar is not null ? wheelNote is null : chassis is null && entry is { Problem: null });
        // the rims, like the wheel, go on the new chassis when there is one, else on the template's option shown
        var rims = rimsChip?.Source as ChassisDonor;
        bool rimsOn = !showSuits && rims is not null && !browseOwnRims && (newCar is not null || chassis is null && entry is { Problem: null });
        _pvLabel = showSuits ? $"suits: {suitShow!.Label.ToLowerInvariant()} {suitShow.Year}" + (suits is null ? " (the template's own)" : "")
            : (browse is not null ? $"{browse.Label.ToLowerInvariant()}{(browse.Season != GameData.CurrentSeason ? " " + browse.Season : "")}" + (browseOwnWheel ? " with its own wheel" : "") + (browseOwnRims ? " with its own rims" : "")
               : newCar is not null ? $"new chassis option: {newCar.Label.ToLowerInvariant()}{(newCar.Season != GameData.CurrentSeason ? " " + newCar.Season : "")}" : entry?.Title.ToLowerInvariant() ?? "")
              + (wheelOn ? $" with the {Short(wheel!).ToLowerInvariant()} wheel" : wheelNote is not null ? " · " + wheelNote : "")
              + (rimsOn ? $"{(wheelOn ? " and" : " with")} {Short(rimsChip!).ToLowerInvariant()} rims" : "");
        SetPreviewStatus("loading " + _pvLabel + "…");
        pv.ShowBusy(showSuits ? "loading the suits…" : "building the preview…");
        try
        {
            string? rigNote = null;
            var (sc, car) = await Task.Run(() =>
            {
                var sc = new PreviewScene(game) { TexSize = 1024, PaintSize = 2048, BlobSink = pv.PutForNext };
                Dictionary<string, object?> car;
                if (showSuits)
                {
                    // suits on a standing figure: the template's own before, the picked ones after
                    var before = suits is not null && ownSuits is not null && ownSuits.Name != suitShow!.Name ? ownSuits : null;
                    car = SuitView.Car(sc, game, suitShow!, before, $"Suits · {suitShow!.Label} {suitShow.Year}");
                    car["sides"] = before is not null;
                }
                else if (rimsOn)
                {
                    // new rims: the car (new chassis or the template's option) built in memory with them, and the wheel
                    if (newCar is not null) rigNote = wheelOn ? ChassisView.WheelProblem(game, newCar, wheel!.Key) : null;
                    var name = newCar is not null ? newCar.Label + (newCar.Season != GameData.CurrentSeason ? $" {newCar.Season}" : "") : entry!.Title;
                    car = WithCams(ChassisView.WithRims(sc, game, tpl, entry, newCar, wheelOn && rigNote is null ? wheel!.Key : null, rims!, name), game, tpl, entry, newCar, team);
                    car["sides"] = false;
                    var w = wheelOn && rigNote is null ? $" · {Short(wheel!)} wheel" : "";
                    car["afterCar"] = car["beforeCar"] = $"{name}{w} · {Short(rimsChip!)} rims";
                    if (tpl is not null && ownEntry is not null) car = BeforeAfter(WithCams(CarView.Template(sc, game, tpl, ownEntry), game, tpl, ownEntry, null, team), car, ownEntry.Title);
                }
                else if (newCar is not null)
                {
                    // the new chassis option, with the new wheel built onto it (when its steering rig takes it)
                    rigNote = wheelOn ? ChassisView.WheelProblem(game, newCar, wheel!.Key) : null;
                    car = WithCams(ChassisView.Car(sc, game, newCar, wheelOn && rigNote is null ? wheel!.Key : null), game, tpl, null, newCar, team);
                    car["sides"] = wheelOn && rigNote is null;
                    Captions(car, newCar.Label + (newCar.Season != GameData.CurrentSeason ? $" {newCar.Season}" : ""), wheelOn && rigNote is null);
                    if (tpl is not null && ownEntry is not null) car = BeforeAfter(WithCams(CarView.Template(sc, game, tpl, ownEntry), game, tpl, ownEntry, null, team), car, ownEntry.Title);
                }
                else if (tpl is not null && entry is { Problem: null })
                {
                    if (wheelOn)
                    {
                        var res = tpl.BuildInMemory(game, wheel!.Key, [entry], new SwapLog(), carPlacement: true, anySeason: true);   // as the build places it
                        car = WheelPreview.Car(sc, game, tpl, res, res.Entries[0], withCar: true);
                    }
                    else car = CarView.Template(sc, game, tpl, entry);
                    WithCams(car, game, tpl, entry, null, team);
                    car["sides"] = wheelOn;
                    Captions(car, entry.Title, wheelOn);
                }
                else throw new InvalidDataException("this chassis option can't be shown: " + (entry?.Problem ?? "nothing picked"));
                return (sc, car);
            });
            if (request != _pvRequest) return;
            if (rigNote is not null) { _pvLabel = $"new chassis option: {newCar!.Label.ToLowerInvariant()} · its older steering rig can't take the new wheel yet"; RefreshSlots(); }
            await pv.ShowSceneInPlace(sc, [car], new Viewer.Options(Sides: (bool)car["sides"]!, Modes: !showSuits, Mode: "car",
                                                       BeforeLabel: showSuits ? "the template's own suits / the new suits" : "the template as it is / with the swap"));
        }
        catch (Exception ex)
        {
            if (request != _pvRequest) return;
            SetPreviewStatus("");
            pv.ShowMessage("the preview could not be made: " + ex.Message);
        }
    }
}
