"""Explicit blueprint checks; runtime behavior is reported separately by engine.py."""

from collections import Counter
import math

from map_document import get_component, iter_entities, parse_vector


def normalize_requirements(value):
    """Accept either a requirements object or a complete reproducible recipe."""
    if not isinstance(value, dict):
        raise ValueError("Requirements must be a JSON object")
    result = value.get("requirements", value)
    if not isinstance(result, dict):
        raise ValueError("requirements must be a JSON object")
    maximum = result.get("max_size")
    if maximum is not None and (not isinstance(maximum, list) or len(maximum) != 2 or
                               any(type(number) is not int or number <= 0 for number in maximum)):
        raise ValueError("max_size must contain two positive integer dimensions")
    cells = result.get("breathable_tiles", [])
    if not isinstance(cells, list) or any(not isinstance(cell, list) or len(cell) != 2 or
                                        any(type(number) is not int for number in cell) for cell in cells):
        raise ValueError("breathable_tiles must contain pairs of integer coordinates")
    prototypes = result.get("required_prototypes", {})
    if not isinstance(prototypes, dict) or any(not isinstance(proto, str) or type(count) is not int or count < 1
                                              for proto, count in prototypes.items()):
        raise ValueError("required_prototypes must map prototype IDs to positive integer counts")
    return result


