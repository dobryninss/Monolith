using System.Numerics;
using Content.Shared.CombatMode;
using Content.Shared.Movement.Components;

namespace Content.Shared._Exodus.Movement;

public sealed partial class SmoothRotationOnMoveSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<CombatModeComponent> _combatModeQuery;
    private EntityQuery<NoRotateOnMoveComponent> _noRotateOnMoveQuery;

    public override void Initialize()
    {
        base.Initialize();

        _combatModeQuery = GetEntityQuery<CombatModeComponent>();
        _noRotateOnMoveQuery = GetEntityQuery<NoRotateOnMoveComponent>();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SmoothRotationOnMoveComponent, InputMoverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var smoothRotation, out var mover, out var transform))
        {
            if (smoothRotation.DisableInCombatMode &&
                _combatModeQuery.TryComp(uid, out var combatMode) &&
                combatMode.IsInCombatMode)
            {
                continue;
            }

            // Combat mode removes this component when it releases the mouse rotator.
            // Restore it before movement so the default mover cannot snap the rotation.
            if (!_noRotateOnMoveQuery.HasComp(uid))
                EnsureComp<NoRotateOnMoveComponent>(uid);

            if (mover.WishDir == Vector2.Zero || smoothRotation.RotationSpeed <= 0f)
                continue;

            var currentRotation = _transform.GetWorldRotation(transform);
            var targetRotation = mover.WishDir.ToWorldAngle();
            var rotationDifference = Angle.ShortestDistance(currentRotation, targetRotation).Theta;
            var maxRotation = MathHelper.DegreesToRadians(smoothRotation.RotationSpeed) * frameTime;

            if (Math.Abs(rotationDifference) <= 0.0001)
                continue;

            if (Math.Abs(rotationDifference) <= maxRotation)
            {
                _transform.SetWorldRotation(transform, targetRotation);
                continue;
            }

            var nextRotation = currentRotation + Math.Sign(rotationDifference) * maxRotation;
            _transform.SetWorldRotation(transform, nextRotation);
        }
    }
}
