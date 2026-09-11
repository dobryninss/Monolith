"""Mapping CLI workflow tests with a small authored prototype catalog."""

from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import shutil
import sys
import unittest
from unittest.mock import patch
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import mapping
from map_document import MappingDocument


class CliTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(__file__).resolve().parents[1]
        self.directory = self.root / f"test-cli-{uuid.uuid4().hex}"
        self.resources = self.directory / "Resources"
        (self.resources / "Prototypes").mkdir(parents=True)
        (self.resources / "Prototypes/test.yml").write_text(
            "- type: entity\n  id: WallSolid\n  name: wall\n"
            "- type: entity\n  id: AbstractWall\n  abstract: true\n"
            "- type: tile\n  id: Space\n"
            "- type: tile\n  id: FloorSteel\n", encoding="utf-8")
        self.session = self.directory / "session.json"
        self.output = self.directory / "map.yml"

    def tearDown(self):
        if self.directory.resolve().parent != self.root:
            raise RuntimeError("Temporary test directory escaped workspace")
        shutil.rmtree(self.directory)

    def arguments(self, command, *arguments):
        return ["--resources", str(self.resources), command,
                "--session", str(self.session), *map(str, arguments)]

    def execute(self, command, *arguments):
        return mapping.execute(mapping.parser().parse_args(self.arguments(command, *arguments)))

    def create(self):
        return self.execute("create", "--name", "Test grid")

    def batch(self, operations, *, revision=None, identifier="request-1"):
        path = self.directory / "batch.json"
        if revision is None:
            revision = mapping.session_read(self.session)[1].revision
        path.write_text(json.dumps({"operations": operations, "expected_revision": revision,
                                    "operation_id": identifier}), encoding="utf-8")
        return path

    def spawn(self, *, identifier="spawn"):
        return self.execute("apply", "--batch", self.batch([
            {"op": "spawn", "prototype": "WallSolid", "x": 0.5, "y": 0.5, "key": identifier}
        ], identifier=identifier))

    def main_result(self, command, *arguments):
        stream = io.StringIO()
        with patch.object(sys, "argv", ["mapping.py", *self.arguments(command, *arguments)]):
            with redirect_stdout(stream):
                status = mapping.main()
        return status, json.loads(stream.getvalue())

    def test_create_session_never_overwrites_existing_file(self):
        self.create()
        original = self.session.read_bytes()
        with self.assertRaises(ValueError):
            self.create()
        self.assertEqual(original, self.session.read_bytes())
        self.assertFalse(self.session.with_suffix(".json.lock").exists())

    def test_failed_batch_preserves_session_and_releases_lock(self):
        self.create()
        original = self.session.read_bytes()
        batch = self.batch([
            {"op": "spawn", "prototype": "WallSolid", "x": 0.5, "y": 0.5},
            {"op": "move", "entity": 999, "x": 1, "y": 1}])
        status, result = self.main_result("apply", "--batch", batch)
        self.assertEqual(status, 1)
        self.assertIn("Unknown entity UID", result["error"])
        self.assertEqual(original, self.session.read_bytes())
        self.assertFalse(self.session.with_suffix(".json.lock").exists())
        self.spawn()

    def test_idempotency_and_stale_revision_survive_session_reload(self):
        before = self.create()["revision"]
        batch = self.batch([{"op": "spawn", "prototype": "WallSolid", "x": 0, "y": 0}], revision=before)
        first = self.execute("apply", "--batch", batch)
        second = self.execute("apply", "--batch", batch)
        self.assertTrue(second["replayed"])
        self.assertEqual(first["revision"], second["revision"])
        self.assertEqual(len(mapping.session_read(self.session)[1].inspect()["entities"]), 1)
        old_request = self.batch([], revision=before, identifier="different")
        original = self.session.read_bytes()
        with self.assertRaises(ValueError):
            self.execute("apply", "--batch", old_request)
        self.assertEqual(original, self.session.read_bytes())

    def test_checkpoint_restores_document_handles_and_requires_current_revision(self):
        start = self.create()["revision"]
        self.execute("checkpoint", "--name", "empty")
        spawned = self.spawn()
        with self.assertRaises(ValueError):
            self.execute("restore", "--name", "empty", "--expected-revision", start)
        result = self.execute("restore", "--name", "empty", "--expected-revision", spawned["revision"])
        self.assertEqual(result["revision"], start)
        document = mapping.session_read(self.session)[1]
        self.assertEqual(document.state["handles"], {})
        self.assertEqual(document.inspect()["entities"], [])
        with self.assertRaises(ValueError):
            self.execute("checkpoint", "--name", "empty")

    def test_external_edits_and_unowned_outputs_are_never_overwritten(self):
        self.create()
        self.output.write_text("unrelated file", encoding="utf-8")
        with self.assertRaises(ValueError):
            self.execute("export", "--output", self.output)
        self.assertEqual(self.output.read_text(), "unrelated file")
        self.output.unlink()
        self.execute("export", "--output", self.output)
        self.output.write_text(self.output.read_text() + "# mapper changed this\n", encoding="utf-8")
        external = self.output.read_bytes()
        self.spawn()
        with self.assertRaises(ValueError):
            self.execute("export", "--output", self.output)
        self.assertEqual(external, self.output.read_bytes())

    def test_owned_export_can_update_and_contains_no_session_state(self):
        self.create()
        self.execute("checkpoint", "--name", "empty")
        self.execute("export", "--output", self.output)
        self.spawn()
        exported = self.execute("export", "--output", self.output)
        document = MappingDocument.from_file(self.output)
        self.assertEqual(len(document.inspect()["entities"]), 1)
        self.assertEqual(document.revision, exported["revision"])
        for field in ("checkpoints", "operations", "handles", "pendingExports", "exports"):
            self.assertNotIn(field, document.data)

    def test_opened_source_has_write_conflict_detection(self):
        MappingDocument.create().save(self.output)
        self.execute("open", self.output)
        self.spawn()
        self.execute("export", "--output", self.output)
        self.output.write_text(self.output.read_text() + "# external\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            self.execute("export", "--output", self.output)

    def test_session_write_failure_keeps_previous_batch_atomically(self):
        self.create()
        before = self.session.read_bytes()
        batch = self.batch([{"op": "spawn", "prototype": "WallSolid", "x": 0, "y": 0}])
        with patch.object(mapping, "atomic_text", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                self.execute("apply", "--batch", batch)
        self.assertEqual(before, self.session.read_bytes())
        self.assertFalse(self.session.with_suffix(".json.lock").exists())

    def test_export_receipt_failure_is_recoverable_without_overwriting_external_changes(self):
        self.create()
        self.spawn()
        original_writer = mapping.session_write
        calls = 0

        def failing_writer(*arguments):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError("receipt write failed")
            return original_writer(*arguments)

        with patch.object(mapping, "session_write", side_effect=failing_writer):
            with self.assertRaises(OSError):
                self.execute("export", "--output", self.output)
        exported_before_retry = self.output.read_bytes()
        state = mapping.session_read(self.session)[0]
        self.assertIn(str(self.output.resolve()), state["pendingExports"])
        self.output.write_bytes(exported_before_retry + b"# edited after interrupted export\n")
        with self.assertRaises(ValueError):
            self.execute("export", "--output", self.output)
        self.output.write_bytes(exported_before_retry)
        self.execute("export", "--output", self.output)
        self.assertEqual(exported_before_retry, self.output.read_bytes())
        self.assertEqual(mapping.session_read(self.session)[0]["pendingExports"], {})

    def test_export_intent_failure_does_not_touch_output(self):
        self.create()
        with patch.object(mapping, "session_write", side_effect=OSError("intent failed")):
            with self.assertRaises(OSError):
                self.execute("export", "--output", self.output)
        self.assertFalse(self.output.exists())

    def test_recipe_cannot_use_session_as_map_output(self):
        recipe = self.directory / "recipe.json"
        recipe.write_text(json.dumps({"operations": []}), encoding="utf-8")
        with self.assertRaises(ValueError):
            self.execute("recipe", recipe, "--output", self.session)
        self.assertFalse(self.session.exists())

    def test_malformed_batch_and_recipe_return_structured_json_errors(self):
        self.create()
        batch = self.directory / "invalid.json"
        for value in (None, 7, {"operations": ["bad"]}, {"operations": None}, {"operations": [{"op": "spawn"}]}):
            with self.subTest(value=value):
                batch.write_text(json.dumps(value), encoding="utf-8")
                status, result = self.main_result("apply", "--batch", batch,
                                                  "--expected-revision", "bad", "--operation-id", "bad")
                self.assertEqual(status, 1)
                self.assertIn("error", result)
        self.session.unlink()
        batch.write_text("[]", encoding="utf-8")
        status, result = self.main_result("recipe", batch)
        self.assertEqual(status, 1)
        self.assertIn("error", result)

    def test_invalid_yaml_json_and_abstract_prototypes_return_json_errors(self):
        self.output.write_text("meta: [", encoding="utf-8")
        status, result = self.main_result("open", self.output)
        self.assertEqual(status, 1)
        self.assertIn("error", result)
        self.create()
        batch = self.directory / "invalid.json"
        batch.write_text("{", encoding="utf-8")
        status, result = self.main_result("apply", "--batch", batch)
        self.assertEqual(status, 1)
        self.assertIn("error", result)
        batch = self.batch([{"op": "spawn", "prototype": "AbstractWall", "x": 0, "y": 0}])
        status, result = self.main_result("apply", "--batch", batch)
        self.assertEqual(status, 1)
        self.assertIn("abstract", result["error"])

    def test_live_lock_rejects_request_without_removing_another_lock(self):
        self.create()
        lock = self.session.with_suffix(".json.lock")
        lock.write_text("other process", encoding="utf-8")
        with self.assertRaises(ValueError):
            self.execute("inspect")
        self.assertEqual(lock.read_text(), "other process")


if __name__ == "__main__":
    unittest.main()
