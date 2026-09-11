using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Shared._Exodus.Tailed;

public sealed partial class TailedEntitySystem
{
    [Dependency] private MetaDataSystem _metadata = default!;

    private EntityQuery<TransformComponent> _transformQuery;
    private EntityQuery<TailedEntitySegmentComponent> _tailSegmentQuery;
    private EntityQuery<JointComponent> _jointQuery;

    private void InitializeTailJointRecovery()
    {
        _transformQuery = GetEntityQuery<TransformComponent>();
        _tailSegmentQuery = GetEntityQuery<TailedEntitySegmentComponent>();
        _jointQuery = GetEntityQuery<JointComponent>();

        UpdatesBefore.Add(typeof(SharedJointSystem));

        SubscribeLocalEvent<TailedEntityComponent, ComponentInit>(OnMapTrackingInit);
        SubscribeLocalEvent<TailedEntitySegmentComponent, ComponentInit>(OnMapTrackingInit);
        SubscribeLocalEvent<TailedEntityComponent, ComponentRemove>(OnMapTrackingRemove);
        SubscribeLocalEvent<TailedEntitySegmentComponent, ComponentRemove>(OnMapTrackingRemove);
        SubscribeLocalEvent<TailedEntityComponent, MetaFlagRemoveAttemptEvent>(OnMapTrackingFlagRemoveAttempt);
        SubscribeLocalEvent<TailedEntitySegmentComponent, MetaFlagRemoveAttemptEvent>(OnMapTrackingFlagRemoveAttempt);
        SubscribeLocalEvent<TailedEntityComponent, MapUidChangedEvent>(OnHeadMapChanged);
        SubscribeLocalEvent<TailedEntitySegmentComponent, MapUidChangedEvent>(OnSegmentMapChanged);
    }

    private void OnMapTrackingInit<T>(Entity<T> ent, ref ComponentInit args) where T : Component
    {
        if (!_netManager.IsClient)
            _metadata.AddFlag(ent, MetaDataFlags.ExtraTransformEvents);
    }

    private void OnMapTrackingRemove<T>(Entity<T> ent, ref ComponentRemove args) where T : Component
    {
        if (!_netManager.IsClient)
            _metadata.RemoveFlag(ent, MetaDataFlags.ExtraTransformEvents);
    }

    private void OnMapTrackingFlagRemoveAttempt<T>(Entity<T> ent, ref MetaFlagRemoveAttemptEvent args) where T : Component
    {
        if (!_netManager.IsClient && ent.Comp.LifeStage <= ComponentLifeStage.Running)
            args.ToRemove &= ~MetaDataFlags.ExtraTransformEvents;
    }

    private void OnHeadMapChanged(Entity<TailedEntityComponent> ent, ref MapUidChangedEvent args)
    {
        if (!_netManager.IsClient)
            ent.Comp.TailJointsDirty = true;
    }

    private void OnSegmentMapChanged(Entity<TailedEntitySegmentComponent> ent, ref MapUidChangedEvent args)
    {
        if (!_netManager.IsClient && TryComp<TailedEntityComponent>(ent.Comp.HeadEntity, out var head))
            head.TailJointsDirty = true;
    }

    private bool TryRestoreTail(Entity<TailedEntityComponent> head)
    {
        if (!CanRestoreTail(head) ||
            !_transformQuery.TryGetComponent(head, out var headTransform) ||
            headTransform.MapUid == null)
        {
            return false;
        }

        var headPosition = _transform.GetWorldPosition(headTransform);
        var headRotation = _transform.GetWorldRotation(headTransform);
        var segmentIndex = 0;

        // Finish every transfer before creating joints: moving another segment may clear
        // its neighbours' joints again. NoFTL round trips have already finished at this point.
        for (var tailIndex = 0; tailIndex < head.Comp.StartOffsets.Count; tailIndex++)
        {
            var previousPosition = headPosition + headRotation.RotateVec(head.Comp.StartOffsets[tailIndex]);
            var previousDirection = GetStartRotation(head.Comp, headRotation, tailIndex).ToWorldVec();

            for (var i = 0; i < head.Comp.Amount; i++)
            {
                var segment = head.Comp.TailSegments[segmentIndex++];
                if (!_transformQuery.TryGetComponent(segment, out var segmentTransform))
                    return false;

                if (segmentTransform.MapUid != headTransform.MapUid)
                {
                    var distance = i == 0
                        ? head.Comp.Spacing * head.Comp.StartSpacingMultiplier
                        : head.Comp.Spacing;
                    var position = previousPosition - previousDirection * distance;
                    _transform.SetMapCoordinates((segment, segmentTransform), new MapCoordinates(position, headTransform.MapID));

                    if (!head.Comp.Running || !CanRestoreTailBody(head) || !CanRestoreTailBody(segment))
                        return false;

                    var rotation = new Angle(previousDirection) + head.Comp.RotationModifier;
                    _transform.SetWorldRotation(segment, NormalizeAngle(rotation));
                    _physics.SetLinearVelocity(segment, Vector2.Zero);
                    _physics.SetAngularVelocity(segment, 0f);
                }

                // Follow the chain's direction, independently of the segment's visual rotation.
                var currentPosition = _transform.GetWorldPosition(segmentTransform);
                var toPrevious = previousPosition - currentPosition;
                if (toPrevious.LengthSquared() > 0f)
                    previousDirection = toPrevious.Normalized();

                previousPosition = currentPosition;
            }
        }

        if (!CanRestoreTail(head) || !TryRestoreTailJoints(head))
            return false;

        head.Comp.TailJointsDirty = false;
        return true;
    }

