"""Contract tests for the editor, including Robust's actual binary tile layout."""

import base64
from contextlib import contextmanager
import copy
from pathlib import Path
import struct
import sys
import shutil
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from map_document import (MappingDocument, MappingError, decode_chunk, dump_yaml,
                          encode_chunk, get_component, load_yaml)


@contextmanager
def temporary_directory():
    # Python 3.14's Windows mode-0700 temp directories exclude sandbox identities.
    root = Path(__file__).resolve().parents[1]
    directory = root / f"test-temp-{uuid.uuid4().hex}"
    directory.mkdir()
    try:
        yield directory
    finally:
        if directory.resolve().parent != root:
            raise RuntimeError("Temporary directory escaped the test workspace")
        shutil.rmtree(directory)


class TileCodecTests(unittest.TestCase):
    def test_known_engine_v6_and_v7_bytes(self):
        # Independent byte fixture: BinaryWriter writes little-endian int + bytes.
        chunk = {"size": 2, "version": 7, "tiles": base64.b64encode(
            b"\x01\x01\x00\x00\x02\x03\x04" + b"\x00" * 21).decode()}
        tiles = decode_chunk(chunk)
        self.assertEqual(tiles[0], (257, 2, 3, 4))
        self.assertEqual(encode_chunk(tiles, size=2), chunk["tiles"])
        legacy = {"size": 2, "version": 6,
                  "tiles": base64.b64encode(struct.pack("<iBB", 72000, 9, 11) * 4).decode()}
        self.assertEqual(decode_chunk(legacy), [(72000, 9, 11, 0)] * 4)
        self.assertEqual(encode_chunk(decode_chunk(legacy), size=2, version=6), legacy["tiles"])

    def test_rejects_corrupt_and_future_chunks(self):
        for chunk in ({"tiles": "!"}, {"tiles": "AAAA", "version": 7},
                      {"tiles": "", "version": 8}):
            with self.assertRaises(MappingError):
                decode_chunk(chunk)
        with self.assertRaises(MappingError):
            encode_chunk([(1, 0, 0, 1)], size=1, version=6)


