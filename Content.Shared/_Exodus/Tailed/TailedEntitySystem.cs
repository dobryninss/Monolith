// (c) Space Exodus Team - EXDS-RL with CLA
// Authors: Lokilife
using System.Numerics;
using Content.Shared.Damage;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Exodus.Tailed;

/// <summary>
/// This system connects all segments of tailed entity.
/// Simply spawn segments with some offsets and initializes joints for them.
/// The worst part is tailed mob movement which is placed in SharedMoverController.
///
/// Probably this system can be used for any other tailed entities other than mob,
/// but I had enough with all this shit, adapt it for your conditions on your own.
/// </summary>
public sealed partial class TailedEntitySystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedJointSystem _joint = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private IGameTiming _timing = default!;

    private EntityQuery<PhysicsComponent> _physicsQuery;

    public override void Initialize()
    {
        base.Initialize();

        _physicsQuery = GetEntityQuery<PhysicsComponent>();

        SubscribeLocalEvent<TailedEntityComponent, ComponentStartup>(OnComponentStartup);
        SubscribeLocalEvent<TailedEntityComponent, ComponentShutdown>(OnComponentShutdown);
        SubscribeLocalEvent<TailedEntitySegmentComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<TailedEntitySegmentComponent, ComponentShutdown>(OnSegmentShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<TailedEntityComponent>();
        while (query.MoveNext(out var uid, out var tailed))
        {
            UpdateTailedMob((uid, tailed), frameTime);
        }
    }

    private void OnDamageChanged(Entity<TailedEntitySegmentComponent> ent, ref DamageChangedEvent args)
    {
        if (!TryComp<DamageableComponent>(ent.Comp.HeadEntity, out var headDamageable))
            return;

        if (args.DamageDelta is not { } damage)
            _damageable.SetDamage(ent.Comp.HeadEntity, headDamageable, args.Damageable.Damage);
        else
            _damageable.TryChangeDamage(ent.Comp.HeadEntity, damage, true, true, headDamageable, args.Origin);
    }

    private void OnComponentStartup(Entity<TailedEntityComponent> ent, ref ComponentStartup args)
    {
        if (ent.Comp.TailSegments.Count == 0)
            InitializeTailSegments((ent.Owner, ent.Comp, Transform(ent.Owner)));
    }

    private void OnComponentShutdown(Entity<TailedEntityComponent> ent, ref ComponentShutdown args)
    {
        foreach (var segment in ent.Comp.TailSegments)
        {
            if (!TerminatingOrDeleted(segment) && !EntityManager.IsQueuedForDeletion(segment))
            {
                _joint.ClearJoints(segment);
                QueueDel(segment);
            }
        }

        ent.Comp.TailSegments.Clear();
    }

    private void OnSegmentShutdown(Entity<TailedEntitySegmentComponent> ent, ref ComponentShutdown args)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        _joint.ClearJoints(ent.Owner);

        if (!TerminatingOrDeleted(ent.Comp.HeadEntity))
            QueueDel(ent.Comp.HeadEntity);
    }

    private void InitializeTailSegments(Entity<TailedEntityComponent, TransformComponent> ent)
    {
        var (uid, comp, xform) = ent;

        var mapUid = xform.MapUid;
        if (mapUid == null)
            return;

        // Ensure the head entity has physics for joints
        if (!HasComp<PhysicsComponent>(uid))
            return;

        var headPos = _transform.GetWorldPosition(xform);
        var headRot = _transform.GetWorldRotation(xform);

        comp.TailSegments.Clear();

        for (var tailIndex = 0; tailIndex < comp.StartOffsets.Count; tailIndex++)
        {
            var startPos = headPos + headRot.RotateVec(comp.StartOffsets[tailIndex]);
            var startRotation = GetStartRotation(comp, headRot, tailIndex);

            for (var i = 0; i < comp.Amount; i++)
            {
                var distance = comp.Spacing * (i + comp.StartSpacingMultiplier);
                var offset = startRotation.ToWorldVec() * distance;
                var spawnPos = startPos - offset;

                var segment = PredictedSpawnAtPosition(comp.Prototype, new EntityCoordinates(mapUid.Value, spawnPos));

                var segmentRotation = new Angle(startRotation.ToWorldVec()) + comp.RotationModifier;
                _transform.SetWorldRotation(segment, NormalizeAngle(segmentRotation));

                var tail = EnsureComp<TailedEntitySegmentComponent>(segment);
                tail.HeadEntity = uid;
                tail.Index = i;
                tail.TailIndex = tailIndex;
                comp.TailSegments.Add(segment);
            }
        }

        var segmentIndex = 0;

        for (var tailIndex = 0; tailIndex < comp.StartOffsets.Count; tailIndex++)
        {
            var prev = uid;

            for (var i = 0; i < comp.Amount; i++)
            {
                var segment = comp.TailSegments[segmentIndex++];

                // Ensure segment has physics before creating joint
                if (!HasComp<PhysicsComponent>(segment))
                    continue;

                var anchorA = i == 0
                    ? comp.StartOffsets[tailIndex] + comp.AnchorAOffset
                    : comp.AnchorAOffset;
                var jointLength = i == 0
                    ? comp.Spacing * comp.StartSpacingMultiplier
                    : comp.Spacing;
                var joint = _joint.CreateDistanceJoint(
                    bodyA: prev,
                    bodyB: segment,
                    anchorA: anchorA,
                    anchorB: comp.AnchorBOffset,
                    minimumDistance: jointLength * 0.8f
                );

                joint.Length = jointLength;
                joint.MinLength = jointLength * comp.MinLengthMultiplier;
                joint.MaxLength = jointLength * comp.MaxLengthMultiplier;

                joint.Stiffness = comp.Stiffness;
                joint.Damping = comp.Damping;

                joint.ID = $"TailJoint_{prev}_{segment}";

                prev = segment;
            }
        }
    }

    private void UpdateTailedMob(Entity<TailedEntityComponent> head, float frameTime)
    {
        var expectedSegments = head.Comp.Amount * head.Comp.StartOffsets.Count;
        if (expectedSegments == 0 || head.Comp.TailSegments.Count != expectedSegments)
            return;

        foreach (var segment in head.Comp.TailSegments)
        {
            if (TerminatingOrDeleted(segment))
                return;
        }

        ApplySegmentVelocities(head, frameTime);

        UpdateSegmentRotation(head, frameTime);
    }

    private void ApplySegmentVelocities(
        Entity<TailedEntityComponent> head,
        float frameTime)
    {
        var tail = head.Comp;
        var headPos = _transform.GetWorldPosition(head);
        var headRotation = _transform.GetWorldRotation(head);
        var segmentIndex = 0;

        for (var tailIndex = 0; tailIndex < tail.StartOffsets.Count; tailIndex++)
        {
            var prevPos = headPos + headRotation.RotateVec(tail.StartOffsets[tailIndex]);
            var prevDirection = GetStartRotation(tail, headRotation, tailIndex).ToWorldVec();

            for (var i = 0; i < tail.Amount; i++)
            {
                var segment = tail.TailSegments[segmentIndex++];

                if (!_physicsQuery.TryGetComponent(segment, out var physics))
                    continue;

                var currentPos = _transform.GetWorldPosition(segment);
                var targetDistance = i == 0
                    ? tail.Spacing * tail.StartSpacingMultiplier
                    : tail.Spacing;
                var targetPos = prevPos - prevDirection * targetDistance;
                Vector2 desiredVelocity;
                var toPrev = prevPos - currentPos;
                var currentDistance = toPrev.Length();
                var directionToPrev = currentDistance > 0f
                    ? toPrev / currentDistance
                    : Vector2.Zero;

                if (i > 0 && currentDistance < tail.Spacing * tail.MinLengthMultiplier)
                {
                    desiredVelocity = -directionToPrev * tail.MaxSegmentSpeed * 0.5f;
                }
                else if (i > 0 && currentDistance > tail.Spacing * tail.MaxLengthMultiplier)
                {
                    desiredVelocity = directionToPrev * tail.MaxSegmentSpeed;
                }
                else
                {
                    var toTarget = targetPos - currentPos;
                    desiredVelocity = toTarget * tail.FollowSharpness;
                }

                if (desiredVelocity.LengthSquared() > tail.MaxSegmentSpeed * tail.MaxSegmentSpeed)
                    desiredVelocity = desiredVelocity.Normalized() * tail.MaxSegmentSpeed;

                var currentVelocity = physics.LinearVelocity;

                var newVelocity = Vector2.Lerp(
                    currentVelocity,
                    desiredVelocity,
                    frameTime * tail.VelocitySmoothing);

                _physics.SetLinearVelocity(segment, newVelocity, body: physics);

                prevPos = currentPos;
                if (currentDistance > 0f)
                    prevDirection = directionToPrev;
            }
        }
    }

    private void UpdateSegmentRotation(
        Entity<TailedEntityComponent> head,
        float frameTime)
    {
        if (!head.Comp.EnableRotationControl)
            return;

        var headPos = _transform.GetWorldPosition(head);
        var headRotation = _transform.GetWorldRotation(head);
        var segmentIndex = 0;

        foreach (var startOffset in head.Comp.StartOffsets)
        {
            var prevPos = headPos + headRotation.RotateVec(startOffset);

            for (var i = 0; i < head.Comp.Amount; i++)
            {
                var segment = head.Comp.TailSegments[segmentIndex++];
                var segmentPos = _transform.GetWorldPosition(segment);

                var direction = prevPos - segmentPos;

                if (direction.LengthSquared() > 0.1f)
                {
                    var targetAngle = NormalizeAngle(MathF.Atan2(direction.Y, direction.X) + head.Comp.RotationModifier);

                    var currentAngle = _transform.GetWorldRotation(segment);

                    var newAngle = Angle.Lerp(
                        currentAngle,
                        targetAngle,
                        frameTime * head.Comp.RotationLerpSpeed);

                    _transform.SetWorldRotation(segment, newAngle);
                }

                prevPos = segmentPos;
            }
        }
    }

    private static Angle NormalizeAngle(Angle angle)
    {
        angle %= MathHelper.TwoPi;
        if (angle < 0)
            angle += MathHelper.TwoPi;
        return angle;
    }

    private static Angle GetStartRotation(TailedEntityComponent component, Angle headRotation, int tailIndex)
    {
        if (tailIndex >= component.StartAngleOffsets.Count)
            return headRotation;

        return headRotation + Angle.FromDegrees(component.StartAngleOffsets[tailIndex]);
    }
}