    private bool CanRestoreTail(Entity<TailedEntityComponent> head)
    {
        if (!head.Comp.Running || !CanRestoreTailBody(head) ||
            head.Comp.TailSegments.Count != head.Comp.Amount * head.Comp.StartOffsets.Count)
        {
            return false;
        }

        foreach (var segment in head.Comp.TailSegments)
        {
            if (!CanRestoreTailBody(segment) ||
                !_tailSegmentQuery.TryGetComponent(segment, out var tail) ||
                !tail.Running ||
                tail.HeadEntity != head.Owner)
            {
                return false;
            }
        }

        return true;
    }

    private bool CanRestoreTailBody(EntityUid uid)
    {
        return !TerminatingOrDeleted(uid) &&
               !EntityManager.IsQueuedForDeletion(uid) &&
               _transformQuery.TryGetComponent(uid, out var transform) && transform.Initialized &&
               _physicsQuery.TryGetComponent(uid, out var physics) && physics.Initialized;
    }

    private bool TryRestoreTailJoints(Entity<TailedEntityComponent> head)
    {
        if (!CanRestoreTailBody(head) ||
            !_transformQuery.TryGetComponent(head, out var headTransform) || headTransform.MapUid == null ||
            head.Comp.TailSegments.Count != head.Comp.Amount * head.Comp.StartOffsets.Count)
        {
            return false;
        }

        var segmentIndex = 0;
        DisableTailJointNetworking(head);

        for (var tailIndex = 0; tailIndex < head.Comp.StartOffsets.Count; tailIndex++)
        {
            var previous = head.Owner;

            for (var i = 0; i < head.Comp.Amount; i++)
            {
                var segment = head.Comp.TailSegments[segmentIndex++];
                if (!CanRestoreTailBody(previous) || !CanRestoreTailBody(segment) ||
                    !_transformQuery.TryGetComponent(segment, out var segmentTransform) ||
                    segmentTransform.MapUid != headTransform.MapUid)
                {
                    return false;
                }

                DisableTailJointNetworking(segment);

                var jointId = $"TailJoint_{previous}_{segment}";
                if (_jointQuery.TryGetComponent(previous, out var joints) && joints.GetJoints.ContainsKey(jointId))
                {
                    previous = segment;
                    continue;
                }

                var anchorA = i == 0
                    ? head.Comp.StartOffsets[tailIndex] + head.Comp.AnchorAOffset
                    : head.Comp.AnchorAOffset;
                var jointLength = i == 0
                    ? head.Comp.Spacing * head.Comp.StartSpacingMultiplier
                    : head.Comp.Spacing;
                var joint = _joint.CreateDistanceJoint(
                    bodyA: previous,
                    bodyB: segment,
                    anchorA: anchorA,
                    anchorB: head.Comp.AnchorBOffset,
                    id: jointId,
                    minimumDistance: jointLength * 0.8f);

                joint.Length = jointLength;
                joint.MinLength = jointLength * head.Comp.MinLengthMultiplier;
                joint.MaxLength = jointLength * head.Comp.MaxLengthMultiplier;
                joint.Stiffness = head.Comp.Stiffness;
                joint.Damping = head.Comp.Damping;

                previous = segment;
            }
        }

        return true;
    }
}
