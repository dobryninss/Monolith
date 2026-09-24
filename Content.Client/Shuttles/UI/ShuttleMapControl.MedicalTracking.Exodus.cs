// Exodus-begin medical tablet contacts, delivered only through the tablet BUI.
using System.Numerics;
using Content.Client._Exodus.MedicalTracking;
using Content.Shared._Exodus.MedicalTracking;
using Content.Shared.Mobs;
using Robust.Client.Graphics;
using Robust.Shared.Map;

namespace Content.Client.Shuttles.UI;

public sealed partial class ShuttleMapControl
{
    private readonly List<MedicalTrackingContact> _medicalContacts = new();
    private readonly List<MedicalMarker> _medicalMarkers = new();
    private readonly Dictionary<Vector2i, int> _medicalBuckets = new();
    private readonly Dictionary<NetEntity, string> _medicalLabels = new();
    private readonly Dictionary<int, (string Label, string Count)> _medicalClusterLabels = new();
    private NetEntity? _selectedMedicalContact;
    private Vector2? _medicalMousePosition;
    private MapId _medicalMap;
    private float _medicalCellSize = -1f;

    public event Action<NetEntity>? MedicalContactSelected;

    private readonly record struct MedicalMarker(MedicalTrackingContact Contact, Vector2 Position,
        int Count, MobState State, string Label = "", string CountText = "");

    public void SetMedicalContacts(List<MedicalTrackingContact> contacts)
    {
        WorldMinRange = 16f;
        _medicalContacts.Clear();
        _medicalContacts.AddRange(contacts);
        _medicalLabels.Clear();
        foreach (var contact in contacts)
            _medicalLabels[contact.Body] = Loc.GetString("medical-tracking-map-contact", ("name", contact.Name), ("status", MedicalTrackingDisplay.Status(contact.State)));
        _medicalCellSize = -1f;
    }

    public void SelectMedicalContact(NetEntity? body)
    {
        _selectedMedicalContact = body;
        _medicalCellSize = -1f;
    }

    public void FocusMedicalPosition(MapCoordinates coordinates, float range)
    {
        if (!_mapManager.MapExists(coordinates.MapId))
            return;

        // Only medical map controls opt into close zoom; shuttle consoles retain their original limits.
        WorldMinRange = 16f;
        ActualRadarRange = Math.Clamp(range, WorldMinRange, WorldMaxRange);
        SetMap(coordinates.MapId, coordinates.Position, recentering: true);
    }

    public void ShowMedicalContacts()
    {
        Vector2? minimum = null;
        var maximum = Vector2.Zero;
        foreach (var contact in _medicalContacts)
        {
            if (contact.Coordinates.MapId != ViewingMap)
                continue;

            var position = contact.Coordinates.Position;
            maximum = minimum == null ? position : Vector2.Max(maximum, position);
            minimum = minimum == null ? position : Vector2.Min(minimum.Value, position);
        }

        if (minimum is not { } min)
            return;

        var span = maximum - min;
        FocusMedicalPosition(new MapCoordinates((min + maximum) / 2f, ViewingMap), MathF.Max(64f, MathF.Max(span.X, span.Y) * 0.6f));
    }

    private void RebuildMedicalMarkers()
    {
        var cellSize = 24f * UIScale / MathF.Max(MinimapScale, 0.001f);
        if (_medicalMap == ViewingMap && _medicalCellSize.Equals(cellSize))
            return;

        _medicalMap = ViewingMap;
        _medicalCellSize = cellSize;
        _medicalBuckets.Clear();
        _medicalMarkers.Clear();
        var selectedIndex = -1;
        foreach (var contact in _medicalContacts)
        {
            if (contact.Coordinates.MapId != ViewingMap)
                continue;

            var position = contact.Coordinates.Position;
            var cell = new Vector2i((int) MathF.Floor(position.X / cellSize), (int) MathF.Floor(position.Y / cellSize));
            var selected = contact.Body == _selectedMedicalContact;
            var index = selected ? -1 : FindMedicalCluster(cell, position, cellSize);
            if (index >= 0)
            {
                var marker = _medicalMarkers[index];
                _medicalBuckets[cell] = index;
                _medicalMarkers[index] = marker with
                {
                    Position = (marker.Position * marker.Count + position) / (marker.Count + 1),
                    Count = marker.Count + 1,
                    State = MedicalTrackingDisplay.Priority(contact.State) < MedicalTrackingDisplay.Priority(marker.State) ? contact.State : marker.State,
                };
            }
            else
            {
                if (!selected)
                    _medicalBuckets.Add(cell, _medicalMarkers.Count);
                else
                    selectedIndex = _medicalMarkers.Count;
                _medicalMarkers.Add(new MedicalMarker(contact, position, 1, contact.State));
            }
        }

        // Draw and hit-test the selected contact above overlapping groups.
        if (selectedIndex >= 0)
            (_medicalMarkers[selectedIndex], _medicalMarkers[^1]) = (_medicalMarkers[^1], _medicalMarkers[selectedIndex]);

        // Reuse labels while zooming; panning does not rebuild the spatial buckets.
        for (var i = 0; i < _medicalMarkers.Count; i++)
        {
            var marker = _medicalMarkers[i];
            if (marker.Count == 1)
            {
                _medicalMarkers[i] = marker with { Label = _medicalLabels[marker.Contact.Body] };
                continue;
            }

            if (!_medicalClusterLabels.TryGetValue(marker.Count, out var text))
            {
                text = (Loc.GetString("medical-tracking-cluster", ("count", marker.Count)), marker.Count.ToString());
                _medicalClusterLabels.Add(marker.Count, text);
            }
            _medicalMarkers[i] = marker with
            {
                Label = text.Label,
                CountText = text.Count,
            };
        }
    }

