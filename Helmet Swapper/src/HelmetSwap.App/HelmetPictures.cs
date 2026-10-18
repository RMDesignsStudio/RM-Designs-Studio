using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using HelmetSwap.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HelmetSwap.App;

/// <summary>
/// The hover pictures of the lists: each helmet in the preview's 3/4 view, drawn once by the preview's own renderer
/// (<see cref="HelmetPage.SnapPage"/>, the same materials and lighting) in a viewer of its own that is never shown, and
/// kept as a PNG on a see-through background in the user's cache (%LOCALAPPDATA%\HelmetSwapper\helmets): nothing is
/// added to the app. They are all made in the background once the game folder is read (<see cref="Prewarm"/>), so a
/// hover shows a picture at once; a game update makes new ones of what changed. (As Swapper's hover previews.)
/// </summary>
sealed class HelmetPictures
{
    /// <summary>Picture size in pixels (shown at half: sharp on high-DPI screens).</summary>
    public const int W = 600, H = 520;
    /// <summary>Bumped when the pictures change (2: the preview's renderer, 3/4 view, see-through); older ones are deleted.</summary>
    const int PictureVersion = 2;

    // HELMETSWAP_PICTURES: another folder (scripted tests, so a test run starts with none made and the user's are left alone)
    static readonly bool TestFolder = Environment.GetEnvironmentVariable("HELMETSWAP_PICTURES") is { Length: > 0 };
    static readonly string CacheDir = Environment.GetEnvironmentVariable("HELMETSWAP_PICTURES") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelmetSwapper", "helmets");
    const string Origin = "https://helmet-pictures.local";

    readonly SemaphoreSlim _one = new(1, 1);
    readonly Dictionary<string, Task<string>> _jobs = [];
    readonly Dictionary<int, TaskCompletionSource<string>> _shots = [];
    Window? _window;
    CoreWebView2? _core;
    TaskCompletionSource? _page;
    byte[] _pageBytes = [], _data = [];
    int _n;

    /// <summary>Where a helmet's picture is kept (named after its helmet file and that file's time).</summary>
    static string PathOf(Driver d)
    {
        var stamp = File.GetLastWriteTimeUtc(d.HelmetErp).Ticks;
        return Path.Combine(CacheDir, $"{d.Folder}_{W}x{H}_v{PictureVersion}_{stamp:x}.png");
    }

    /// <summary>The cached picture of a driver's own helmet (drawn first when needed).</summary>
    public Task<string> For(GameInstall game, Driver d)
    {
        var path = PathOf(d);
        lock (_jobs)
        {
            if (_jobs.TryGetValue(path, out var job) && !job.IsFaulted) return job;
            return _jobs[path] = File.Exists(path) ? Task.FromResult(path) : Render(game, d, path);
        }
    }

