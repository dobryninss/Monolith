using System.Numerics;
using Content.Server._Exodus.FireControl;
using Content.Shared._Mono.FireControl;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Exodus;

[TestFixture]
[TestOf(typeof(IntrinsicFireControlSystem))]
public sealed class IntrinsicFireControlTest
{
    [TestPrototypes]
    private const string Prototypes = """
- type: entity
  id: TestIntrinsicFireControlGun
  components:
  - type: Gun
    fireRate: 1
    recoil: 0
    cameraRecoilScalar: 0
    minAngle: 0
    maxAngle: 0
    soundGunshot:
      path: /Audio/Weapons/Guns/Gunshots/laser_cannon2.ogg
    soundEmpty:
      path: /Audio/Weapons/Guns/EmptyAlarm/smg_empty_alarm.ogg
  - type: BasicEntityAmmoProvider
    proto: BulletDisablerPractice
    capacity: 1
    count: 1

- type: entity
  id: TestIntrinsicFireControlOwner
  parent: TestIntrinsicFireControlGun
  components:
  - type: Physics
    bodyType: Kinematic
    canCollide: false
  - type: RadarConsole
    followEntity: true
  - type: IntrinsicFireControl
    weapons:
    - prototype: TestIntrinsicFireControlGun
      offset: 2, 1
  - type: UserInterface
    interfaces:
      enum.FireControlConsoleUiKey.Key:
        type: FireControlConsoleBoundUserInterface
        requireInputValidation: false
""";

    [Test]
    public async Task WeaponCoordinatesFollowMovementWithoutUiRefresh()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();

        await server.WaitAssertion(() =>
        {
            var sourceMap = maps.CreateMap();
            var destinationMap = maps.CreateMap();
            var owner = entities.SpawnEntity("TestIntrinsicFireControlOwner", new EntityCoordinates(sourceMap, Vector2.Zero));
            var opened = new BoundUIOpenedEvent(FireControlConsoleUiKey.Key, owner, owner);
            entities.EventBus.RaiseLocalEvent(owner, opened);
            var ui = entities.System<SharedUserInterfaceSystem>();
            Assert.That(ui.TryGetUiState<FireControlConsoleBoundInterfaceState>(owner, FireControlConsoleUiKey.Key, out var state), Is.True);
            var snapshot = state!;
            Assert.That(snapshot.FireControllables, Has.Length.EqualTo(2));

            transform.SetCoordinates(owner, new EntityCoordinates(sourceMap, new Vector2(100f, -50f)));
            transform.SetWorldRotation(owner, Angle.FromDegrees(90));
            AssertWeaponPositions(snapshot);

            // The original UI snapshot must also remain valid after moving to another map.
            transform.SetCoordinates(owner, new EntityCoordinates(destinationMap, new Vector2(-200f, 75f)));
            AssertWeaponPositions(snapshot);
            Assert.That(ui.TryGetUiState<FireControlConsoleBoundInterfaceState>(owner, FireControlConsoleUiKey.Key, out var unchanged), Is.True);
            Assert.That(unchanged, Is.SameAs(snapshot));

            entities.DeleteEntity(sourceMap);
            entities.DeleteEntity(destinationMap);
        });

        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();

        void AssertWeaponPositions(FireControlConsoleBoundInterfaceState state)
        {
            foreach (var entry in state.FireControllables)
            {
                var weapon = entities.GetEntity(entry.NetEntity);
                var expected = transform.GetMapCoordinates(weapon);
                var actual = transform.ToMapCoordinates(entities.GetCoordinates(entry.Coordinates));
                Assert.That(actual.MapId, Is.EqualTo(expected.MapId));
                Assert.That(Vector2.Distance(actual.Position, expected.Position), Is.LessThan(0.001f));
            }
        }
    }

    [Test]
    public async Task FiringAudioIncludesOwnerOnlyForInterfaceShots([Values] bool throughInterface, [Values] bool loaded)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>().CreateMap();
            var owner = entities.SpawnEntity("TestIntrinsicFireControlOwner", new EntityCoordinates(map, Vector2.Zero));
            var ammo = entities.GetComponent<BasicEntityAmmoProviderComponent>(owner);
            ammo.Count = loaded ? 1 : 0;
            entities.Dirty(owner, ammo);
            var target = new EntityCoordinates(map, new Vector2(10f, 0f));
            var expectedSound = loaded
                ? "/Audio/Weapons/Guns/Gunshots/laser_cannon2.ogg"
                : "/Audio/Weapons/Guns/EmptyAlarm/smg_empty_alarm.ogg";

            Fire();
            AssertAudio();
            // A rejected request during cooldown must not produce another firing sound.
            Fire();
            AssertAudio();
            entities.DeleteEntity(map);

            void Fire()
            {
                if (throughInterface)
                {
                    var message = new FireControlConsoleFireMessage([entities.GetNetEntity(owner)], entities.GetNetCoordinates(target))
                    {
                        Actor = owner,
                        UiKey = FireControlConsoleUiKey.Key,
                    };
                    entities.EventBus.RaiseLocalEvent(owner, message);
                }
                else
                {
                    entities.System<SharedGunSystem>().AttemptShoot(owner, owner, entities.GetComponent<GunComponent>(owner), target);
                }
            }

            void AssertAudio()
            {
                var query = entities.EntityQueryEnumerator<AudioComponent, TransformComponent>();
                var count = 0;
                while (query.MoveNext(out _, out var audio, out var transform))
                {
                    if (transform.MapUid != map || audio.FileName != expectedSound)
                        continue;

                    Assert.That(audio.ExcludedEntity, Is.EqualTo(throughInterface ? (EntityUid?) null : owner));
                    count++;
                }

                Assert.That(count, Is.EqualTo(1));
            }
        });

        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }
}
