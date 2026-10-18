using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HelmetSwap.App;

/// <summary>
/// The in-app 3D preview: a WebView2 showing the preview page (<see cref="Core.HelmetPage"/>, embedded mode) served from
/// memory at https://helmet.local/ (no files, no network). The parts are switched by message once the page is ready.
/// </summary>
sealed class PreviewHost(WebView2 view)
{
    const string Origin = "https://helmet.local";
    byte[] _page = [];
    readonly List<string> _queue = [];
    int _n;
    bool _ready;
    CoreWebView2? _core;

    /// <summary>The page has loaded its data and drawn.</summary>
    public event Action? Ready;
    /// <summary>A part button was clicked on the page (the new SwapParts bits).</summary>
    public event Action<int>? PartsPicked;

    internal static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelmetSwapper", "WebView2");

    public async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
        await view.EnsureCoreWebView2Async(env);
        _core = view.CoreWebView2;
        view.AllowExternalDrop = false;
        _core.Settings.AreDefaultContextMenusEnabled = false;
        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.IsZoomControlEnabled = false;
        _core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += (_, e) =>
        {
            e.Response = new Uri(e.Request.Uri).AbsolutePath == "/index.html"
                ? _core.Environment.CreateWebResourceResponse(new MemoryStream(_page, false), 200, "OK", "Content-Type: text/html; charset=utf-8\nCache-Control: no-store")
                : _core.Environment.CreateWebResourceResponse(null, 404, "Not found", "");
        };
        _core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
        _core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "parts" && doc.RootElement.TryGetProperty("p", out var pv))
                PartsPicked?.Invoke(pv.GetInt32());
            else if (doc.RootElement.TryGetProperty("type", out t) && t.GetString() == "ready")
            {
                _ready = true;
                foreach (var m in _queue) _core.PostWebMessageAsJson(m);
                _queue.Clear();
                Ready?.Invoke();
            }
        };
    }

    // the loading layer (as Swapper's): the helmet shown dims under a spinning ring and a note while the next one is built;
    // the next page starts with it on and fades it out once its helmet is drawn
    const string BusyCss = """
        #busy{position:fixed;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;background:rgba(17,17,17,.5);opacity:0;transition:opacity .22s ease-in-out;pointer-events:none;z-index:50}
        #busy.on{opacity:1}
        #busy .ring{width:28px;height:28px;box-sizing:border-box;border:3px solid rgba(191,178,234,.25);border-top-color:#e9e2ff;border-radius:50%;animation:busyspin .8s linear infinite}
        #busy .txt{font:700 12px "Roboto Mono",Consolas,monospace;color:#e9e2ff;letter-spacing:.02em;background:rgba(28,0,64,.85);padding:7px 14px}
        @keyframes busyspin{to{transform:rotate(360deg)}}
        """;
    const string BusyJs = """
        window.__busy=t=>{let o=document.getElementById('busy');
          if(!o){o=document.createElement('div');o.id='busy';o.innerHTML='<div class="ring"></div><div class="txt"></div>';document.body.appendChild(o);}
          o.querySelector('.txt').textContent=t;requestAnimationFrame(()=>requestAnimationFrame(()=>o.classList.add('on')));};
        window.__idle=()=>{const o=document.getElementById('busy');if(o)o.classList.remove('on');};
        """;

    /// <summary>Show a page (replaces the one shown); <paramref name="busy"/>: it starts under the loading layer with this
    /// note, until its helmet is drawn.</summary>
    public void ShowHtml(string html, string? busy = null)
    {
        if (_core is null) return;
        var layer = $"<style>{BusyCss}</style><script>{BusyJs}</script>" +
                    (busy is null ? "" : $"""<div id="busy" class="on"><div class="ring"></div><div class="txt">{WebUtility.HtmlEncode(busy)}</div></div>""");
        // first thing in the body: the page paints before the helmet's data (most of the page) is read
        int body = html.IndexOf("<body", StringComparison.Ordinal), open = body < 0 ? -1 : html.IndexOf('>', body);
        html = open < 0 ? layer + html : html.Insert(open + 1, layer);
        _page = Encoding.UTF8.GetBytes(html);
        _ready = false;
        _queue.Clear();
        _core.Navigate($"{Origin}/index.html?p={++_n}");
    }

    /// <summary>The app's embedded Roboto Mono Bold as an @font-face rule (data URI), so the note shows it on a PC that
    /// doesn't have it installed (the preview page itself gets its fonts from <c>MainWindow.FontCss</c>).</summary>
    static readonly Lazy<string> NoteFont = new(() =>
    {
        try
        {
            using var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Fonts/RobotoMono-Bold.ttf"))!.Stream;
            using var ms = new MemoryStream(); s.CopyTo(ms);
            return $"@font-face{{font-family:\"Roboto Mono\";font-weight:700;font-style:normal;src:url(data:font/ttf;base64,{Convert.ToBase64String(ms.ToArray())}) format(\"truetype\")}}";
        }
        catch (Exception) { return ""; }   // a missing font: the note falls back to Consolas, as before
    });

    /// <summary>A plain note in place of the 3D view.</summary>
    public void ShowMessage(string text) => ShowHtml($$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        {{NoteFont.Value}}
        html,body{margin:0;height:100%;background:#111111}
        div{height:100%;display:flex;align-items:center;justify-content:center;text-align:center;padding:0 30px;box-sizing:border-box;
          font:700 12.5px "Roboto Mono",Consolas,monospace;color:#bfb2ea}
        </style></head><body><div>{{WebUtility.HtmlEncode(text)}}</div></body></html>
        """);

    /// <summary>Dim the helmet shown under the loading layer, with a note ("building the preview…"), while the next is made.</summary>
    public void ShowBusy(string text)
    {
        if (_core is null || !_ready) return;
        _ = _core.ExecuteScriptAsync($"window.__busy&&window.__busy({JsonSerializer.Serialize(text)})");
    }

    /// <summary>Take the loading layer away (the helmet shown stays).</summary>
    public void HideBusy()
    {
        if (_core is null) return;
        _ = _core.ExecuteScriptAsync("window.__idle&&window.__idle()");
    }

    /// <summary>The parts the after helmet takes from the donor (SwapParts bits).</summary>
    public void SetParts(int parts) => Send(JsonSerializer.Serialize(new { type = "parts", p = parts }));

    void Send(string json)
    {
        if (_core is null) return;
        if (_ready) _core.PostWebMessageAsJson(json);
        else _queue.Add(json);
    }
}