    async Task<string> Render(GameInstall game, Driver d, string path)
    {
        await _one.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            await Host();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // the helmet's data, built off the UI thread; the page fetches it, draws it and sends the picture back
            _data = Encoding.UTF8.GetBytes(await Task.Run(() => HelmetPage.SnapData(game, d)));
            var built = clock.ElapsedMilliseconds;
            int id = ++_n;
            var shot = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shots[id] = shot;
            _core!.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "snap", id, url = $"{Origin}/data.json?n={id}", w = W, h = H }));
            try
            {
                if (await Task.WhenAny(shot.Task, Task.Delay(TimeSpan.FromSeconds(60))) != shot.Task) throw new TimeoutException("the picture took too long");
            }
            finally { _shots.Remove(id); _data = []; }
            var url = await shot.Task;
            var png = Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(path + ".tmp", png);
            File.Move(path + ".tmp", path, overwrite: true);
            if (TestFolder) File.AppendAllText(Path.Combine(CacheDir, "times.log"),
                $"{Path.GetFileName(path)}  data {built}  shot {clock.ElapsedMilliseconds - built}" + Environment.NewLine);
            return path;
        }
        finally { _one.Release(); }
    }

    /// <summary>Make every picture not made yet, one after another in the background (smallest helmet file first, so the
    /// quickest are ready soonest), reporting how far it is; <paramref name="keepGoing"/> false stops it.</summary>
    public async Task Prewarm(GameInstall game, IEnumerable<Driver> drivers, Func<bool>? keepGoing = null, Action<PrewarmProgress?>? progress = null)
    {
        // pictures from an older version of the app are made again: drop them
        try
        {
            if (Directory.Exists(CacheDir))
                foreach (var old in Directory.EnumerateFiles(CacheDir, "*.png").Where(f => !f.Contains($"_v{PictureVersion}_", StringComparison.Ordinal)))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        static long SizeOf(Driver d) { try { return new FileInfo(d.HelmetErp).Length; } catch (IOException) { return 0; } }
        var list = drivers.DistinctBy(d => d.Folder).Where(d => !File.Exists(PathOf(d))).OrderBy(SizeOf).ToList();
        if (list.Count == 0) return;
        // the time left: the PC's measured time per byte of helmet file, times the bytes still to draw; the first picture
        // also starts the viewer, so the measuring starts after it
        long bytesLeft = list.Sum(SizeOf), bytesTimed = 0;
        var spent = TimeSpan.Zero;
        try
        {
            progress?.Invoke(new PrewarmProgress(0, list.Count, null));
            for (int i = 0; i < list.Count; i++)
            {
                if (keepGoing?.Invoke() == false) return;   // turned off: stop after the picture in hand
                var d = list[i];
                var t0 = DateTime.UtcNow;
                try { await For(game, d); }
                catch (Exception) { /* that one is made again when it is hovered */ }
                await Task.Delay(150);   // gently: the app's own work (and the rest of the PC) goes first
                long size = SizeOf(d);
                bytesLeft -= size;
                if (i > 0) { spent += DateTime.UtcNow - t0; bytesTimed += size; }
                progress?.Invoke(new PrewarmProgress(i + 1, list.Count,
                    i >= 3 && bytesTimed > 0 ? TimeSpan.FromTicks((long)((double)spent.Ticks / bytesTimed * bytesLeft)) : null));
            }
        }
        finally { progress?.Invoke(null); }
    }

    /// <summary>The hidden viewer: a window that never shows on screen or in the taskbar, with the pictures page loaded once.</summary>
    async Task Host()
    {
        if (_page is not null) { await _page.Task; return; }
        _page = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new WebView2();
        _window = new Window
        {
            Width = 400, Height = 300, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize, Content = view, Title = "Helmet pictures",
        };
        _window.Show();
        var env = await CoreWebView2Environment.CreateAsync(null, PreviewHost.DataDir);
        await view.EnsureCoreWebView2Async(env);
        var core = _core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        _pageBytes = Encoding.UTF8.GetBytes(HelmetPage.SnapPage());
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var at = new Uri(e.Request.Uri).AbsolutePath;
            e.Response = at == "/index.html"
                ? core.Environment.CreateWebResourceResponse(new MemoryStream(_pageBytes, false), 200, "OK", "Content-Type: text/html; charset=utf-8\nCache-Control: no-store")
                : at == "/data.json"
                ? core.Environment.CreateWebResourceResponse(new MemoryStream(_data, false), 200, "OK", "Content-Type: application/json; charset=utf-8\nCache-Control: no-store")
                : core.Environment.CreateWebResourceResponse(null, 404, "Not found", "");
        };
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "page") _page.TrySetResult();
            else if (type == "snap" && m.TryGetProperty("id", out var idv) && _shots.TryGetValue(idv.GetInt32(), out var shot))
            {
                if (m.TryGetProperty("png", out var png) && png.GetString() is { } url) shot.TrySetResult(url);
                else shot.TrySetException(new InvalidDataException(m.TryGetProperty("error", out var er) ? er.GetString() : "no picture"));
            }
        };
        core.Navigate(Origin + "/index.html");
        await _page.Task;
    }

    /// <summary>Close the hidden viewer (with the app).</summary>
    public void Close() => _window?.Close();
}

/// <summary>How far the background previews are: pictures made of all to make, and the time left once it can be told.</summary>
public sealed record PrewarmProgress(int Done, int Total, TimeSpan? Left);
