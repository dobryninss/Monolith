using System.Numerics;
using Robust.Client.Graphics;

namespace Content.Client._Exodus.Radar;

/// <summary>
/// Draws optional radar labels in the grid IFF style without needing the entity in PVS.
/// Positions and marker extents are in pixels; only the text and padding scale with the UI.
/// </summary>
public static class RadarBlipLabelRenderer
{
    public static void Draw(
        DrawingHandleScreen handle,
        Font font,
        Vector2 position,
        Vector2 viewSize,
        float markerExtent,
        float uiScale,
        LocId label,
        float distance,
        Color color)
    {
        var displayedDistance = distance < 50f ? $"{distance:0.0}" :
            distance < 1000f ? $"{distance:0}" : $"{distance / 1000f:0.0}k";
        var text = Loc.GetString("shuttle-console-iff-label",
            ("name", Loc.GetString(label)), ("distance", displayedDistance));
        var scale = uiScale * 0.9f;
        var dimensions = handle.GetDimensions(font, text, scale);
        var padding = 4f * uiScale;
        var gap = markerExtent + padding;

        // Prefer the left side like grid IFF labels, switching sides near the edge.
        var labelPosition = position + new Vector2(-dimensions.X - gap, -dimensions.Y * 0.5f);
        if (labelPosition.X < padding)
            labelPosition.X = position.X + gap;

        var minPosition = new Vector2(padding);
        var maxPosition = Vector2.Max(minPosition, viewSize - dimensions - minPosition);
        labelPosition = Vector2.Clamp(labelPosition, minPosition, maxPosition);
        handle.DrawString(font, labelPosition, text, scale, color);
    }
}
