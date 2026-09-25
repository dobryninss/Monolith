using System.Numerics;
using Content.Server._Exodus.Genetics;
using Content.Server.Cloning;
using Content.Server.Medical.Components;
using Content.Server.Power.Components;
using Content.Shared._Exodus.Genetics;
using Content.Shared._Shitmed.Body.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Exodus;

[TestFixture]
[TestOf(typeof(GeneticsSystem))]
public sealed class GeneticsTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FullAndAdminInjectorsApplyOnceWithExpectedDamage(bool admin)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var genetics = entities.System<GeneticsSystem>();

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>().CreateMap();
            var user = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            var target = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, new Vector2(0.5f, 0)));
            var item = entities.SpawnEntity(admin ? "GeneticInjectorNoBreathing" : "GeneticInjector",
                new EntityCoordinates(map, Vector2.Zero));
            Assert.That(genetics.TryGetLivingGenome(target, out var genome), Is.True);
            var round = genetics.GetRound();
            var breathing = round.Mutations.IndexOf("GeneticNoBreathing");
            var injector = entities.GetComponent<GeneticInjectorComponent>(item);
            injector.InjectionTime = TimeSpan.Zero;
            if (admin)
            {
                injector.Context = "old-round";
                injector.Block = -1; // The configured mutation must resolve against this round.
            }
            else
            {
                injector.Context = round.Context;
                injector.Sample = genetics.Capture((target, genome!));
                injector.Sample.Blocks[breathing] = 0xFFF;
                injector.Sample.Blocks[round.Mutations.IndexOf("GeneticColdResistance")] = 0xFFF;
                injector.Sample.Blocks[round.Mutations.IndexOf("GeneticLethargy")] = 0xFFF;
            }
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, item), Is.True);
            var damage = entities.GetComponent<DamageableComponent>(target);
            var poison = damage.Damage.DamageDict["Poison"];
            var expectedDamage = Content.Shared.FixedPoint.FixedPoint2.New(admin ? 0 : 5);
            var interaction = new AfterInteractEvent(user, item, target, new EntityCoordinates(map, new Vector2(0.5f, 0)), true);
            entities.EventBus.RaiseComponentEvent(item, injector, interaction);
            Assert.That(injector.Used, Is.True);
            Assert.That(genome!.Blocks[breathing], Is.EqualTo(0xFFF));
            Assert.That(damage.Damage.DamageDict["Poison"] - poison, Is.EqualTo(expectedDamage));
            if (!admin)
                Assert.That(genome.Blocks, Is.EqualTo(injector.Sample!.Blocks));
            var revision = genome.Revision;
            interaction = new AfterInteractEvent(user, item, target, new EntityCoordinates(map, new Vector2(0.5f, 0)), true);
            entities.EventBus.RaiseComponentEvent(item, injector, interaction);
            Assert.That(genome.Revision, Is.EqualTo(revision), "Used injectors cannot apply twice.");
            Assert.That(damage.Damage.DamageDict["Poison"] - poison, Is.EqualTo(expectedDamage));
            entities.DeleteEntity(map);
            DeleteCipher(entities, round.Context);
        });
        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ThreeDigitActivationEmptyBlocksAndChemicalReset()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var genetics = entities.System<GeneticsSystem>();

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>().CreateMap();
            var body = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            Assert.That(genetics.TryGetLivingGenome(body, out var genome), Is.True);
            var round = genetics.GetRound();
            Assert.That(round.Mutations.Count, Is.EqualTo(50));
            Assert.That(genome!.StabilityCapacity, Is.EqualTo(60));
            var empty = round.Mutations.IndexOf(null);
            Assert.That(empty, Is.GreaterThanOrEqualTo(0));
            Assert.That(genetics.TrySetBlock((body, genome), empty, 0xFFF, body), Is.True);
            Assert.That(genome.Active, Is.Empty);
            Assert.That(genome.Stability, Is.EqualTo(60));

            var block = round.Mutations.IndexOf("GeneticNoBreathing");
            foreach (var value in new[] { 0xF00, 0xDFB, 0xD9F, 0xCFF })
            {
                Assert.That(genetics.TrySetBlock((body, genome), block, value, body), Is.True);
                Assert.That(genome.Active, Is.Empty, $"Every digit matters: {value:X3} must fail D/A/C.");
            }
            foreach (var value in new[] { 0xDAC, 0xDAF, 0xFBC, 0xFFF })
            {
                Assert.That(genetics.TrySetBlock((body, genome), block, value, body), Is.True);
                Assert.That(genome.Active.Count, Is.EqualTo(1));
            }
            var sample = genetics.Capture((body, genome));
            var baseline = genome.Baseline.ToArray();
            Assert.That(genetics.TryStabilize(body), Is.True);
            Assert.That(genome.Active, Is.Empty);
            Assert.That(genome.Blocks, Is.All.EqualTo(0));
            Assert.That(genome.Stability, Is.EqualTo(60));
            Assert.That(genome.Baseline, Is.EqualTo(baseline));
            Assert.That(sample.Blocks[block], Is.EqualTo(0xFFF), "Medicine must not modify stored samples.");
            var revision = genome.Revision;
            Assert.That(genetics.TryStabilize(body), Is.True);
            Assert.That(genome.Revision, Is.EqualTo(revision), "Subsequent metabolism must not rescan an unchanged genome.");

            for (var digit = 0; digit < 3; digit++)
            {
                genetics.TrySetBlock((body, genome), empty, 0x123, body);
                Assert.That(genetics.TryRandomizeDigit((body, genome), empty, digit, body), Is.True);
                var mask = 0xFFF ^ (0xF << ((2 - digit) * 4));
                Assert.That(genome.Blocks[empty] & mask, Is.EqualTo(0x123 & mask));
            }
            revision = genome.Revision;
            Assert.That(genetics.TryRandomizeDigit((body, genome), empty, 3, body), Is.False);
            Assert.That(genome.Revision, Is.EqualTo(revision));
            entities.System<MobStateSystem>().ChangeMobState(body, MobState.Critical);
            Assert.That(genetics.TryGetLivingGenome(body, out _), Is.True);
            entities.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);
            Assert.That(genetics.TryGetLivingGenome(body, out _), Is.False);
            Assert.That(genetics.TryRandomizeDigit((body, genome), empty, 0, body), Is.False);
            entities.DeleteEntity(map);
            DeleteCipher(entities, round.Context);
        });
        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrdinaryLaboratoryHidesGenesRejectsExactEditsAndStopsOnDeath()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var genetics = entities.System<GeneticsSystem>();
        var laboratories = entities.System<GeneticsLaboratorySystem>();

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>().CreateMap();
            var patient = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            var user = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, new Vector2(0.5f, 0)));
            var machine = entities.SpawnEntity("GeneticsLaboratory", new EntityCoordinates(map, Vector2.Zero));
            // Power networking is not the subject of this test.
            entities.RemoveComponent<ApcPowerReceiverComponent>(machine);
            var lab = entities.GetComponent<GeneticsLaboratoryComponent>(machine);
            lab.ScanDuration = TimeSpan.Zero;
            lab.EditDuration = TimeSpan.Zero;
            lab.EditCost = 0;
            lab.GenomeInjectorCost = 0;
            var scanner = entities.GetComponent<MedicalScannerComponent>(machine);
            Assert.That(entities.System<SharedContainerSystem>().Insert(patient, scanner.BodyContainer), Is.True);
            Assert.That(genetics.TryGetLivingGenome(patient, out var genome), Is.True);
            var round = genetics.GetRound();

            void Send(GeneticsOperation operation, int block = 0, int digit = 0)
            {
                var message = new GeneticsMessage(operation, entities.GetNetEntity(patient), genome!.Revision,
                    block: block, value: 0xFFF, digit: digit) { Actor = user };
                entities.EventBus.RaiseComponentEvent(machine, lab, message);
            }

            Send(GeneticsOperation.Scan);
            Assert.That(lab.Pending, Is.Not.Null);
            laboratories.Update(0);
            var ui = entities.System<UserInterfaceSystem>();
            Assert.That(ui.TryGetUiState<GeneticsUiState>(machine, GeneticsUiKey.Laboratory, out var state), Is.True);
            Assert.That(state!.Stability, Is.Null);
            Assert.That(state.Blocks.Count, Is.EqualTo(50));
            foreach (var block in state.Blocks)
            {
                Assert.That(block.Name, Is.Null);
                Assert.That(block.Description, Is.Null);
                Assert.That(block.Active, Is.Null);
            }
            var revision = genome!.Revision;
            foreach (var operation in new[] { GeneticsOperation.SetBlock, GeneticsOperation.Reset, GeneticsOperation.RestoreBuffer })
            {
                Send(operation);
                Assert.That(lab.Pending, Is.Null, "Forged ADMIN operations must be rejected on the server.");
                Assert.That(genome.Revision, Is.EqualTo(revision));
            }
            var empty = round.Mutations.IndexOf(null);
            var previous = genome.Blocks[empty];
            var damage = entities.GetComponent<DamageableComponent>(patient);
            var poison = damage.Damage.DamageDict["Poison"];
            Send(GeneticsOperation.Edit, empty, 1);
            laboratories.Update(0);
            Assert.That(genome.Blocks[empty] & 0xF0F, Is.EqualTo(previous & 0xF0F));
            Assert.That(damage.Damage.DamageDict["Poison"] - poison, Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.New(10)));

            Send(GeneticsOperation.PrintGenome);
            var plannedSample = lab.Pending!.Sample;
            Assert.That(plannedSample!.Blocks, Is.EqualTo(genome.Blocks));
            laboratories.Update(0);
            var injectors = entities.AllEntityQueryEnumerator<GeneticInjectorComponent>();
            var found = false;
            while (injectors.MoveNext(out _, out var injector))
            {
                if (injector.Sample is not { } printed || !ReferenceEquals(printed, plannedSample))
                    continue;
                found = true;
                Assert.That(printed.Blocks, Is.EqualTo(genome.Blocks));
            }
            Assert.That(found, Is.True);
            Send(GeneticsOperation.PrintGenome);
            Assert.That(lab.Pending, Is.Not.Null);
            entities.System<MobStateSystem>().ChangeMobState(patient, MobState.Dead);
            Assert.That(lab.Pending, Is.Null);
            Assert.That(lab.ScannedRevision, Is.EqualTo(-1));
            Send(GeneticsOperation.StoreBuffer);
            Assert.That(lab.Buffers[0], Is.Null);
            entities.DeleteEntity(map);
            DeleteCipher(entities, round.Context);
        });
        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SamplesAreIndependentAndRequireTheCurrentCipher()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var genetics = entities.System<GeneticsSystem>();

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>().CreateMap();
            var donor = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            var recipient = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.One));
            Assert.That(genetics.TryGetGenome(donor, out var donorGenome), Is.True);
            Assert.That(genetics.TryGetGenome(recipient, out var recipientGenome), Is.True);

            var round = genetics.GetRound();
            Assert.That(genetics.GetRound(), Is.SameAs(round), "The nullspace cipher must be reused.");
            Assert.That(donorGenome!.Context, Is.EqualTo(recipientGenome!.Context));
            Assert.That(donorGenome.Active, Is.Empty);
            var block = round.Mutations.IndexOf("GeneticNoBreathing");
            Assert.That(block, Is.GreaterThanOrEqualTo(0));

            var baseline = genetics.Capture((donor, donorGenome));
            Assert.That(genetics.TrySetBlock((donor, donorGenome), block, GeneticsSystem.MaxBlockValue, donor), Is.True);
            var sample = genetics.Capture((donor, donorGenome));
            Assert.That(genetics.TryApply((recipient, recipientGenome), sample, donor), Is.True);
            sample.Blocks[block] = 0;
            Assert.That(recipientGenome.Blocks[block], Is.EqualTo(GeneticsSystem.MaxBlockValue));
            Assert.That(genetics.TryApply((donor, donorGenome), baseline, donor), Is.True);
            Assert.That(donorGenome.Active, Is.Empty);
            Assert.That(recipientGenome.Active, Does.Contain(round.Mutations[block]));

            var revision = recipientGenome.Revision;
            sample.Context = "another-round";
            Assert.That(genetics.TryApply((recipient, recipientGenome), sample, donor), Is.False);
            sample.Context = round.Context;
            sample.Blocks[block] = ushort.MaxValue;
            Assert.That(genetics.TryApply((recipient, recipientGenome), sample, donor), Is.False);
            Assert.That(genetics.TrySetBlock((recipient, recipientGenome), -1, 0, donor), Is.False);
            Assert.That(recipientGenome.Revision, Is.EqualTo(revision));

            entities.DeleteEntity(map);
            DeleteCipher(entities, round.Context);
        });
        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemovingGenesPreservesInnateTraitsAndNormalStaminaRecovery()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var genetics = entities.System<GeneticsSystem>();
        var stamina = entities.System<StaminaSystem>();

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>().CreateMap();
            var body = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            var innate = entities.AddComponent<BreathingImmunityComponent>(body);
            Assert.That(genetics.TryGetGenome(body, out var genome), Is.True);
            var round = genetics.GetRound();
            var breathing = round.Mutations.IndexOf("GeneticNoBreathing");
            var hulk = round.Mutations.IndexOf("GeneticHulk");
            Assert.That(genetics.TrySetBlock((body, genome!), breathing, GeneticsSystem.MaxBlockValue, body), Is.True);
            Assert.That(genetics.TrySetBlock((body, genome!), hulk, GeneticsSystem.MaxBlockValue, body), Is.True);

            var respiration = new RespirationAttemptEvent();
            entities.EventBus.RaiseLocalEvent(body, ref respiration);
            Assert.That(respiration.Cancelled, Is.True);
            var staminaComponent = entities.GetComponent<StaminaComponent>(body);
            stamina.TakeStaminaDamage(body, 40, staminaComponent, visual: false);
            Assert.That(staminaComponent.StaminaDamage, Is.EqualTo(10));
            stamina.TakeStaminaDamage(body, -4, staminaComponent, visual: false);
            Assert.That(staminaComponent.StaminaDamage, Is.EqualTo(6), "Recovery must not be reduced by Hulk.");

            Assert.That(genetics.TrySetBlock((body, genome!), breathing, 0, body), Is.True);
            Assert.That(genetics.TrySetBlock((body, genome!), hulk, 0, body), Is.True);
            respiration = new RespirationAttemptEvent();
            entities.EventBus.RaiseLocalEvent(body, ref respiration);
            Assert.That(respiration.Cancelled, Is.False);
            Assert.That(entities.GetComponent<BreathingImmunityComponent>(body), Is.SameAs(innate));
            stamina.TakeStaminaDamage(body, 4, staminaComponent, visual: false);
            Assert.That(staminaComponent.StaminaDamage, Is.EqualTo(10));

            entities.AddComponent<GeneticIncompatibleComponent>(body);
            Assert.That(genetics.TryGetGenome(body, out _), Is.False);
            Assert.That(genetics.TrySetBlock((body, genome!), breathing, GeneticsSystem.MaxBlockValue, body), Is.False);

            entities.DeleteEntity(map);
            DeleteCipher(entities, round.Context);
        });
        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MimicCloneKeepsOriginalBiologyAndRestorableAppearance(bool removeEffects)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var genetics = entities.System<GeneticsSystem>();

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>().CreateMap();
            var body = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            var model = entities.SpawnEntity("MobReptilian", new EntityCoordinates(map, new Vector2(0.5f, 0)));
            var metadata = entities.System<MetaDataSystem>();
            metadata.SetEntityName(body, "Original genetics test subject");
            metadata.SetEntityName(model, "Mimicry test model");
            var appearance = entities.GetComponent<HumanoidAppearanceComponent>(body);
            var species = appearance.Species;
            var voice = appearance.Voice;
            Assert.That(genetics.TryGetGenome(body, out var genome), Is.True);
            var round = genetics.GetRound();
            var block = round.Mutations.IndexOf("GeneticMimic");
            Assert.That(genetics.TrySetBlock((body, genome!), block, GeneticsSystem.MaxBlockValue, body), Is.True);
            var mimic = new GeneticMimicEvent { Performer = body, Target = model };
            entities.EventBus.RaiseLocalEvent(body, mimic);
            Assert.That(mimic.Handled, Is.True);
            Assert.That(appearance.Species, Is.Not.EqualTo(species));

            var clone = entities.System<CloningSystem>().SpawnClone(new EntityCoordinates(map, Vector2.One), null, sourceBody: body);
            Assert.That(entities.GetComponent<MetaDataComponent>(clone).EntityPrototype!.ID, Is.EqualTo("MobHuman"));
            Assert.That(genetics.TryGetGenome(clone, out var cloneGenome), Is.True);
            Assert.That(genetics.TrySetBlock((clone, cloneGenome!), block, 0, clone), Is.True);
            var cloneAppearance = entities.GetComponent<HumanoidAppearanceComponent>(clone);
            Assert.That(cloneAppearance.Species, Is.EqualTo(species));
            Assert.That(cloneAppearance.Voice, Is.EqualTo(voice));
            Assert.That(entities.GetComponent<MetaDataComponent>(clone).EntityName, Is.EqualTo("Original genetics test subject"));
            Assert.That(appearance.Species, Is.Not.EqualTo(species), "Restoring the clone must not alter the source.");

            if (removeEffects)
                entities.RemoveComponent<GeneticEffectsComponent>(body);
            else
                Assert.That(genetics.TrySetBlock((body, genome!), block, 0, body), Is.True);
            Assert.That(appearance.Species, Is.EqualTo(species));
            Assert.That(appearance.Voice, Is.EqualTo(voice));
            entities.DeleteEntity(map);
            DeleteCipher(entities, round.Context);
        });
        await server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    private static void DeleteCipher(IEntityManager entities, string context)
    {
        var query = entities.AllEntityQueryEnumerator<GeneticsRoundComponent>();
        while (query.MoveNext(out var uid, out var round))
        {
            if (round.Context == context)
                entities.QueueDeleteEntity(uid);
        }
    }
}
