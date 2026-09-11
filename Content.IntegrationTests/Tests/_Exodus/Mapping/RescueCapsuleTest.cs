using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Portable;
using Content.Server.Body.Components;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.Atmos;
using Content.Shared.Buckle;
using Content.Shared.Doors.Systems;
using Content.Shared.Gravity;
using Content.Shared.Movement.Systems;
using Content.Shared.Power.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.UserInterface;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Exodus.Mapping;

[TestFixture]
public sealed class RescueCapsuleTest
{
    private static readonly ResPath CapsulePath = new("/Maps/_Exodus/Shuttles/rescue_capsule.yml");
    private static readonly ResPath CheckpointPath = new("/Mapping/rescue-capsule-test.yml");

    /// <summary>
    /// Exercises the generated map through the real loader and simulation, including
    /// draft preservation, the electrical network, helm input and human respiration.
    /// </summary>
    [Test]
    public async Task CapsuleSurvivesSaveLoadAndSupportsFlightAndBreathing()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        var loader = entities.System<MapLoaderSystem>();
        var atmos = entities.System<AtmosphereSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        EntityUid draft = default;
        EntityUid capsule = default;
        EntityUid helm = default;
        EntityUid pilot = default;
        EntityUid passenger = default;
        EntityUid scrubber = default;
        EntityUid apc = default;
        EntityUid substation = default;
        MapId draftMapId = default;
        MapId testMapId = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out draftMapId, runMapInit: false);
            Assert.That(loader.TryLoadGrid(draftMapId, CapsulePath, out var draftGrid), Is.True);
            draft = draftGrid!.Value.Owner;
            Assert.That(maps.IsInitialized(draftMapId), Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(draft).EntityLifeStage,
                Is.LessThan(EntityLifeStage.MapInitialized));
            Assert.That(loader.TrySaveGrid(draft, CheckpointPath), Is.True);

            maps.CreateMap(out testMapId, runMapInit: false);
            Assert.That(loader.TryLoadGrid(testMapId, CheckpointPath, out var testGrid), Is.True);
            capsule = testGrid!.Value.Owner;
            var grid = entities.GetComponent<MapGridComponent>(capsule);
            Assert.That(grid.LocalAABB.Width, Is.LessThanOrEqualTo(6));
            Assert.That(grid.LocalAABB.Height, Is.LessThanOrEqualTo(6));
            Assert.That(entities.GetComponent<GravityComponent>(capsule).Enabled, Is.True);
            Assert.That(entities.GetComponent<PhysicsComponent>(capsule).BodyType, Is.EqualTo(BodyType.Dynamic));
            maps.InitializeMap(testMapId);
        });

        // Let node groups, APC consumers and machine parts settle before using the helm.
        await pair.RunSeconds(10);

        await server.WaitAssertion(() =>
        {
            AssertFlightHardwareAndPower(entities, capsule);
            helm = Find(entities, capsule, "ComputerShuttle");
            scrubber = Find(entities, capsule, "PortableScrubber");
            apc = Find(entities, capsule, "APCBasic");
            substation = Find(entities, capsule, "SubstationWallBasic");
            pilot = entities.SpawnEntity("MobHuman", new EntityCoordinates(capsule, 2.5f, 3.5f));
            passenger = entities.SpawnEntity("MobHuman", new EntityCoordinates(capsule, 3.5f, 2.5f));

            var buckle = entities.System<SharedBuckleSystem>();
            Assert.That(buckle.TryBuckle(pilot, pilot, Find(entities, capsule, "ChairPilotSeat", new Vector2(2.5f, 3.5f))), Is.True);
            Assert.That(buckle.TryBuckle(passenger, passenger, Find(entities, capsule, "ChairPilotSeat", new Vector2(3.5f, 2.5f))), Is.True);
            Assert.That(entities.System<AccessReaderSystem>().IsAllowed(pilot, helm), Is.True);
            var consoleLock = entities.GetComponent<ShuttleConsoleLockComponent>(helm);
            Assert.That(entities.System<SharedShuttleConsoleLockSystem>().GetEffectiveLockState(helm, consoleLock), Is.False);

            // Use the same attempt event as opening the console, so normal pilot checks run.
            var attempt = new ActivatableUIOpenAttemptEvent(pilot);
            entities.EventBus.RaiseLocalEvent(helm, attempt);
            Assert.That(attempt.Cancelled, Is.False);
            Assert.That(entities.GetComponent<PilotComponent>(pilot).Console, Is.EqualTo(helm));
            AssertBreathing(entities, atmos, pilot);
            AssertBreathing(entities, atmos, passenger);

            // Boarding must not vent the cabin when the external docking door is held open.
            var airlock = Find(entities, capsule, "AirlockShuttle");
            Assert.That(entities.System<SharedDoorSystem>().TryOpenAndBolt(airlock), Is.True);
        });

        var directions = new (ShuttleButtons Button, Vector2 Direction)[]
        {
            (ShuttleButtons.StrafeUp, Vector2.UnitY),
            (ShuttleButtons.StrafeDown, -Vector2.UnitY),
            (ShuttleButtons.StrafeLeft, -Vector2.UnitX),
            (ShuttleButtons.StrafeRight, Vector2.UnitX),
        };

        foreach (var (button, direction) in directions)
        {
            await server.WaitAssertion(() =>
            {
                physics.SetLinearVelocity(capsule, Vector2.Zero);
                physics.SetAngularVelocity(capsule, 0);
                transforms.SetLocalRotation(capsule, Angle.Zero);
                entities.GetComponent<PilotComponent>(pilot).HeldButtons = button;
            });
            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                var velocity = entities.GetComponent<PhysicsComponent>(capsule).LinearVelocity;
                Assert.That(Vector2.Dot(velocity, direction), Is.GreaterThan(0.01f), $"No thrust for {button}.");
                AssertFlightHardwareAndPower(entities, capsule);
            });
        }

        foreach (var button in new[] { ShuttleButtons.RotateLeft, ShuttleButtons.RotateRight })
        {
            await server.WaitAssertion(() =>
            {
                physics.SetLinearVelocity(capsule, Vector2.Zero);
                physics.SetAngularVelocity(capsule, 0);
                entities.GetComponent<PilotComponent>(pilot).HeldButtons = button;
            });
            await pair.RunSeconds(1);
            await server.WaitAssertion(() =>
            {
                var angularVelocity = entities.GetComponent<PhysicsComponent>(capsule).AngularVelocity;
                var sign = button == ShuttleButtons.RotateLeft ? 1 : -1;
                Assert.That(angularVelocity * sign, Is.GreaterThan(0.001f), $"No rotation for {button}.");
            });
        }

        await server.WaitAssertion(() =>
        {
            transforms.SetLocalRotation(capsule, Angle.Zero);
            physics.SetLinearVelocity(capsule, Vector2.UnitX);
            physics.SetAngularVelocity(capsule, 0.1f);
            entities.GetComponent<PilotComponent>(pilot).HeldButtons = ShuttleButtons.Brake;
        });
        await pair.RunSeconds(5);
        await server.WaitAssertion(() =>
        {
            var body = entities.GetComponent<PhysicsComponent>(capsule);
            Assert.That(body.LinearVelocity.Length(), Is.LessThan(0.5f));
            Assert.That(Math.Abs(body.AngularVelocity), Is.LessThan(0.05f));
            entities.GetComponent<PilotComponent>(pilot).HeldButtons = ShuttleButtons.None;
        });

        // More than a minute of two actual respirators catches leaking hulls and unpowered life support.
        await pair.RunSeconds(60);
        await server.WaitAssertion(() =>
        {
            AssertFlightHardwareAndPower(entities, capsule);
            AssertBreathing(entities, atmos, pilot);
            AssertBreathing(entities, atmos, passenger);
            Assert.That(entities.GetComponent<PortableScrubberComponent>(scrubber).Air.GetMoles(Gas.CarbonDioxide), Is.GreaterThan(0));
            Assert.That(entities.GetComponent<BatteryComponent>(apc).CurrentCharge, Is.GreaterThan(10000));
            Assert.That(entities.GetComponent<BatteryComponent>(substation).CurrentCharge, Is.GreaterThan(1800000));
            Assert.That(maps.IsInitialized(draftMapId), Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(draft).EntityLifeStage,
                Is.LessThan(EntityLifeStage.MapInitialized));
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertFlightHardwareAndPower(IEntityManager entities, EntityUid grid)
    {
        var shuttle = entities.GetComponent<ShuttleComponent>(grid);
        foreach (var thrust in shuttle.LinearThrust)
            Assert.That(thrust, Is.GreaterThan(0), "Every cardinal direction needs usable thrust.");
        Assert.That(shuttle.AngularThrust, Is.GreaterThan(0));

        var thrusters = entities.System<ThrusterSystem>();
        var thrusterQuery = entities.AllEntityQueryEnumerator<ThrusterComponent, TransformComponent>();
        while (thrusterQuery.MoveNext(out var uid, out var thruster, out var transform))
        {
            if (transform.GridUid == grid)
                Assert.That(thrusters.CanEnable(uid, thruster), Is.True, $"Thruster {uid} is blocked or unpowered.");
        }

        var count = 0;
        var query = entities.AllEntityQueryEnumerator<ApcPowerReceiverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var receiver, out var transform))
        {
            if (transform.GridUid != grid)
                continue;

            count++;
            Assert.That(receiver.NeedsPower, Is.True, $"Device {uid} bypasses the power network.");
            Assert.That(receiver.Powered, Is.True, $"Device {uid} has no APC power.");
        }

        Assert.That(count, Is.GreaterThanOrEqualTo(10));
        var generator = Find(entities, grid, "GeneratorWallmountAPU");
        Assert.That(entities.GetComponent<PowerSupplierComponent>(generator).CurrentSupply, Is.GreaterThan(0));
    }

    private static void AssertBreathing(IEntityManager entities, AtmosphereSystem atmos, EntityUid human)
    {
        var mixture = atmos.GetContainingMixture(human);
        Assert.That(mixture, Is.Not.Null);
        Assert.That(mixture!.Pressure, Is.InRange(90f, 115f));
        Assert.That(mixture.Temperature, Is.InRange(280f, 310f));
        Assert.That(mixture.GetMoles(Gas.Oxygen) / mixture.TotalMoles, Is.InRange(0.19f, 0.23f));
        var respirator = entities.GetComponent<RespiratorComponent>(human);
        Assert.That(respirator.SuffocationCycles, Is.Zero);
    }

    private static EntityUid Find(IEntityManager entities, EntityUid grid, string prototype, Vector2? position = null)
    {
        var query = entities.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var metadata, out var transform))
        {
            if (transform.GridUid == grid && metadata.EntityPrototype?.ID == prototype &&
                (position == null || transform.LocalPosition == position.Value))
                return uid;
        }

        throw new KeyNotFoundException($"Capsule has no {prototype} at {position}.");
    }
}
