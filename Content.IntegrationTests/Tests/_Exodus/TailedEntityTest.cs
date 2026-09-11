using System.Numerics;
using Content.Shared._Exodus.Tailed;
using Content.Shared.Damage;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Dynamics.Joints;

namespace Content.IntegrationTests.Tests._Exodus;

[TestFixture]
[TestOf(typeof(TailedEntitySystem))]
public sealed class TailedEntityTest
{
    [TestPrototypes]
    private const string Prototypes = """
- type: entity
  id: TestTailedHead
  components:
  - type: Physics
    bodyType: Kinematic
    canCollide: false
  - type: TailedEntity
    prototype: TestTailedSegment
    amount: 3
    maxSegmentSpeed: 0
    enableRotationControl: false

- type: entity
  id: TestMultiTailedHead
  parent: TestTailedHead
  components:
  - type: TailedEntity
    spacing: 1.5
    startSpacingMultiplier: 1.75
    startOffsets:
    - 0, 2
    - 0, -2
    - 1, 0
    startAngleOffsets: [30, -45]
    anchorAOffset: -0.25, 0.125
    anchorBOffset: 0.25, -0.125
    minLengthMultiplier: 0.7
    maxLengthMultiplier: 1.3
    stiffness: 35
    damping: 4

- type: entity
  id: TestSingleSegmentMultiTailedHead
  parent: TestMultiTailedHead
  components:
  - type: TailedEntity
    amount: 1

- type: entity
  id: TestTailedSegment
  components:
  - type: Physics
    bodyType: Kinematic
    canCollide: false

- type: entity
  id: TestTailedRockSpitter
  parent: TestTailedHead
  components:
  - type: TailedEntity
    prototype: TestTailedRockSegment
  - type: Damageable
    damageContainer: Biological
  - type: Gun
    projectileSpeed: 450
    fireRate: 0.15
    recoil: 0
    cameraRecoilScalar: 0
    minAngle: 0
    maxAngle: 0
    soundGunshot: null
    soundEmpty: null
  - type: BasicEntityAmmoProvider
    proto: SpaceLeviathanRockProjectile

- type: entity
  id: TestTailedRockSegment
  parent: TestTailedSegment
  components:
  - type: Physics
    canCollide: true
  - type: Damageable
    damageContainer: Biological
  - type: Fixtures
    fixtures:
      body:
        shape: !type:PhysShapeAabb
          bounds: "-0.4,-0.4,0.4,0.4"
        hard: true
        layer: [BulletImpassable]
        mask: []
""";

