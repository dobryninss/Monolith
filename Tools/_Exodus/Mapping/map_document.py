"""Offline, pre-MapInit SS14 map editing. Coordinates and IDs are YAML-local.

Tile packing follows Robust.Shared/EntitySerialization/MapChunkSerializer.cs.
The editor preserves unrecognised fields and YAML tags; comments/formatting are
not preserved. Session handles and request receipts never enter exported YAML.
"""

from __future__ import annotations

import base64
import copy
import hashlib
import math
import os
from pathlib import Path
import re
import struct
from typing import Any, Iterator
import uuid

import yaml


class MappingError(ValueError):
    """Invalid map or edit request; no batch changes have been committed."""


class TaggedMapping(dict):
    """A safe YAML mapping with an engine-defined tag."""


class TaggedSequence(list):
    """A safe YAML sequence with an engine-defined tag."""


class TaggedScalar(str):
    """A safe YAML scalar with an engine-defined tag."""


class MapLoader(getattr(yaml, "CSafeLoader", yaml.SafeLoader)):
    pass


class MapDumper(getattr(yaml, "CSafeDumper", yaml.SafeDumper)):
    def ignore_aliases(self, data: Any) -> bool:
        return True


# PyYAML's YAML 1.1 implicit rules turn sprite states such as "on" into booleans.
# SS14 expects those to remain strings. Do not change the global SafeLoader.
MapLoader.yaml_implicit_resolvers = {
    key: [(tag, regex) for tag, regex in resolvers
          if tag not in ("tag:yaml.org,2002:bool", "tag:yaml.org,2002:timestamp")]
    for key, resolvers in yaml.SafeLoader.yaml_implicit_resolvers.items()
}
MapLoader.add_implicit_resolver(
    "tag:yaml.org,2002:bool", re.compile(r"^(?:true|True|TRUE|false|False|FALSE)$"),
    list("tTfF"))


def _unknown_tag(loader: MapLoader, tag: str, node: yaml.Node) -> Any:
    if isinstance(node, yaml.MappingNode):
        value = TaggedMapping(loader.construct_mapping(node, deep=True))
    elif isinstance(node, yaml.SequenceNode):
        value = TaggedSequence(loader.construct_sequence(node, deep=True))
    else:
        value = TaggedScalar(loader.construct_scalar(node))
    value.yaml_tag = tag
    return value


def _represent_tag(dumper: MapDumper, value: Any) -> yaml.Node:
    if isinstance(value, dict):
        return dumper.represent_mapping(value.yaml_tag, value)
    if isinstance(value, list):
        return dumper.represent_sequence(value.yaml_tag, value)
    return dumper.represent_scalar(value.yaml_tag, str(value))


MapLoader.add_multi_constructor("", _unknown_tag)
for _tagged_type in (TaggedMapping, TaggedSequence, TaggedScalar):
    MapDumper.add_representer(_tagged_type, _represent_tag)


def load_yaml(text: str) -> Any:
    """Load safe YAML, retaining arbitrary Robust !type: tags."""
    return yaml.load(text, Loader=MapLoader)


def dump_yaml(data: Any) -> str:
    return yaml.dump(data, Dumper=MapDumper, allow_unicode=True,
                     sort_keys=False, default_flow_style=False, width=120)


def iter_entities(data: dict) -> Iterator[tuple[str, dict]]:
    for group in data.get("entities", []):
        for entity in group.get("entities", []):
            yield group.get("proto", ""), entity


def get_component(entity: dict, component_type: str) -> dict | None:
    return next((component for component in entity.get("components", [])
                 if component.get("type") == component_type), None)


