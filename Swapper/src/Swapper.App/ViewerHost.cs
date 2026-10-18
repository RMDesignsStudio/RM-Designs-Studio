using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Swapper.App;

/// <summary>
/// The Studio viewer inside the window (the preview column or its pop-out window): a WebView2 showing a page served from
/// memory at https://swapper.local/ (no files, no network). The scene's data is handed over raw (/blob/N).
/// </summary>
sealed class ViewerHost(WebView2 view)
{
    const string Origin = "https://swapper.local";
    readonly ConcurrentDictionary<string, (byte[] Data, string Mime)> _files = new(StringComparer.Ordinal);
    readonly HashSet<string> _keep = new(StringComparer.Ordinal);
    int _next, _page;
    CoreWebView2? _core;

    /// <summary>The page has loaded its scene and drawn it.</summary>
    public event Action? Ready;
    /// <summary>The page's own timings of the last scene (for test logs).</summary>
    public string? LastTimes { get; private set; }

    static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RM Designs Studio", "Swapper", "WebView2");

    static readonly (string File, string Family, int Weight)[] Fonts =
    [
        ("Presicav-Regular.ttf", "Presicav", 400), ("Presicav-Bold.ttf", "Presicav", 700), ("Presicav-Heavy.ttf", "Presicav", 900),
        ("RobotoMono-Regular.ttf", "Roboto Mono", 400), ("RobotoMono-Bold.ttf", "Roboto Mono", 700),
    ];

