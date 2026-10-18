using System.Numerics;

namespace HelmetSwap.Core;

/// <summary>
/// Where a helmet's wing (rear spoiler) is: on top, from the crown back. The wing is its own "wing" surface when solid;
/// a clear wing shares the "helmet_transparent" surface with the clear vents (further forward) and the chin strip (low
/// down), so it is told apart by position: a triangle whose centre is above y = 1.64 m and behind z = 0.03 m.
/// </summary>
public static class HelmetParts
{
    public static bool InWingZone(Vector3 a, Vector3 b, Vector3 c) => (a.Y + b.Y + c.Y) / 3 > 1.64f && (a.Z + b.Z + c.Z) / 3 < 0.03f;

    /// <summary>Which part a triangle of a driver's helmet parts model belongs to:
    /// <list type="bullet">
    /// <item>the visor strip: the "visor_attachments" surface (the sponsor strip above the visor);</item>
    /// <item>the wing: the "wing" surface inside the wing zone, and clear triangles there when the helmet has a wing (a
    /// helmet with none only has small clear vents there, which are attachments);</item>
    /// <item>the attachments: everything else - the clear vents and chin strip, and the "wing" surface outside the wing
    /// zone: every Schuberth's front (chin) vents, painted on its wing texture (a few Bells have such pieces too).</item>
    /// </list></summary>
    public static SwapParts ClassOf(string slot, bool hasWing, Vector3 a, Vector3 b, Vector3 c) =>
        slot == "visor_attachments" ? SwapParts.VisorStrip
        : (slot == "wing" || slot == "helmet_transparent" && hasWing) && InWingZone(a, b, c) ? SwapParts.Wing
        : SwapParts.Attachments;

    /// <summary>Front vents (the "wing" surface outside the wing zone) given their own surface name, when the wing and
    /// the attachments come from different drivers: each needs its own driver's wing material.</summary>
    public const string VentsSlot = "helmet_attachments";

    /// <summary>Logo / badge decals the game draws in its own colours (the My Team parts model, used by most career
    /// designs, carries them): not part of the helmet's look here.</summary>
    public static bool IsDecal(string slot) => slot.StartsWith("helmet_logo", StringComparison.Ordinal) || slot.StartsWith("helmet_badge", StringComparison.Ordinal);
}
