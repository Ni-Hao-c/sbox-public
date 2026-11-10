"""Summarize MeasureGpuPasses output; parent and child GPU scopes overlap.

Usage: python analyze-gpu-passes.py passes.json --output summary.json
Focus is retained as metadata. Timings are not rejected solely for background
execution on a setup where foreground/background rendering uses the same policy.
"""

import argparse
import collections
import json
import math
import statistics
from pathlib import Path


def stats(values):
    values = sorted(values)
    return {"mean": statistics.mean(values), "median": statistics.median(values),
            "p95": values[math.ceil(len(values) * 0.95) - 1]}


def summarize(segment):
    history = segment["ConsecutiveSamples"]
    if not history or any(b[0] - a[0] != 1 for a, b in zip(history, history[1:])):
        raise ValueError("Frame history must be nonempty and consecutive")
    if any(not math.isfinite(v) for row in history for v in row):
        raise ValueError("Nonfinite frame history")
    frames, seen = [], set()
    for snapshot in segment["Timelines"]:
        if snapshot["FrameId"] in seen:
            raise ValueError("Repeated completed GPU frame")
        seen.add(snapshot["FrameId"])
        rows = snapshot["Rows"]
        values = collections.defaultdict(float)
        for row in rows:
            if not row["Measured"]:
                continue
            duration = row["Duration"]
            if not math.isfinite(duration) or duration < 0:
                raise ValueError("Invalid GPU duration")
            values[row["Name"]] += duration
            if row["Name"] in ("Titanfall2StaticSceneMDI", "T2 cutout group", "T2 opaque group"):
                parent = row["Parent"]
                visited = set()
                while 0 <= parent < len(rows) and parent not in visited:
                    visited.add(parent)
                    name = rows[parent]["Name"]
                    kind = next((kind for kind in ("Opaque", "DepthPrepass", "Shadow")
                                 if f" {kind} slot " in name), None)
                    if kind:
                        label = "MDI" if row["Name"] == "Titanfall2StaticSceneMDI" else row["Name"][3:]
                        values[f"TF2 {kind} {label}"] += duration
                        break
                    parent = rows[parent]["Parent"]
        frames.append(values)
    if not frames:
        raise ValueError("No GPU snapshots")
    names = set().union(*(frame.keys() for frame in frames))
    return {
        "view": segment["View"], "mode": segment["Mode"], "focus": segment["Focus"],
        "position": segment["Position"], "rotation": segment["Rotation"],
        "scale": segment["RenderScale"], "vsync": segment["VSync"], "max_fps": segment["MaxFrameRate"],
        "gpu_snapshots": len(frames), "consecutive_frames": len(history),
        "timings": {name: stats([row[index] for row in history]) for name, index in
                    (("frame_ms", 1), ("gpu_latest_ms", 2), ("idle_ms", 3), ("render_ms", 4), ("physics_ms", 5))},
        "passes": {name: stats([frame.get(name, 0) for frame in frames]) for name in sorted(names)},
        "observed": {name: stats([row[name] for row in segment["Observed"]]) for name in
                     ("DrawCalls", "TrianglesRendered", "MaterialChanges", "AggregateIndirectSubmits", "AggregateIndirectFragments")},
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = {"source": str(args.capture.resolve()),
              "segments": [summarize(segment) for segment in json.loads(args.capture.read_text(encoding="utf-8-sig"))]}
    text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        args.output.write_text(text, encoding="utf-8")
    else:
        print(text)