    public async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
        await view.EnsureCoreWebView2Async(env);
        _core = view.CoreWebView2;
        view.AllowExternalDrop = false;   // a file dropped on the view must not open in it
        _core.Settings.AreDefaultContextMenusEnabled = false;
        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.IsZoomControlEnabled = false;
        _core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += Serve;
        // a scene page that has loaded takes the next scenes in place (ShowSceneInPlace)
        _core.NavigationCompleted += (_, e) => _swappable = e.IsSuccess && _scenePage == _page;
        _core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
        _core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
            {
                LastTimes = doc.RootElement.TryGetProperty("times", out var tm) ? tm.ToString() : null;
                Ready?.Invoke();
            }
        };
        // the page's fonts are the ones built into the exe (served from memory), whatever the PC has installed
        foreach (var (file, _, _) in Fonts)
            if (System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Fonts/{file}")) is { } res)
            {
                using var m = new MemoryStream();
                res.Stream.CopyTo(m);
                _files[$"/fonts/{file}"] = (m.ToArray(), "font/ttf");
            }
    }

    string FontFaces => string.Concat(Fonts.Where(f => _files.ContainsKey($"/fonts/{f.File}"))
        .Select(f => $"@font-face{{font-family:\"{f.Family}\";src:url(/fonts/{f.File}) format(\"truetype\");font-weight:{f.Weight};font-display:block}}"));

    void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath;
        e.Response = _files.TryGetValue(path, out var f)
            ? _core!.Environment.CreateWebResourceResponse(new MemoryStream(f.Data, false), 200, "OK", $"Content-Type: {f.Mime}\nCache-Control: no-store")
            : _core!.Environment.CreateWebResourceResponse(null, 404, "Not found", "");
    }

    /// <summary>Keep binary data for the next scene (a <see cref="PreviewScene.BlobSink"/>); returns its path.</summary>
    public string PutForNext(byte[] data)
    {
        var path = $"/blob/{Interlocked.Increment(ref _next)}";
        _files[path] = (data, "application/octet-stream");
        lock (_keep) _keep.Add(path);
        return path;
    }

    void Page(string body, string script)
    {
        // a new scene replaces the old one: its data is let go
        lock (_keep)
        {
            foreach (var k in _files.Keys.Where(k => k.StartsWith("/blob/", StringComparison.Ordinal)).ToList())
                if (!_keep.Contains(k)) _files.TryRemove(k, out _);
            _keep.Clear();
        }
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Swapper</title><style>
            {{FontFaces}}
            html,body{margin:0;height:100%;overflow:hidden;background:#111111}
            {{Viewer.Css}}
            .empty{height:100%;display:flex;align-items:center;justify-content:center;text-align:center;padding:0 30px;box-sizing:border-box;font:700 13px "Roboto Mono",Consolas,monospace;color:#bfb2ea;background:#111111}
            #busy{position:fixed;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;background:rgba(17,17,17,.5);opacity:0;transition:opacity .22s ease-in-out;pointer-events:none;z-index:50}
            #busy.on{opacity:1}
            #busy .ring{width:28px;height:28px;box-sizing:border-box;border:3px solid rgba(191,178,234,.25);border-top-color:#e9e2ff;border-radius:50%;animation:busyspin .8s linear infinite}
            #busy .txt{font:700 12px "Roboto Mono",Consolas,monospace;color:#e9e2ff;letter-spacing:.02em;background:rgba(28,0,64,.85);padding:7px 14px}
            @keyframes busyspin{to{transform:rotate(360deg)} }
            </style></head><body>{{body}}<script>{{script}}</script></body></html>
            """;
        _files["/index.html"] = (Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        _swappable = false;
        _core?.Navigate($"{Origin}/index.html?p={++_page}");
    }

    public void ShowMessage(string text) => Page($"<div class=\"empty\">{Viewer.E(text)}</div>", "");

    /// <summary>A still picture of the scene shown (once it is ready): PNG bytes, or null. <paramref name="optionsJson"/>
    /// is the viewer's snapshot options ({w,h,view} or a camera {t,yaw,pitch,dist,fov}).</summary>
    public async Task<byte[]?> SnapshotAsync(string optionsJson)
    {
        if (_core is null) return null;
        var json = await _core.ExecuteScriptAsync($"window.__pv?window.__pv.snapshot({optionsJson}):null");
        if (JsonSerializer.Deserialize<string?>(json) is not { } url || !url.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) return null;
        return Convert.FromBase64String(url["data:image/png;base64,".Length..]);
    }

    /// <summary>Show cars (built by <paramref name="sc"/>, their blobs already handed over with <see cref="PutForNext"/>).</summary>
    public void ShowScene(PreviewScene sc, IReadOnlyList<Dictionary<string, object?>> cars, Viewer.Options options)
    {
        _files["/scene.json"] = (Encoding.UTF8.GetBytes(Viewer.Json(sc, cars)), "application/json");
        var body = Viewer.Markup("pv", cars, options with { Full = true });
        // __swap: the next scene into this same page (ShowSceneInPlace); a new page takes about 2 s before its script runs
        var script = Viewer.Script + $$"""

            window.__start=async()=>{const t0=performance.now(),root=document.getElementById('pv');
              const data=await (await fetch('/scene.json?s='+(++window.__n))).json();const t1=performance.now();
              const api=window.__pv=StudioViewerInit(root,{data,mode:window.__mode});const t2=performance.now();
              api.onReady=()=>{window.__idle();window.chrome.webview.postMessage({type:'ready',times:{start:Math.round(t0),json:Math.round(t1-t0),init:Math.round(t2-t1),data:Math.round(performance.now()-t2) } });};};
            window.__busy=t=>{let o=document.getElementById('busy');
              if(!o){o=document.createElement('div');o.id='busy';o.innerHTML='<div class="ring"></div><div class="txt"></div>';document.body.appendChild(o);}
              o.querySelector('.txt').textContent=t;requestAnimationFrame(()=>requestAnimationFrame(()=>o.classList.add('on')));};
            window.__idle=()=>{const o=document.getElementById('busy');if(o)o.classList.remove('on');};
            window.__n=0;window.__mode='{{options.Mode}}';
            window.__swap=(html,mode)=>{for(const c of document.querySelectorAll('canvas')){const g=c.getContext('webgl2'),x=g&&g.getExtension('WEBGL_lose_context');if(x)x.loseContext();}
              const busy=document.getElementById('busy');window.__pv=null;window.__mode=mode;document.body.innerHTML=html;
              if(busy)document.body.appendChild(busy);window.__start();};
            window.__start();
            """;
        Page(body, script);
        _scenePage = _page;
    }

    /// <summary>
    /// The loading layer over the car shown (dimmed, a spinning ring and <paramref name="text"/>), while the next one is
    /// made; the next scene's first draw takes it away. Only over a scene page that has loaded (a new page shows its own
    /// "Loading 3D data").
    /// </summary>
    public void ShowBusy(string text)
    {
        if (_core is null || !_swappable) return;
        _ = _core.ExecuteScriptAsync($"window.__busy&&window.__busy({JsonSerializer.Serialize(text)})");
    }

    /// <summary>Take the loading layer away (the change was given up, e.g. it failed).</summary>
    public void HideBusy()
    {
        if (_core is null || !_swappable) return;
        _ = _core.ExecuteScriptAsync("window.__idle&&window.__idle()");
    }

    /// <summary>The page number of the last scene page, and whether it has loaded (so it can take scenes in place).</summary>
    int _scenePage;
    bool _swappable;

    /// <summary>
    /// As <see cref="ShowScene"/>, but into the scene page already loaded (its viewer is let go and a new one starts on the
    /// new data): about 0.2 s a scene instead of 2.5 s. When no scene page has loaded yet (the first scene, after a
    /// message, or while one is still loading), the page is loaded as <see cref="ShowScene"/> does.
    /// </summary>
    public async Task ShowSceneInPlace(PreviewScene sc, IReadOnlyList<Dictionary<string, object?>> cars, Viewer.Options options)
    {
        if (_core is null || !_swappable)
        {
            ShowScene(sc, cars, options);
            return;
        }
        lock (_keep)
        {
            foreach (var k in _files.Keys.Where(k => k.StartsWith("/blob/", StringComparison.Ordinal)).ToList())
                if (!_keep.Contains(k)) _files.TryRemove(k, out _);
            _keep.Clear();
        }
        _files["/scene.json"] = (Encoding.UTF8.GetBytes(Viewer.Json(sc, cars)), "application/json");
        var body = Viewer.Markup("pv", cars, options with { Full = true });
        await _core.ExecuteScriptAsync($"window.__swap({JsonSerializer.Serialize(body)},{JsonSerializer.Serialize(options.Mode)})");
    }
}
