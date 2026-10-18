using System.IO;
using System.Numerics;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Swapper.Core;

namespace Swapper.App;

/// <summary>
/// The hover pictures of the lists (the car in side profile, its rims and its steering wheel face on, the suits on a
/// figure): each is drawn once by the Studio viewer itself (the same
/// materials and lighting as the preview) in a viewer of its own that is never shown, and kept as a PNG in the user's
/// cache (%LOCALAPPDATA%\RM Designs Studio\Swapper\pictures), on a transparent background: nothing is added to the app.
/// They are all made in the background once the game folder is read (<see cref="Prewarm"/>), so a hover shows a picture
/// at once, as the steering wheels' do; a game update makes new ones of what changed.
/// </summary>
sealed class HoverPictures
{
    /// <summary>Picture size in pixels (shown at half: sharp on high-DPI screens).</summary>
    public const int W = 720, H = 480;
    /// <summary>Bumped when the pictures change (4: decal edges no longer see-through; 5: no skin through the gloves); older ones are deleted.</summary>
    const int PictureVersion = 5;

    // SWAPPER_PICTURES: another folder (scripted tests, so a test run starts with none made and the user's are left alone)
    static readonly bool TestFolder = Environment.GetEnvironmentVariable("SWAPPER_PICTURES") is { Length: > 0 };
    /// <summary>The largest texture the previews read (SWAPPER_PREVIEW_TEX overrides it in tests).</summary>
    static readonly int TexSize = int.TryParse(Environment.GetEnvironmentVariable("SWAPPER_PREVIEW_TEX"), out var t) ? t : 1024;
    static readonly string CacheDir = Environment.GetEnvironmentVariable("SWAPPER_PICTURES") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RM Designs Studio", "Swapper", "pictures");
    readonly SemaphoreSlim _one = new(1, 1);
    readonly Dictionary<string, Task<string>> _jobs = [];
    Window? _window;
    ViewerHost? _host;
    TaskCompletionSource? _ready;

    /// <summary>Where a pick's picture is kept (named after the game file it comes from and that file's time).</summary>
    static string PathOf(Pick kind, object source)
    {
        var (id, file) = source switch
        {
            ChassisDonor c => (c.Id, c.ErpPath),
            SuitSet s => (s.Name, s.ErpPath!),
            _ => throw new ArgumentException("no picture for this pick"),
        };
        var stamp = File.GetLastWriteTimeUtc(file).Ticks;
        return Path.Combine(CacheDir, $"{kind.ToString().ToLowerInvariant()}_{id.Replace('@', '_')}_{W}x{H}_v{PictureVersion}_{stamp:x}.png");
    }

    /// <summary>The cached picture for a pick (rendered first when needed).</summary>
    public Task<string> For(GameData game, Pick kind, object source)
    {
        var path = PathOf(kind, source);
        lock (_jobs)
        {
            if (_jobs.TryGetValue(path, out var job) && !job.IsFaulted) return job;
            return _jobs[path] = File.Exists(path) ? Task.FromResult(path) : Render(game, kind, source, path);
        }
    }