    [Test]
    public async Task MapChangesRestoreExistingChain(
        [Values] MapTransfer transfer,
        [Values("TestTailedHead", "TestMultiTailedHead", "TestSingleSegmentMultiTailedHead")] string headPrototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        EntityUid sourceMap = default;
        EntityUid destinationMap = default;
        EntityUid head = default;
        EntityUid[] segments = [];
        Vector2[] segmentOffsets = [];
        Angle[] segmentRotations = [];
        Joint healthyJoint = default!;

        await server.WaitAssertion(() =>
        {
            sourceMap = maps.CreateMap(out var sourceMapId);
            destinationMap = maps.CreateMap();
            head = entities.SpawnEntity(headPrototype, new EntityCoordinates(sourceMap, new Vector2(0.5f)));
            segments = entities.GetComponent<TailedEntityComponent>(head).TailSegments.ToArray();
            AssertChain(entities, head, sourceMap, segments);
            healthyJoint = FindJoint(entities, head, segments[0]);

            // Recovery must preserve the spawn layout, including the sprite rotation modifier.
            segmentOffsets = new Vector2[segments.Length];
            segmentRotations = new Angle[segments.Length];
            var headPosition = transform.GetWorldPosition(head);
            for (var i = 0; i < segments.Length; i++)
            {
                segmentOffsets[i] = transform.GetWorldPosition(segments[i]) - headPosition;
                segmentRotations[i] = transform.GetWorldRotation(segments[i]);
            }

            switch (transfer)
            {
                case MapTransfer.Head:
                    transform.SetWorldRotation(head, Angle.FromDegrees(37));
                    transform.SetCoordinates(head, new EntityCoordinates(destinationMap, new Vector2(20f)));
                    Assert.That(entities.GetComponent<JointComponent>(head).JointCount, Is.Zero);
                    break;
                case MapTransfer.FirstSegment:
                    var tailed = entities.GetComponent<TailedEntityComponent>(head);
                    var firstSegment = segments[tailed.StartOffsets.Count > 1 ? tailed.Amount : 0];
                    transform.SetCoordinates(firstSegment, new EntityCoordinates(destinationMap, new Vector2(20f)));
                    Assert.That(entities.GetComponent<JointComponent>(firstSegment).JointCount, Is.Zero);
                    break;
                case MapTransfer.LastSegment:
                    transform.SetCoordinates(segments[^1], new EntityCoordinates(destinationMap, new Vector2(20f)));
                    Assert.That(entities.GetComponent<JointComponent>(segments[^1]).JointCount, Is.Zero);
                    break;
                case MapTransfer.Grid:
                case MapTransfer.GridRoundTrip:
                    var grid = server.ResolveDependency<IMapManager>().CreateGridEntity(sourceMapId);
                    maps.SetTile(grid.Owner, grid.Comp, headPosition.Floored(), new Tile(1));
                    transform.SetParent(head, grid.Owner);
                    foreach (var segment in segments)
                    {
                        maps.SetTile(grid.Owner, grid.Comp, transform.GetWorldPosition(segment).Floored(), new Tile(1));
                        transform.SetParent(segment, grid.Owner);
                    }

                    transform.SetCoordinates(grid.Owner, new EntityCoordinates(destinationMap, Vector2.Zero));
                    if (transfer == MapTransfer.GridRoundTrip)
                    {
                        // NoFTL can return entities before the next update, leaving their final map unchanged.
                        transform.SetCoordinates(grid.Owner, new EntityCoordinates(sourceMap, Vector2.Zero));
                    }

                    Assert.That(entities.GetComponent<JointComponent>(head).JointCount, Is.Zero);
                    foreach (var segment in segments)
                        Assert.That(entities.GetComponent<JointComponent>(segment).JointCount, Is.Zero);
                    break;
            }
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var expectedMap = transfer is MapTransfer.Head or MapTransfer.Grid ? destinationMap : sourceMap;
            AssertChain(entities, head, expectedMap, segments);
            Assert.That(entities.GetComponent<TailedEntityComponent>(head).TailJointsDirty, Is.False);
            if (transfer == MapTransfer.LastSegment)
                Assert.That(FindJoint(entities, head, segments[0]), Is.SameAs(healthyJoint));

            var headPosition = transform.GetWorldPosition(head);
            var headRotation = transform.GetWorldRotation(head);
            for (var i = 0; i < segments.Length; i++)
            {
                var expectedPosition = headPosition + headRotation.RotateVec(segmentOffsets[i]);
                var expectedRotation = headRotation + segmentRotations[i];
                Assert.That(Vector2.Distance(transform.GetWorldPosition(segments[i]), expectedPosition), Is.LessThan(0.001f));
                Assert.That(Angle.ShortestDistance(expectedRotation, transform.GetWorldRotation(segments[i])).Theta,
                    Is.Zero.Within(0.001));
            }

            entities.DeleteEntity(sourceMap);
            entities.DeleteEntity(destinationMap);
        });

        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SegmentDeletionStillDeletesEntireChain(
        [Values] bool pendingMapRepair,
        [Values("TestTailedHead", "TestMultiTailedHead", "TestSingleSegmentMultiTailedHead")] string headPrototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        EntityUid sourceMap = default;
        EntityUid destinationMap = default;
        EntityUid head = default;
        EntityUid[] segments = [];

        await server.WaitAssertion(() =>
        {
            sourceMap = maps.CreateMap();
            destinationMap = maps.CreateMap();
            head = entities.SpawnEntity(headPrototype, new EntityCoordinates(sourceMap, Vector2.Zero));
            segments = entities.GetComponent<TailedEntityComponent>(head).TailSegments.ToArray();
            AssertChain(entities, head, sourceMap, segments);

            if (pendingMapRepair)
                transform.SetCoordinates(head, new EntityCoordinates(destinationMap, Vector2.Zero));

            entities.QueueDeleteEntity(segments[1]);
        });

        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            Assert.That(entities.EntityExists(head), Is.False);
            foreach (var segment in segments)
                Assert.That(entities.EntityExists(segment), Is.False);

            entities.DeleteEntity(sourceMap);
            entities.DeleteEntity(destinationMap);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RockSpitIgnoresOnlyItsOwnTail(bool ignoreShooter)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var transform = entities.System<SharedTransformSystem>();
        EntityUid map = default;
        EntityUid shooter = default;
        EntityUid target = default;

        await server.WaitAssertion(() =>
        {
            map = entities.System<SharedMapSystem>().CreateMap();
            shooter = entities.SpawnEntity("TestTailedRockSpitter", new EntityCoordinates(map, Vector2.Zero));
            var firstSegment = entities.GetComponent<TailedEntityComponent>(shooter).TailSegments[0];
            var direction = Vector2.Normalize(transform.GetWorldPosition(firstSegment) - transform.GetWorldPosition(shooter));
            // Keep the target's explosion well outside the shooter's tail.
            target = entities.SpawnEntity("TestTailedRockSpitter", new EntityCoordinates(map, direction * 100f));

            entities.System<SharedGunSystem>().AttemptShoot(shooter, shooter,
                entities.GetComponent<GunComponent>(shooter), new EntityCoordinates(map, direction * 110f));

            var projectiles = entities.EntityQueryEnumerator<ProjectileComponent>();
            var shots = 0;
            while (projectiles.MoveNext(out var uid, out var projectile))
            {
                if (projectile.Shooter != shooter || projectile.Weapon != shooter)
                    continue;

                projectile.IgnoreShooter = ignoreShooter;
                entities.Dirty(uid, projectile);
                shots++;
            }

            Assert.That(shots, Is.EqualTo(1));
        });

        await server.WaitRunTicks(20);

        await server.WaitAssertion(() =>
        {
            var shooterDamage = entities.GetComponent<DamageableComponent>(shooter).TotalDamage.Float();
            var targetDamage = entities.GetComponent<DamageableComponent>(target).TotalDamage.Float();
            if (ignoreShooter)
            {
                Assert.That(shooterDamage, Is.Zero);
                Assert.That(targetDamage, Is.GreaterThan(0f));
            }
            else
            {
                Assert.That(shooterDamage, Is.GreaterThan(0f));
                Assert.That(targetDamage, Is.Zero);
            }

            entities.DeleteEntity(map);
        });

        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    private static void AssertChain(IEntityManager entities, EntityUid head, EntityUid map, EntityUid[] segments)
    {
        var tailed = entities.GetComponent<TailedEntityComponent>(head);
        Assert.That(segments, Has.Length.EqualTo(tailed.Amount * tailed.StartOffsets.Count));
        Assert.That(tailed.TailSegments, Is.EqualTo(segments));
        Assert.That(entities.GetComponent<TransformComponent>(head).MapUid, Is.EqualTo(map));
        Assert.That(entities.GetComponent<JointComponent>(head).JointCount, Is.EqualTo(tailed.StartOffsets.Count));

        for (var tailIndex = 0; tailIndex < tailed.StartOffsets.Count; tailIndex++)
        {
            var previous = head;
            for (var i = 0; i < tailed.Amount; i++)
            {
                var segment = segments[tailIndex * tailed.Amount + i];
                var tail = entities.GetComponent<TailedEntitySegmentComponent>(segment);
                var joints = entities.GetComponent<JointComponent>(segment);
                Assert.That(tail.HeadEntity, Is.EqualTo(head));
                Assert.That(tail.Index, Is.EqualTo(i));
                Assert.That(tail.TailIndex, Is.EqualTo(tailIndex));
                Assert.That(entities.GetComponent<TransformComponent>(segment).MapUid, Is.EqualTo(map));
                Assert.That(joints.JointCount, Is.EqualTo(i == tailed.Amount - 1 ? 1 : 2));
                var joint = FindJoint(entities, previous, segment);
                Assert.That(joint, Is.TypeOf<DistanceJoint>());
                Assert.That(joints.GetJoints[joint.ID], Is.SameAs(joint));

                var distanceJoint = (DistanceJoint) joint;
                var expectedLength = tailed.Spacing * (i == 0 ? tailed.StartSpacingMultiplier : 1f);
                var expectedAnchorA = tailed.AnchorAOffset + (i == 0 ? tailed.StartOffsets[tailIndex] : Vector2.Zero);
                Assert.That(distanceJoint.LocalAnchorA, Is.EqualTo(expectedAnchorA));
                Assert.That(distanceJoint.LocalAnchorB, Is.EqualTo(tailed.AnchorBOffset));
                Assert.That(distanceJoint.Length, Is.EqualTo(expectedLength).Within(0.001f));
                Assert.That(distanceJoint.MinLength, Is.EqualTo(expectedLength * tailed.MinLengthMultiplier).Within(0.001f));
                Assert.That(distanceJoint.MaxLength, Is.EqualTo(expectedLength * tailed.MaxLengthMultiplier).Within(0.001f));
                Assert.That(distanceJoint.Stiffness, Is.EqualTo(tailed.Stiffness));
                Assert.That(distanceJoint.Damping, Is.EqualTo(tailed.Damping));
                previous = segment;
            }
        }
    }

    private static Joint FindJoint(IEntityManager entities, EntityUid bodyA, EntityUid bodyB)
    {
        foreach (var joint in entities.GetComponent<JointComponent>(bodyA).GetJoints.Values)
        {
            if (joint.BodyAUid == bodyA && joint.BodyBUid == bodyB)
                return joint;
        }

        throw new AssertionException($"Missing joint between {bodyA} and {bodyB}.");
    }

    public enum MapTransfer : byte
    {
        Head,
        FirstSegment,
        LastSegment,
        Grid,
        GridRoundTrip,
    }
}
