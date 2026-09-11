"""Deterministic, standalone grid schematics; not an in-engine screenshot.

Each grid is shown in its own local coordinates. SVG carries per-entity titles
and prototype legends; PNG uses the same schematic geometry when Pillow exists.
Runtime sprite layers, atmosphere, lighting and connections are not simulated.
"""

from __future__ import annotations

from collections import Counter
from copy import deepcopy
import hashlib
from html import escape
import math
from pathlib import Path
import re

try:
    from .map_document import decode_chunk, get_component, iter_entities, parse_vector
except ImportError:
    from map_document import decode_chunk, get_component, iter_entities, parse_vector


LAYERS = {
    "structure": "#d6dee8", "door": "#f7ba65", "pipe": "#68ceef",
    "power": "#f5da64", "entity": "#b9a4f2", "unknown": "#ff7185",
}

LAYER_ORDER = {"power": 0, "pipe": 1, "structure": 2, "door": 3, "entity": 4, "unknown": 5}


def _symbol(comps, prototype):
    """Choose schematic symbols from component capabilities, not entity IDs."""
    tags = set(comps.get("Tag", {}).get("tags", []))
    if "Cable" in comps:
        groups = {node.get("nodeGroupID") for node in comps.get("NodeContainer", {}).get("nodes", {}).values()
                  if isinstance(node, dict)}
        if "HVPower" in groups:
            return "power", "HV", "#f3787b"
        if "MVPower" in groups:
            return "power", "MV", "#f1d468"
        return "power", "LV", "#69bc91"
    if {"PipeAppearance", "PipeColorVisuals"} & comps.keys():
        return "pipe", "PIPE", "#68ceef"
    if "Door" in comps:
        return "door", "DOOR", "#f7ba65"
    if "Wall" in tags:
        return "wall", "WALL", "#aebfd0"
    if "Window" in tags:
        return "window", "WIN", "#7bd3ef"
    if "Airtight" in comps and "Physics" in comps:
        return "seal", "SEAL", "#7bd3ef"
    if "Thruster" in comps:
        if comps["Thruster"].get("thrusterType") == "Angular":
            return "gyro", "GYRO", "#d5b3fb"
        return "thruster", "THR", "#f5a276"
    for component, shape, label, color in (
        ("ShuttleConsole", "console", "HELM", "#83d6f5"),
        ("Computer", "console", "COMP", "#83d6f5"),
        ("Strap", "seat", "SEAT", "#c2b5f5"),
        ("GasCanister", "canister", "GAS", "#86d9bf"),
        ("PortableScrubber", "machine", "SCRUB", "#86d9bf"),
        ("PowerSupplier", "machine", "GEN", "#f5d787"),
        ("Apc", "machine", "APC", "#94d5a5"),
        ("PowerNetworkBattery", "machine", "BAT", "#dfb889"),
        ("PoweredLight", "light", "LIT", "#fff2aa"),
    ):
        if component in comps:
            return shape, label, color
    words = re.findall(r"[A-Z][a-z]*|[a-z]+|[0-9]+", prototype)
    label = "".join(word[0] for word in words)[:4].upper() or "OBJ"
    return "object", label, LAYERS["entity"]


def _color(identifier: str) -> str:
    digest = hashlib.sha256(identifier.encode("utf-8")).digest()
    return "#" + "".join(f"{50 + byte % 66:02x}" for byte in digest[:3])


def _angle(value) -> float:
    text = str(value or 0).strip()
    number = float(text.split()[0])
    # Robust's YAML Angle serializer reads bare numbers as degrees.
    if not text.endswith("rad"):
        number = math.radians(number)
    if not math.isfinite(number):
        raise ValueError("Non-finite entity rotation")
    return number


def _bounds(value):
    if value is None:
        return None
    if isinstance(value, str):
        value = value.replace(",", " ").split()
    if len(value) != 4:
        raise ValueError("Bounds require x0, y0, x1, y1")
    box = tuple(float(item) for item in value)
    if not all(math.isfinite(item) and item.is_integer() for item in box) or box[0] > box[2] or box[1] > box[3]:
        raise ValueError("Bounds must have integer values and x0 <= x1, y0 <= y1")
    return int(box[0]), int(box[1]), int(box[2]) + 1, int(box[3]) + 1


