from contextlib import ExitStack
from copy import deepcopy
from pathlib import Path
import sys
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from catalog import PrototypeCatalog
from map_document import encode_chunk
from render import render_document
from test_map_document import temporary_directory


class RenderTests(unittest.TestCase):
    def setUp(self):
        self.temp = ExitStack()
        self.addCleanup(self.temp.close)
        self.root = self.temp.enter_context(temporary_directory())
        resources = self.root / "Resources"
        (resources / "Prototypes").mkdir(parents=True)
        (resources / "Prototypes" / "test.yml").write_text("""
- type: entity
  id: Device
  components:
  - type: Door
- type: tile
  id: TestFloor
""", encoding="utf-8")
        self.catalog = PrototypeCatalog(resources)
        # Asymmetric tiny chunk verifies row-major order and negative indices.
        payload = encode_chunk([(1, 0, 0, 0), (0, 0, 0, 0),
                                (1, 0, 1, 0), (1, 0, 2, 0)], size=2)
        self.data = {
            "meta": {"format": 7, "category": "Grid"}, "grids": [1],
            "tilemap": {0: "Space", 1: "TestFloor"},
            "entities": [
                {"proto": "", "entities": [{"uid": 1, "components": [
                    {"type": "Transform", "parent": "invalid"},
                    {"type": "MetaData", "name": "Research <grid>"},
                    {"type": "MapGrid", "chunkSize": 2, "chunks": {"-1,0": {
                        "ind": "-1,0", "size": 2, "version": 7, "tiles": payload}}},
                ]}]},
                {"proto": "Device", "entities": [{"uid": 2, "components": [
                    {"type": "Transform", "parent": 1, "pos": "-1.5,0.5", "rot": "1.5707963267948966 rad"},
                    {"type": "MetaData", "name": "Door <A&B>"},
                ]}]},
            ],
        }

    def test_svg_is_standalone_deterministic_and_does_not_mutate_input(self):
        before = deepcopy(self.data)
        output = self.root / "preview.svg"
        result = render_document(self.data, output, self.catalog)
        first = output.read_bytes()
        render_document(self.data, output, self.catalog)
        self.assertEqual(first, output.read_bytes())
        self.assertEqual(before, self.data)
        root = ET.fromstring(first)
        self.assertEqual(root.tag, "{http://www.w3.org/2000/svg}svg")
        titles = [element.text for element in root.iter("{http://www.w3.org/2000/svg}title")]
        self.assertTrue(any("Door <A&B>" in title for title in titles))
        self.assertTrue(any("(-2,0)" in title for title in titles))
        self.assertTrue(any("(-1,1)" in title for title in titles))
        self.assertFalse(any("(-1,0)" in title for title in titles))
        self.assertEqual(result["bounds"], {"1": [-2, 0, -1, 1]})
        self.assertEqual(result["tiles"], 3)
        self.assertEqual(result["entities"], 1)
        self.assertEqual(result["mode"], "schematic")

    def test_inclusive_single_tile_bounds(self):
        result = render_document(self.data, self.root / "crop.svg", self.catalog, bounds=[-2, 0, -2, 0])
        self.assertEqual(result["tiles"], 1)
        self.assertEqual(result["entities"], 1)
        self.assertEqual(result["bounds"], {"1": [-2, 0, -2, 0]})

    def test_nested_parent_transform_and_grid_local_panels(self):
        self.data["entities"][1]["entities"].append({"uid": 3, "components": [
            {"type": "Transform", "parent": 2, "pos": "1,0"},
        ]})
        # Global grid transforms must not alter the local preview.
        self.data["entities"][0]["entities"][0]["components"][0]["pos"] = "100,200"
        output = self.root / "nested.svg"
        result = render_document(self.data, output, self.catalog)
        self.assertEqual(result["entities"], 2)
        self.assertIn("(-1.5,1.5)", output.read_text(encoding="utf-8"))

    def test_unknown_prototype_warning_and_invalid_selection(self):
        self.data["entities"][1]["proto"] = "Missing"
        result = render_document(self.data, self.root / "unknown.svg", self.catalog)
        self.assertEqual(result["warnings"], ["Unknown entity prototype: Missing"])
        with self.assertRaisesRegex(ValueError, "not in the document"):
            render_document(self.data, self.root / "bad.svg", self.catalog, grid=99)

    def test_numeric_yaml_angles_are_degrees(self):
        self.data["entities"][1]["entities"][0]["components"][0]["rot"] = 90
        self.data["entities"][1]["entities"].append({"uid": 3, "components": [
            {"type": "Transform", "parent": 2, "pos": "1,0"},
        ]})
        output = self.root / "numeric-angle.svg"
        render_document(self.data, output, self.catalog)
        self.assertIn("(-1.5,1.5)", output.read_text(encoding="utf-8"))

    def test_network_layer_is_below_equipment_and_small_tiles_are_legible(self):
        self.data["entities"][0]["entities"].append({"uid": 3, "components": [
            {"type": "Transform", "parent": 1, "pos": "-1.5,0.5"},
            {"type": "Cable"},
        ]})
        output = self.root / "layers.svg"
        render_document(self.data, output, self.catalog)
        root = ET.fromstring(output.read_bytes())
        groups = [element for element in root.iter("{http://www.w3.org/2000/svg}g")
                  if element.get("data-layer")]
        self.assertEqual([group.get("data-layer") for group in groups], ["power", "door"])
        tiles = [element for element in root.iter("{http://www.w3.org/2000/svg}rect")
                 if element.find("{http://www.w3.org/2000/svg}title") is not None]
        self.assertEqual(float(tiles[0].get("width")), 64)
        labels = [element.text for element in root.iter("{http://www.w3.org/2000/svg}text")]
        self.assertIn("DOOR", labels)

    def test_png_has_real_signature_when_pillow_available(self):
        try:
            from PIL import Image
        except ImportError:
            self.skipTest("Optional Pillow is not installed")
        output = self.root / "preview.png"
        result = render_document(self.data, output, self.catalog)
        self.assertEqual(result["format"], "png")
        with Image.open(output) as image:
            self.assertGreater(image.width, 0)
            self.assertGreater(image.height, 0)
            self.assertEqual(image.format, "PNG")


if __name__ == "__main__":
    unittest.main()
