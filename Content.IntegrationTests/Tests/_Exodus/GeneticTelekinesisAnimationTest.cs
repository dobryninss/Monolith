using System.Collections.Generic;
using System.Numerics;
using Content.Client._Exodus.Genetics;
using Content.Shared._Exodus.Genetics;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Exodus;

[TestFixture]
[TestOf(typeof(GeneticTelekinesisAnimationSystem))]
public sealed class GeneticTelekinesisAnimationTest
{
    [TestCase("complete")]
    [TestCase("cancel")]
    [TestCase("timeout")]
    public async Task PlacedItemRemainsInTheVisibleSpriteTreeAfterAnimation(string outcome)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        EntityUid item = default;
        await server.WaitPost(() =>
        {
            item = server.EntMan.SpawnEntity("Crowbar", map.MapCoords.Offset(new Vector2(6, 0)));
        });
        await pair.RunTicksSync(5);

        await client.WaitAssertion(() =>
        {
            var entities = client.EntMan;
            var uid = pair.ToClientUid(item);
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var xform = entities.GetComponent<TransformComponent>(uid);
            var transform = entities.System<SharedTransformSystem>();
            var sprites = entities.System<SpriteSystem>();
            var tree = entities.System<SpriteTreeSystem>();
            var animations = entities.System<GeneticTelekinesisAnimationSystem>();
            var timing = client.ResolveDependency<IGameTiming>();

            // Use a nonzero native offset to verify that restoration preserves the item's sprite.
            var originalOffset = new Vector2(0.1f, 0.1f);
            sprite.NoRotation = false;
            sprites.SetSnapCardinals((uid, sprite), false);
            transform.SetWorldRotation(uid, Angle.Zero);
            sprites.SetOffset((uid, sprite), originalOffset);
            tree.QueueTreeUpdate((uid, sprite));
            tree.UpdateTreePositions();

            var end = xform.Coordinates;
            var start = end.Offset(new Vector2(-6, 0));
            var startMap = transform.ToMapCoordinates(start);
            var endMap = transform.ToMapCoordinates(end);
            var duration = TimeSpan.FromSeconds(0.35);
            entities.EventBus.RaiseEvent(EventSource.Network, new GeneticTelekinesisAnimationEvent(
                entities.GetNetEntity(uid), entities.GetNetCoordinates(start), entities.GetNetCoordinates(end), duration));
            var animation = entities.GetComponent<GeneticTelekinesisAnimationComponent>(uid);

            bool InVisibleTree(Vector2 position)
            {
                var found = new List<Entity<SpriteComponent, TransformComponent>>();
                // Use the same coarse spatial lookup as the renderer, not freshly calculated sprite bounds.
                tree.QueryAabb(found, endMap.MapId, Box2.CenteredAround(position, Vector2.One), approx: true);
                foreach (var entry in found)
                {
                    if (entry.Owner == uid)
                        return true;
                }
                return false;
            }

            animations.FrameUpdate(0);
            tree.UpdateTreePositions();
            Assert.That(InVisibleTree(startMap.Position), Is.True, "The flying sprite must enter the tree at its visual position.");

            animation.Started = timing.CurTime - duration / 2;
            animations.FrameUpdate(0);
            tree.UpdateTreePositions();
            Assert.That(InVisibleTree((startMap.Position + endMap.Position) / 2), Is.True);

            switch (outcome)
            {
                case "complete":
                    animation.Started = timing.CurTime - duration;
                    animations.FrameUpdate(0);
                    break;
                case "timeout":
                    animation.Expires = timing.CurTime;
                    animations.FrameUpdate(0);
                    break;
                case "cancel":
                    entities.RemoveComponent<GeneticTelekinesisAnimationComponent>(uid);
                    break;
            }

            tree.UpdateTreePositions();
            Assert.That(sprite.Offset, Is.EqualTo(originalOffset));
            Assert.That(xform.Coordinates, Is.EqualTo(end), "The visual effect must not move the actual item.");
            Assert.That(InVisibleTree(endMap.Position), Is.True, "The stationary item must remain visible at its destination.");
            Assert.That(InVisibleTree(startMap.Position), Is.False, "No stale culling bounds may remain at the flight's origin.");
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(item));
        await pair.CleanReturnAsync();
    }
}