    private int FindMedicalCluster(Vector2i cell, Vector2 position, float cellSize)
    {
        if (_medicalBuckets.TryGetValue(cell, out var sameCell))
            return sameCell;

        // Check adjacent buckets so nearby clients across a cell boundary still form one marker.
        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                if (_medicalBuckets.TryGetValue(cell + new Vector2i(x, y), out var index) &&
                    Vector2.DistanceSquared(position, _medicalMarkers[index].Position) < cellSize * cellSize)
                    return index;
            }
        }

        return -1;
    }

    private int MedicalMarkerAt(Vector2 position)
    {
        for (var i = _medicalMarkers.Count - 1; i >= 0; i--)
        {
            var marker = _medicalMarkers[i];
            var relative = marker.Position - Offset;
            var pixel = ScalePosition(relative with { Y = -relative.Y });
            if (Vector2.DistanceSquared(pixel, position) <= 144f * UIScale * UIScale)
                return i;
        }

        return -1;
    }

    private bool TrySelectMedicalContact(Vector2 position)
    {
        if (_medicalContacts.Count == 0 || InFtl || !_mapManager.MapExists(ViewingMap))
            return false;

        RebuildMedicalMarkers();
        var index = MedicalMarkerAt(position);
        if (index < 0)
            return false;

        var marker = _medicalMarkers[index];
        if (marker.Count > 1)
            FocusMedicalPosition(new MapCoordinates(marker.Position, ViewingMap), WorldRange / 2f);
        else
            MedicalContactSelected?.Invoke(marker.Contact.Body);
        return true;
    }

    private void DrawMedicalContacts(DrawingHandleScreen handle, Matrix3x2 mapTransform)
    {
        if (_medicalContacts.Count == 0)
            return;

        RebuildMedicalMarkers();
        var hovered = _medicalMousePosition is { } mouse ? MedicalMarkerAt(mouse) : -1;
        var labelIndex = hovered;
        for (var i = 0; i < _medicalMarkers.Count; i++)
        {
            var marker = _medicalMarkers[i];
            var relative = Vector2.Transform(marker.Position, mapTransform);
            var position = ScalePosition(relative with { Y = -relative.Y });
            if (position.X < 0f || position.Y < 0f || position.X > PixelSize.X || position.Y > PixelSize.Y)
                continue;

            var color = MedicalTrackingDisplay.StatusColor(marker.State);
            var selected = marker.Contact.Body == _selectedMedicalContact;
            if (selected)
            {
                handle.DrawCircle(position, 14f * UIScale, Color.White);
                handle.DrawCircle(position, 12f * UIScale, Color.Black);
                if (hovered < 0)
                    labelIndex = i;
            }
            handle.DrawCircle(position, 10f * UIScale, Color.Black);
            handle.DrawCircle(position, 8f * UIScale, color);
            if (marker.Count > 1)
            {
                var size = handle.GetDimensions(Font, marker.CountText, UIScale);
                handle.DrawString(Font, position - size / 2f, marker.CountText, UIScale, Color.Black);
            }
            else
            {
                handle.DrawRect(new UIBox2(position - new Vector2(1.5f, 5f) * UIScale, position + new Vector2(1.5f, 5f) * UIScale), Color.Black);
                handle.DrawRect(new UIBox2(position - new Vector2(5f, 1.5f) * UIScale, position + new Vector2(5f, 1.5f) * UIScale), Color.Black);
            }
        }

        if (labelIndex >= 0)
        {
            var marker = _medicalMarkers[labelIndex];
            var relative = marker.Position - Offset;
            var position = ScalePosition(relative with { Y = -relative.Y });
            var size = handle.GetDimensions(Font, marker.Label, UIScale);
            var origin = position - new Vector2(size.X / 2f, size.Y + 20f * UIScale);
            origin = Vector2.Clamp(origin, Vector2.Zero, Vector2.Max(Vector2.Zero, PixelSize - size - new Vector2(8f * UIScale)));
            handle.DrawRect(UIBox2.FromDimensions(origin, size + new Vector2(8f * UIScale)), Color.Black.WithAlpha(0.9f));
            handle.DrawString(Font, origin + new Vector2(4f * UIScale), marker.Label, UIScale, Color.White);
        }
    }
}
// Exodus-end
