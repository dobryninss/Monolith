"""Blueprint and atmosphere checks against the fork's actual map serializers."""

import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from catalog import PrototypeCatalog
from map_document import MappingDocument
from validation import atmosphere_tiles, mixture_summary, snapshot_atmosphere, validate_document, validate_simulation


AIR = {"temperature": 293.15, "volume": 2500,
       "moles": {"Oxygen": 21.824879, "Nitrogen": 82.10312}}


class FixtureCatalog:
    def has(self, identifier, kind="entity"):
        known = {"tile": {"Space", "FloorSteel"}, "entity": {"WallSolid"}}
        return identifier in known.get(kind, set())


class AtmosphereSerializationTests(unittest.TestCase):
    def test_v2_negative_chunk_coordinates_and_row_major_masks(self):
        air, vacuum = copy.deepcopy(AIR), {"moles": {}, "temperature": 0}
        component = {"version": 2, "data": {
            "chunkSize": 4, "uniqueMixes": [air, vacuum], "tiles": {
                "-1,-2": {"0": (1 << 0) | (1 << 3) | (1 << 6) | (1 << 15)},
                "0,-1": {"1": 1 << 8}}}}
        tiles = atmosphere_tiles(component)
        self.assertEqual(set(tiles), {(-4, -8), (-1, -8), (-2, -7), (-1, -5), (0, -2)})
        self.assertEqual(tiles[-2, -7], AIR)
        self.assertEqual(tiles[0, -2], vacuum)

    def test_v1_legacy_coordinates_and_mixture_index(self):
        component = {"version": 1, "uniqueMixes": [copy.deepcopy(AIR), {"moles": []}],
                     "tiles": {"-17,2": 0, "-1,-1": 1}}
        self.assertEqual(atmosphere_tiles(component), {(-17, 2): AIR, (-1, -1): {"moles": []}})

    def test_v2_overlap_is_rejected(self):
        component = {"version": 2, "data": {"chunkSize": 4, "uniqueMixes": [AIR, AIR],
                                              "tiles": {"0,0": {0: 3, 1: 2}}}}
        with self.assertRaises(ValueError):
            atmosphere_tiles(component)

    def test_invalid_masks_indices_and_fractional_coordinates_are_rejected(self):
        for coordinates, index, mask in (("0,0", -1, 1), ("0,0", 1, 1),
                                          ("0,0", 0, -1), ("0,0", 0, 1 << 32),
                                          ("0,0", 0.5, 1), ("0,0", 0, 1.5),
                                          ("0.5,0", 0, 1)):
            with self.subTest(coordinates=coordinates, index=index, mask=mask):
                component = {"version": 2, "data": {"chunkSize": 4, "uniqueMixes": [AIR],
                                                      "tiles": {coordinates: {index: mask}}}}
                with self.assertRaises(ValueError):
                    atmosphere_tiles(component)
        for coordinates, index in (("0,0", -1), ("0,0", 1), ("1.5,0", 0)):
            with self.subTest(legacy=(coordinates, index)):
                with self.assertRaises(ValueError):
                    atmosphere_tiles({"version": 1, "uniqueMixes": [AIR], "tiles": {coordinates: index}})

    def test_engine_null_empty_payload_is_valid(self):
        for component in ({"version": 1, "uniqueMixes": None, "tiles": None},
                          {"version": 2, "data": {"chunkSize": 4, "uniqueMixes": None, "tiles": None}},
                          {"version": 2, "data": {"chunkSize": 4}}):
            with self.subTest(component=component):
                self.assertEqual(atmosphere_tiles(component), {})

    def test_pressure_and_oxygen_are_partial_pressures(self):
        summary = mixture_summary(AIR)
        self.assertAlmostEqual(summary["pressure_kpa"], 101.325, delta=0.02)
        self.assertAlmostEqual(summary["oxygen_kpa"], 21.278, delta=0.02)
        self.assertEqual(summary["carbon_dioxide_kpa"], 0)

    def test_legacy_list_includes_every_fork_gas(self):
        # Actual Gas enum: Oxygen..Frezon, BZ, Healium, Nitrium, Pluoxium.
        quantities = [10, 30, 0, 0, 0, 0, 0, 0, 0, 5, 7, 11, 13, 0, 0, 0]
        summary = mixture_summary({"temperature": 300, "volume": 1000, "moles": quantities})
        self.assertEqual(summary["moles"]["BZ"], 5)
        self.assertEqual(summary["moles"]["Healium"], 7)
        self.assertEqual(summary["moles"]["Nitrium"], 11)
        self.assertEqual(summary["moles"]["Pluoxium"], 13)
        self.assertAlmostEqual(summary["pressure_kpa"], sum(quantities) * 8.314462618 * 300 / 1000)

    def test_zero_temperature_only_allowed_for_vacuum(self):
        for moles in ({}, {"Oxygen": 0}, [0] * 16):
            summary = mixture_summary({"temperature": 0, "moles": moles})
            self.assertEqual(summary["pressure_kpa"], 0)
            self.assertEqual(summary["oxygen_kpa"], 0)
        with self.assertRaises(ValueError):
            mixture_summary({"temperature": 0, "moles": {"Oxygen": 1}})
        with self.assertRaises(ValueError):
            mixture_summary({"temperature": -1, "moles": {}})

    def test_nonfinite_negative_amounts_and_invalid_volume_are_rejected(self):
        for mixture in (
            {**AIR, "volume": 0}, {**AIR, "volume": float("inf")},
            {**AIR, "temperature": float("nan")},
            {**AIR, "moles": {"Oxygen": -1}},
            {**AIR, "moles": {"Oxygen": float("inf")}},
        ):
            with self.subTest(mixture=mixture):
                with self.assertRaises(ValueError):
                    mixture_summary(mixture)