def _collect(data: dict, catalog, grid, bounds) -> tuple[list[dict], list[str]]:
    if data.get("meta", {}).get("format") != 7:
        raise ValueError("Schematic preview supports map format 7")
    rows = list(iter_entities(data))
    entities = {int(entity["uid"]): (prototype, entity) for prototype, entity in rows}
    roots = [int(uid) for uid in data.get("grids", [])]
    if grid is not None:
        selected = int(grid)
        if selected not in roots:
            raise ValueError(f"Grid YAML UID {selected} is not in the document")
        roots = [selected]
    if not roots:
        raise ValueError("Document contains no grids to preview")
    missing = set()
    prototypes = {}

    def components(prototype, entity):
        if prototype not in prototypes:
            if prototype and catalog.has(prototype):
                prototypes[prototype] = catalog.components(prototype)
            else:
                prototypes[prototype] = []
                if prototype:
                    missing.add(prototype)
        result = {item["type"]: deepcopy(item) for item in prototypes[prototype]}
        for kind in entity.get("missingComponents", []):
            result.pop(kind, None)
        for item in entity.get("components", []):
            result.setdefault(item["type"], {}).update(item)
        return result

    # Transform components can inherit authored anchored/rotation data.
    resolved = {uid: components(prototype, entity) for uid, (prototype, entity) in entities.items()}
    root_set = set(roots)
    locations = {}

    def locate(uid, visiting):
        if uid in locations:
            return locations[uid]
        if uid in root_set:
            return uid, 0.0, 0.0, 0.0
        if uid in visiting:
            raise ValueError(f"Transform parent cycle at YAML UID {uid}")
        transform = resolved[uid].get("Transform", {})
        parent = transform.get("parent")
        try:
            parent = int(parent)
        except (ValueError, TypeError):
            locations[uid] = None
            return None
        if parent not in entities:
            locations[uid] = None
            return None
        origin = locate(parent, visiting | {uid})
        if origin is None:
            locations[uid] = None
            return None
        x, y = parse_vector(transform.get("pos", "0,0"))
        if not math.isfinite(x) or not math.isfinite(y):
            raise ValueError(f"Non-finite position at YAML UID {uid}")
        parent_grid, px, py, rotation = origin
        cosine, sine = math.cos(rotation), math.sin(rotation)
        value = (parent_grid, px + x * cosine - y * sine,
                 py + x * sine + y * cosine, rotation + _angle(transform.get("rot", 0)))
        locations[uid] = value
        return value

    panels = []
    tilemap = {int(key): value for key, value in data.get("tilemap", {}).items()}
    for root in roots:
        if root not in entities:
            raise ValueError(f"Missing grid entity for YAML UID {root}")
        _, root_entity = entities[root]
        comp = get_component(root_entity, "MapGrid")
        if comp is None:
            comp = resolved[root].get("MapGrid")
        if comp is None:
            raise ValueError(f"YAML UID {root} has no MapGrid component")
        tile_size = float(comp.get("tileSize", 1))
        if tile_size != 1:
            raise ValueError("Schematic currently requires grid tileSize 1")
        size = int(comp.get("chunkSize", 16))
        tiles = []
        for key, chunk in comp.get("chunks", {}).items():
            cx, cy = parse_vector(chunk.get("ind", key))
            chunk_size = int(chunk.get("size", size))
            for index, (tile_id, flags, variant, rotation) in enumerate(decode_chunk(chunk, default_size=size)):
                tile = tilemap.get(tile_id)
                if tile is None:
                    raise ValueError(f"Tile ID {tile_id} is absent from tilemap")
                if tile == "Space":
                    continue
                x, y = int(cx) * chunk_size + index % chunk_size, int(cy) * chunk_size + index // chunk_size
                if bounds and not (bounds[0] <= x < bounds[2] and bounds[1] <= y < bounds[3]):
                    continue
                tiles.append({"x": x, "y": y, "tile": tile, "variant": variant,
                              "flags": flags, "rotationMirroring": rotation})
        markers = []
        for uid, (prototype, entity) in entities.items():
            if uid in root_set:
                continue
            location = locate(uid, set())
            if location is None or location[0] != root:
                continue
            _, x, y, angle = location
            if bounds and not (bounds[0] <= x < bounds[2] and bounds[1] <= y < bounds[3]):
                continue
            comps = resolved[uid]
            types = set(comps)
            layer = "entity"
            if "Airtight" in types and "Physics" in types:
                layer = "structure"
            if "Door" in types:
                layer = "door"
            if {"PipeAppearance", "PipeColorVisuals"} & types:
                layer = "pipe"
            if "Cable" in types:
                layer = "power"
            if prototype in missing:
                layer = "unknown"
            symbol, label, color = _symbol(comps, prototype)
            if layer == "unknown":
                symbol, label, color = "object", "?", LAYERS["unknown"]
            name = comps.get("MetaData", {}).get("name")
            markers.append({"uid": uid, "prototype": prototype or "(no prototype)",
                            "name": str(name or prototype or f"Entity {uid}"),
                            "x": x, "y": y, "angle": angle, "layer": layer,
                            "symbol": symbol, "label": label, "color": color})
        if bounds:
            box = bounds
        else:
            positions = [(tile["x"], tile["y"]) for tile in tiles]
            positions.extend((item["x"], item["y"]) for item in markers)
            if positions:
                box = (math.floor(min(x for x, _ in positions)), math.floor(min(y for _, y in positions)),
                       math.floor(max(x for x, _ in positions)) + 1, math.floor(max(y for _, y in positions)) + 1)
            else:
                box = (0, 0, 1, 1)
        name = resolved[root].get("MetaData", {}).get("name", f"Grid {root}")
        panels.append({"grid": root, "name": str(name), "bounds": box,
                       "tiles": sorted(tiles, key=lambda item: (item["y"], item["x"])),
                       "entities": sorted(markers, key=lambda item: (LAYER_ORDER[item["layer"]], item["uid"]))})
    return panels, [f"Unknown entity prototype: {identifier}" for identifier in sorted(missing)]


