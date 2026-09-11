"""Exercise blueprints with an existing Robust server binary, without building it.

Every run owns a separate process, configuration, data directory and loopback port.
The editable document is never map-initialized. Simulation saves are diagnostics.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import queue
import re
import shutil
import socket
import subprocess
import threading
import time
import uuid


class EngineError(RuntimeError):
    pass


class EngineSession:
    def __init__(self, repository: Path, directory: Path, timeout: float = 180):
        self.repository = repository.resolve()
        self.directory = directory.resolve()
        self.timeout = timeout
        self.process = None
        self.lines: list[str] = []
        self.output: queue.Queue[str | None] = queue.Queue()
        self.log = None

    def __enter__(self):
        binary = self.repository / "bin/Content.Server/Content.Server.dll"
        if not binary.is_file():
            raise EngineError(f"Existing server binary not found: {binary}. Build it manually first.")
        self.directory.mkdir(parents=True, exist_ok=False)
        (self.directory / "data").mkdir()
        config = self.directory / "server.toml"
        config.write_text("# Exodus: isolated mapping validation server.\n", encoding="utf-8")
        self.log = (self.directory / "server.log").open("w", encoding="utf-8")
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        ready = "MAPPING_READY_" + uuid.uuid4().hex
        args = ["dotnet", str(binary), "--config-file", str(config),
                "--data-dir", str(self.directory / "data")]
        for value in (f"net.port={port}", "net.bindto=127.0.0.1", "status.enabled=false",
                      "hub.advertise=false", "game.dummyticker=true", "game.lobbyenabled=true",
                      "game.auto_pause_empty=false", "auth.mode=2", "log.enabled=false"):
            args.extend(["--cvar", value])
        args.append("+echo " + ready)
        try:
            self.process = subprocess.Popen(
                args, cwd=self.repository, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                bufsize=1, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            self.reader = threading.Thread(target=self._read, daemon=True)
            self.reader.start()
            self._until(ready, self.timeout)
            self.startup_lines = list(self.lines)
            return self
        except BaseException:
            self.close()
            raise

    def _read(self):
        try:
            for line in self.process.stdout:
                self.output.put(line)
        finally:
            self.output.put(None)

    def _consume(self, item):
        if item is None:
            raise EngineError(f"Server exited unexpectedly; see {self.directory / 'server.log'}")
        self.lines.append(item)
        self.log.write(item)
        self.log.flush()

    def _until(self, marker: str, timeout: float):
        deadline = time.monotonic() + timeout
        result = []
        while time.monotonic() < deadline:
            try:
                item = self.output.get(timeout=min(1, max(0.01, deadline - time.monotonic())))
            except queue.Empty:
                if self.process.poll() is not None:
                    raise EngineError(f"Server exited ({self.process.returncode}); see {self.directory / 'server.log'}")
                continue
            self._consume(item)
            result.append(item)
            if marker in item and "echo " + marker not in item:
                return "".join(result)
        raise EngineError(f"Timed out waiting for server; see {self.directory / 'server.log'}")

    def command(self, command: str):
        if "\n" in command or "\r" in command:
            raise ValueError("Console commands must be a single line")
        marker = "MAPPING_DONE_" + uuid.uuid4().hex
        self.process.stdin.write(command + "\necho " + marker + "\n")
        self.process.stdin.flush()
        return self._until(marker, self.timeout)

    def settle(self, seconds: float):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                self._consume(self.output.get(timeout=min(0.2, max(0.01, deadline - time.monotonic()))))
            except queue.Empty:
                if self.process.poll() is not None:
                    raise EngineError("Server stopped during simulation")

    def grids(self, map_id: int):
        # lsgrid prints EntityUid, whereas savegrid takes NetEntity. Join explicitly.
        listing = self.command("lsgrid")
        local_ids = {int(uid) for uid, mid in re.findall(r"(\d+): map: (\d+), ent:", listing)
                     if int(mid) == map_id}
        pretty = self.command("entities with MapGrid")
        return [int(net) for uid, net in re.findall(r"\((\d+)/n(\d+)", pretty)
                if int(uid) in local_ids]

    def close(self):
        if self.process is not None:
            if self.process.poll() is None:
                try:
                    self.process.stdin.write("shutdown\n")
                    self.process.stdin.flush()
                    self.process.wait(timeout=15)
                except (OSError, subprocess.TimeoutExpired):
                    self.process.kill()
                    self.process.wait(timeout=5)
            if hasattr(self, "reader"):
                self.reader.join(timeout=2)
            while not self.output.empty():
                line = self.output.get_nowait()
                if line is not None and self.log:
                    self.log.write(line)
            for stream in (self.process.stdin, self.process.stdout):
                if stream:
                    try:
                        stream.close()
                    except OSError:
                        pass
        if self.log:
            self.log.close()

    def __exit__(self, *_):
        self.close()


def _errors(lines):
    return [line.strip() for line in lines if re.search(r"\b(ERR|ERRO|ERROR|FTL|FATL|FATAL)\b", line)]


def check_with_engine(source: Path, repository: Path, output: Path, seconds: float = 10,
                      timeout: float = 180, requirements=None):
    from map_document import MappingDocument, load_yaml
    from validation import normalize_requirements, validate_simulation

    if not 0 <= seconds <= 600:
        raise ValueError("Simulation duration must be between 0 and 600 seconds")
    if not 0 < timeout <= 600:
        raise ValueError("Server command timeout must be between 0 and 600 seconds")
    requirements = normalize_requirements(requirements if requirements is not None else {})
    source = source.resolve()
    output = output.resolve()
    original_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    document = MappingDocument.from_file(source)
    category = document.data["meta"]["category"]
    if category not in ("Grid", "Map"):
        raise ValueError("Engine validation supports Map and Grid documents")
    # Reserve the run directory before starting; never overwrite another run's report.
    output.mkdir(parents=True, exist_ok=False)
    result = {"source": str(source), "source_sha256": original_hash, "category": category,
              "simulation_seconds": seconds, "passed": False, "controls_exercised": False,
              "directory": str(output), "startup_errors": {}, "map_errors": {}}
    try:
        # MapInit is one-way. Even paused entities can have content-side timers:
        # end the authoring process before running the independent simulation.
        with EngineSession(repository, output / "authoring", timeout) as engine:
            data = engine.directory / "data"
            shutil.copyfile(source, data / "input.yml")
            if category == "Grid":
                engine.command("addmap 100 true")
            engine.command(f"load{category.lower()} 100 input.yml 0 0 0 true")
            engine.settle(0.5)
            grids = engine.grids(100)
            if len(grids) != len(document.data.get("grids", [])):
                raise EngineError(f"Expected {len(document.data.get('grids', []))} grids, loaded {len(grids)}")
            save = f"savegrid {grids[0]}" if category == "Grid" else "savemap 100"
            engine.command(save + " blueprint.yml")
            blueprint = data / "blueprint.yml"
            canonical = MappingDocument.from_file(blueprint)
            state = engine.command("lsmap")
            if not re.search(r"100: .*init: False", state):
                raise EngineError("Authoring map unexpectedly initialized")
            result["authoring_map_initialized"] = False
            result["canonical_blueprint"] = str(blueprint)
            blueprint_hash = hashlib.sha256(blueprint.read_bytes()).hexdigest()
            result["startup_errors"]["authoring"] = _errors(engine.startup_lines)
            result["map_errors"]["authoring"] = _errors(engine.lines[len(engine.startup_lines):])

        with EngineSession(repository, output / "simulation", timeout) as engine:
            data = engine.directory / "data"
            shutil.copyfile(blueprint, data / "input.yml")
            if category == "Grid":
                engine.command("addmap 101 true")
            engine.command(f"load{category.lower()} 101 input.yml 0 0 0 true")
            engine.settle(0.5)
            if len(engine.grids(101)) != len(canonical.grid_ids):
                raise EngineError("Saved blueprint did not reload with the same grid count")
            result["roundtrip_loaded"] = True
            engine.command("mapinit 101")
            engine.settle(seconds)
            state = engine.command("lsmap")
            if not re.search(r"101: .*init: True, paused: False", state):
                raise EngineError("Simulation map did not become initialized and unpaused")
            engine.command("savemap 101 simulation.yml true")
            if not (data / "simulation.yml").is_file():
                raise EngineError("Simulation snapshot was not saved")
            result["authoring_unchanged"] = hashlib.sha256(blueprint.read_bytes()).hexdigest() == blueprint_hash
            result["simulation_snapshot"] = str(data / "simulation.yml")
            snapshot = load_yaml((data / "simulation.yml").read_text(encoding="utf-8"))
            result["simulation_checks"] = validate_simulation(snapshot, requirements)
            result["startup_errors"]["simulation"] = _errors(engine.startup_lines)
            result["map_errors"]["simulation"] = _errors(engine.lines[len(engine.startup_lines):])
            result["passed"] = (result["authoring_unchanged"] and result["simulation_checks"]["passed"]
                                and not any(result["map_errors"].values())
                                and not any(result["startup_errors"].values()))
            result["limitations"] = ["No player input or piloting was exercised.",
                                     "This is an isolated map simulation, not a full round or load test."]
    except (EngineError, OSError, ValueError) as exc:
        result["error"] = str(exc)
    if hashlib.sha256(source.read_bytes()).hexdigest() != original_hash:
        result["passed"] = False
        result["error"] = "Source changed during engine validation"
    (output / "report.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return result