class DocumentTests(unittest.TestCase):
    def setUp(self):
        self.document = MappingDocument.create(name="Test grid")

    def spawn(self, **overrides):
        operation = {"op": "spawn", "prototype": "WallSolid", "x": 0.5, "y": 0.5}
        operation.update(overrides)
        return self.document.apply_batch([operation])["results"][0]["uid"]

    def test_negative_chunk_edges_and_nonzero_space_id(self):
        self.document.data["tilemap"] = {19: "Space"}
        self.document.apply_batch([
            {"op": "tiles", "bounds": [-17, -1, 16, 0], "tile": "FloorSteel",
             "flags": 2, "variant": 3, "rotationMirroring": 6}])
        tiles = self.document.tiles()
        self.assertEqual(len(tiles), 68)
        self.assertEqual({(tile["x"], tile["y"]) for tile in tiles},
                         {(x, y) for x in range(-17, 17) for y in range(-1, 1)})
        self.assertTrue(all(tile["rotationMirroring"] == 6 for tile in tiles))
        self.document.apply_batch([{"op": "tiles", "bounds": [-17, -1, 16, 0], "tile": "Space"}])
        self.assertEqual(self.document.tiles(), [])
        self.assertEqual(get_component(self.document.get_entity(1), "MapGrid")["chunks"], {})

    def test_partial_legacy_chunk_edit_preserves_other_tiles(self):
        self.document.data["tilemap"][5] = "FloorSteel"
        legacy_tiles = [(5, index % 256, (index + 1) % 256, 0) for index in range(256)]
        grid = get_component(self.document.get_entity(1), "MapGrid")
        grid["chunks"]["0,0"] = {"ind": "0,0", "version": 6,
                                    "futureField": {"preserve": True},
                                    "tiles": encode_chunk(legacy_tiles, version=6)}
        self.document.apply_batch([{"op": "tiles", "bounds": [2, 3, 2, 3], "tile": "Space"}])
        updated = get_component(self.document.get_entity(1), "MapGrid")["chunks"]["0,0"]
        decoded = decode_chunk(updated)
        expected = legacy_tiles.copy()
        expected[3 * 16 + 2] = (0, 0, 0, 0)
        self.assertEqual(decoded, expected)
        self.assertEqual(updated["futureField"], {"preserve": True})

    def test_failed_batch_is_atomic_and_does_not_consume_ids(self):
        before = self.document.revision
        with self.assertRaises(MappingError):
            self.document.apply_batch([
                {"op": "spawn", "prototype": "WallSolid", "x": 0.5, "y": 0.5, "key": "wall"},
                {"op": "tiles", "bounds": [2, 0, 1, 0], "tile": "FloorSteel"}])
        self.assertEqual(self.document.revision, before)
        self.assertEqual(self.document.state["handles"], {})
        self.assertEqual(self.spawn(), 2)

    def test_revision_conflict_and_persisted_idempotency(self):
        operations = [{"op": "spawn", "prototype": "WallSolid", "x": 0.5, "y": 0.5, "key": "wall"}]
        before = self.document.revision
        first = self.document.apply_batch(operations, before, "request-1")
        loaded = MappingDocument(load_yaml(dump_yaml(self.document.data)))
        loaded.restore_state(self.document.state)
        replayed = loaded.apply_batch(operations, before, "request-1")
        self.assertTrue(replayed["replayed"])
        self.assertEqual(first["results"], replayed["results"])
        self.assertEqual(len(list(loaded.iter_entities())), 2)
        with self.assertRaises(MappingError):
            loaded.apply_batch([], operation_id="request-1")
        with self.assertRaises(MappingError):
            loaded.apply_batch([], expected_revision=before)

    def test_deleted_ids_are_not_reused_after_session_reload(self):
        uid = self.spawn(key="old")
        self.document.apply_batch([{"op": "delete", "entity": "old"}])
        loaded = MappingDocument(copy.deepcopy(self.document.data))
        loaded.restore_state(self.document.state)
        result = loaded.apply_batch([{"op": "spawn", "prototype": "WallSolid", "x": 0, "y": 0}])
        self.assertGreater(result["results"][0]["uid"], uid)

    def test_rejects_any_post_init_entity_on_load_and_export(self):
        uid = self.spawn()
        self.document.get_entity(uid)["mapInit"] = True
        with self.assertRaises(MappingError):
            MappingDocument(self.document.data)
        with temporary_directory() as directory:
            target = Path(directory) / "bad.yml"
            with self.assertRaises(MappingError):
                self.document.save(target)
            self.assertFalse(target.exists())

    def test_tags_unknown_fields_and_compact_sequences_survive_export(self):
        uid = self.spawn(components=[{"type": "Example", "sound": {
            "$tag": "!type:SoundPathSpecifier", "$value": {"path": "/Audio/example.ogg"}}}])
        self.document.data["futureTopLevel"] = load_yaml("sequence: !example [one, two]\nscalar: !scalar on\n")
        self.document.get_entity(uid)["futureEntityField"] = [1, 2, 3]
        with temporary_directory() as directory:
            target = Path(directory) / "map.yml"
            before = copy.deepcopy(self.document.data)
            self.document.save(target)
            text = target.read_text(encoding="utf-8")
            loaded = MappingDocument.from_file(target)
            self.assertEqual(before, loaded.data)
            self.assertEqual(self.document.revision, loaded.revision)
            self.assertIn("!type:SoundPathSpecifier", text)
            self.assertIn("!example", text)
            self.assertIn("\ngrids:\n- 1\n", text)
            self.assertNotIn("requestHash", text)
            self.assertNotIn("handles:", text)

    def test_handles_patch_references_and_field_replacement(self):
        first = self.spawn(key="first", components=[{"type": "Example", "values": {"a": 1, "b": 2}, "list": [1, 2]}])
        second = self.spawn(key="second")
        self.document.apply_batch([{"op": "patch", "entity": "first", "components": [
            {"type": "Example", "values": {"b": 3}, "list": [9], "target": {"$entity": "second"}}]}])
        example = get_component(self.document.get_entity(first), "Example")
        self.assertEqual(example["values"], {"b": 3})
        self.assertEqual(example["list"], [9])
        self.assertEqual(example["target"], second)
        self.document.apply_batch([{"op": "patch", "entity": first,
                                    "components": [{"type": "Example", "values": {}}]}])
        self.assertEqual(get_component(self.document.get_entity(first), "Example")["values"], {})

    def test_hierarchy_cycles_missing_parents_and_children_block_commit(self):
        first, second = self.spawn(), self.spawn()
        before = self.document.revision
        for patches in ([{"type": "Transform", "parent": 999}],
                        [{"type": "Transform", "parent": first}]):
            with self.assertRaises(MappingError):
                self.document.apply_batch([{"op": "patch", "entity": first, "components": patches}])
        self.assertEqual(before, self.document.revision)
        self.document.apply_batch([{"op": "patch", "entity": second,
                                    "components": [{"type": "Transform", "parent": first}]}])
        with self.assertRaises(MappingError):
            self.document.apply_batch([{"op": "delete", "entity": first}])

    def test_map_creation_and_inspection_nested_transform(self):
        document = MappingDocument.create(category="Map", name="Example")
        self.assertEqual(document.data["maps"], [1])
        self.assertEqual(document.grid_ids, [2])
        self.assertEqual(document.data["orphans"], [])
        receipt = document.apply_batch([
            {"op": "spawn", "prototype": "Parent", "x": 2, "y": 2, "rotation": 90, "key": "parent"},
            {"op": "spawn", "prototype": "Child", "x": 1, "y": 0,
             "components": [{"type": "Transform", "parent": {"$entity": "parent"}}]}])
        child_uid = receipt["results"][1]["uid"]
        child = next(entity for entity in document.inspect(bounds=[2, 3, 2, 3])["entities"] if entity["uid"] == child_uid)
        self.assertAlmostEqual(child["x"], 2)
        self.assertAlmostEqual(child["y"], 3)
        self.assertEqual(child["rotation"], 90)

    def test_nonfinite_positions_and_invalid_byte_rejected(self):
        for operation in (
            {"op": "spawn", "prototype": "WallSolid", "x": float("nan"), "y": 0},
            {"op": "spawn", "prototype": "WallSolid", "x": 0, "y": 0, "anchored": "false"},
            {"op": "tiles", "bounds": [0, 0, 0, 0], "tile": "FloorSteel", "flags": 256},
            {"op": "tiles", "bounds": [0.5, 0, 0, 0], "tile": "FloorSteel"},
        ):
            with self.assertRaises(MappingError):
                self.document.apply_batch([operation])

    def test_map_component_init_flag_and_nonfinite_patch_are_rejected(self):
        document = MappingDocument.create(category="Map")
        with self.assertRaises(MappingError):
            document.apply_batch([{"op": "patch", "entity": 1,
                                   "components": [{"type": "Map", "mapInitialized": True}]}])
        with self.assertRaises(MappingError):
            document.apply_batch([{"op": "patch", "entity": 2, "components": [
                {"type": "Example", "nested": [{"amount": float("inf")}]}]}])

    def test_spawning_and_moving_preserve_coordinate_precision(self):
        coordinate = 10000.123456789
        uid = self.spawn(x=coordinate)
        self.assertEqual(self.document.inspect()["entities"][0]["x"], coordinate)
        self.document.apply_batch([{"op": "move", "entity": uid, "y": -coordinate}])
        self.assertEqual(self.document.inspect()["entities"][0]["y"], -coordinate)

    def test_duplicate_uid_rejected(self):
        data = copy.deepcopy(self.document.data)
        data["entities"][0]["entities"].append(copy.deepcopy(data["entities"][0]["entities"][0]))
        with self.assertRaises(MappingError):
            MappingDocument(data)

    def test_existing_fork_map_semantic_roundtrip(self):
        root = Path(__file__).resolve().parents[4]
        path = root / "Resources/Maps/_Exodus/Supercapitals/chengdu.yml"
        document = MappingDocument.from_file(path)
        with temporary_directory() as directory:
            target = Path(directory) / "roundtrip.yml"
            document.save(target)
            loaded = MappingDocument.from_file(target)
            self.assertEqual(document.data, loaded.data)
            self.assertEqual(document.tiles(), loaded.tiles())


if __name__ == "__main__":
    unittest.main()