    async Task<string> Render(GameData game, Pick kind, object source, string path)
    {
        await _one.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            var host = await Host();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // the scene, built off the UI thread; its data goes to the hidden viewer. A car gives three pictures from one
            // scene: the whole car in side profile (Chassis), its front-left rim face on (Rims), its steering wheel face on (Wheel)
            var (sc, car, shots) = await Task.Run(() =>
            {
                var sc = new PreviewScene(game) { TexSize = TexSize, PaintSize = TexSize, BlobSink = host.PutForNext };
                Dictionary<string, object?> car;
                var shots = new List<(string Path, string Options)>();
                switch (source)
                {
                    case SuitSet set:
                        car = SuitView.Car(sc, game, set, null, set.Label);
                        shots.Add((path, $"{{w:{W},h:{H},clear:true,view:'three',zoom:0.92}}"));
                        break;
                    case ChassisDonor donor:
                        car = CarView.Stock(sc, game, donor);
                        var model = new CarModel(game, Catalog.ModelTokenOf(donor), [ErpX.Load(donor.ErpPath)], donor.Season);
                        var nt = NodeTable.Parse(model.Render);
                        var at = PreviewScene.NodeWorld(nt)(nt.Nodes.First(n => n.Name == "wheel_fl").Index).Translation;
                        // side profile: the camera on the car's middle, far enough for its length to fill about 85% of the width
                        var box = (float[])car["box"]!;
                        double len = box[5] - box[2], fov = 30 * Math.PI / 180, dist = len / 0.84 / (2 * Math.Tan(fov / 2) * W / H);
                        shots.Add((PathOf(Pick.Chassis, donor), FormattableString.Invariant(
                            $"{{w:{W},h:{H},clear:true,t:[{(box[0] + box[3]) / 2},{(box[1] + box[4]) / 2},{(box[2] + box[5]) / 2}],yaw:{Math.PI / 2},pitch:0.03,dist:{dist},fov:30}}")));
                        // the front-left rim face on, from the outside
                        shots.Add((PathOf(Pick.Rims, donor), FormattableString.Invariant($"{{w:{W},h:{H},clear:true,t:[{at.X},{at.Y},{at.Z}],yaw:{-Math.PI / 2},pitch:0.02,dist:2.05,fov:30}}")));
                        shots.Add((PathOf(Pick.Wheel, donor), $"{{w:{W},h:{H},clear:true,mode:'wheel',view:'front',zoom:0.95}}"));
                        break;
                    default: throw new ArgumentException("no picture for this pick");
                }
                car["sides"] = false;
                return (sc, car, shots);
            });
            var built = clock.ElapsedMilliseconds;
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await host.ShowSceneInPlace(sc, [car], new Viewer.Options(Sides: false, Modes: false, Mode: "car"));
            if (await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(60))) != _ready.Task) throw new TimeoutException("the picture took too long");
            Directory.CreateDirectory(CacheDir);
            var shown = clock.ElapsedMilliseconds;
            foreach (var (p, options) in shots)
            {
                var png = await host.SnapshotAsync(options) ?? throw new InvalidDataException("the picture could not be read back");
                await File.WriteAllBytesAsync(p + ".tmp", png);
                File.Move(p + ".tmp", p, overwrite: true);
            }
            if (TestFolder) File.AppendAllText(Path.Combine(CacheDir, "times.log"),
                $"{Path.GetFileName(path)}  build {built}  show {shown - built}  shots {clock.ElapsedMilliseconds - shown}  page {host.LastTimes}" + Environment.NewLine);
            return path;
        }
        finally { _one.Release(); }
    }

    /// <summary>Make every picture not made yet, one after another in the background (each a few seconds, once).</summary>
    public async Task Prewarm(GameData game, IEnumerable<(Pick Kind, object Source)> items, Func<bool>? keepGoing = null,
                              Action<PrewarmProgress?>? progress = null)
    {
        // pictures from an older version of the app are made again: drop them
        try
        {
            if (Directory.Exists(CacheDir))
                foreach (var old in Directory.EnumerateFiles(CacheDir, "*.png").Where(f => !f.Contains($"_v{PictureVersion}_", StringComparison.Ordinal)))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        // smallest game file first: the quickest previews are ready soonest (a car's own wheel picture keeps coming with it)
        static long SizeOf(object source)
        {
            var file = source switch { ChassisDonor c => c.ErpPath, SuitSet s => s.ErpPath, _ => null };
            try { return file is null ? 0 : new FileInfo(file).Length; } catch (IOException) { return 0; }
        }
        var list = items.OrderBy(x => SizeOf(x.Source)).ToList();
        // a car's scene makes its chassis, rims and steering wheel pictures together; a wheel picture of a car still to be
        // made comes with it, so only the scenes still to draw count towards the time left
        bool Made(Pick kind, object source) => kind == Pick.Chassis
            ? new[] { Pick.Chassis, Pick.Rims, Pick.Wheel }.All(k => File.Exists(PathOf(k, source)))
            : File.Exists(PathOf(kind, source));
        // the scenes still to draw from a point on (a wheel of a car still to draw comes with that car's scene)
        List<(Pick Kind, object Source)> Left(int from)
        {
            var cars = list.Skip(from).Where(x => x.Kind == Pick.Chassis && !Made(x.Kind, x.Source)).Select(x => ((ChassisDonor)x.Source).Id).ToHashSet();
            return list.Skip(from).Where(x => !Made(x.Kind, x.Source) && !(x.Kind == Pick.Wheel && cars.Contains(((ChassisDonor)x.Source).Id))).ToList();
        }
        // what a scene costs, in suits: a car (its 3 pictures, a bigger model) takes about 2.3 times a suit on any PC
        // (measured: 0.63 s and 0.27 s); the PC's own speed is measured as it goes
        static double Cost(Pick kind) => kind == Pick.Suits ? 1 : 2.3;
        int total = Left(0).Count, drawn = 0;
        // the time left: the PC's measured time per unit of cost, times the cost still to draw; the first scene also starts
        // the viewer, so the measuring starts after it
        var spent = TimeSpan.Zero;
        double units = 0;
        try
        {
            if (total == 0) return;
            progress?.Invoke(new PrewarmProgress(0, total, null));
            for (int i = 0; i < list.Count; i++)
            {
                if (keepGoing?.Invoke() == false) return;   // turned off: stop after the picture in hand
                var (kind, source) = list[i];
                if (Made(kind, source)) continue;
                var t0 = DateTime.UtcNow;
                try { await For(game, kind, source); }
                catch (Exception) { /* that one is made again when it is hovered */ }
                await Task.Delay(150);   // gently: the app's own work (and the rest of the PC) goes first
                if (drawn++ > 0) { spent += DateTime.UtcNow - t0; units += Cost(kind); }
                var left = Left(i + 1);
                double unitsLeft = left.Sum(x => Cost(x.Kind));
                // shown once a few scenes have been timed
                progress?.Invoke(new PrewarmProgress(total - left.Count, total,
                    drawn >= 4 && units > 0 ? TimeSpan.FromTicks((long)(spent.Ticks / units * unitsLeft)) : null));
            }
        }
        finally { progress?.Invoke(null); }
    }

    /// <summary>The hidden viewer: a window that never shows on screen or in the taskbar.</summary>
    async Task<ViewerHost> Host()
    {
        if (_host is not null) return _host;
        var view = new WebView2();
        _window = new Window
        {
            Width = 400, Height = 300, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize, Content = view, Title = "Swapper pictures",
        };
        _window.Show();
        var host = new ViewerHost(view);
        await host.InitAsync();
        host.Ready += () => _ready?.TrySetResult();
        return _host = host;
    }

    /// <summary>Close the hidden viewer (with the app).</summary>
    public void Close() => _window?.Close();
}

/// <summary>How far the background previews are: scenes drawn of all to draw, and the time left once it can be told.</summary>
public sealed record PrewarmProgress(int Done, int Total, TimeSpan? Left);