def _layout(panels):
    extent = max(max(panel["bounds"][2] - panel["bounds"][0], panel["bounds"][3] - panel["bounds"][1])
                 for panel in panels)
    scale, margin, top, gap = min(64, 720 / extent), 54, 110, 88
    widest = max((panel["bounds"][2] - panel["bounds"][0]) * scale for panel in panels)
    width = max(800, math.ceil(widest + margin * 2))
    for panel in panels:
        panel["left"], panel["top"], panel["scale"] = margin, top, scale
        top += (panel["bounds"][3] - panel["bounds"][1]) * scale + gap
    tile_counts = Counter(tile["tile"] for panel in panels for tile in panel["tiles"])
    entity_counts = Counter(item["prototype"] for panel in panels for item in panel["entities"])
    height = math.ceil(top + 86 + 20 * (len(tile_counts) + len(entity_counts)))
    return width, height, top, tile_counts, entity_counts


def _point(panel, x, y):
    x0, _, _, y1 = panel["bounds"]
    return panel["left"] + (x - x0) * panel["scale"], panel["top"] + (y1 - y) * panel["scale"]


def _geometry(marker, x, y, size):
    """Shared SVG/PNG geometry: shape, coordinates, fill, stroke, stroke width."""
    color, symbol = marker["color"], marker["symbol"]
    line_width = max(0.6, size / 32)
    if symbol in {"power", "pipe"}:
        offset = {"HV": 0.17, "MV": 0.28, "LV": 0.39, "PIPE": -0.36}[marker["label"]] * size
        return [("line", (x - size * 0.36, y + offset, x + size * 0.36, y + offset), None, color, line_width)]
    if symbol == "wall":
        half = size * 0.47
        return [("rect", (x - half, y - half, x + half, y + half), "#43566b", color, line_width)]
    if symbol in {"window", "door", "seal"}:
        half = size * 0.46
        background = "#243f50" if symbol != "door" else "#64513a"
        result = [("rect", (x - half, y - half, x + half, y + half), background, color, line_width)]
        if symbol == "window":
            result.append(("rect", (x - half * 0.8, y - half * 0.8, x + half * 0.8, y + half * 0.8), None, color, line_width))
        return result
    half = size * (0.25 if symbol == "light" else 0.36)
    shape = "ellipse" if symbol in {"gyro", "canister", "light"} else "rect"
    result = [(shape, (x - half, y - half, x + half, y + half), "#172538", color, line_width)]
    if symbol == "console":
        result.append(("line", (x - half * 0.75, y - half * 0.6, x + half * 0.75, y - half * 0.6), None, color, line_width * 2))
    if symbol == "seat":
        result.append(("line", (x - half, y + half * 0.8, x + half, y + half * 0.8), None, color, line_width * 2))
    if symbol in {"thruster", "gyro"}:
        dx, dy = -math.sin(marker["angle"]), -math.cos(marker["angle"])
        result.append(("line", (x + dx * half, y + dy * half,
                               x + dx * size * 0.49, y + dy * size * 0.49), None, color, line_width * 2))
    return result


