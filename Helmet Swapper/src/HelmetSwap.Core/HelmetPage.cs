using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BCnEncoder.Decoder;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;

namespace HelmetSwap.Core;

/// <summary>
/// The interactive 3D preview page: the target's helmet before, after the swap, the donor's own, or before and after
/// side by side, with the parts (design, visor, wing, attachments) switchable on the page. Real geometry (the shell and
/// the helmet parts model, LOD 0, split into wing / attachments exactly as the swap splits them), each slot's material
/// from the driver's material binding and its textures (colour, normal map, surface map: R = occlusion, G = roughness,
/// B = metallic), the visor as tinted glass (metallic visors reflect in their tint colour) and the clear parts as clear
/// plastic that shows, slightly bent and frosted, what is behind it. A self-contained WebGL2 page, as the Steering
/// Wheel Swapper's preview: no internet, no libraries.
/// </summary>
public static class HelmetPage
{
    /// <summary>The page for <paramref name="target"/> with <paramref name="donor"/>'s <paramref name="parts"/>; with no
    /// donor, just the target's own helmet. <paramref name="embedded"/>: for the app's preview panel (fills the window, no
    /// page header; the app's part boxes drive the parts by message, and the page says when it is ready).</summary>
    /// <paramref name="fontCss"/>: @font-face rules for the RM Designs fonts (Presicav, Roboto Mono), so the page shows them on a
    /// PC that doesn't have them installed (the app passes its embedded fonts).
    public static string Page(GameInstall game, Driver target, Driver? donor, SwapParts parts, int textureSize = 2048, bool embedded = false, string? fontCss = null) =>
        Page(game, target, donor is null ? null : PartPicks.Of(donor, SwapParts.All), donor is null ? 0 : parts, textureSize, embedded, fontCss);

