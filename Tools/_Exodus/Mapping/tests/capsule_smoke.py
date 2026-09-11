"""Exercise capsule power, normal helm admission and two occupants with a prebuilt server.

Run directly; this deliberately is not discovered by unittest. No build is performed.
The server only listens on loopback and owns a new data directory. Keyboard input is
not simulated: Pilot.HeldButtons is read-only in the existing console VV interface.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import sys
import uuid

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from engine import EngineError, EngineSession, _errors


REPOSITORY = Path(__file__).resolve().parents[4]


def smoke(source: Path, output: Path, seconds: int = 60) -> dict:
    if seconds < 30 or seconds > 300:
        raise ValueError("Occupant simulation must last between 30 and 300 seconds")
    output = output.resolve()
    if output.exists():
        raise ValueError(f"Output directory already exists: {output}")
    source = source.resolve()
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    report = {
        "source": str(source), "source_sha256": source_hash,
        "occupant_seconds": seconds, "passed": False,
        "controls_exercised": False, "commands": [],
        "limitations": [
            "No keyboard strafe, rotation, braking or FTL input was exercised.",
            "Pilot.HeldButtons is read-only through the existing VV console API.",
            "The docking door remained closed during this smoke test.",
        ],
    }
    try:
        with EngineSession(REPOSITORY, output) as engine:
            def command(text: str) -> str:
                # The Windows server's stdin uses OEM encoding. Exec reads UTF-8 files.
                # Its commands are deferred, so wait for a marker INSIDE that file.
                if any(ord(char) > 127 for char in text):
                    marker = "CAPSULE_UNICODE_" + uuid.uuid4().hex
                    filename = marker + ".cfg"
                    (engine.directory / "data" / filename).write_text(
                        text + "\necho " + marker + "\n", encoding="utf-8")
                    result = engine.command("exec " + filename)
                    if marker not in result:
                        result += engine._until(marker, engine.timeout)
                else:
                    result = engine.command(text)
                report["commands"].append({"command": text, "output": result})
                return result

            def read(uid: int, path: str):
                result = command(f"vvread /entity/{uid}/{path}")
                payload = "\n".join(line for line in result.splitlines()
                                    if line and not line.startswith(("[", "MAPPING_", "CAPSULE_")))
                return yaml.safe_load(payload)

            def find(component: str):
                return [(int(local), int(net)) for local, net in
                        re.findall(r"\((\d+)/n(\d+)", command("entities with " + component))]

            def require(condition: bool, message: str):
                if not condition:
                    raise EngineError(message)

            def consumer_power(grid: int):
                consumers = []
                for local, net in find("ApcPowerReceiver"):
                    if read(local, "Transform/GridUid") != grid:
                        continue
                    consumer = {"entity": local, "net_entity": net,
                                "powered": read(local, "ApcPowerReceiver/Powered"),
                                "needs_power": read(local, "ApcPowerReceiver/NeedsPower"),
                                "load": read(local, "ApcPowerReceiver/Load")}
                    require(consumer["powered"] is True and consumer["needs_power"] is True,
                            f"Consumer {local} is unpowered or bypasses electrical power")
                    consumers.append(consumer)
                require(len(consumers) >= 10, "Not all ten capsule power consumers were found")
                return consumers

            shutil.copyfile(source, engine.directory / "data/input.yml")
            command("addmap 100 true")
            command("loadgrid 100 input.yml 0 0 0 true")
            command("mapinit 100")
            engine.settle(12)
            grids = find("MapGrid")
            require(len(grids) == 1, "Expected exactly one capsule grid")
            grid, _ = grids[0]
            helm, helm_net = find("ShuttleConsole")[0]
            scrubber, _ = find("PortableScrubber")[0]
            report["linear_thrust"] = read(grid, "Shuttle/LinearThrust")
            report["angular_thrust"] = read(grid, "Shuttle/AngularThrust")
            require(len(report["linear_thrust"]) == 4 and min(report["linear_thrust"]) > 0,
                    "The four movement directions do not all have enabled thrusters")
            require(report["angular_thrust"] > 0, "The gyroscope is not enabled")
            report["power_before"] = consumer_power(grid)

            seats = {str(read(local, "Transform/LocalPosition")): (local, net)
                     for local, net in find("Strap")}
            occupants = []
            for position in ("2.5,3.5", "3.5,2.5"):
                seat, seat_net = seats[position]
                before = set(find("Respirator"))
                command(f"spawn MobHuman {seat_net}")
                created = set(find("Respirator")) - before
                require(len(created) == 1, "Human did not spawn")
                person, person_net = created.pop()
                command(f'invokeverb {person_net} {seat_net} "Вы"')
                require(read(person, "Buckle/BuckledTo") == seat, "Occupant could not buckle into the seat")
                occupants.append((person, person_net))

            pilot, pilot_net = occupants[0]
            command(f'invokeverb {pilot_net} {helm_net} "Переключить интерфейс"')
            require(read(pilot, "Pilot/Console") == helm,
                    "Normal console activation did not admit the human as pilot")
            report["helm_admission"] = True

            samples = []
            remaining = seconds
            while remaining:
                elapsed = min(15, remaining)
                engine.settle(elapsed)
                remaining -= elapsed
                sample = {"seconds": seconds - remaining, "occupants": []}
                for person, _ in occupants:
                    state = {"entity": person, "mob_state": read(person, "MobState/CurrentState"),
                             "suffocation_cycles": read(person, "Respirator/SuffocationCycles")}
                    require(state["mob_state"] == "Alive" and state["suffocation_cycles"] == 0,
                            f"Occupant {person} did not remain alive and breathing")
                    sample["occupants"].append(state)
                samples.append(sample)
                print(f"Occupants alive and breathing after {seconds - remaining}s", file=sys.stderr, flush=True)
            report["breathing_samples"] = samples
            report["scrubber_air"] = read(scrubber, "PortableScrubber/Air")
            require(report["scrubber_air"].get("moles", {}).get("CarbonDioxide", 0) > 0,
                    "The scrubber did not collect carbon dioxide from the occupants")
            report["power_after"] = consumer_power(grid)
            require(read(pilot, "Pilot/Console") == helm, "Pilot lost the console during simulation")
            report["startup_errors"] = _errors(engine.startup_lines)
            report["scenario_errors"] = _errors(engine.lines[len(engine.startup_lines):])
            require(not report["scenario_errors"], "The server logged errors during the scenario")
            # Humans have save:false and live pilot references cannot form a valid blueprint.
            # Diagnostics are VV reads; do not save this initialized scenario as a map.
            report["passed"] = True
    except (EngineError, OSError, KeyError, TypeError, ValueError) as exc:
        report["error"] = str(exc)
    if hashlib.sha256(source.read_bytes()).hexdigest() != source_hash:
        report["passed"] = False
        report["error"] = "Input source changed during the scenario"
    output.mkdir(parents=True, exist_ok=True)
    (output / "smoke-report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path, required=True, help="New directory for the isolated server")
    parser.add_argument("--seconds", type=int, default=60)
    args = parser.parse_args()
    try:
        result = smoke(args.source, args.output, args.seconds)
    except (OSError, ValueError) as exc:
        print(json.dumps({"passed": False, "error": str(exc)}))
        return 1
    print(json.dumps({key: value for key, value in result.items() if key != "commands"},
                     ensure_ascii=False, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