def _svg(panels, width, height, legend_y, tile_counts, entity_counts):
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" '
             f'viewBox="0 0 {width} {height}" role="img">',
             '<title>Mapping blueprint schematic</title>',
             '<desc>Each panel uses grid-local coordinates, positive Y upwards. '
             'Symbols are schematic, not game sprites or verified network connections.</desc>',
             '<rect width="100%" height="100%" fill="#101926"/>',
             '<g font-family="monospace" fill="#e0e9f2">',
             '<text x="30" y="32" font-size="20">Mapping blueprint schematic</text>',
             '<text x="30" y="55" font-size="12">Grid-local coordinates; +Y up. '
             'Geometry only; runtime sprites, lighting and networks are not simulated.</text>']
    for panel in panels:
        x0, y0, x1, y1 = panel["bounds"]
        left, top, size = panel["left"], panel["top"], panel["scale"]
        grid_width, grid_height = (x1 - x0) * size, (y1 - y0) * size
        parts.append(f'<g id="grid-{panel["grid"]}">')
        parts.append(f'<text x="{left}" y="{top - 29}" font-size="14">'
                     f'{escape(panel["name"])} — grid YAML UID {panel["grid"]}</text>')
        parts.append(f'<rect x="{left}" y="{top}" width="{grid_width}" height="{grid_height}" '
                     'fill="#162335" stroke="#4c657c"/>')
        for tile in panel["tiles"]:
            x, y = _point(panel, tile["x"], tile["y"] + 1)
            parts.append(f'<rect x="{x:g}" y="{y:g}" width="{size}" height="{size}" '
                         f'fill="{_color(tile["tile"])}"><title>{escape(tile["tile"])} '
                         f'({tile["x"]},{tile["y"]}), variant {tile["variant"]}</title></rect>')
        # Axis density is bounded for large maps; SVG remains legible when zoomed.
        step = max(1, math.ceil(max(x1 - x0, y1 - y0) / 100))
        for x in range(math.ceil(x0 / step) * step, math.floor(x1) + 1, step):
            sx, _ = _point(panel, x, y0)
            parts.append(f'<path d="M {sx:g} {top:g} v {grid_height:g}" stroke="#7692aa" stroke-opacity="0.2"/>')
            if x % (step * 2) == 0:
                parts.append(f'<text x="{sx:g}" y="{top - 8:g}" font-size="9">{x}</text>')
        for y in range(math.ceil(y0 / step) * step, math.floor(y1) + 1, step):
            _, sy = _point(panel, x0, y)
            parts.append(f'<path d="M {left:g} {sy:g} h {grid_width:g}" stroke="#7692aa" stroke-opacity="0.2"/>')
            if y % (step * 2) == 0:
                parts.append(f'<text x="{left - 9:g}" y="{sy + 3:g}" text-anchor="end" font-size="9">{y}</text>')
        for marker in panel["entities"]:
            x, y = _point(panel, marker["x"], marker["y"])
            color = marker["color"]
            title = (f'{marker["name"]}; {marker["prototype"]}; YAML UID {marker["uid"]}; '
                     f'({marker["x"]:g},{marker["y"]:g}); layer {marker["layer"]}')
            parts.append(f'<g data-uid="{marker["uid"]}" data-layer="{marker["layer"]}">'
                         f'<title>{escape(title)}</title>')
            for shape, coordinates, fill, stroke, stroke_width in _geometry(marker, x, y, size):
                ax, ay, bx, by = coordinates
                style = f'fill="{fill or "none"}" stroke="{stroke}" stroke-width="{stroke_width:g}"'
                if shape == "rect":
                    parts.append(f'<rect x="{ax:g}" y="{ay:g}" width="{bx - ax:g}" height="{by - ay:g}" {style}/>')
                elif shape == "ellipse":
                    parts.append(f'<ellipse cx="{(ax + bx) / 2:g}" cy="{(ay + by) / 2:g}" '
                                 f'rx="{(bx - ax) / 2:g}" ry="{(by - ay) / 2:g}" {style}/>')
                else:
                    parts.append(f'<path d="M {ax:g} {ay:g} L {bx:g} {by:g}" {style}/>')
            if size >= 36 and marker["symbol"] not in {"power", "pipe", "wall"}:
                parts.append(f'<text x="{x:g}" y="{y:g}" text-anchor="middle" dominant-baseline="central" '
                             f'font-size="{min(12, size / 5):g}" font-weight="bold" fill="{color}">'
                             f'{escape(marker["label"])}</text>')
            parts.append('</g>')
        parts.append('</g>')
    parts.append(f'<text x="30" y="{legend_y:g}" font-size="16">Legend</text>')
    legend_y += 24
    for index, (layer, color) in enumerate(LAYERS.items()):
        x = 30 + index * 120
        parts.append(f'<circle cx="{x}" cy="{legend_y - 4:g}" r="4" fill="{color}"/>')
        parts.append(f'<text x="{x + 12}" y="{legend_y:g}" font-size="12">{layer}</text>')
    legend_y += 30
    labels = {item["prototype"]: item["label"] for panel in panels for item in panel["entities"]}
    colors = {item["prototype"]: item["color"] for panel in panels for item in panel["entities"]}
    for kind, counts in (("tile", tile_counts), ("entity", entity_counts)):
        for identifier, count in sorted(counts.items()):
            color = _color(identifier) if kind == "tile" else colors[identifier]
            parts.append(f'<rect x="30" y="{legend_y - 10:g}" width="10" height="10" fill="{color}"/>')
            parts.append(f'<text x="48" y="{legend_y:g}" font-size="12">'
                         f'{escape(labels[identifier]) + ": " if kind == "entity" else "tile: "}'
                         f'{escape(identifier)} × {count}</text>')
            legend_y += 20
    parts.append('</g></svg>')
    return "\n".join(parts) + "\n"


