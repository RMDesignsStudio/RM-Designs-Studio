using System.Globalization;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using Swapper.Core.TeamSimplified;

namespace Swapper.Core;

/// <summary>
/// The game's own cameras on a car, for the preview: each car's <c>-camscargame.layer</c> has an attachment point per
/// in-game camera (car space: wheel centres at y = 0, front = +z, +x = the car's left) and the camera's settings. The
/// viewer's Onboard is the game's offset T-cam (<c>CamPodOffset</c>, attachment "podoffset"): over the driver's helmet,
/// to one side (Red Bull 26: 0.109 m left, 0.765 m up, 0.216 m behind the cockpit point, tilted 10 degrees down, field of
/// view 85, near clip 0.21 m). The centred T-cam ("pod") is used for a car without one.
/// </summary>
public static class CarCams
{
    static readonly Regex Point = new(@"""AttachmentPoint"": \{(?<body>[^{}]*)\}", RegexOptions.Compiled);
    static readonly Regex Offset = new(@"""Offset"": ""vec3 (?<v>[^""]+)""", RegexOptions.Compiled);
    static readonly Regex Rotation = new(@"""Rotation"": ""vec3 (?<v>[^""]+)""", RegexOptions.Compiled);
    static readonly Regex Name = new(@"""Attachment name"": ""shnm (?<n>[^""]+)""", RegexOptions.Compiled);
    static readonly Regex Fov = new(@"""FOV"": ""fp32 (?<v>[^""]+)""", RegexOptions.Compiled);
    static readonly Regex Near = new(@"""Near clip"": ""fp32 (?<v>[^""]+)""", RegexOptions.Compiled);

    /// <summary>The viewer's cameras from the first ERP that has a car camera layer, or null:
    /// {"onboard": [x, y, z, pitch (radians, down), vertical field of view (degrees), near clip (m)]}.</summary>
    public static Dictionary<string, float[]>? Of(params ErpFile?[] erps)
    {
        foreach (var erp in erps)
        {
            var layer = erp?.Resources.FirstOrDefault(r => r.Identifier.EndsWith("-camscargame.layer", StringComparison.Ordinal));
            if (layer is null) continue;
            string json;
            try { json = ChassisSwap.Pkg.ToJson(ErpX.Data(layer)); } catch (Exception) { continue; }
            if ((Onboard(json, "podoffset", "CamPodOffset") ?? Onboard(json, "pod", "CamPod")) is { } cam) return new() { ["onboard"] = cam };
        }
        return null;
    }

    static float[] Vec(string s) => s.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    static float[]? Onboard(string json, string attachment, string camera)
    {
        foreach (Match m in Point.Matches(json))
        {
            var body = m.Groups["body"].Value;
            if (Name.Match(body) is not { Success: true } n || n.Groups["n"].Value != attachment || Offset.Match(body) is not { Success: true } o) continue;
            var p = Vec(o.Groups["v"].Value);
            if (p.Length != 3) continue;
            float pitch = Rotation.Match(body) is { Success: true } r && Vec(r.Groups["v"].Value) is { Length: 3 } rv ? rv[0] : 0;
            // the camera's own settings: its field of view (horizontal, at 16:9) and near clip
            float fov = 0, near = 0;
            int at = json.IndexOf($"\"stri camscargame${camera}\"", StringComparison.Ordinal);
            if (at >= 0)
            {
                int end = json.IndexOf("\"stri camscargame$", at + camera.Length + 20, StringComparison.Ordinal);
                var block = json[at..(end < 0 ? json.Length : end)];
                if (Fov.Match(block) is { Success: true } f) fov = float.Parse(f.Groups["v"].Value, CultureInfo.InvariantCulture);
                if (Near.Match(block) is { Success: true } c) near = float.Parse(c.Groups["v"].Value, CultureInfo.InvariantCulture);
            }
            float vfov = fov > 0 ? (float)(2 * Math.Atan(Math.Tan(fov * Math.PI / 360) / (16.0 / 9)) * 180 / Math.PI) : 0;
            return [p[0], p[1], p[2], (float)(pitch * Math.PI / 180), vfov, near];
        }
        return null;
    }
}
