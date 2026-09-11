#!/usr/bin/env python3
"""Structured, file-based mapping tools for agents. Run --help for the CLI."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import sys
import uuid

import yaml

from map_document import MappingDocument, dump_yaml, load_yaml
from catalog import PrototypeCatalog


ROOT = Path(__file__).resolve().parents[3]


def atomic_text(path: Path, text: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as writer:
            writer.write(text)
            writer.flush()
            os.fsync(writer.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


@contextmanager
def locked(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    lock = path.with_name(path.name + ".lock")
    try:
        descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError as exc:
        raise ValueError(f"Session is locked by another operation: {lock}") from exc
    try:
        with os.fdopen(descriptor, "w") as stream:
            stream.write(str(os.getpid()))
        yield
    finally:
        lock.unlink()


def session_read(path: Path):
    session = read_json(path)
    if not isinstance(session, dict):
        raise ValueError("Session must be a JSON object")
    if session.get("format") != 1:
        raise ValueError("Unsupported mapping session version")
    document = MappingDocument(load_yaml(session["document"]))
    document.restore_state(session.get("state", {}))
    return session, document


def session_write(path: Path, session: dict, document: MappingDocument):
    session.update(format=1, document=dump_yaml(document.data), state=document.state)
    atomic_text(path, json.dumps(session, ensure_ascii=False, indent=2) + "\n")


def session_new(document: MappingDocument, source: Path | None = None):
    session = {"format": 1, "id": uuid.uuid4().hex, "checkpoints": {}, "exports": {}}
    if source:
        source = source.resolve()
        session["source"] = str(source)
        session["exports"][str(source)] = hashlib.sha256(source.read_bytes()).hexdigest()
    return session


def export_document(session: dict, document: MappingDocument, destination: Path, session_path: Path):
    """Journal the export before changing either file, so interrupted writes can retry."""
    destination = destination.resolve()
    if destination == session_path.resolve():
        raise ValueError("Map output and session must use different paths")
    actual = None
    pending = session.get("pendingExports", {}).get(str(destination), {})
    if destination.exists():
        expected = session.get("exports", {}).get(str(destination))
        actual = hashlib.sha256(destination.read_bytes()).hexdigest()
        if actual not in (expected, pending.get("before"), pending.get("after")):
            raise ValueError(f"Destination already exists or changed outside this session: {destination}. Open it first or export to a new path.")
    document.validate()
    document.data["meta"]["entityCount"] = sum(1 for _ in document.iter_entities())
    content = dump_yaml(document.data)
    planned = hashlib.sha256(content.encode("utf-8")).hexdigest()
    # If saving the output or the final receipt fails, this record accepts either
    # the old file or the exact planned file on retry; unrelated edits still fail.
    session.setdefault("pendingExports", {})[str(destination)] = {"before": actual, "after": planned}
    session_write(session_path, session, document)
    if actual != planned:
        atomic_text(destination, content)
    session.setdefault("exports", {})[str(destination)] = planned
    session["pendingExports"].pop(str(destination))
    session_write(session_path, session, document)
    return {"output": str(destination), "sha256": session["exports"][str(destination)],
            "revision": document.revision}


def catalog_for(args):
    return PrototypeCatalog(args.resources.resolve())


def validate_operations(operations, catalog):
    if not isinstance(operations, list) or any(not isinstance(operation, dict) for operation in operations):
        raise ValueError("operations must be a list of JSON objects")
    for operation in operations:
        if operation.get("op") == "spawn":
            prototype = operation.get("prototype")
            if not isinstance(prototype, str) or not catalog.has(prototype):
                raise ValueError(f"Unknown entity prototype: {prototype!r}")
            if catalog.describe(prototype)["abstract"]:
                raise ValueError(f"Cannot spawn abstract entity prototype: {prototype}")
        if operation.get("op") == "tiles":
            tile = operation.get("tile")
            if not isinstance(tile, str) or not catalog.has(tile, kind="tile"):
                raise ValueError(f"Unknown tile prototype: {tile!r}")


def execute(args):
    if args.command == "catalog":
        catalog = catalog_for(args)
        return catalog.describe(args.id, kind=args.kind) if args.id else catalog.search(args.query, kind=args.kind, limit=args.limit)
    if args.command == "engine-check":
        from engine import check_with_engine
        return check_with_engine(args.source, ROOT, args.output, args.seconds, args.timeout,
                                 read_json(args.requirements) if args.requirements else {})
    if args.command == "validate-file":
        from validation import validate_document
        return validate_document(MappingDocument.from_file(args.source), catalog_for(args),
                                 read_json(args.requirements) if args.requirements else {})
    if args.command == "render-file":
        from render import render_document
        return render_document(MappingDocument.from_file(args.source).data, args.output,
                               catalog_for(args), grid=args.grid, bounds=args.bounds)

    path = args.session.resolve()
    with locked(path):
        if args.command in ("create", "open", "recipe"):
            if path.exists():
                raise ValueError(f"Session already exists: {path}")
            if args.command == "open":
                document = MappingDocument.from_file(args.source)
                session = session_new(document, args.source)
            else:
                recipe = read_json(args.recipe) if args.command == "recipe" else {}
                if not isinstance(recipe, dict):
                    raise ValueError("Recipe must be a JSON object")
                document = MappingDocument.create(
                    category=recipe.get("category", getattr(args, "category", "Grid")),
                    name=recipe.get("name", getattr(args, "name", "")),
                    grid_components=recipe.get("grid_components", []))
                session = session_new(document)
                if args.command == "recipe":
                    operations = recipe.get("operations", [])
                    validate_operations(operations, catalog_for(args))
                    document.apply_batch(operations, expected_revision=document.revision,
                                         operation_id="recipe:" + hashlib.sha256(args.recipe.read_bytes()).hexdigest())
                    session["requirements"] = recipe.get("requirements", {})
            result = {"session": str(path), "revision": document.revision, "grids": document.grid_ids}
            if args.command == "recipe" and args.output:
                result.update(export_document(session, document, args.output, path))
            else:
                session_write(path, session, document)
            return result

        session, document = session_read(path)
        result = {"session": str(path), "revision": document.revision}
        if args.command == "inspect":
            result.update(document.inspect(grid=args.grid, bounds=args.bounds))
        elif args.command == "apply":
            batch = read_json(args.batch)
            if not isinstance(batch, (dict, list)):
                raise ValueError("Batch must be an object with operations or a list of operations")
            operations = batch if isinstance(batch, list) else batch["operations"]
            revision = args.expected_revision or (batch.get("expected_revision") if isinstance(batch, dict) else None)
            operation_id = args.operation_id or (batch.get("operation_id") if isinstance(batch, dict) else None)
            if not revision or not operation_id:
                raise ValueError("apply requires expected_revision and operation_id in the batch or command arguments")
            validate_operations(operations, catalog_for(args))
            result.update(document.apply_batch(operations, expected_revision=revision, operation_id=operation_id))
            session_write(path, session, document)
        elif args.command == "export":
            result.update(export_document(session, document, args.output, path))
        elif args.command == "checkpoint":
            if args.name in session["checkpoints"]:
                raise ValueError("Checkpoint name already exists")
            session["checkpoints"][args.name] = {"document": dump_yaml(document.data), "state": document.state}
            session_write(path, session, document)
            result["checkpoint"] = args.name
        elif args.command == "restore":
            if not args.expected_revision or args.expected_revision != document.revision:
                raise ValueError("restore requires the current expected_revision")
            checkpoint = session["checkpoints"][args.name]
            document = MappingDocument(load_yaml(checkpoint["document"]))
            document.restore_state(checkpoint["state"])
            session_write(path, session, document)
            result.update(revision=document.revision, restored=args.name)
        elif args.command == "validate":
            from validation import validate_document
            requirements = read_json(args.requirements) if args.requirements else session.get("requirements", {})
            result.update(validate_document(document, catalog_for(args), requirements))
        elif args.command == "render":
            from render import render_document
            result.update(render_document(document.data, args.output, catalog_for(args),
                                          grid=args.grid, bounds=args.bounds))
        return result


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--resources", type=Path, default=ROOT / "Resources")
    commands = result.add_subparsers(dest="command", required=True)
    for name in ("create", "open", "recipe", "inspect", "apply", "export", "checkpoint", "restore", "validate", "render"):
        command = commands.add_parser(name)
        command.add_argument("--session", type=Path, required=True, help="Path to a tool session JSON, outside Resources")
        if name == "create":
            command.add_argument("--category", choices=("Grid", "Map"), default="Grid")
            command.add_argument("--name", default="")
        if name == "open":
            command.add_argument("source", type=Path)
        if name == "recipe":
            command.add_argument("recipe", type=Path)
            command.add_argument("--output", type=Path)
        if name in ("export", "render"):
            command.add_argument("--output", type=Path, required=True)
        if name in ("inspect", "render"):
            command.add_argument("--grid", type=int)
            command.add_argument("--bounds", type=int, nargs=4)
        if name == "apply":
            command.add_argument("--batch", type=Path, required=True)
            command.add_argument("--expected-revision")
            command.add_argument("--operation-id")
        if name in ("checkpoint", "restore"):
            command.add_argument("--name", required=True)
        if name == "restore":
            command.add_argument("--expected-revision", required=True)
        if name == "validate":
            command.add_argument("--requirements", type=Path)
    command = commands.add_parser("catalog")
    command.add_argument("query", nargs="?", default="")
    command.add_argument("--id")
    command.add_argument("--kind", choices=("entity", "tile"), default="entity")
    command.add_argument("--limit", type=int, default=20)
    command = commands.add_parser("engine-check")
    command.add_argument("source", type=Path)
    command.add_argument("--output", type=Path, required=True, help="New directory for an isolated run, logs and results")
    command.add_argument("--seconds", type=float, default=10)
    command.add_argument("--timeout", type=float, default=180)
    command.add_argument("--requirements", type=Path, help="Requirements JSON, or a complete recipe containing requirements")
    command = commands.add_parser("validate-file")
    command.add_argument("source", type=Path)
    command.add_argument("--requirements", type=Path)
    command = commands.add_parser("render-file")
    command.add_argument("source", type=Path)
    command.add_argument("--output", type=Path, required=True)
    command.add_argument("--grid", type=int)
    command.add_argument("--bounds", type=int, nargs=4)
    return result


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    try:
        result = execute(parser().parse_args())
        print(json.dumps(result, ensure_ascii=False, indent=2, default=str))
        return 1 if isinstance(result, dict) and result.get("passed") is False else 0
    except (ValueError, KeyError, TypeError, AttributeError, OSError, yaml.YAMLError) as exc:
        print(json.dumps({"error": str(exc), "type": type(exc).__name__}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
