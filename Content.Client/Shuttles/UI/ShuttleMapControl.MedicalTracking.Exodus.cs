// Exodus-begin medical tablet contacts, delivered only through the tablet BUI.
using System.Numerics;
using Content.Client._Exodus.MedicalTracking;
using Content.Shared._Exodus.MedicalTracking;
using Content.Shared.Mobs;
using Robust.Client.Graphics;

namespace Content.Client.Shuttles.UI;

public sealed partial class ShuttleMapControl
{
    private readonly List<(MedicalTrackingContact Contact, string Label)> _medicalContacts = new();

    public void SetMedicalContacts(List<MedicalTrackingContact> contacts)
    {
        _medicalContacts.Clear();
        foreach (var contact in contacts)
            _medicalContacts.Add((contact, MedicalTrackingWindow.ContactText(contact)));
    }

    private void DrawMedicalContacts(DrawingHandleScreen handle, Matrix3x2 mapTransform)
    {
        foreach (var (contact, label) in _medicalContacts)
        {
            if (contact.Coordinates.MapId != ViewingMap)
                continue;

            var relative = Vector2.Transform(contact.Coordinates.Position, mapTransform);
            var position = ScalePosition(relative with { Y = -relative.Y });
            if (position.X < 0f || position.Y < 0f || position.X > PixelSize.X || position.Y > PixelSize.Y)
                continue;

            var color = contact.State switch
            {
                MobState.Alive => Color.LimeGreen,
                MobState.Critical => Color.Orange,
                _ => Color.Red,
            };
            handle.DrawCircle(position, 4f * UIScale, color);
            DrawMapObjectLabel(handle, position, label, color);
        }
    }
}
// Exodus-end