def atmosphere_tiles(component):
    """Decode TileAtmosCollectionSerializer's saved mixtures by local tile."""
    result = {}
    version = component.get("version", 1)
    payload = (component.get("data") or {}) if version == 2 else component
    mixtures = payload.get("uniqueMixes") or []
    def integer(value):
        if type(value) is int:
            return value
        if isinstance(value, str):
            try:
                return int(value)
            except ValueError:
                pass
        raise ValueError("Atmosphere indices, masks and sizes must be integers")

    def coordinates_at(value):
        coordinates = parse_vector(value)
        if any(number != int(number) for number in coordinates):
            raise ValueError("Atmosphere coordinates must be integers")
        return tuple(map(int, coordinates))

    def mixture_at(index):
        index = integer(index)
        if not 0 <= index < len(mixtures):
            raise ValueError(f"Atmosphere mixture index {index} is out of range")
        return mixtures[index]

    if version == 1:
        for coordinates, index in (payload.get("tiles") or {}).items():
            result[coordinates_at(coordinates)] = mixture_at(index)
    elif version == 2:
        size = integer(payload.get("chunkSize", 4))
        if size < 1 or size > 5:
            raise ValueError("Atmosphere bitmask chunk size is unsupported")
        for coordinates, assignments in (payload.get("tiles") or {}).items():
            cx, cy = coordinates_at(coordinates)
            for index, mask in assignments.items():
                mixture = mixture_at(index)
                mask = integer(mask)
                if not 0 <= mask < (1 << (size * size)):
                    raise ValueError("Atmosphere bitmask exceeds its chunk bounds")
                for bit in range(size * size):
                    if mask & (1 << bit):
                        key = (cx * size + bit % size, cy * size + bit // size)
                        if key in result:
                            raise ValueError(f"Atmosphere tile {key} occurs in multiple mixtures")
                        result[key] = mixture
    else:
        raise ValueError(f"Unsupported atmosphere serialization version {version}")
    return result


def mixture_summary(mixture):
    moles = mixture.get("moles", {})
    if isinstance(moles, list):
        # Legacy Gas enum order in Content.Shared/Atmos/Atmospherics.cs.
        names = ("Oxygen", "Nitrogen", "CarbonDioxide", "Plasma", "Tritium", "WaterVapor",
                 "Ammonia", "NitrousOxide", "Frezon", "BZ", "Healium", "Nitrium", "Pluoxium")
        # Older serializers wrote the SIMD-padded array (currently 16 slots).
        if any(float(amount) != 0 for amount in moles[len(names):]):
            raise ValueError("Legacy gas array contains unknown gas IDs")
        moles = dict(zip(names, moles))
    temperature = float(mixture.get("temperature", 0))
    volume = float(mixture.get("volume", 2500))
    if volume <= 0 or not math.isfinite(volume):
        raise ValueError("Gas mixture volume must be finite and positive")
    amounts = {key: float(value) for key, value in moles.items()}
    if any(value < 0 or not math.isfinite(value) for value in amounts.values()):
        raise ValueError("Gas quantities must be finite and nonnegative")
    if not math.isfinite(temperature) or temperature < 0 or (temperature == 0 and sum(amounts.values()) > 0):
        raise ValueError("Gas temperature must be positive, or zero for an empty mixture")
    factor = 8.314462618 * temperature / volume
    return {"temperature_k": temperature, "pressure_kpa": sum(amounts.values()) * factor,
            "oxygen_kpa": amounts.get("Oxygen", 0) * factor,
            "carbon_dioxide_kpa": amounts.get("CarbonDioxide", 0) * factor,
            "other_gases_kpa": sum(value for gas, value in amounts.items()
                                   if gas not in ("Oxygen", "Nitrogen", "CarbonDioxide")) * factor,
            "moles": amounts}


def breathable(summary):
    """Conservative human-cabin check, not a replacement for game respiration."""
    return (80 <= summary["pressure_kpa"] <= 120 and 16 <= summary["oxygen_kpa"] <= 30
            and 273 <= summary["temperature_k"] <= 313 and summary["carbon_dioxide_kpa"] <= 0.5
            and summary["other_gases_kpa"] <= 0.01)


def validate_document(document, catalog, requirements=None):
    requirements = normalize_requirements(requirements if requirements is not None else {})
    document.validate()
    errors, warnings, grids = [], [], []
    if requirements.get("breathable_tiles") and not document.grid_ids:
        errors.append({"code": "missing_required_grid"})
    counts = Counter()
    for prototype, entity in document.iter_entities():
        if prototype:
            counts[prototype] += 1
            if not catalog.has(prototype):
                errors.append({"code": "unknown_prototype", "entity": entity["uid"], "prototype": prototype})
    for tile in set(document.data["tilemap"].values()):
        if not catalog.has(tile, kind="tile"):
            errors.append({"code": "unknown_tile", "tile": tile})
    for prototype, minimum in requirements.get("required_prototypes", {}).items():
        if counts[prototype] < minimum:
            errors.append({"code": "required_prototype", "prototype": prototype,
                           "expected_minimum": minimum, "actual": counts[prototype]})
    for grid in document.grid_ids:
        inspection = document.inspect(grid)
        tiles = inspection["tiles"]
        if not tiles:
            warnings.append({"code": "empty_grid", "grid": grid})
            for cell in requirements.get("breathable_tiles", []):
                errors.append({"code": "missing_initial_air", "grid": grid, "cell": cell})
            continue
        x0, x1 = min(tile["x"] for tile in tiles), max(tile["x"] for tile in tiles)
        y0, y1 = min(tile["y"] for tile in tiles), max(tile["y"] for tile in tiles)
        size = [x1 - x0 + 1, y1 - y0 + 1]
        maximum = requirements.get("max_size")
        if maximum and any(value > limit for value, limit in zip(size, maximum)):
            errors.append({"code": "maximum_grid_size", "grid": grid, "actual": size, "maximum": maximum})
        occupied = {(tile["x"], tile["y"]) for tile in tiles}
        duplicates = set()
        for entity in inspection["entities"]:
            cell = (math.floor(entity["x"]), math.floor(entity["y"]))
            if entity["anchored"] is True and cell not in occupied:
                warnings.append({"code": "anchored_without_tile", "entity": entity["uid"], "cell": cell})
            # Different entity types may intentionally share a tile (cables, wall attachments).
            key = (entity["prototype"], entity["x"], entity["y"], entity["rotation"])
            if key in duplicates:
                warnings.append({"code": "duplicate_placement", "entity": entity["uid"], "prototype": entity["prototype"], "cell": cell})
            duplicates.add(key)
        air = get_component(document.get_entity(grid), "GridAtmosphere")
        air_tiles = atmosphere_tiles(air) if air else {}
        summaries = []
        for coordinates in requirements.get("breathable_tiles", []):
            cell = tuple(coordinates)
            mixture = air_tiles.get(cell)
            if mixture is None:
                errors.append({"code": "missing_initial_air", "grid": grid, "cell": cell})
                continue
            summary = mixture_summary(mixture)
            summaries.append({"cell": cell, **summary})
            if not breathable(summary):
                errors.append({"code": "initial_air_outside_requested_range", "grid": grid, "cell": cell, **summary})
        grids.append({"grid": grid, "bounds": [x0, y0, x1, y1], "size": size,
                      "tiles": len(tiles), "entities": len(inspection["entities"]),
                      "initial_air_tiles": len(air_tiles), "air_checks": summaries})
    return {"passed": not errors, "mode": "blueprint", "errors": errors, "warnings": warnings,
            "grids": grids, "prototype_counts": dict(sorted(counts.items())),
            "not_checked": ["C# field schemas and defaults", "Power network operation", "Hull airtightness",
                            "Piloting and movement", "Game round setup", "Server performance"]}


def snapshot_atmosphere(data):
    result = []
    for _, entity in iter_entities(data):
        air = get_component(entity, "GridAtmosphere")
        if air:
            result.append({"grid": entity["uid"], "tiles": [
                {"cell": cell, **mixture_summary(mixture)}
                for cell, mixture in sorted(atmosphere_tiles(air).items())]})
    return result


def validate_simulation(data, requirements=None):
    """Read actual serialized gases and power flow after an isolated engine run."""
    requirements = normalize_requirements(requirements if requirements is not None else {})
    errors, grids, power = [], [], []
    for _, entity in iter_entities(data):
        if get_component(entity, "MapGrid") is None:
            continue
        air = atmosphere_tiles(get_component(entity, "GridAtmosphere") or {})
        checks = []
        for coordinates in requirements.get("breathable_tiles", []):
            cell = tuple(coordinates)
            if cell not in air:
                errors.append({"code": "missing_simulated_air", "grid": entity["uid"], "cell": cell})
                continue
            summary = mixture_summary(air[cell])
            checks.append({"cell": cell, **summary})
            if not breathable(summary):
                errors.append({"code": "simulated_air_outside_requested_range", "grid": entity["uid"],
                               "cell": cell, **summary})
        grids.append({"grid": entity["uid"], "air_checks": checks})
    for prototype, entity in iter_entities(data):
        network = get_component(entity, "PowerNetworkBattery")
        if network is not None:
            power.append({"entity": entity["uid"], "prototype": prototype,
                          "demand_w": network.get("loadingNetworkDemand", 0),
                          "receiving_w": network.get("currentReceiving", 0),
                          "supply_w": network.get("currentSupply", 0),
                          "battery_override": get_component(entity, "Battery")})
    if requirements.get("breathable_tiles") and not grids:
        errors.append({"code": "missing_simulated_grid"})
    return {"passed": not errors, "errors": errors, "grids": grids, "power_telemetry": power,
            "note": "Serialized power flow is telemetry; it does not assert every consumer is powered."}
