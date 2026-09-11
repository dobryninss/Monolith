"""Offline prototype discovery and YAML inheritance for mapping tools.

This resolves authored YAML, not runtime component defaults, localization, or
serialization hooks. Robust applies parents in order (first parent wins), merges
component registries by type, and replaces ordinary datafields as whole values.
See SerializationManager.Composition.cs and ComponentRegistrySerializer.cs.
"""

from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import re

try:
    from .map_document import dump_yaml, load_yaml
except ImportError:
    from map_document import dump_yaml, load_yaml


class PrototypeCatalog:
    """Index entity/tile YAML prototypes below a Resources directory."""

    KINDS = ("entity", "tile")

    def __init__(self, resources: Path):
        self.resources = Path(resources).resolve()
        self._raw: dict[tuple[str, str], dict] = {}
        self._sources: dict[tuple[str, str], str] = {}
        self._resolved: dict[tuple[str, str], dict] = {}
        self._duplicates: set[tuple[str, str]] = set()
        self.diagnostics: list[str] = []
        # Source attributes describe composition, never C# default values.
        self._field_rules = self._read_field_rules()
        prototype_root = self.resources / "Prototypes"
        if not prototype_root.is_dir():
            raise ValueError(f"Prototype directory does not exist: {prototype_root}")

        paths = sorted(set(prototype_root.rglob("*.yml")) | set(prototype_root.rglob("*.yaml")))
        for path in paths:
            source = path.relative_to(self.resources).as_posix()
            try:
                entries = load_yaml(path.read_text(encoding="utf-8-sig"))
            except Exception as exc:
                self.diagnostics.append(f"{source}: could not parse YAML: {exc}")
                continue
            if entries is None:
                continue
            if not isinstance(entries, list):
                self.diagnostics.append(f"{source}: prototype document must be a sequence")
                continue
            for entry in entries:
                if not isinstance(entry, dict) or entry.get("type") not in self.KINDS:
                    continue
                kind, identifier = entry["type"], entry.get("id")
                if not isinstance(identifier, str) or not identifier:
                    self.diagnostics.append(f"{source}: {kind} prototype without a string id")
                    continue
                key = (kind, identifier)
                if key in self._raw:
                    self._duplicates.add(key)
                    self.diagnostics.append(f"Duplicate {kind} {identifier}: {self._sources[key]}, {source}")
                    continue
                self._raw[key] = entry
                self._sources[key] = source

    def _key(self, identifier: str, kind: str) -> tuple[str, str]:
        if kind not in self.KINDS:
            raise ValueError(f"Unsupported prototype kind {kind!r}; expected entity or tile")
        return kind, identifier

    def has(self, identifier: str, kind: str = "entity") -> bool:
        key = self._key(identifier, kind)
        return key in self._raw and key not in self._duplicates

    def search(self, query: str, kind: str = "entity", limit: int = 20) -> list[dict]:
        self._key("", kind)
        if limit < 0:
            raise ValueError("Search limit must be nonnegative")
        words = query.casefold().split()
        results = []
        for key, raw in self._raw.items():
            if key[0] != kind or key in self._duplicates:
                continue
            name = self._name(key, set())
            source = self._sources[key]
            text = f"{key[1]} {name} {source}".casefold()
            if not all(word in text for word in words):
                continue
            results.append({
                "id": key[1], "kind": kind, "name": name, "source": source,
                "abstract": bool(raw.get("abstract", False)),
            })
        needle = query.casefold()
        results.sort(key=lambda result: (
            result["id"].casefold() != needle,
            not result["id"].casefold().startswith(needle),
            result["id"].casefold(), result["id"],
        ))
        return results[:limit]

    def describe(self, identifier: str, kind: str = "entity") -> dict:
        key = self._key(identifier, kind)
        data = deepcopy(self._resolve(key, []))
        return {
            "id": identifier,
            "kind": kind,
            "name": self._name(key, set()),
            "source": self._sources[key],
            "parents": self._parents(self._raw[key]),
            "abstract": bool(self._raw[key].get("abstract", False)),
            "data": data,
            "yaml": dump_yaml(data),
            "resolution": "authored-yaml",
            "notes": [
                "No C# defaults, localization, category inference, or runtime serialization hooks.",
                "Parents apply in order; the child and then the first parent win conflicts.",
                "Ordinary datafields replace entire values; component registries merge by type.",
                "Always/NeverPushInheritance fields are discovered from available C# source attributes.",
            ],
        }

    def components(self, identifier: str) -> list[dict]:
        return deepcopy(self._resolve(("entity", identifier), []).get("components", []))

    def _name(self, key: tuple[str, str], seen: set) -> str:
        return self._inherited_name(key, seen) or key[1]

    def _inherited_name(self, key: tuple[str, str], seen: set) -> str | None:
        if key in seen or key not in self._raw:
            return None
        seen.add(key)
        raw = self._raw[key]
        if "name" in raw:
            return str(raw["name"])
        for parent in self._parents(raw):
            parent_key = (key[0], parent)
            if parent_key in self._raw:
                name = self._inherited_name(parent_key, seen)
                if name is not None:
                    return name
        return None

    @staticmethod
    def _parents(raw: dict) -> list[str]:
        value = raw.get("parent", [])
        if value is None:
            return []
        if isinstance(value, str):
            return [value]
        if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
            raise ValueError(f"Invalid parent list in prototype {raw.get('id')}")
        return value

    def _resolve(self, key: tuple[str, str], stack: list) -> dict:
        if key in self._duplicates:
            raise ValueError(f"Ambiguous duplicate prototype {key[0]} {key[1]}")
        if key not in self._raw:
            raise ValueError(f"Unknown prototype {key[0]} {key[1]}")
        if key in stack:
            chain = " -> ".join(item[1] for item in [*stack, key])
            raise ValueError(f"Prototype inheritance cycle: {chain}")
        if key in self._resolved:
            return self._resolved[key]
        raw = self._raw[key]
        result = deepcopy(raw)
        if key[0] == "entity":
            result["components"] = self._merge_components(result.get("components", []), [])
        for parent in self._parents(raw):
            inherited = self._resolve((key[0], parent), [*stack, key])
            for field, value in inherited.items():
                # Categories are resolved separately by Robust, not YAML composition.
                if field in {"id", "type", "parent", "abstract", "categories"}:
                    continue
                if key[0] == "entity" and field == "components":
                    result[field] = self._merge_components(result.get(field, []), value)
                elif field not in result:
                    result[field] = deepcopy(value)
        self._resolved[key] = result
        return result

    def _merge_components(self, child: list, parent: list) -> list[dict]:
        if not isinstance(child, list) or not isinstance(parent, list):
            raise ValueError("A component registry must be a YAML sequence")
        result = deepcopy(child)
        indices = {}
        for index, component in enumerate(result):
            if not isinstance(component, dict) or not isinstance(component.get("type"), str):
                raise ValueError("Every component requires a string type")
            if component["type"] in indices:
                raise ValueError(f"Duplicate component {component['type']}")
            indices[component["type"]] = index
        for component in parent:
            if not isinstance(component, dict) or not isinstance(component.get("type"), str):
                raise ValueError("Every component requires a string type")
            kind = component["type"]
            if kind not in indices:
                indices[kind] = len(result)
                result.append(deepcopy(component))
                continue
            target = result[indices[kind]]
            rules = self._field_rules.get(kind, {})
            for field, value in component.items():
                behavior, field_type = rules.get(field, ("default", ""))
                if behavior == "never":
                    continue
                if field not in target:
                    target[field] = deepcopy(value)
                elif behavior == "always":
                    current = target[field]
                    if "ComponentRegistry" in field_type:
                        target[field] = self._merge_components(current, value)
                    elif isinstance(current, dict) and isinstance(value, dict):
                        combined = deepcopy(current)
                        for nested_key, nested_value in value.items():
                            if nested_key not in combined:
                                combined[nested_key] = deepcopy(nested_value)
                        target[field] = combined
                    elif isinstance(current, list) and isinstance(value, list):
                        target[field] = deepcopy(current) + deepcopy(value)
                    # Scalars keep the child's value, as PushComposition does.
        return result

    def _read_field_rules(self) -> dict[str, dict[str, tuple[str, str]]]:
        """Read simple C# field annotations without evaluating any source code.

        This is intentionally limited to direct component datafields; it is not a
        C# serializer or an implementation of nested custom DataDefinitions.
        """
        rules: dict[str, dict[str, tuple[str, str]]] = {}
        repo = self.resources.parent
        roots = [repo / "Content.Shared", repo / "Content.Server",
                 repo / "Content.Client", repo / "RobustToolbox" / "Robust.Shared" / "GameObjects"]
        attributes = re.compile(
            r"(?P<attrs>(?:\[[^\]]*\]\s*)+)"
            r"(?:public|private|internal|protected)\s+"
            r"(?P<type>[^;={}\n]+?)\s+(?P<name>\w+)\s*(?:=|;|\{)",
            re.MULTILINE,
        )
        for root in roots:
            if not root.is_dir():
                continue
            for path in root.rglob("*.cs"):
                source = path.read_text(encoding="utf-8-sig")
                if "PushInheritance" not in source:
                    continue
                classes = re.findall(r"\bclass\s+(\w+Component)\b", source)
                if len(classes) != 1:
                    continue
                kind = classes[0][:-len("Component")]
                for match in attributes.finditer(source):
                    attrs = match["attrs"]
                    if "DataField" not in attrs:
                        continue
                    behavior = "always" if "AlwaysPushInheritance" in attrs else "never"
                    if "AlwaysPushInheritance" not in attrs and "NeverPushInheritance" not in attrs:
                        continue
                    explicit = re.search(r'DataField\s*\(\s*"([^"]+)"', attrs)
                    name = match["name"].lstrip("_")
                    name = explicit[1] if explicit else name[0].lower() + name[1:]
                    rules.setdefault(kind, {})[name] = (behavior, match["type"])
        return rules
