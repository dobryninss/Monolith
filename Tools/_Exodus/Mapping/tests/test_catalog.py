from contextlib import ExitStack
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from catalog import PrototypeCatalog
from map_document import load_yaml
from test_map_document import temporary_directory


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = ExitStack()
        self.addCleanup(self.temp.close)
        self.repo = self.temp.enter_context(temporary_directory())
        self.resources = self.repo / "Resources"
        (self.resources / "Prototypes").mkdir(parents=True)

    def write(self, text, name="test.yml"):
        (self.resources / "Prototypes" / name).write_text(text, encoding="utf-8")

    def test_first_parent_wins_and_component_fields_replace_whole_values(self):
        self.write("""
- type: entity
  id: First
  abstract: true
  name: first name
  components:
  - type: Example
    value: first
    inherited: yes
    numbers: [1, 2]
    options: {a: 1, b: 2}
  - type: OnlyFirst
- type: entity
  id: Second
  components:
  - type: Example
    value: second
    extra: true
  - type: OnlySecond
- type: entity
  id: Child
  parent: [First, Second]
  components:
  - type: Example
    numbers: [3]
    options: {b: 9}
""")
        catalog = PrototypeCatalog(self.resources)
        description = catalog.describe("Child")
        self.assertFalse(description["abstract"])
        self.assertEqual(description["name"], "first name")
        components = {item["type"]: item for item in catalog.components("Child")}
        self.assertEqual(components["Example"], {
            "type": "Example", "numbers": [3], "options": {"b": 9},
            "value": "first", "inherited": "yes", "extra": True,
        })
        self.assertIn("OnlyFirst", components)
        self.assertIn("OnlySecond", components)
        components["Example"]["numbers"].append(4)
        self.assertEqual(catalog.components("Child")[0]["numbers"], [3])

    def test_always_push_attributes_discovered_from_source(self):
        source = self.repo / "Content.Shared" / "ExampleComponent.cs"
        source.parent.mkdir()
        source.write_text('''
public sealed partial class ExampleComponent : Component
{
    [DataField, AlwaysPushInheritance]
    public List<string> Values = new();
    [DataField("settings"), AlwaysPushInheritance]
    public Dictionary<string, int> Options = new();
    [DataField, NeverPushInheritance]
    public int Local;
}
''', encoding="utf-8")
        self.write("""
- type: entity
  id: Base
  components:
  - type: Example
    values: [base]
    settings: {base: 1, common: 1}
    local: 4
- type: entity
  id: Child
  parent: Base
  components:
  - type: Example
    values: [child]
    settings: {child: 2, common: 2}
""")
        component = PrototypeCatalog(self.resources).components("Child")[0]
        self.assertEqual(component["values"], ["child", "base"])
        self.assertEqual(component["settings"], {"child": 2, "common": 2, "base": 1})
        self.assertNotIn("local", component)

    def test_tags_survive_inherited_components_and_yaml_description(self):
        self.write("""
- type: entity
  id: Base
  components:
  - type: Effect
    effect: !type:Foo
      state: on
- type: entity
  id: Child
  parent: Base
""")
        catalog = PrototypeCatalog(self.resources)
        description = catalog.describe("Child")
        effect = description["data"]["components"][0]["effect"]
        self.assertEqual(effect.yaml_tag, "!type:Foo")
        self.assertEqual(effect["state"], "on")
        self.assertEqual(load_yaml(description["yaml"])["components"][0]["effect"].yaml_tag, "!type:Foo")

    def test_search_id_name_and_source_and_kind(self):
        self.write("""
- type: entity
  id: Base
- type: entity
  id: Named
  name: Russian chamber
- type: entity
  id: Room
  parent: [Base, Named]
- type: tile
  id: Room
  name: tiles-room
""", "rooms.yml")
        catalog = PrototypeCatalog(self.resources)
        self.assertEqual(catalog.describe("Room")["name"], "Russian chamber")
        self.assertEqual(catalog.search("Room")[0]["id"], "Room")
        self.assertEqual(len(catalog.search("rooms.yml chamber")), 2)
        self.assertEqual(catalog.search("tiles", kind="tile")[0]["id"], "Room")
        self.assertEqual(catalog.search("", limit=0), [])
        self.assertFalse(catalog.has("Missing"))

    def test_cycles_missing_parents_and_duplicates_are_explicit(self):
        self.write("""
- type: entity
  id: A
  parent: B
- type: entity
  id: B
  parent: A
- type: entity
  id: MissingChild
  parent: Missing
- type: entity
  id: Duplicate
- type: entity
  id: Duplicate
""")
        catalog = PrototypeCatalog(self.resources)
        with self.assertRaisesRegex(ValueError, "cycle"):
            catalog.describe("A")
        with self.assertRaisesRegex(ValueError, "Unknown prototype"):
            catalog.describe("MissingChild")
        with self.assertRaisesRegex(ValueError, "duplicate"):
            catalog.describe("Duplicate")
        self.assertFalse(catalog.has("Duplicate"))
        self.assertTrue(catalog.diagnostics)


if __name__ == "__main__":
    unittest.main()