    /// <summary>The page for <paramref name="target"/> with each part from its pick (<paramref name="picks"/>: the helmet each
    /// part comes from; one helmet for all = the classic swap), <paramref name="parts"/> of them switched on in the after view
    /// (the part buttons, or the app's slots by message, switch them); null picks: the target's own helmet.</summary>
    public static string Page(GameInstall game, Driver target, PartPicks? picks, SwapParts parts, int textureSize = 2048, bool embedded = false, string? fontCss = null)
    {
        if (picks is { IsEmpty: true }) picks = null;
        var donors = picks?.Donors ?? [];
        Driver? donor = donors.FirstOrDefault();
        bool mixed = donors.Count > 1;
        var sc = new HelmetScene(game) { TexSize = textureSize };
        var drivers = new List<Dictionary<string, object?>> { sc.Driver(target) };
        foreach (var d in donors) drivers.Add(sc.Driver(d));
        if (donors.Count == 0) drivers.Add(drivers[0]);
        sc.Finish();
        var data = new Dictionary<string, object?>
        {
            ["tex"] = sc.Textures, ["mats"] = sc.Materials, ["geo"] = sc.Geometry,
            ["drivers"] = drivers,
            // part bit -> the driver it comes from (index into drivers), for every picked part
            ["src"] = PartPicks.Each.Where(p => picks?[p] is not null).ToDictionary(p => ((int)p).ToString(), p => 1 + donors.IndexOf(picks![p]!)),
            ["parts"] = (int)(parts & (picks?.Parts ?? 0)),
            ["single"] = donor is null,
            ["mixed"] = mixed,
            ["embedded"] = embedded,
            // another make's design needs the target's own shell binding (to hide their shell)
            ["designLocked"] = picks?[SwapParts.Design] is { } dd && target.Shell != dd.Shell && !target.OwnShellBinding,
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string Who(Driver x) => x.Name + (x.YearTag == "" ? "" : " " + x.YearTag);
        var from = donor ?? target;
        var sides = donor is null ? "" :
            $"""<div class="pv-seg"><button data-side="before">Before</button><button data-side="after">After</button>{(mixed ? "" : $"""<button data-side="donor">{E(donor.ShortName)}</button>""")}<button data-side="both" class="on">Side by side</button></div>""";
        // the part buttons: the parts picked (a part with no helmet picked for it stays the driver's own)
        var partButtons = donor is null ? "" : mixed
            ? "<div class=\"pv-seg pv-parts\">" + string.Concat(new[] { (1, "Shell"), (2, "Visor"), (16, "Visor strip"), (4, "Wing"), (8, "Attachments") }
                  .Where(x => picks![(SwapParts)x.Item1] is not null).Select(x => $"""<button data-part="{x.Item1}">{x.Item2}</button>""")) + "</div>" :
            """<div class="pv-seg pv-parts"><button data-part="1">Shell</button><button data-part="2">Visor</button><button data-part="16">Visor strip</button><button data-part="4">Wing</button><button data-part="8">Attachments</button></div>""";
        var swapTips = donor is null ? "" : mixed ? $"""
                    <div><b>Before / After</b><span>{E(target.Name.ToLowerInvariant())}'s own helmet / with the parts picked</span></div>
                    <div><b>Parts</b><span>switch a picked part on or off in the after helmet</span></div>
""" : $"""
                    <div><b>Before / After</b><span>{E(target.Name.ToLowerInvariant())}'s own helmet / with the swap</span></div>
                    <div><b>{E(donor.ShortName)}</b><span>the donor's own helmet</span></div>
                    <div><b>Parts</b><span>which parts the after helmet takes from {E(donor.ShortName.ToLowerInvariant())}</span></div>
""";
        var viewer = $$"""
            <div class="pv" id="pv">
              <div class="pv-bar">
                {{sides}}{{partButtons}}
                <div class="pv-seg pv-themes"><button data-theme="purple">Purple</button><button data-theme="grey">Grey</button><button data-theme="white">White</button></div>
                <div class="pv-seg"><button class="pv-clay" title="Untextured: plain clay, to see the shapes (shell, wing, strip, vents)">Untextured</button></div>
                <div class="pv-seg pv-views"></div>
                <div class="pv-help" tabindex="0"><button type="button">Controls</button>
                  <div class="pv-tip">
                    <div><b>Left-drag</b><span>turn the camera</span></div>
                    <div><b>Right-drag</b><span>move (hand tool) · or Shift + drag</span></div>
                    <div><b>Scroll</b><span>zoom in / out</span></div>
                    <div><b>Double-click</b><span>back to the selected view</span></div>{{swapTips}}
                    <div><b>Studio</b><span>purple, grey or white background</span></div>
                    <div><b>Untextured</b><span>plain clay, to see the shapes</span></div>
                  </div></div>
              </div>
              <div class="pv-stage"><canvas class="pv-canvas"></canvas>
                <div class="pv-lab pv-lab-a"></div><div class="pv-lab pv-lab-b"></div><div class="pv-msg"></div></div>
              <script type="application/json" class="pv-data">{{json.Replace("</", "<\\/")}}</script>
            </div>
            """;
        var title = donor is null ? $"{E(Who(target))} ({E(target.ShellName)})"
            : mixed ? $"{E(string.Join(" + ", donors.Select(Who)))} → {E(Who(target))} ({E(target.ShellName)})"
            : $"{E(Who(from))} ({E(from.ShellName)}) → {E(Who(target))} ({E(target.ShellName)})";
        var body = embedded ? viewer : $$"""
            <div class="band"><div class="band-in"><b>Helmet preview</b><span>{{title}}</span></div></div>
            <main>
            {{viewer}}
            <p class="note">*an approximation of the in-game look: real geometry, materials and textures, simplified lighting · parts from another make may not sit perfectly on the shell, which the after view shows</p>
            </main>
            """;
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Helmet Preview</title><style>{{fontCss}}{{Css}}</style></head><body class="{{(embedded ? "page embed" : "page")}}">
            {{body}}
            <script>{{Script}}
            HelmetPreviewInit(document.getElementById("pv"));</script>
            </body></html>
            """;
    }

    static string E(string s) => WebUtility.HtmlEncode(s);

    // ---------------------------------------------------------------- hover pictures

    /// <summary>The data of one driver's own helmet for <see cref="SnapPage"/> (the page's JSON, fetched by it).</summary>
    public static string SnapData(GameInstall game, Driver d, int textureSize = 1024)
    {
        var sc = new HelmetScene(game) { TexSize = textureSize };
        var own = sc.Driver(d);
        sc.Finish();
        var data = new Dictionary<string, object?>
        {
            ["tex"] = sc.Textures, ["mats"] = sc.Materials, ["geo"] = sc.Geometry, ["drivers"] = new[] { own, own },
            ["src"] = new Dictionary<string, int>(), ["parts"] = 0, ["single"] = true, ["mixed"] = false, ["embedded"] = true,
            ["designLocked"] = false, ["snap"] = true,
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// The page the app's hover pictures are drawn on (in a viewer that is never shown): loaded once; for each helmet the app
    /// sends {type:'snap', id, url, w, h}, the page fetches the helmet's data (<see cref="SnapData"/>) from the url, draws it
    /// with the preview's own renderer in the 3/4 view on a see-through background, and answers {type:'snap', id, png} (a PNG
    /// data URL) or {type:'snap', id, error}. The previous helmet's WebGL context is let go first.
    /// </summary>
    public static string SnapPage() => $$$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Helmet pictures</title><style>{{{Css}}}</style></head><body class="page embed">
        <div id="host"></div>
        <script>{{{Script}}}
        async function snap(url,w,h){
          const old=document.querySelector('#pv canvas');
          if(old){const g=old.getContext('webgl2');const l=g&&g.getExtension('WEBGL_lose_context');if(l)l.loseContext();}
          const text=await (await fetch(url)).text();
          const host=document.getElementById('host');
          host.innerHTML='<div class="pv" id="pv" style="width:'+w+'px;height:'+h+'px"><div class="pv-bar" style="display:none"><div class="pv-seg pv-views"></div><button class="pv-clay"></button></div>'+
            '<div class="pv-stage"><canvas class="pv-canvas"></canvas><div class="pv-lab pv-lab-a"></div><div class="pv-lab pv-lab-b"></div><div class="pv-msg"></div></div>'+
            '<script type="application/json" class="pv-data"></'+'script></div>';
          const root=document.getElementById('pv');root.querySelector('.pv-data').textContent=text;
          const done=new Promise((ok,no)=>{root.addEventListener('hpv-ready',ok,{once:true});root.addEventListener('hpv-fail',e=>no(e.detail),{once:true});});
          HelmetPreviewInit(root);
          await done;
          const png=root.hpv.snapshot(w,h);if(!png)throw 'nothing drawn';return png;}
        const wv=window.chrome.webview;
        wv.addEventListener('message',async e=>{const m=e.data;if(!m||m.type!=='snap')return;
          try{wv.postMessage({type:'snap',id:m.id,png:await snap(m.url,m.w,m.h)});}catch(err){wv.postMessage({type:'snap',id:m.id,error:String(err)});}});
        wv.postMessage({type:'page'});
        </script></body></html>
        """;

    // ---------------------------------------------------------------- page

    const string Css = """
        .page{margin:0;background:#F9F9F9;color:#111;font:14px/1.5 "Presicav","Segoe UI",system-ui,sans-serif}
        .page .band{background:#4300AD}
        .page .band-in{max-width:1280px;margin:0 auto;padding:16px 20px;display:flex;flex-wrap:wrap;gap:6px 18px;align-items:center;justify-content:space-between;color:#F9F9F9}
        .page .band b{font-weight:800;letter-spacing:.16em;text-transform:uppercase;font-size:13px}
        .page .band span{font:700 12.5px "Roboto Mono",Consolas,monospace}
        .page main{max-width:1280px;margin:0 auto;padding:18px 20px 30px}
        .note{font:700 12px "Roboto Mono",Consolas,monospace;color:#4300AD;margin:10px 0 0}
        .pv{position:relative;background:#111111;border:1px solid #2b2140}
        .pv-bar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;padding:8px;background:#1C0040}
        .pv-seg{display:flex}
        .pv button{font:700 10.5px "Presicav","Segoe UI",sans-serif;letter-spacing:.12em;text-transform:uppercase;color:#E9E2FF;
          background:transparent;border:1px solid #4b3a74;padding:7px 11px;cursor:pointer}
        .pv-seg button+button{border-left:0}
        .pv button.on{background:#4300AD;border-color:#4300AD;color:#fff}
        .pv button:disabled{opacity:.35;cursor:not-allowed}
        .pv-parts button.on{background:#2a0a6e;border-color:#6a4bd0}
        .pv-stage{position:relative}
        .pv-canvas{display:block;width:100%;height:min(72vh,680px);cursor:grab;touch-action:none}
        .pv-canvas:active{cursor:grabbing}
        .pv-help{margin-left:auto;position:relative}
        .pv-help .pv-tip{display:none;position:absolute;right:0;top:calc(100% + 6px);z-index:5;background:#1C0040;border:1px solid #4300AD;
          padding:10px 14px;box-shadow:0 6px 18px rgba(0,0,0,.35);font:700 11.5px/1.9 "Roboto Mono",Consolas,monospace;color:#E9E2FF;white-space:nowrap}
        .pv-help:hover .pv-tip,.pv-help:focus-within .pv-tip{display:block}
        .pv-tip div{display:flex;gap:14px}.pv-tip b{min-width:118px;color:#fff;font-family:"Presicav","Segoe UI",sans-serif;font-size:9.5px;letter-spacing:.12em;text-transform:uppercase}
        .pv-msg{position:absolute;left:12px;bottom:10px;font:700 12px "Roboto Mono",Consolas,monospace;color:#bfb2ea;pointer-events:none}
        .pv-lab{position:absolute;top:10px;font:700 12px "Roboto Mono",Consolas,monospace;color:#bfb2ea;pointer-events:none}
        .pv-lab-a{left:12px}.pv-lab-b{left:calc(50% + 12px)}
        .pv-lab b{display:block;font:800 10.5px "Presicav","Segoe UI",sans-serif;letter-spacing:.14em;text-transform:uppercase}
        html:has(.embed),.embed{height:100%;overflow:hidden;background:#111111}
        .embed .pv{height:100%;display:flex;flex-direction:column;border:0}
        .embed .pv-stage{flex:1;min-height:0}
        .embed .pv-canvas{height:100%}
        """;

    // A small WebGL2 renderer: GGX lighting (key + fill) in a studio environment with a shadow-mapped floor; the helmet
    // paint with its normal and surface maps; glass drawn over a resolved copy of the scene so it can show what is behind
    // it, tinted (visor) or clear and slightly frosted (clear plastic), with Fresnel reflections; 4x MSAA.
    const string Script = """
        function HelmetPreviewInit(root){
        if(!root)return;
        const data=JSON.parse(root.querySelector('.pv-data').textContent);
        const canvas=root.querySelector('.pv-canvas'),msg=root.querySelector('.pv-msg'),labA=root.querySelector('.pv-lab-a'),labB=root.querySelector('.pv-lab-b');
        const gl=canvas.getContext('webgl2',{antialias:false,alpha:false});
        if(!gl){msg.textContent='3D preview needs WebGL2 (any current browser).';return;}
        const VS=`#version 300 es
        in vec3 aPos;in vec4 aNrm;in vec4 aTan;in vec4 aUv;
        uniform mat4 uVP,uLightVP;uniform float uLift;uniform vec3 uOff,uScale;out vec3 vPos;out vec3 vN;out vec4 vT;out vec2 vUv;out vec4 vSh;
        void main(){vec3 p=aPos*uScale+uOff+vec3(0.,uLift,0.);vPos=p;vN=aNrm.xyz/uScale;vT=aTan;vUv=aUv.xy;vSh=uLightVP*vec4(p,1.);gl_Position=uVP*vec4(p,1.);}`;
        const SVS=`#version 300 es
        in vec3 aPos;uniform mat4 uLightVP;void main(){gl_Position=uLightVP*vec4(aPos,1.);}`;
        const SFS=`#version 300 es
        precision mediump float;out vec4 o;void main(){o=vec4(1.);}`;
        const FS=`#version 300 es
        precision highp float;
        in vec3 vPos;in vec3 vN;in vec4 vT;in vec2 vUv;in vec4 vSh;
        uniform highp sampler2DShadow uShadow;uniform int uHasShadow;
        uniform sampler2D uD,uN,uS,uScene;uniform int uKind,uClay,uSnap;uniform vec3 uRoom,uGlow,uFloor,uEye;uniform vec4 uHas,uP0,uP1,uClip;uniform vec2 uScreen;uniform mat4 uV;
        out vec4 o;
        const float PI=3.14159265;
        // a neutral studio: an overhead softbox, low strip lights and a soft side glow (the theme's colour)
        vec3 env(vec3 r,float rough){float w=mix(.02,.55,rough);vec3 c=uRoom;
          c+=vec3(2.4)*smoothstep(.8-w,.8+w*.5,r.y);
          c+=vec3(1.)*(1.-smoothstep(0.,.05+w,abs(r.y-.2)))*(.55+.45*abs(r.x));
          c+=uGlow*smoothstep(.1-w,.7,-r.x)*(1.-smoothstep(0.,.45+w,abs(r.y-.25)));
          return mix(c,uRoom+vec3(.14),rough*rough*.5);}
        float D(float nh,float a){float a2=a*a;float d=nh*nh*(a2-1.)+1.;return a2/(PI*d*d);}
        float G(float nv,float nl,float a){float k=(a+1.)*(a+1.)/8.;return nv/(nv*(1.-k)+k)*nl/(nl*(1.-k)+k);}
        vec3 F(float c,vec3 f0){return f0+(1.-f0)*pow(1.-c,5.);}
        vec3 lin(vec3 c){return pow(c,vec3(2.2));}
        vec3 tm(vec3 c){return clamp(c*(2.51*c+.03)/(c*(2.43*c+.59)+.14),0.,1.);}
        // normal maps: RG = tangent-space XY (B is stored as 0), Z rebuilt. The tangent follows +u; cross(n,t)*w follows -v,
        // and the maps' green points along +v (DirectX style, checked on the maps themselves), hence -y
        vec3 nmap(vec2 uv,vec3 n){vec3 t=vT.xyz-n*dot(n,vT.xyz);if(dot(t,t)<1e-8)return n;t=normalize(t);vec3 b=cross(n,t)*(vT.w<0.?-1.:1.);
          vec2 xy=texture(uN,uv).rg*2.-1.;vec3 tn=vec3(xy.x,-xy.y,sqrt(max(0.,1.-dot(xy,xy))));return normalize(t*tn.x+b*tn.y+n*tn.z);}
        float shadowAt(){if(uHasShadow==0)return 1.;vec3 p=vSh.xyz/vSh.w*.5+.5;if(any(lessThan(p,vec3(0.)))||any(greaterThan(p,vec3(1.))))return 1.;
          vec2 t=1./vec2(textureSize(uShadow,0));float s=0.;for(int x=-2;x<=2;x++)for(int y=-2;y<=2;y++)s+=texture(uShadow,vec3(p.xy+vec2(x,y)*t,p.z-.0006));return s/25.;}
        void main(){
          vec3 n=normalize(vN);bool back=!gl_FrontFacing;if(back)n=-n;float sh0=shadowAt();float sh=mix(.3,1.,sh0);
          if(uKind==9){float r=length(vPos.xz-uP0.xy);float cont=1.-smoothstep(uP0.z*.5,uP0.z*1.4,r);float fade=1.-smoothstep(uP0.w*.45,uP0.w,r);
            o=vec4(pow(mix(uFloor,uFloor*.07,clamp((1.-sh0)*.85+cont*.3,0.,1.)),vec3(1./2.2)),fade*uP1.x);return;}
          vec2 uv=vUv;vec3 v=normalize(uEye-vPos);
          vec3 L[2];L[0]=normalize(vec3(-.35,.8,.6));L[1]=normalize(vec3(.65,.3,-.5));vec3 C[2];C[0]=vec3(3.1);C[1]=vec3(.85,.9,1.05);
          if(uKind==0){
            // helmet paint, solid wing, visor strip: colour, normal map, surface map (R = occlusion, G = roughness, B = metallic)
            vec3 alb=vec3(.5);float rough=.45,metal=0.,occ=1.;
            if(back){alb=vec3(.018);rough=.92;}   // the inside of the shell: the dark lining (seen through the visor)
            else if(uClay==1){alb=vec3(.6);rough=.42;}   // untextured: plain clay, the shapes only
            else{if(uHas.x>0.)alb=lin(texture(uD,uv).rgb);
              if(uHas.z>0.){vec4 s=texture(uS,uv);occ=mix(.25,1.,s.r);rough=clamp(s.g,.03,1.);metal=s.b;}
              if(uHas.y>0.)n=nmap(uv,n);}
            // specular anti-aliasing: where the (normal-mapped) surface turns faster than a pixel - vent grilles, seams,
            // small embossing - widen the highlight instead of letting it sparkle
            vec3 dx=dFdx(n),dy=dFdy(n);float a=sqrt(clamp(rough*rough*rough*rough+min(.5*(dot(dx,dx)+dot(dy,dy)),.18),0.,1.));rough=sqrt(a);
            float nv=max(dot(n,v),1e-4);vec3 f0=mix(vec3(.04),alb,metal);vec3 col=vec3(0.);
            for(int i=0;i<2;i++){vec3 l=L[i];vec3 h=normalize(l+v);float nl=max(dot(n,l),0.);
              if(nl>0.){float nh=max(dot(n,h),0.);vec3 Fr=F(max(dot(h,v),0.),f0);vec3 spec=D(nh,a)*G(nv,nl,a)*Fr/(4.*nv*nl+1e-4);vec3 kd=(1.-Fr)*(1.-metal);
                col+=(kd*alb/PI+spec)*C[i]*nl*(i==0?sh:1.)*mix(1.,occ,.6);}}
            float ao=occ*mix(.7,1.,sh0)*(back?.35:1.);
            vec3 r=reflect(-v,n);vec3 Fe=F(nv,f0);
            col+=((1.-Fe)*(1.-metal)*alb*env(n,1.)*.55+Fe*env(r,rough)*(1.-rough*.7)*.6)*ao;
            o=vec4(pow(tm(col),vec3(1./2.2)),1.);return;}
          // glass: what is behind (the scene drawn so far), seen through the part and bent a little by it, plus reflections
          float nv=max(dot(n,v),1e-4);vec3 trans,f0;float rough,bend,lod;
          if(uKind==1){
            // the visor: tinted by its colour sheet; metallic (iridium) visors reflect in that colour; me_alpha = how much
            // of the light the tint takes
            vec3 tint=uClay==1?vec3(.1):uHas.x>0.?lin(texture(uD,uv).rgb):vec3(.05);float metal=0.;rough=.06;
            if(uHas.z>0.&&uClay==0){vec4 s=texture(uS,uv);rough=clamp(s.g,.02,1.);metal=s.b;}
            float al=uP0.x;f0=mix(vec3(.02),tint,metal);trans=vec3(1.-al)+al*mix(tint,vec3(.12),metal);bend=.006;lod=rough*5.;}
          else if(uKind==4){
            // the tear-offs: a film over the visor, almost untinted (me_alpha 0.02, IOR 1.33); what shows in the game are
            // its long, stretched highlights (me_anisotropy) and its edges and tabs
            rough=.02;f0=vec3(pow((uP0.z-1.)/(uP0.z+1.),2.));trans=vec3(1.-uP0.x)*mix(1.,.82,pow(1.-nv,3.));bend=0.;lod=0.;}
          else{
            // clear plastic (helmet_transparent): IOR me_refractionIOR, a little frosted (me_roughness); thicker-looking
            // towards its edges, so the shape of a clear wing or vent shows
            // thin plastic barely shifts what is behind it: a small bend, a very light blur
            rough=uP0.y;float ior=uP0.z;f0=vec3(pow((ior-1.)/(ior+1.),2.));trans=vec3(.97,.98,.99)*mix(1.,.62,pow(1.-nv,3.));bend=.0025;lod=.35;}
          vec3 nvw=mat3(uV)*n;
          vec2 suv=clamp(gl_FragCoord.xy/uScreen-nvw.xy*bend,uClip.xy,uClip.zw);
          vec3 behind=lin(textureLod(uScene,suv,lod).rgb);
          vec3 Fv=F(nv,f0);vec3 spec=vec3(0.);float a=max(rough*rough,.002);
          for(int i=0;i<2;i++){vec3 l=L[i];vec3 h=normalize(l+v);float nl=max(dot(n,l),0.);
            if(nl>0.)spec+=D(max(dot(n,h),0.),a)*G(nv,nl,a)*F(max(dot(h,v),0.),f0)/(4.*nv*nl+1e-4)*C[i]*nl*(i==0?sh:1.);}
          if(uKind==4){
            // anisotropic highlights (Ashikhmin-Shirley, exponents me_anisotropy along / across the tangent): the streaks of
            // stacked tear-off film
            vec3 t=vT.xyz-n*dot(n,vT.xyz);spec=vec3(0.);
            if(dot(t,t)>1e-8){t=normalize(t);vec3 b=cross(n,t);float eu=uP1.x,ev=uP1.y;
              for(int i=0;i<2;i++){vec3 l=L[i];vec3 h=normalize(l+v);float nl=max(dot(n,l),0.),nh=max(dot(n,h),1e-4),hk=max(dot(h,v),1e-4);
                if(nl<=0.)continue;float th=dot(h,t),bh=dot(h,b);float ex=(eu*th*th+ev*bh*bh)/max(1.-nh*nh,1e-4);
                spec+=min(sqrt((eu+1.)*(ev+1.))/(8.*PI)*pow(nh,ex)/(hk*max(nl,nv)),40.)*F(hk,f0)*C[i]*nl*(i==0?sh:1.);}}}
          vec3 refl=Fv*env(reflect(-v,n),rough)*.6+spec;
          if(uKind==2)refl+=vec3(.015)*env(n,1.);   // a faint milky haze: the plastic scatters a little light
          vec3 gc=pow(clamp(behind*trans*(1.-Fv)+tm(refl),0.,1.),vec3(1./2.2));
          // a picture on a see-through background: over nothing, glass keeps only its own look (tint, reflections)
          if(uSnap==1){float ab=textureLod(uScene,suv,lod).a;float ag=clamp((uKind==1?.55:uKind==4?.06:.18)+dot(tm(refl),vec3(.45)),0.,1.);
            o=vec4(gc,ab+(1.-ab)*ag);return;}
          o=vec4(gc,1.);}`;
        function sh(t,s){const x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw gl.getShaderInfoLog(x);return x;}
        const ATTR=['aPos','aNrm','aTan','aUv'];
        function program(vs,fs){const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,vs));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,fs));
          ATTR.forEach((a,i)=>gl.bindAttribLocation(p,i,a));gl.linkProgram(p);return p;}
        let prog,sprog;
        try{prog=program(VS,FS);sprog=program(SVS,SFS);}catch(e){msg.textContent='3D preview: '+e;return;}
        if(!gl.getProgramParameter(prog,gl.LINK_STATUS)){msg.textContent='3D preview: '+gl.getProgramInfoLog(prog);return;}
        const U={};const u=n=>U[n]||(U[n]=gl.getUniformLocation(prog,n));
        function bytes(b){const s=atob(b),a=new Uint8Array(s.length);for(let i=0;i<s.length;i++)a[i]=s.charCodeAt(i);return a;}
        async function inflate(b){return new Uint8Array(await new Response(new Blob([bytes(b)]).stream().pipeThrough(new DecompressionStream('deflate'))).arrayBuffer());}
        // textures: block-compressed levels as shipped (BC1/2/3 s3tc, BC4/5 rgtc, BC7 bptc) or plain RGBA
        const ext={s3tc:gl.getExtension('WEBGL_compressed_texture_s3tc'),rgtc:gl.getExtension('EXT_texture_compression_rgtc'),bptc:gl.getExtension('EXT_texture_compression_bptc')};
        const aniso=gl.getExtension('EXT_texture_filter_anisotropic');
        const FMT={bc1:['s3tc',0x83F1],bc2:['s3tc',0x83F2],bc3:['s3tc',0x83F3],bc4:['rgtc',0x8DBB],bc5:['rgtc',0x8DBD],bc7:['bptc',0x8E8C]};
        const missing=new Set(),texs=[];
        function texture(i){if(i==null||i<0)return null;if(texs[i]!==undefined)return texs[i];const t=data.tex[i];if(t.f==='none')return texs[i]=null;
          if(t.f!=='rgba'&&!(FMT[t.f]&&ext[FMT[t.f][0]])){missing.add(t.f);return texs[i]=null;}
          const x=gl.createTexture();gl.activeTexture(gl.TEXTURE8);gl.bindTexture(gl.TEXTURE_2D,x);   // a scratch unit: never one a draw samples
          if(t.f==='rgba'){gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8,t.w,t.h,0,gl.RGBA,gl.UNSIGNED_BYTE,t.l[0]);gl.generateMipmap(gl.TEXTURE_2D);}
          else{t.l.forEach((l,j)=>gl.compressedTexImage2D(gl.TEXTURE_2D,j,FMT[t.f][1],Math.max(1,t.w>>j),Math.max(1,t.h>>j),0,l));
            gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAX_LEVEL,t.l.length-1);}
          t.l=null;return texs[i]=x;}
        const repeat=gl.createSampler();gl.samplerParameteri(repeat,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.samplerParameteri(repeat,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
        gl.samplerParameteri(repeat,gl.TEXTURE_WRAP_S,gl.REPEAT);gl.samplerParameteri(repeat,gl.TEXTURE_WRAP_T,gl.REPEAT);if(aniso)gl.samplerParameterf(repeat,aniso.TEXTURE_MAX_ANISOTROPY_EXT,8);
        // geometry: float32 positions, half normal/tangent/uv, 16/32-bit indices; uploaded on first draw
        function vao(g){if(g.vao)return g.vao;const v=gl.createVertexArray();gl.bindVertexArray(v);
          [['pos',3,gl.FLOAT],['nrm',4,gl.HALF_FLOAT],['tan',4,gl.HALF_FLOAT],['uv',4,gl.HALF_FLOAT]].forEach(([k,n,ty],loc)=>{
            const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,g[k],gl.STATIC_DRAW);gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,n,ty,false,0,0);g[k]=null;});
          const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,g.idx,gl.STATIC_DRAW);g.idx=null;
          gl.bindVertexArray(null);return g.vao=v;}
        let floorV=null;
        function floorVao(){if(floorV)return floorV;const v=gl.createVertexArray();gl.bindVertexArray(v);const S=4;
          const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-S,0,-S,S,0,-S,S,0,S,-S,0,S]),gl.STATIC_DRAW);
          gl.enableVertexAttribArray(0);gl.vertexAttribPointer(0,3,gl.FLOAT,false,0,0);
          const nb=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,nb);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1]),gl.STATIC_DRAW);
          gl.enableVertexAttribArray(1);gl.vertexAttribPointer(1,4,gl.FLOAT,false,0,0);
          const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,new Uint16Array([0,2,1,0,3,2]),gl.STATIC_DRAW);
          gl.bindVertexArray(null);return floorV=v;}
        function material(m){const t=m.t,has=[t.d,t.n,t.s,-1].map(i=>texture(i)?1:0);
          [['uD',t.d],['uN',t.n],['uS',t.s]].forEach(([n,i],k)=>{gl.activeTexture(gl.TEXTURE0+k);gl.bindTexture(gl.TEXTURE_2D,texture(i));gl.bindSampler(k,repeat);gl.uniform1i(u(n),k);});
          gl.uniform1i(u('uKind'),m.k);gl.uniform4fv(u('uHas'),has);gl.uniform4fv(u('uP0'),m.p.slice(0,4));gl.uniform4fv(u('uP1'),m.p.length>4?m.p.slice(4,8):[0,0,0,0]);}
        function drawGroups(list,kind){for(const g of list){const m=data.mats[g.m];if(m.k!==kind)continue;const x=data.geo[g.g];material(m);gl.bindVertexArray(vao(x));
          gl.drawElements(gl.TRIANGLES,x.count,x.w32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT,0);}}

        // ---- the helmets: target (0) and donor (1); a helmet = shell + design from one driver, visor tint from one,
        // wing from one, attachments (clear vents, chin strip, visor tab) from one, as the swap builds it
        const DR=data.drivers,T=DR[0],Dn=DR[1],SRC=data.src||{};let parts=data.parts;
        function compose(p,all){const pick=b=>(p&b)?(all?Dn:DR[SRC[b]]||T):T,des=pick(1),vis=pick(2),wing=pick(4),att=pick(8),strip=pick(16);
          const list=[...des.helmet,...des.visor.map(g=>({g:g.g,m:vis.visorMat,b:g.b})).filter(g=>g.m>=0),...(des.tear||[]).map(g=>({g:g.g,m:vis.tearMat,b:g.b})).filter(g=>g.m>=0),...wing.wing,...strip.strip,...att.rest];
          const bounds=l=>{const b=[9,9,9,-9,-9,-9];for(const g of l)for(let k=0;k<3;k++){b[k]=Math.min(b[k],g.b[k]);b[k+3]=Math.max(b[k+3],g.b[k+3]);}return b;};
          return {list,box:bounds(list),shell:bounds(des.helmet)};}
        function partsText(p){const n=[[1,'shell'],[2,'visor'],[16,'visor strip'],[4,'wing'],[8,'attachments']].filter(([b])=>p&b).map(([,s])=>s);
          return p===31?'whole helmet':p===0?'nothing':n.join(' + ');}
        // parts from several helmets: "shell from Max V, wing from Nico H"
        function mixText(p){const n=[[1,'shell'],[2,'visor'],[16,'visor strip'],[4,'wing'],[8,'attachments']].filter(([b])=>p&b).map(([b,s])=>s+' from '+DR[SRC[b]].short);
          return n.length?n.join(', '):'nothing';}
        const SIDES={before:()=>({...compose(0),title:data.single?'':'Before',name:T.name}),
          after:()=>({...compose(parts),title:'After',name:data.mixed?mixText(parts):partsText(parts)+' from '+Dn.name}),
          donor:()=>({...compose(31,true),title:Dn.short,name:Dn.name+"'s own"})};

        // ---- framebuffers: the scene is drawn into a 4x MSAA buffer; before glass it is resolved into a mipmapped texture
        // the glass samples (sharp for the visor, blurred a little for frosted plastic)
        let W=0,H=0,msFb=null,msC=null,msD=null,scFb=null,scTex=null;
        const samples=Math.min(4,gl.getParameter(gl.MAX_SAMPLES));
        function targets(w,h){if(w===W&&h===H)return;W=w;H=h;
          if(msFb){gl.deleteFramebuffer(msFb);gl.deleteRenderbuffer(msC);gl.deleteRenderbuffer(msD);gl.deleteFramebuffer(scFb);gl.deleteTexture(scTex);}
          msC=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,msC);gl.renderbufferStorageMultisample(gl.RENDERBUFFER,samples,gl.RGBA8,w,h);
          msD=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,msD);gl.renderbufferStorageMultisample(gl.RENDERBUFFER,samples,gl.DEPTH_COMPONENT24,w,h);
          msFb=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,msFb);gl.framebufferRenderbuffer(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.RENDERBUFFER,msC);
          gl.framebufferRenderbuffer(gl.FRAMEBUFFER,gl.DEPTH_ATTACHMENT,gl.RENDERBUFFER,msD);
          scTex=gl.createTexture();gl.activeTexture(gl.TEXTURE8);gl.bindTexture(gl.TEXTURE_2D,scTex);
          gl.texStorage2D(gl.TEXTURE_2D,Math.floor(Math.log2(Math.max(w,h)))+1,gl.RGBA8,w,h);
          gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
          scFb=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,scFb);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,scTex,0);
          gl.bindFramebuffer(gl.FRAMEBUFFER,null);}
        function resolve(){gl.disable(gl.SCISSOR_TEST);gl.bindFramebuffer(gl.READ_FRAMEBUFFER,msFb);gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER,scFb);
          gl.blitFramebuffer(0,0,W,H,0,0,W,H,gl.COLOR_BUFFER_BIT,gl.NEAREST);
          gl.activeTexture(gl.TEXTURE3);gl.bindTexture(gl.TEXTURE_2D,scTex);gl.generateMipmap(gl.TEXTURE_2D);gl.bindFramebuffer(gl.FRAMEBUFFER,msFb);}

        // ---- shadows: one map per view (side by side shows two helmets), re-rendered only when that helmet changes
        const SM=2048,KEY=norm([-.35,.8,.6]);
        const shadows=[0,1].map(()=>{const t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texStorage2D(gl.TEXTURE_2D,1,gl.DEPTH_COMPONENT24,SM,SM);
          gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
          gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_MODE,gl.COMPARE_REF_TO_TEXTURE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_FUNC,gl.LEQUAL);
          const fb=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,fb);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.DEPTH_ATTACHMENT,gl.TEXTURE_2D,t,0);
          gl.drawBuffers([gl.NONE]);gl.readBuffer(gl.NONE);const ok=gl.checkFramebufferStatus(gl.FRAMEBUFFER)===gl.FRAMEBUFFER_COMPLETE;gl.bindFramebuffer(gl.FRAMEBUFFER,null);
          return {t,fb,ok,key:'',vp:I4()};});
        function I4(){return [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];}
        function ortho(r,n,f){return [1/r,0,0,0,0,1/r,0,0,0,0,-2/(f-n),0,0,0,-(f+n)/(f-n),1];}
        function updateShadow(s,side,box){const key=side.list.map(g=>g.g).join(',');if(!s.ok||key===s.key)return;s.key=key;
          const c=[(box[0]+box[3])/2,(box[1]+box[4])/2,(box[2]+box[5])/2],r=Math.hypot(box[3]-box[0],box[4]-box[1],box[5]-box[2])*.75;
          s.vp=mul(ortho(r,.01,r*4),look([c[0]+KEY[0]*r*2,c[1]+KEY[1]*r*2,c[2]+KEY[2]*r*2],c,[0,1,0]));
          gl.bindFramebuffer(gl.FRAMEBUFFER,s.fb);gl.viewport(0,0,SM,SM);gl.disable(gl.SCISSOR_TEST);gl.clear(gl.DEPTH_BUFFER_BIT);
          gl.useProgram(sprog);gl.uniformMatrix4fv(gl.getUniformLocation(sprog,'uLightVP'),false,s.vp);
          gl.enable(gl.DEPTH_TEST);gl.depthMask(true);gl.disable(gl.BLEND);gl.enable(gl.POLYGON_OFFSET_FILL);gl.polygonOffset(1.5,3);
          for(const g of side.list){if(data.mats[g.m].k!==0)continue;const x=data.geo[g.g];gl.bindVertexArray(vao(x));gl.drawElements(gl.TRIANGLES,x.count,x.w32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT,0);}
          gl.disable(gl.POLYGON_OFFSET_FILL);gl.bindVertexArray(null);gl.bindFramebuffer(gl.FRAMEBUFFER,null);}

        // ---- camera: the helmet faces +z; yaw 0 = from the front
        const VIEWS=[['front','Front',{yaw:0,pitch:.08,dist:.78}],['three','3/4',{yaw:-.75,pitch:.22,dist:.8}],['side','Side',{yaw:-Math.PI/2,pitch:.08,dist:.78}],
          ['back','Back',{yaw:Math.PI,pitch:.12,dist:.78}],['top','Top',{yaw:-.4,pitch:1.2,dist:.8}]];
        let cam=null,target=[0,0,0],sideMode=data.single?'before':'both',FOV=30;
        function frame(){const b=SIDES.before().box,a=SIDES.after().box;const lo=[0,1,2].map(k=>Math.min(a[k],b[k])),hi=[0,1,2].map(k=>Math.max(a[k+3],b[k+3]));
          return {c:[(lo[0]+hi[0])/2,(lo[1]+hi[1])/2,(lo[2]+hi[2])/2],lo,hi};}
        function home(v){cam={...v};target=frame().c;}
        function persp(f,a,n,fa){const t=1/Math.tan(f/2);return [t/a,0,0,0,0,t,0,0,0,0,(fa+n)/(n-fa),-1,0,0,2*fa*n/(n-fa),0];}
        function look(e,c,up){const z=norm(sub(e,c)),x=norm(cross(up,z)),y=cross(z,x);return [x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,-dot(x,e),-dot(y,e),-dot(z,e),1];}
        function mul(a,b){const r=new Array(16).fill(0);for(let i=0;i<4;i++)for(let j=0;j<4;j++)for(let k=0;k<4;k++)r[j*4+i]+=a[k*4+i]*b[j*4+k];return r;}
        function sub(a,b){return [a[0]-b[0],a[1]-b[1],a[2]-b[2]];}function dot(a,b){return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];}
        function cross(a,b){return [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];}function norm(a){const l=Math.hypot(...a);return [a[0]/l,a[1]/l,a[2]/l];}
        function eye(k){const d=cam.dist*(k||1);return [target[0]+Math.sin(cam.yaw)*Math.cos(cam.pitch)*d,target[1]+Math.sin(cam.pitch)*d,target[2]+Math.cos(cam.yaw)*Math.cos(cam.pitch)*d];}

        // ---- studio colours (RM Designs palette): purple (#1C0040 / #4300AD glow), grey (#111111), white (#F2F2F2)
        const THEMES={purple:{bg:[.07,.05,.12],room:[.022,.021,.026],glow:[.07,.015,.32],floor:[.17,.155,.21],text:'#bfb2ea'},
          grey:{bg:[.067,.067,.067],room:[.022,.022,.022],glow:[0,0,0],floor:[.17,.17,.17],text:'#bdbdbd'},
          white:{bg:[.949,.949,.949],room:[.42,.42,.42],glow:[0,0,0],floor:[.86,.86,.86],text:'#4300AD'}};
        let theme='purple';try{const t=localStorage.getItem('hpv-theme');if(THEMES[t])theme=t;}catch(e){}
        function setTheme(t){theme=t;root.querySelectorAll('[data-theme]').forEach(x=>x.classList.toggle('on',x.dataset.theme===t));
          canvas.style.background='rgb('+THEMES[t].bg.map(v=>Math.round(Math.pow(v,1/2.2)*255)).join(',')+')';try{localStorage.setItem('hpv-theme',t);}catch(e){}draw();}
        root.querySelectorAll('[data-theme]').forEach(b=>b.onclick=()=>setTheme(b.dataset.theme));
        // untextured (clay): on / off, remembered like the studio
        let clay=false;try{clay=localStorage.getItem('hpv-clay')==='1';}catch(e){}
        const clayBtn=root.querySelector('.pv-clay');
        function setClay(c){clay=c;clayBtn.classList.toggle('on',c);try{localStorage.setItem('hpv-clay',c?'1':'0');}catch(e){}draw();}
        clayBtn.onclick=()=>setClay(!clay);clayBtn.classList.toggle('on',clay);

        let ready=false,pending=false;
        function draw(){if(!ready||pending)return;pending=true;requestAnimationFrame(()=>{pending=false;render();});}
        // snap {w,h}: the driver's own helmet alone on a see-through background, no floor, read back as RGBA (hover pictures)
        function render(snap){
          const dpr=Math.min(window.devicePixelRatio||1,2),w=snap?snap.w:canvas.clientWidth*dpr|0,h=snap?snap.h:canvas.clientHeight*dpr|0;
          if(w<2||h<2)return;
          if(!snap&&(canvas.width!==w||canvas.height!==h)){canvas.width=w;canvas.height=h;}
          targets(w,h);
          const th=THEMES[theme];
          const views=snap?[SIDES.before()]:sideMode==='both'?[SIDES.before(),SIDES.after()]:[SIDES[sideMode]()];
          const vw=w/views.length|0;
          // a tall, narrow view (a narrow preview, or side by side): step back so the helmet still fits across
          const e=eye(Math.max(1,.9*h/vw));
          views.forEach((s,i)=>updateShadow(shadows[i],s,s.box));
          gl.bindFramebuffer(gl.FRAMEBUFFER,msFb);gl.disable(gl.SCISSOR_TEST);gl.viewport(0,0,w,h);
          if(snap)gl.clearColor(0,0,0,0);else gl.clearColor(Math.pow(th.bg[0],1/2.2),Math.pow(th.bg[1],1/2.2),Math.pow(th.bg[2],1/2.2),1);
          gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
          gl.useProgram(prog);gl.enable(gl.DEPTH_TEST);gl.disable(gl.CULL_FACE);gl.uniform1i(u('uSnap'),snap?1:0);
          gl.uniform3fv(u('uRoom'),th.room);gl.uniform1i(u('uClay'),clay&&!snap?1:0);gl.uniform3fv(u('uGlow'),th.glow);gl.uniform3fv(u('uFloor'),th.floor);gl.uniform3fv(u('uEye'),e);
          gl.uniform2fv(u('uScreen'),[w,h]);
          const V=look(e,target,[0,1,0]),VP=mul(persp(FOV*Math.PI/180,vw/h,.02,20),V);gl.uniformMatrix4fv(u('uVP'),false,VP);gl.uniformMatrix4fv(u('uV'),false,V);
          const fr=frame(),rad=Math.hypot(fr.hi[0]-fr.lo[0],fr.hi[2]-fr.lo[2])/2;
          function viewport(i){gl.enable(gl.SCISSOR_TEST);gl.viewport(i*vw,0,vw,h);gl.scissor(i*vw,0,vw,h);
            gl.uniform4fv(u('uClip'),[(i*vw+1)/w,1/h,((i+1)*vw-1)/w,1-1/h]);
            gl.uniformMatrix4fv(u('uLightVP'),false,shadows[i].vp);gl.uniform1i(u('uHasShadow'),shadows[i].ok?1:0);
            gl.activeTexture(gl.TEXTURE7);gl.bindTexture(gl.TEXTURE_2D,shadows[i].t);gl.bindSampler(7,null);gl.uniform1i(u('uShadow'),7);}
          // solid parts, then the floor (with the helmet's shadow), then glass over a copy of what is drawn
          gl.uniform3fv(u('uOff'),[0,0,0]);gl.uniform3fv(u('uScale'),[1,1,1]);
          views.forEach((s,i)=>{viewport(i);gl.disable(gl.BLEND);gl.depthMask(true);drawGroups(s.list,0);
            gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.depthMask(false);gl.uniform1i(u('uKind'),9);
            // the floor fades out as the camera goes below it, so the helmet can be seen from underneath
            const floorA=Math.max(0,Math.min(1,(e[1]-fr.lo[1])/.04));
            gl.uniform4fv(u('uP0'),[fr.c[0],fr.c[2],rad,rad*7]);gl.uniform4fv(u('uP1'),[floorA,0,0,0]);gl.uniform1f(u('uLift'),fr.lo[1]-.001);
            if(floorA>0&&!snap){gl.bindVertexArray(floorVao());gl.drawElements(gl.TRIANGLES,6,gl.UNSIGNED_SHORT,0);}gl.uniform1f(u('uLift'),0);});
          gl.disable(gl.BLEND);gl.depthMask(false);
          // glass: outer faces only (a back face drawn after the front one would replace it, as glass doesn't write depth)
          gl.enable(gl.CULL_FACE);gl.cullFace(gl.BACK);
          for(const kind of [1,4,2]){resolve();gl.useProgram(prog);gl.activeTexture(gl.TEXTURE3);gl.bindTexture(gl.TEXTURE_2D,scTex);gl.bindSampler(3,null);gl.uniform1i(u('uScene'),3);
            views.forEach((s,i)=>{viewport(i);drawGroups(s.list,kind);});}
          gl.disable(gl.CULL_FACE);
          gl.depthMask(true);gl.bindVertexArray(null);gl.disable(gl.SCISSOR_TEST);
          if(snap){resolve();gl.bindFramebuffer(gl.READ_FRAMEBUFFER,scFb);const px=new Uint8Array(w*h*4);gl.readPixels(0,0,w,h,gl.RGBA,gl.UNSIGNED_BYTE,px);
            gl.bindFramebuffer(gl.FRAMEBUFFER,null);return px;}
          // resolve, then copy to the canvas (a multisampled buffer can only be resolved into the same format)
          resolve();gl.bindFramebuffer(gl.READ_FRAMEBUFFER,scFb);gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER,null);gl.blitFramebuffer(0,0,w,h,0,0,w,h,gl.COLOR_BUFFER_BIT,gl.NEAREST);
          gl.bindFramebuffer(gl.FRAMEBUFFER,null);
          const lab=s=>'<b>'+s.title+'</b>'+s.name;
          labA.innerHTML=views.length>1?lab(views[0]):'';labB.innerHTML=views.length>1?lab(views[1]):'';
          [labA,labB,msg].forEach(x=>x.style.color=th.text);
          msg.innerHTML=(views.length>1?'':(views[0].title?views[0].title+' · ':'')+views[0].name)+(missing.size?' · this browser cannot show '+[...missing].join('/')+' textures':'');}

        // ---- controls
        const viewBar=root.querySelector('.pv-views');
        VIEWS.forEach(([k,label,v],i)=>{const b=document.createElement('button');b.textContent=label;if(i===1)b.classList.add('on');
          b.onclick=()=>{home(v);viewBar.querySelectorAll('button').forEach(x=>x.classList.toggle('on',x===b));draw();};viewBar.appendChild(b);});
        home(VIEWS[1][2]);
        let drag=null;
        canvas.addEventListener('contextmenu',e=>e.preventDefault());
        canvas.addEventListener('dblclick',()=>{const b=viewBar.querySelector('button.on');if(b&&ready)b.click();});
        canvas.addEventListener('pointerdown',e=>{drag={x:e.clientX,y:e.clientY,b:e.button};canvas.setPointerCapture(e.pointerId);});
        canvas.addEventListener('pointerup',()=>drag=null);
        canvas.addEventListener('pointermove',e=>{if(!drag||!ready)return;const dx=e.clientX-drag.x,dy=e.clientY-drag.y;drag.x=e.clientX;drag.y=e.clientY;
          if(drag.b===2||e.shiftKey){const k=cam.dist*.0011;target[0]-=dx*k*Math.cos(cam.yaw);target[2]+=dx*k*Math.sin(cam.yaw);target[1]+=dy*k;}
          else{cam.yaw-=dx*.008;cam.pitch=Math.max(-1.45,Math.min(1.45,cam.pitch+dy*.008));}draw();});
        canvas.addEventListener('wheel',e=>{e.preventDefault();if(!ready)return;cam.dist=Math.max(.25,Math.min(3,cam.dist*Math.exp(e.deltaY*.0012)));draw();},{passive:false});
        function setSide(s){sideMode=s;root.querySelectorAll('[data-side]').forEach(x=>x.classList.toggle('on',x.dataset.side===s));draw();}
        root.querySelectorAll('[data-side]').forEach(b=>b.onclick=()=>setSide(b.dataset.side));
        const partBtns=[...root.querySelectorAll('[data-part]')];
        function showParts(){partBtns.forEach(x=>x.classList.toggle('on',(parts&+x.dataset.part)!==0));}
        partBtns.forEach(b=>{if(+b.dataset.part===1&&data.designLocked)b.disabled=true;
          b.onclick=()=>{parts^=+b.dataset.part;showParts();if(host)host.postMessage({type:'parts',p:parts});
            if(sideMode==='before'||sideMode==='donor')setSide('after');else draw();};});
        showParts();
        // for scripted checks: root.hpv.view({yaw,pitch,dist}), root.hpv.side('after')
        root.hpv={view:v=>{Object.assign(cam,v);draw();},side:setSide,clay:setClay,parts:p=>{parts=p;showParts();draw();},target:t=>{target=t;draw();},frame,snapshot};
        // the hover picture: the 3/4 view as a PNG (data URL) with a see-through background (the edges' colour un-premultiplied)
        function snapshot(w,h){home(VIEWS[1][2]);cam.dist*=.84;const px=render({w,h});if(!px)return null;
          const c=document.createElement('canvas');c.width=w;c.height=h;const x=c.getContext('2d'),img=x.createImageData(w,h),o=img.data;
          for(let y=0;y<h;y++){const s=(h-1-y)*w*4,d=y*w*4;for(let i=0;i<w*4;i+=4){const a=px[s+i+3];o[d+i+3]=a;
            if(a>0){const k=255/a;o[d+i]=Math.min(255,px[s+i]*k);o[d+i+1]=Math.min(255,px[s+i+1]*k);o[d+i+2]=Math.min(255,px[s+i+2]*k);}}}
          x.putImageData(img,0,0);return c.toDataURL('image/png');}
        // inside the app (WebView2): the app's part boxes set the parts (not on the hover pictures' page)
        const host=data.snap?null:window.chrome&&window.chrome.webview;
        if(host)host.addEventListener('message',e=>{const m=e.data;if(m&&m.type==='parts'){parts=m.p;showParts();draw();}});
        if(typeof DecompressionStream==='undefined'){msg.textContent='3D preview needs a current browser (Chrome, Edge or Firefox).';return;}
        msg.textContent='Loading 3D data…';
        Promise.all([...data.tex.map(async t=>{t.l=await Promise.all(t.l.map(inflate));}),
          ...data.geo.map(async g=>{for(const k of ['pos','nrm','tan','uv','idx'])g[k]=await inflate(g[k]);})])
          .then(()=>{ready=true;if(data.snap){root.dispatchEvent(new Event('hpv-ready'));return;}
            new ResizeObserver(draw).observe(canvas);setTheme(theme);if(host)host.postMessage({type:'ready'});
            // the app's loading layer (if any) fades once the helmet is drawn
            requestAnimationFrame(()=>requestAnimationFrame(()=>window.__idle&&window.__idle()));})
          .catch(e=>{msg.textContent='3D preview could not load its data: '+e;root.dispatchEvent(new CustomEvent('hpv-fail',{detail:String(e)}));});}
        """;
}

/// <summary>
/// The data behind <see cref="HelmetPage"/>: per driver the shell's helmet and visor surfaces and the helmet parts
/// model split into wing / attachments, with the materials their binding gives each slot; textures kept in their GPU
/// block format so the browser uploads them as they are; all binary data zlib + base64, shared where identical.
/// </summary>
internal sealed class HelmetScene(GameInstall game)
{
    public const int KSolid = 0, KVisor = 1, KClear = 2, KTear = 4;

    public List<Dictionary<string, object?>> Textures { get; } = [];
    public List<Dictionary<string, object?>> Materials { get; } = [];
    public List<Dictionary<string, object>> Geometry { get; } = [];
    readonly List<(ErpResource Srv, string Name, int Max)> _pending = [];
    readonly Dictionary<string, int> _tex = new(StringComparer.Ordinal), _mat = new(StringComparer.Ordinal), _geo = new(StringComparer.Ordinal);
    Dictionary<string, ErpResource>? _common;

    /// <summary>Largest side of the helmet design textures; the smaller parts get half.</summary>
    public int TexSize = 2048;

    sealed record Lookup(Dictionary<string, ErpResource> Own, Dictionary<string, ErpResource> Common)
    {
        public ErpResource? Find(string id) => Own.GetValueOrDefault(id) ?? Common.GetValueOrDefault(id);
    }

    /// <summary>A driver's helmet for the page: name, the shell's helmet surfaces (with their design) and visor surfaces
    /// (the visor tint is a material of its own, so another driver's can go on), the wing and the attachments.</summary>
    public Dictionary<string, object?> Driver(Driver d)
    {
        _common ??= ErpX.Index(ErpX.Load(HelmetSwapper.CommonHelmetErp(game)));
        var own = ErpX.Index(ErpX.Load(d.HelmetErp));
        foreach (var f in HelmetSwapper.ReadErps(game, d)) foreach (var r in ErpX.Load(f).Resources) own.TryAdd(r.Identifier, r);
        var lk = new Lookup(own, _common);
        var embId = lk.Find(HelmetSwapper.MainBinding(d)) is not null ? HelmetSwapper.MainBinding(d) : $"{d.EaidRoot}idf/{d.Token}.emb?context=default";
        var binding = lk.Find(embId) is { } emb ? EgoMaterial.ParseBinding(ErpX.Data(emb)) : [];
        int Mat(string slot) => binding.TryGetValue(slot, out var mid) ? Material(lk, mid, slot == "helmet" ? TexSize : TexSize / 2) : -1;

        var shell = HelmetSwapper.ShellRoot(d.Shell!);
        bool hasWing = d.Spoiler != "no wing";
        return new()
        {
            ["name"] = d.Name + (d.YearTag == "" ? "" : " " + d.YearTag),
            ["short"] = d.ShortName,
            ["helmet"] = Groups(lk, shell, null, s => s == "helmet" ? Mat(s) : -1),
            ["visor"] = Groups(lk, shell, null, s => s == "visor" ? 0 : -1),   // geometry only; the page pairs it with a visor material
            ["visorMat"] = Mat("visor"),
            // the tear-offs: part of the shell; their material goes with the visor
            ["tear"] = Groups(lk, shell, null, s => s == "visor_tear" ? 0 : -1),
            ["tearMat"] = Mat("visor_tear"),
            // the parts model's three groups (as the swap splits them); front vents keep this driver's wing material
            ["wing"] = Groups(lk, HelmetSwapper.Attachments(d), (s, a, b, c) => HelmetParts.ClassOf(s, hasWing, a, b, c) == SwapParts.Wing, Mat),
            ["strip"] = Groups(lk, HelmetSwapper.Attachments(d), (s, a, b, c) => HelmetParts.ClassOf(s, hasWing, a, b, c) == SwapParts.VisorStrip, Mat),
            ["rest"] = Groups(lk, HelmetSwapper.Attachments(d), (s, a, b, c) => HelmetParts.ClassOf(s, hasWing, a, b, c) == SwapParts.Attachments, Mat),
        };
    }

    static int KindOf(string shader) => shader switch
    {
        _ when shader.StartsWith("char_visor_tear") => KTear,       // the tear-off film: almost clear, streaky highlights
        _ when shader.StartsWith("char_visor") => KVisor,
        _ when shader.StartsWith("char_helmet_attachment") => KClear,
        _ => KSolid,
    };

    /// <summary>The page material for a material eaid, or -1 if it is not drawn.</summary>
    int Material(Lookup lk, string mid, int size)
    {
        if (lk.Find(mid) is not { } mr) return -1;
        EgoMaterial m;
        try { m = EgoMaterial.Parse(ErpX.Data(mr)); }
        catch (InvalidDataException) { return -1; }
        int kind = KindOf(m.ShaderName);
        if (kind < 0) return -1;
        int T(string sampler, int max) => kind == KClear || m.Textures.GetValueOrDefault(sampler) is not { } id ? -1 : Texture(lk, id, max);
        var tex = new Dictionary<string, int>
        {
            ["d"] = T("me_diffuseTex", kind == KVisor ? 256 : size),
            ["n"] = kind == KVisor ? -1 : T("me_normalTex", size),
            ["s"] = T("me_surfaceTex", kind == KVisor ? 256 : Math.Max(size / 2, 64)),
        };
        var aniso = m.Values.GetValueOrDefault("me_anisotropy") ?? [];
        float[] p = [m.Value("me_alpha", 1), m.Value("me_roughness", 0.3f), m.Value("me_indexOfRefraction", m.Value("me_refractionIOR", 1.5f)), m.Value("me_translucency", 0),
                     aniso.Length > 0 ? aniso[0] : 0, aniso.Length > 1 ? aniso[1] : 0, 0, 0];
        var key = $"{mid}|{string.Join(",", tex.Values)}";
        if (_mat.TryGetValue(key, out var idx)) return idx;
        Materials.Add(new() { ["name"] = mid.Split('/')[^1], ["k"] = kind, ["t"] = tex, ["p"] = p });
        return _mat[key] = Materials.Count - 1;
    }

    /// <summary>A texture for the page (index into <see cref="Textures"/>), or -1; the same image asked for at
    /// different sizes is stored once, at the largest.</summary>
    int Texture(Lookup lk, string srvId, int maxSize)
    {
        if (lk.Find(srvId) is not { } r) return -1;
        string key;
        try
        {
            var srv = new ErpGfxSRVResource();
            srv.FromResource(r);
            var surface = r.ParentFile.FindResource(srv.Fragment0.SurfaceResourceName);
            using var md5 = System.Security.Cryptography.MD5.Create();
            if (surface is not null) foreach (var f in surface.Fragments) { var d = f.GetDataArray(true); md5.TransformBlock(d, 0, d.Length, null, 0); }
            md5.TransformFinalBlock([], 0, 0);
            key = Convert.ToHexString(md5.Hash!);
        }
        catch (Exception) { return -1; }
        if (_tex.TryGetValue(key, out var idx))
        {
            _pending[idx] = _pending[idx] with { Max = Math.Max(_pending[idx].Max, maxSize) };
            return idx;
        }
        _pending.Add((r, srvId.Split('/')[^1], maxSize));
        return _tex[key] = _pending.Count - 1;
    }

    /// <summary>Decode every texture asked for (unreadable ones become entries the page skips).</summary>
    public void Finish()
    {
        for (int i = Textures.Count; i < _pending.Count; i++)
        {
            var (srv, name, max) = _pending[i];
            Dictionary<string, object?>? t = null;
            try
            {
                var dds = HelmetTemplate.FullResolution(game, srv);
                t = Blocks(dds, max) ?? Rgba(dds, Math.Min(max, 1024));
            }
            catch (Exception) { }
            if (t is not null) t["name"] = name;
            Textures.Add(t ?? new() { ["name"] = name, ["f"] = "none", ["w"] = 0, ["h"] = 0, ["l"] = new List<string>() });
        }
    }

    /// <summary>The DDS mip chain in its block format, from the first level within <paramref name="maxSize"/>² pixels
    /// down to 4x4.</summary>
    static Dictionary<string, object?>? Blocks(DdsFile dds, int maxSize)
    {
        var (fmt, bb) = FormatOf(dds);
        if (fmt is null) return null;
        int w = (int)dds.header.width, h = (int)dds.header.height, n = Math.Max(1, (int)dds.header.mipMapCount);
        var data = dds.bdata;
        var levels = new List<string>();
        int off = 0, first = -1;
        for (int i = 0; i < n; i++)
        {
            int lw = Math.Max(1, w >> i), lh = Math.Max(1, h >> i);
            int size = Math.Max(1, (lw + 3) / 4) * Math.Max(1, (lh + 3) / 4) * bb;
            if (off + size > data.Length || lw < 4 || lh < 4) break;
            if ((long)lw * lh <= (long)maxSize * maxSize)
            {
                if (first < 0) first = i;
                levels.Add(Pack(data.AsSpan(off, size)));
            }
            off += size;
        }
        if (first < 0) return null;
        return new() { ["f"] = fmt, ["w"] = Math.Max(1, w >> first), ["h"] = Math.Max(1, h >> first), ["l"] = levels };
    }

    static (string?, int) FormatOf(DdsFile dds)
    {
        const uint DX10 = 0x30315844;
        uint cc = dds.header.ddspf.fourCC;
        if (cc == DX10)
            return (int)dds.header10.dxgiFormat switch
            {
                >= 70 and <= 72 => ("bc1", 8), >= 73 and <= 75 => ("bc2", 16), >= 76 and <= 78 => ("bc3", 16),
                >= 79 and <= 81 => ("bc4", 8), >= 82 and <= 84 => ("bc5", 16), >= 97 and <= 99 => ("bc7", 16),
                _ => (null, 0),
            };
        return cc switch
        {
            0x31545844 => ("bc1", 8),
            0x32545844 or 0x33545844 => ("bc2", 16),
            0x34545844 or 0x35545844 => ("bc3", 16),
            0x31495441 or 0x55344342 => ("bc4", 8),
            0x32495441 or 0x55354342 => ("bc5", 16),
            _ => (null, 0),
        };
    }

    /// <summary>Other formats: decoded RGBA8, one level.</summary>
    static Dictionary<string, object?>? Rgba(DdsFile dds, int maxSize)
    {
        using var ms = new MemoryStream();
        dds.Write(ms);
        ms.Position = 0;
        var levels = new BcDecoder().DecodeAllMipMaps2D(ms);
        if (levels.Length == 0) return null;
        var img = levels.FirstOrDefault(l => l.Width <= maxSize && l.Height <= maxSize);
        if (img.IsEmpty) img = levels[^1];
        var rgba = new byte[img.Width * img.Height * 4];
        for (int y = 0; y < img.Height; y++) MemoryMarshal.AsBytes(img.Span.GetRowSpan(y)).CopyTo(rgba.AsSpan(y * img.Width * 4));
        return new() { ["f"] = "rgba", ["w"] = img.Width, ["h"] = img.Height, ["l"] = new List<string> { Pack(rgba) } };
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>One draw group per surface of a model's LOD 0 whose slot has a material (<paramref name="material"/> >= 0),
    /// keeping the triangles <paramref name="keep"/> accepts: float32 positions (model space, metres), half normal /
    /// tangent (w = sign) / uv, 16- or 32-bit indices, and the group's bounds.</summary>
    List<Dictionary<string, object>> Groups(Lookup lk, string root, Func<string, Vector3, Vector3, Vector3, bool>? keep, Func<string, int> material)
    {
        var groups = new List<Dictionary<string, object>>();
        if (lk.Find(root + "lod=0&mesh") is not { } mr) return groups;
        var mesh = MeshDesc.Parse(ErpX.Data(mr));
        if (lk.Find(mesh.IndexBuffer) is not { } ibr) return groups;
        var ib = ErpX.Data(ibr, 1);
        bool wideIb = BitConverter.ToInt32(ErpX.Data(ibr, 0), 4) == 4;
        for (int s = 0; s < mesh.Records.Count; s++)
        {
            if (HelmetParts.IsDecal(mesh.Slots[s])) continue;
            int mat = material(mesh.Slots[s]);
            if (mat < 0) continue;
            // streams by type: 0x27 position (3 floats), 0x23 half4 (normal, then tangent), 0x1D half2 uv
            byte[]? pos = null, nrm = null, tan = null, uvs = null; int sn = 8, st = 8, su = 4;
            foreach (var k in mesh.StreamsOf(s))
            {
                if (lk.Find(mesh.Streams[k]) is not { } vr) continue;
                var hdr = ErpX.Data(vr, 0);
                int stride = BitConverter.ToInt32(hdr, 4), type = BitConverter.ToInt32(hdr, 12);
                if (type == 0x27 && pos is null) pos = ErpX.Data(vr, 1);
                else if (type == 0x23 && nrm is null) { nrm = ErpX.Data(vr, 1); sn = stride; }
                else if (type == 0x23 && tan is null) { tan = ErpX.Data(vr, 1); st = stride; }
                else if (type == 0x1D && uvs is null) { uvs = ErpX.Data(vr, 1); su = stride; }
            }
            if (pos is null || nrm is null || uvs is null) continue;
            var (off, count) = mesh.IndexRange(s);
            int bv = BitConverter.ToInt32(mesh.Records[s], 20);
            int Idx(int i) => (wideIb ? (int)BitConverter.ToUInt32(ib, i * 4) : BitConverter.ToUInt16(ib, i * 2)) + bv;
            Half H(byte[] d, int o) => BitConverter.ToHalf(d, o);

            List<float> P = []; List<Half> N = [], T = [], U = []; List<uint> I = [];
            var remap = new Dictionary<int, uint>();
            var box = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
            var tri = new int[3];
            for (int t = 0; t + 2 < count; t += 3)
            {
                for (int k = 0; k < 3; k++) tri[k] = Idx(off + t + k);
                if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2]) continue;
                if (keep is not null && !keep(mesh.Slots[s], MeshDesc.Position(pos, tri[0]), MeshDesc.Position(pos, tri[1]), MeshDesc.Position(pos, tri[2]))) continue;
                foreach (var v in tri)
                {
                    if (!remap.TryGetValue(v, out var nv))
                    {
                        var p = MeshDesc.Position(pos, v);
                        var n = new Vector3((float)H(nrm, v * sn), (float)H(nrm, v * sn + 2), (float)H(nrm, v * sn + 4));
                        n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY;
                        nv = (uint)(P.Count / 3);
                        P.AddRange([p.X, p.Y, p.Z]);
                        N.AddRange([(Half)n.X, (Half)n.Y, (Half)n.Z, (Half)1f]);
                        if (tan is not null) T.AddRange([H(tan, v * st), H(tan, v * st + 2), H(tan, v * st + 4), (float)H(tan, v * st + 6) < 0 ? (Half)(-1f) : (Half)1f]);
                        else T.AddRange([(Half)1f, (Half)0f, (Half)0f, (Half)1f]);
                        U.AddRange([H(uvs, v * su), H(uvs, v * su + 2), (Half)0f, (Half)0f]);
                        box[0] = Math.Min(box[0], p.X); box[1] = Math.Min(box[1], p.Y); box[2] = Math.Min(box[2], p.Z);
                        box[3] = Math.Max(box[3], p.X); box[4] = Math.Max(box[4], p.Y); box[5] = Math.Max(box[5], p.Z);
                        remap[v] = nv;
                    }
                    I.Add(nv);
                }
            }
            if (I.Count == 0) continue;
            if ((int)Materials[mat]["k"]! == KClear) SmoothNormals(P, N);
            bool w32 = P.Count / 3 > 65535;
            var geo = new Dictionary<string, object>
            {
                ["count"] = I.Count, ["w32"] = w32,
                ["pos"] = B64(P), ["nrm"] = B64(N), ["tan"] = B64(T), ["uv"] = B64(U),
                ["idx"] = w32 ? B64(I) : B64(I.Select(x => (ushort)x).ToList()),
            };
            var gkey = string.Join("|", new[] { "pos", "nrm", "tan", "uv", "idx" }.Select(k => (string)geo[k]));
            if (!_geo.TryGetValue(gkey, out var gi))
            {
                Geometry.Add(geo);
                _geo[gkey] = gi = Geometry.Count - 1;
            }
            groups.Add(new() { ["slot"] = mesh.Slots[s], ["m"] = mat, ["g"] = gi, ["b"] = box });
        }
        return groups;
    }

    /// <summary>Clear plastic is modelled with hard-edged (faceted) normals, which in glass shows every triangle as a
    /// separate reflection; average the normals of vertices at the same place, except across creases sharper than 60°.</summary>
    static void SmoothNormals(List<float> P, List<Half> N)
    {
        int n = P.Count / 3;
        var at = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var key = ((int)MathF.Round(P[i * 3] * 5000), (int)MathF.Round(P[i * 3 + 1] * 5000), (int)MathF.Round(P[i * 3 + 2] * 5000));
            if (!at.TryGetValue(key, out var l)) at[key] = l = [];
            l.Add(i);
        }
        Vector3 Nrm(int i) => new((float)N[i * 4], (float)N[i * 4 + 1], (float)N[i * 4 + 2]);
        var smooth = new Vector3[n];
        foreach (var l in at.Values)
            foreach (var i in l)
            {
                var ni = Nrm(i); var sum = Vector3.Zero;
                foreach (var j in l) { var nj = Nrm(j); if (Vector3.Dot(ni, nj) > 0.5f) sum += nj; }
                smooth[i] = sum.LengthSquared() > 1e-8f ? Vector3.Normalize(sum) : ni;
            }
        for (int i = 0; i < n; i++) { N[i * 4] = (Half)smooth[i].X; N[i * 4 + 1] = (Half)smooth[i].Y; N[i * 4 + 2] = (Half)smooth[i].Z; }
    }

    static string B64<T>(List<T> l) where T : struct => Pack(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(l)));

    /// <summary>zlib + base64; the page inflates it with the browser's DecompressionStream.</summary>
    static string Pack(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