class BlueprintValidationTests(unittest.TestCase):
    def setUp(self):
        self.catalog = FixtureCatalog()
        self.document = MappingDocument.create(grid_components=[{
            "type": "GridAtmosphere", "version": 2,
            "data": {"chunkSize": 4, "uniqueMixes": [copy.deepcopy(AIR)], "tiles": {"0,0": {0: 51}}}}])
        self.document.apply_batch([{"op": "tiles", "bounds": [0, 0, 1, 1], "tile": "FloorSteel"}])

    def validate(self, requirements=None):
        return validate_document(self.document, self.catalog, requirements)

    def test_requested_size_prototypes_and_missing_air_are_errors(self):
        result = self.validate({"max_size": [1, 1], "required_prototypes": {"WallSolid": 1},
                                "breathable_tiles": [[2, 0]]})
        self.assertFalse(result["passed"])
        self.assertEqual({error["code"] for error in result["errors"]},
                         {"maximum_grid_size", "required_prototype", "missing_initial_air"})

    def test_ordinary_air_passes_but_vacuum_and_plasma_do_not(self):
        self.assertTrue(self.validate({"breathable_tiles": [[0, 0], [1, 1]]})["passed"])
        for mixture in ({"moles": {}, "temperature": 0},
                        {**AIR, "moles": {"Oxygen": 21.824879, "Plasma": 82.10312}}):
            self.document.apply_batch([{"op": "patch", "entity": 1, "components": [{
                "type": "GridAtmosphere", "data": {"chunkSize": 4, "uniqueMixes": [mixture],
                                                     "tiles": {"0,0": {0: 1}}}}]}])
            result = self.validate({"breathable_tiles": [[0, 0]]})
            self.assertFalse(result["passed"])
            self.assertIn("initial_air_outside_requested_range", {error["code"] for error in result["errors"]})

    def test_empty_grid_does_not_skip_requested_air_checks(self):
        document = MappingDocument.create()
        result = validate_document(document, self.catalog, {"breathable_tiles": [[0, 0]]})
        self.assertFalse(result["passed"])
        self.assertIn("missing_initial_air", {error["code"] for error in result["errors"]})

    def test_simulation_requires_requested_grid_and_air(self):
        requirements = {"breathable_tiles": [[0, 0]]}
        self.assertTrue(validate_simulation(self.document.data, requirements)["passed"])
        missing = validate_simulation({"entities": []}, requirements)
        self.assertFalse(missing["passed"])
        self.assertEqual(missing["errors"], [{"code": "missing_simulated_grid"}])
        self.document.apply_batch([{"op": "patch", "entity": 1,
                                   "components": [{"type": "GridAtmosphere", "data": {}}]}])
        self.assertFalse(validate_simulation(self.document.data, requirements)["passed"])

    def test_malformed_requirements_fail_explicitly(self):
        for requirements in (["bad"], {"max_size": [6]}, {"max_size": [6, 6, 6]},
                             {"max_size": [6.5, 6]}, {"required_prototypes": {"WallSolid": -1}},
                             {"required_prototypes": []}, {"breathable_tiles": [[0.5, 1]]},
                             {"breathable_tiles": [[0]]}):
            with self.subTest(requirements=requirements):
                with self.assertRaises(ValueError):
                    self.validate(requirements)

    def test_duplicate_and_unsupported_anchoring_are_warnings(self):
        self.document.apply_batch([
            {"op": "spawn", "prototype": "WallSolid", "x": 5.5, "y": -1.5, "anchored": True},
            {"op": "spawn", "prototype": "WallSolid", "x": 5.5, "y": -1.5, "anchored": True}])
        result = self.validate()
        self.assertTrue(result["passed"])
        self.assertEqual({warning["code"] for warning in result["warnings"]},
                         {"duplicate_placement", "anchored_without_tile"})

    def test_unknown_entities_and_tiles_are_errors(self):
        self.document.apply_batch([
            {"op": "spawn", "prototype": "MissingPrototype", "x": 0, "y": 0},
            {"op": "tiles", "bounds": [0, 0, 0, 0], "tile": "MissingTile"}])
        result = self.validate()
        self.assertFalse(result["passed"])
        self.assertEqual({error["code"] for error in result["errors"]}, {"unknown_prototype", "unknown_tile"})

    def test_runtime_snapshot_reports_all_air_tiles_including_vacuum(self):
        self.document.apply_batch([{"op": "patch", "entity": 1, "components": [{
            "type": "GridAtmosphere", "data": {"chunkSize": 4,
                "uniqueMixes": [AIR, {"moles": {}, "temperature": 0}], "tiles": {"0,0": {0: 1, 1: 2}}}}]}])
        data = copy.deepcopy(self.document.data)
        data["entities"][0]["entities"][0]["mapInit"] = True
        snapshots = snapshot_atmosphere(data)
        self.assertEqual(len(snapshots), 1)
        tiles = {tuple(tile["cell"]): tile for tile in snapshots[0]["tiles"]}
        self.assertGreater(tiles[0, 0]["pressure_kpa"], 100)
        self.assertEqual(tiles[1, 0]["pressure_kpa"], 0)


class CapsuleBlueprintTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(__file__).resolve().parents[4]
        cls.catalog = PrototypeCatalog(cls.root / "Resources")
        recipe = json.loads((cls.root / "Tools/_Exodus/Mapping/examples/rescue_capsule.json").read_text(encoding="utf-8"))
        cls.requirements = recipe["requirements"]
        cls.document = MappingDocument.create(category=recipe["category"], name=recipe["name"],
                                               grid_components=recipe["grid_components"])
        cls.document.apply_batch(recipe["operations"])

    def test_capsule_recipe_resolves_real_prototypes_and_breathable_tiles(self):
        result = validate_document(self.document, self.catalog, self.requirements)
        self.assertTrue(result["passed"], result["errors"])
        self.assertEqual(result["warnings"], [])
        self.assertEqual(len(result["grids"]), 1)
        self.assertEqual(result["grids"][0]["size"], [6, 6])
        self.assertEqual(result["grids"][0]["tiles"], 36)
        self.assertEqual(len(result["grids"][0]["air_checks"]), len(self.requirements["breathable_tiles"]))
        self.assertEqual(result["prototype_counts"]["SmallThruster"], 4)
        self.assertEqual(result["prototype_counts"]["ComputerShuttle"], 1)

    def test_exported_capsule_matches_reproducible_recipe(self):
        exported = MappingDocument.from_file(self.root / "Resources/Maps/_Exodus/Shuttles/rescue_capsule.yml")
        self.assertEqual(exported.data, self.document.data)

    def test_capsule_loses_validation_when_oxygen_is_removed(self):
        document = MappingDocument(copy.deepcopy(self.document.data))
        document.apply_batch([{"op": "patch", "entity": 1, "components": [{
            "type": "GridAtmosphere", "data": {"chunkSize": 4, "uniqueMixes": [], "tiles": {}}}]}])
        result = validate_document(document, self.catalog, self.requirements)
        self.assertFalse(result["passed"])
        self.assertEqual(len(result["errors"]), len(self.requirements["breathable_tiles"]))
        self.assertTrue(all(error["code"] == "missing_initial_air" for error in result["errors"]))


if __name__ == "__main__":
    unittest.main()