def _png(panels, output, width, height, legend_y, tile_counts, entity_counts):
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError as exc:
        raise ValueError("PNG preview requires Pillow; use an .svg output or install Pillow") from exc
    # Large maps keep a bounded raster allocation; SVG preserves all detail.
    factor = min(1.0, 4096 / width, 8192 / height)
    image = Image.new("RGB", (max(1, math.ceil(width * factor)), max(1, math.ceil(height * factor))), "#101926")
    draw = ImageDraw.Draw(image)
    font_size = max(8, round(12 * factor))
    font = None
    for face in ("DejaVuSans.ttf", "arial.ttf", "LiberationSans-Regular.ttf"):
        try:
            font = ImageFont.truetype(face, size=font_size)
            break
        except OSError:
            continue
    if font is None:
        font = ImageFont.load_default(size=font_size)

    def text(x, y, content, color="#e0e9f2"):
        draw.text((x * factor, y * factor), content, fill=color, font=font)

    text(30, 18, "Mapping blueprint schematic — grid-local coordinates; +Y up")
    text(30, 40, "Schematic symbols; runtime sprites, lighting and networks are not simulated.")
    for panel in panels:
        text(panel["left"], panel["top"] - 25, f'{panel["name"]} — grid {panel["grid"]}')
        for tile in panel["tiles"]:
            x, y = _point(panel, tile["x"], tile["y"] + 1)
            size = panel["scale"]
            draw.rectangle((x * factor, y * factor, (x + size) * factor, (y + size) * factor), fill=_color(tile["tile"]))
        for marker in panel["entities"]:
            x, y = _point(panel, marker["x"], marker["y"])
            size = panel["scale"]
            for shape, coordinates, fill, stroke, stroke_width in _geometry(marker, x, y, size):
                coordinates = tuple(coordinate * factor for coordinate in coordinates)
                line_width = max(1, round(stroke_width * factor))
                if shape == "line":
                    draw.line(coordinates, fill=stroke, width=line_width)
                else:
                    method = draw.rectangle if shape == "rect" else draw.ellipse
                    method(coordinates, fill=fill, outline=stroke, width=line_width)
            if size * factor >= 36 and marker["symbol"] not in {"power", "pipe", "wall"}:
                draw.text((x * factor, y * factor), marker["label"], fill=marker["color"], font=font, anchor="mm")
        x0, y0, x1, y1 = panel["bounds"]
        step = max(1, math.ceil(max(x1 - x0, y1 - y0) / 25))
        for x in range(math.ceil(x0 / step) * step, math.floor(x1) + 1, step):
            sx, _ = _point(panel, x, y0)
            text(sx, panel["top"] - 12, str(x))
        for y in range(math.ceil(y0 / step) * step, math.floor(y1) + 1, step):
            _, sy = _point(panel, x0, y)
            text(panel["left"] - 30, sy, str(y))
    text(30, legend_y, "Legend")
    legend_y += 24
    for index, (layer, color) in enumerate(LAYERS.items()):
        text(30 + index * 120, legend_y, layer, color)
    legend_y += 30
    labels = {item["prototype"]: item["label"] for panel in panels for item in panel["entities"]}
    colors = {item["prototype"]: item["color"] for panel in panels for item in panel["entities"]}
    for kind, counts in (("tile", tile_counts), ("entity", entity_counts)):
        for identifier, count in sorted(counts.items()):
            label = labels[identifier] if kind == "entity" else "tile"
            text(30, legend_y, f"{label}: {identifier} x {count}", colors[identifier] if kind == "entity" else "#e0e9f2")
            legend_y += 20
    image.save(output, format="PNG")


def render_document(data: dict, output_path: Path, catalog, grid=None, bounds=None) -> dict:
    """Export a schematic; bounds are inclusive grid-local tile indices."""
    output = Path(output_path)
    suffix = output.suffix.casefold()
    if suffix not in {".svg", ".png"}:
        raise ValueError("Preview output must end in .svg or .png")
    panels, warnings = _collect(data, catalog, grid, _bounds(bounds))
    width, height, legend_y, tile_counts, entity_counts = _layout(panels)
    output.parent.mkdir(parents=True, exist_ok=True)
    if suffix == ".svg":
        output.write_text(_svg(panels, width, height, legend_y, tile_counts, entity_counts), encoding="utf-8")
    else:
        _png(panels, output, width, height, legend_y, tile_counts, entity_counts)
    return {
        "output": str(output.resolve()), "format": suffix[1:], "mode": "schematic",
        "grids": [panel["grid"] for panel in panels],
        "entities": sum(entity_counts.values()), "tiles": sum(tile_counts.values()),
        "bounds": {str(panel["grid"]): [panel["bounds"][0], panel["bounds"][1],
                                         panel["bounds"][2] - 1, panel["bounds"][3] - 1]
                   for panel in panels},
        "warnings": warnings,
    }