def _integer(value: Any, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise MappingError(f"{label} must be an integer")
    return value


def _number(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise MappingError(f"{label} must be a finite number")
    return float(value)


def parse_vector(value: Any) -> tuple[float, float]:
    if isinstance(value, str):
        parts = value.split(",")
    elif isinstance(value, (tuple, list)):
        parts = value
    else:
        raise MappingError(f"Invalid vector: {value!r}")
    if len(parts) != 2:
        raise MappingError(f"Expected two vector coordinates: {value!r}")
    try:
        result = (float(parts[0]), float(parts[1]))
    except (ValueError, TypeError) as exc:
        raise MappingError(f"Invalid vector: {value!r}") from exc
    if not all(math.isfinite(coordinate) for coordinate in result):
        raise MappingError(f"Non-finite vector: {value!r}")
    return result


def _angle(value: Any) -> float:
    if isinstance(value, str) and value.rstrip().endswith("rad"):
        return math.degrees(float(value.rstrip()[:-3].strip()))
    return float(value)


def _bounds(value: Any) -> tuple[int, int, int, int]:
    if not isinstance(value, (list, tuple)) or len(value) != 4:
        raise MappingError("bounds must be [min_x, min_y, max_x, max_y], inclusive")
    x0, y0, x1, y1 = (_integer(item, "bounds coordinate") for item in value)
    if x0 > x1 or y0 > y1:
        raise MappingError("bounds minimum exceeds maximum")
    return x0, y0, x1, y1


def decode_chunk(chunk: dict, default_size: int = 16) -> list[tuple[int, int, int, int]]:
    """Decode row-major tiles: (YAML tile ID, flags, variant, rotation/mirroring)."""
    version = int(chunk.get("version", 1))
    if not 1 <= version <= 7:
        raise MappingError(f"Unsupported tile chunk version {version}")
    size = int(chunk.get("size", default_size))
    if not 1 <= size <= 256:
        raise MappingError(f"Unsupported tile chunk size {size}")
    layout = struct.Struct("<iBBB" if version >= 7 else "<iBB" if version >= 6 else "<HBB")
    try:
        payload = base64.b64decode(chunk["tiles"], validate=True)
    except (ValueError, TypeError, KeyError) as exc:
        raise MappingError("Invalid base64 tile chunk") from exc
    expected_length = size * size * layout.size
    if len(payload) != expected_length:
        raise MappingError(f"Tile chunk has {len(payload)} bytes; expected {expected_length}")
    return [values if version >= 7 else (*values, 0)
            for values in layout.iter_unpack(payload)]


def encode_chunk(tiles: list[tuple[int, int, int, int]], *, size: int = 16,
                 version: int = 7) -> str:
    """Encode a complete chunk. Legacy chunks can be retained when untouched."""
    if version not in (6, 7):
        raise MappingError("Writing tile chunks supports versions 6 and 7")
    if len(tiles) != size * size:
        raise MappingError("Tile count does not match chunk dimensions")
    layout = struct.Struct("<iBBB" if version == 7 else "<iBB")
    payload = bytearray()
    for tile in tiles:
        if len(tile) != 4 or any(isinstance(value, bool) or not isinstance(value, int) for value in tile):
            raise MappingError("Tile values must be four integers")
        if not 0 <= tile[0] <= 2147483647 or any(not 0 <= value <= 255 for value in tile[1:]):
            raise MappingError("Tile ID or byte is outside its supported range")
        if version == 6 and tile[3] != 0:
            raise MappingError("Version 6 cannot encode tile rotation/mirroring")
        payload.extend(layout.pack(*(tile if version == 7 else tile[:3])))
    return base64.b64encode(payload).decode("ascii")


def _merge(target: dict, update: dict) -> None:
    """Replace the supplied component fields; preserve unspecified fields."""
    for key, value in update.items():
        target[key] = copy.deepcopy(value)


class MappingDocument:
    def __init__(self, data: dict):
        if not isinstance(data, dict):
            raise MappingError("Map root must be a mapping")
        self.data = copy.deepcopy(data)
        self._handles: dict[str, int] = {}
        self._operations: dict[str, dict] = {}
        self._reindex()
        self._next_uid = max(self._entities, default=0) + 1
        self.validate()

    @classmethod
    def create(cls, category: str = "Grid", name: str = "",
               grid_components: list[dict] | None = None) -> MappingDocument:
        if category not in ("Grid", "Map"):
            raise MappingError("New documents support Grid or Map categories")
        grid_uid = 1 if category == "Grid" else 2
        entities = []
        if category == "Map":
            entities.append({"uid": 1, "components": [
                {"type": "MetaData", "name": name}, {"type": "Transform"},
                {"type": "Map", "mapPaused": True}]})
        grid = {"uid": grid_uid, "components": [
            {"type": "MetaData", "name": name},
            {"type": "Transform", "parent": "invalid" if category == "Grid" else 1},
            {"type": "MapGrid", "chunks": {}},
            {"type": "Physics", "bodyType": "Static"},
            {"type": "Fixtures", "fixtures": {}}]}
        entities.append(grid)
        document = cls({
            "meta": {"format": 7, "category": category, "entityCount": len(entities)},
            "maps": [1] if category == "Map" else [], "grids": [grid_uid],
            "orphans": [grid_uid] if category == "Grid" else [], "nullspace": [],
            "tilemap": {0: "Space"}, "entities": [{"proto": "", "entities": entities}]})
        if grid_components:
            document.apply_batch([{"op": "patch", "entity": grid_uid, "components": grid_components}])
        return document

    @classmethod
    def from_file(cls, path: str | Path) -> MappingDocument:
        return cls(load_yaml(Path(path).read_text(encoding="utf-8-sig")))

    load = from_file

    @property
    def revision(self) -> str:
        return hashlib.sha256(dump_yaml(self.data).encode("utf-8")).hexdigest()

    @property
    def grid_ids(self) -> list[int]:
        return list(self.data["grids"])

    @property
    def state(self) -> dict:
        return {"version": 1, "revision": self.revision,
                "nextUid": self._next_uid,
                "handles": copy.deepcopy(self._handles),
                "operations": copy.deepcopy(self._operations)}

    def restore_state(self, state: dict) -> None:
        if state.get("version") != 1 or state.get("revision") != self.revision:
            raise MappingError("Session state does not match map revision; reimport the map")
        handles = state.get("handles", {})
        if not isinstance(handles, dict) or any(uid not in self._entities for uid in handles.values()):
            raise MappingError("Session contains invalid entity handles")
        self._handles = copy.deepcopy(handles)
        self._operations = copy.deepcopy(state.get("operations", {}))
        self._next_uid = max(_integer(state.get("nextUid", self._next_uid), "nextUid"), self._next_uid)

    def _reindex(self) -> None:
        self._entities: dict[int, tuple[str, dict]] = {}
        for prototype, entity in iter_entities(self.data):
            uid = _integer(entity.get("uid"), "entity UID")
            if uid <= 0 or uid in self._entities:
                raise MappingError(f"Invalid or duplicate entity UID {uid}")
            self._entities[uid] = (prototype, entity)

    def iter_entities(self) -> Iterator[tuple[str, dict]]:
        return iter_entities(self.data)

    def get_entity(self, uid: int | str) -> dict:
        return self._entities[self._resolve_uid(uid)][1]

    def _resolve_uid(self, value: Any) -> int:
        if isinstance(value, str) and value in self._handles:
            value = self._handles[value]
        uid = _integer(value, "entity UID (or existing logical handle)")
        if uid not in self._entities:
            raise MappingError(f"Unknown entity UID {uid}")
        return uid

    def _grid(self, uid: int | str | None) -> tuple[int, dict]:
        if uid is None:
            if len(self.grid_ids) != 1:
                raise MappingError("Specify grid when the document has multiple grids")
            uid = self.grid_ids[0]
        uid = self._resolve_uid(uid)
        component = get_component(self.get_entity(uid), "MapGrid")
        if uid not in self.grid_ids or component is None:
            raise MappingError(f"Entity {uid} is not a grid")
        return uid, component

    def validate(self) -> None:
        if self.data.get("meta", {}).get("format") != 7:
            raise MappingError("Only map format 7 is editable; resave older maps in the mapping editor first")
        if self.data["meta"].get("postmapinit") is True:
            raise MappingError("Post-MapInit documents cannot be edited or exported as mapping sources")
        for field in ("maps", "grids", "orphans", "nullspace"):
            values = self.data.get(field)
            if not isinstance(values, list) or len(values) != len(set(values)):
                raise MappingError(f"Invalid root {field} UID list")
            for uid in values:
                if uid not in self._entities:
                    raise MappingError(f"Root {field} references missing entity {uid}")
        parents = {}
        for uid, (_, entity) in self._entities.items():
            if entity.get("mapInit") not in (None, False):
                raise MappingError(f"Entity {uid} is post-MapInit; restore a pre-init checkpoint")
            map_component = get_component(entity, "Map")
            if map_component and map_component.get("mapInitialized") not in (None, False):
                raise MappingError(f"Map entity {uid} is post-MapInit; restore a pre-init checkpoint")
            component_types = set()
            for component in entity.get("components", []):
                kind = component.get("type")
                if not isinstance(kind, str) or kind in component_types:
                    raise MappingError(f"Entity {uid} contains an invalid or duplicate component {kind!r}")
                component_types.add(kind)
            transform = get_component(entity, "Transform") or {}
            parse_vector(transform.get("pos", "0,0"))
            if not math.isfinite(_angle(transform.get("rot", 0))):
                raise MappingError(f"Entity {uid} has invalid rotation")
            parent = transform.get("parent", "invalid")
            if parent not in (None, "invalid", 0):
                if parent not in self._entities:
                    raise MappingError(f"Entity {uid} references missing transform parent {parent}")
                parents[uid] = parent
        visited = set()
        for uid in parents:
            chain = set()
            while uid in parents and uid not in visited:
                if uid in chain:
                    raise MappingError(f"Transform hierarchy contains a cycle at entity {uid}")
                chain.add(uid)
                uid = parents[uid]
            visited.update(chain)
        tilemap = self.data.get("tilemap")
        if not isinstance(tilemap, dict) or "Space" not in tilemap.values():
            raise MappingError("tilemap must include Space")
        for uid in self.grid_ids:
            _, grid = self._grid(uid)
            for key, chunk in grid.get("chunks", {}).items():
                if parse_vector(key) != parse_vector(chunk.get("ind", key)):
                    raise MappingError(f"Grid {uid} chunk key and ind disagree")
                if any(value != int(value) for value in parse_vector(key)):
                    raise MappingError("Chunk coordinates must be integers")
                for tile_id, _, _, _ in decode_chunk(chunk, grid.get("chunkSize", 16)):
                    if tile_id not in tilemap:
                        raise MappingError(f"Grid {uid} references missing tilemap ID {tile_id}")
        maps, grids, orphans = self.data["maps"], self.grid_ids, self.data["orphans"]
        category = self.data["meta"].get("category")
        if category == "Grid" and (len(grids) != 1 or maps or orphans != grids):
            raise MappingError("Grid category requires one orphaned grid and no maps")
        if category == "Map" and (len(maps) != 1 or orphans):
            raise MappingError("Map category requires one map and no orphaned entities")
        for uid in maps:
            if get_component(self.get_entity(uid), "Map") is None:
                raise MappingError(f"Map entity {uid} lacks Map component")

    def save(self, path: str | Path) -> None:
        """Atomically export the map only, without tool-specific metadata."""
        self._reindex()
        self.validate()
        self.data["meta"]["entityCount"] = len(self._entities)
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        temp_path = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
        try:
            with temp_path.open("x", encoding="utf-8", newline="\n") as output:
                output.write(dump_yaml(self.data))
            os.replace(temp_path, path)
        finally:
            if temp_path.exists():
                temp_path.unlink()

    def tiles(self, grid: int | None = None, bounds: list[int] | None = None) -> list[dict]:
        _, component = self._grid(grid)
        region = _bounds(bounds) if bounds is not None else None
        tilemap = self.data["tilemap"]
        result = []
        size = component.get("chunkSize", 16)
        for key, chunk in component.get("chunks", {}).items():
            chunk_x, chunk_y = map(int, parse_vector(chunk.get("ind", key)))
            chunk_size = chunk.get("size", size)
            for index, (tile_id, flags, variant, rotation) in enumerate(decode_chunk(chunk, size)):
                if tilemap[tile_id] == "Space":
                    continue
                x = chunk_x * chunk_size + index % chunk_size
                y = chunk_y * chunk_size + index // chunk_size
                if region and not (region[0] <= x <= region[2] and region[1] <= y <= region[3]):
                    continue
                result.append({"x": x, "y": y, "tile": tilemap[tile_id], "flags": flags,
                               "variant": variant, "rotationMirroring": rotation})
        return result

    def inspect(self, grid: int | None = None, bounds: list[int] | None = None) -> dict:
        grid_uid, _ = self._grid(grid)
        region = _bounds(bounds) if bounds is not None else None
        entities = []
        for prototype, entity in self.iter_entities():
            uid = entity["uid"]
            if uid == grid_uid:
                continue
            transform = get_component(entity, "Transform") or {}
            x, y = parse_vector(transform.get("pos", "0,0"))
            rotation = _angle(transform.get("rot", 0))
            parent = transform.get("parent", "invalid")
            while parent in self._entities and parent != grid_uid:
                parent_transform = get_component(self.get_entity(parent), "Transform") or {}
                px, py = parse_vector(parent_transform.get("pos", "0,0"))
                pr = _angle(parent_transform.get("rot", 0))
                angle = math.radians(pr)
                x, y = px + x * math.cos(angle) - y * math.sin(angle), py + x * math.sin(angle) + y * math.cos(angle)
                rotation += pr
                parent = parent_transform.get("parent", "invalid")
            if parent != grid_uid:
                continue
            if region and not (region[0] <= x < region[2] + 1 and region[1] <= y < region[3] + 1):
                continue
            entities.append({"uid": uid, "prototype": prototype, "x": x, "y": y,
                             "rotation": rotation, "anchored": transform.get("anchored"),
                             "components": copy.deepcopy(entity.get("components", []))})
        return {"grid": grid_uid, "revision": self.revision,
                "tiles": self.tiles(grid_uid, bounds), "entities": entities}

    def apply_batch(self, operations: list[dict], expected_revision: str | None = None,
                    operation_id: str | None = None) -> dict:
        """Apply all operations on a copy, validate, then commit exactly once."""
        if not isinstance(operations, list) or any(not isinstance(op, dict) for op in operations):
            raise MappingError("operations must be a list of mappings")
        request_hash = hashlib.sha256(dump_yaml(operations).encode("utf-8")).hexdigest()
        if operation_id is not None:
            if not isinstance(operation_id, str) or not operation_id:
                raise MappingError("operation_id must be a non-empty string")
            previous = self._operations.get(operation_id)
            if previous is not None:
                if previous["requestHash"] != request_hash:
                    raise MappingError("operation_id was already used for a different batch")
                return {**copy.deepcopy(previous["receipt"]), "replayed": True,
                        "current_revision": self.revision}
        if expected_revision is not None and expected_revision != self.revision:
            raise MappingError("Revision conflict: reload the map before applying this batch")
        work = copy.deepcopy(self)
        results = []
        for index, operation in enumerate(operations):
            try:
                results.append(work._apply(operation))
            except (ValueError, TypeError, KeyError, OverflowError) as exc:
                raise MappingError(f"Operation {index} ({operation.get('op')}): {exc}") from exc
        work._reindex()
        work.data["meta"]["entityCount"] = len(work._entities)
        work.validate()
        receipt = {"revision": work.revision, "results": results,
                   "handles": copy.deepcopy(work._handles), "replayed": False}
        if operation_id is not None:
            work._operations[operation_id] = {"requestHash": request_hash, "receipt": copy.deepcopy(receipt)}
        self.__dict__.update(work.__dict__)
        return receipt

    def _json_value(self, value: Any) -> Any:
        if isinstance(value, dict):
            if set(value) == {"$entity"}:
                return self._resolve_uid(value["$entity"])
            if set(value) == {"$tag", "$value"}:
                inner = self._json_value(value["$value"])
                tagged = (TaggedMapping(inner) if isinstance(inner, dict) else
                          TaggedSequence(inner) if isinstance(inner, list) else TaggedScalar(inner))
                if not isinstance(value["$tag"], str) or not value["$tag"].startswith("!"):
                    raise MappingError("$tag must be a YAML tag beginning with !")
                tagged.yaml_tag = value["$tag"]
                return tagged
            result = copy.deepcopy(value)
            for key, inner in value.items():
                result[key] = self._json_value(inner)
            return result
        if isinstance(value, list):
            result = copy.deepcopy(value)
            result[:] = [self._json_value(inner) for inner in value]
            return result
        if isinstance(value, float) and not math.isfinite(value):
            raise MappingError("Component data cannot contain non-finite numbers")
        return copy.deepcopy(value)

    def _patch_components(self, entity: dict, components: list[dict]) -> None:
        if not isinstance(components, list):
            raise MappingError("components must be a list")
        seen = set()
        for component in self._json_value(components):
            if not isinstance(component, dict) or not isinstance(component.get("type"), str):
                raise MappingError("Each component requires a string type")
            if component["type"] in seen:
                raise MappingError(f"Duplicate component patch {component['type']}")
            seen.add(component["type"])
            existing = get_component(entity, component["type"])
            if existing is None:
                entity.setdefault("components", []).append(component)
            else:
                _merge(existing, component)

    def _apply(self, operation: dict) -> dict:
        kind = operation.get("op")
        if kind == "tiles":
            return self._set_tiles(operation)
        if kind == "spawn":
            grid_uid, _ = self._grid(operation.get("grid"))
            prototype = operation.get("prototype")
            if not isinstance(prototype, str) or not prototype:
                raise MappingError("spawn requires a non-empty prototype")
            key = operation.get("key")
            if key is not None and (not isinstance(key, str) or not key or key in self._handles):
                raise MappingError("key must be a new non-empty logical handle")
            uid = self._next_uid
            self._next_uid += 1
            x, y = _number(operation.get("x"), "x"), _number(operation.get("y"), "y")
            rotation = _number(operation.get("rotation", 0), "rotation")
            transform = {"type": "Transform", "parent": grid_uid, "pos": f"{x:.17g},{y:.17g}", "rot": rotation}
            if "anchored" in operation:
                if not isinstance(operation["anchored"], bool):
                    raise MappingError("anchored must be boolean")
                transform["anchored"] = operation["anchored"]
            entity = {"uid": uid, "components": [transform]}
            self._patch_components(entity, operation.get("components", []))
            group = next((group for group in self.data["entities"] if group.get("proto", "") == prototype), None)
            if group is None:
                group = {"proto": prototype, "entities": []}
                self.data["entities"].append(group)
            group["entities"].append(entity)
            self._entities[uid] = (prototype, entity)
            if key is not None:
                self._handles[key] = uid
            return {"op": kind, "uid": uid, "key": key}
        if kind not in ("patch", "move", "delete"):
            raise MappingError(f"Unsupported operation {kind!r}")
        uid = self._resolve_uid(operation.get("entity"))
        entity = self.get_entity(uid)
        if kind == "patch":
            self._patch_components(entity, operation.get("components", []))
        elif kind == "move":
            transform = {"type": "Transform"}
            old = get_component(entity, "Transform") or {}
            x, y = parse_vector(old.get("pos", "0,0"))
            x, y = _number(operation.get("x", x), "x"), _number(operation.get("y", y), "y")
            transform["pos"] = f"{x:.17g},{y:.17g}"
            if "rotation" in operation:
                transform["rot"] = _number(operation["rotation"], "rotation")
            if "grid" in operation:
                transform["parent"] = self._grid(operation["grid"])[0]
            self._patch_components(entity, [transform])
        else:
            if uid in self.grid_ids or uid in self.data["maps"]:
                raise MappingError("Deleting map/grid roots is not supported")
            for _, child in self.iter_entities():
                if (get_component(child, "Transform") or {}).get("parent") == uid:
                    raise MappingError(f"Entity {uid} has children; delete or reparent them first")
            for group in self.data["entities"]:
                group["entities"][:] = [entry for entry in group["entities"] if entry["uid"] != uid]
            self.data["entities"][:] = [group for group in self.data["entities"] if group["entities"]]
            for field in ("orphans", "nullspace"):
                self.data[field][:] = [entry for entry in self.data[field] if entry != uid]
            self._handles = {key: value for key, value in self._handles.items() if value != uid}
            del self._entities[uid]
        return {"op": kind, "uid": uid}

    def _set_tiles(self, operation: dict) -> dict:
        grid_uid, grid = self._grid(operation.get("grid"))
        x0, y0, x1, y1 = _bounds(operation.get("bounds"))
        if (x1 - x0 + 1) * (y1 - y0 + 1) > 1_000_000:
            raise MappingError("A single tile operation is limited to one million tiles")
        tile_name = operation.get("tile")
        if not isinstance(tile_name, str) or not tile_name:
            raise MappingError("tile must be a non-empty prototype ID")
        values = [_integer(operation.get(field, 0), field) for field in ("flags", "variant", "rotationMirroring")]
        if any(not 0 <= value <= 255 for value in values):
            raise MappingError("Tile flags, variant and rotationMirroring must fit a byte")
        tilemap = self.data["tilemap"]
        tile_id = next((key for key, value in tilemap.items() if value == tile_name), None)
        if tile_id is None:
            tile_id = max(tilemap, default=-1) + 1
            tilemap[tile_id] = tile_name
        space_id = next(key for key, value in tilemap.items() if value == "Space")
        size = grid.get("chunkSize", 16)
        chunks = grid.setdefault("chunks", {})
        for cy in range(y0 // size, y1 // size + 1):
            for cx in range(x0 // size, x1 // size + 1):
                key = f"{cx},{cy}"
                existing = chunks.get(key)
                if existing and existing.get("size", size) != size:
                    raise MappingError("Chunk size differs from grid chunkSize")
                tiles = decode_chunk(existing, size) if existing else [(space_id, 0, 0, 0)] * (size * size)
                for y in range(max(y0, cy * size), min(y1, (cy + 1) * size - 1) + 1):
                    for x in range(max(x0, cx * size), min(x1, (cx + 1) * size - 1) + 1):
                        tiles[(y - cy * size) * size + x - cx * size] = (tile_id, *values)
                if all(tilemap[tile[0]] == "Space" for tile in tiles):
                    chunks.pop(key, None)
                    continue
                chunk = copy.deepcopy(existing) if existing else {"ind": key}
                chunk.update({"tiles": encode_chunk(tiles, size=size), "version": 7})
                if size != 16:
                    chunk["size"] = size
                chunks[key] = chunk
        return {"op": "tiles", "grid": grid_uid, "count": (x1 - x0 + 1) * (y1 - y0 + 1)}
