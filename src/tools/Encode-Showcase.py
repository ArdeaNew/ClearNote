"""Encode the offscreen showcase frames. Requires Pillow; not used by the app."""

import argparse
import json
from pathlib import Path

from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("render_directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    manifest = json.loads((args.render_directory / "animation.json").read_text())
    frames = []
    durations = []
    size = (manifest["width"], manifest["height"])
    scale = manifest.get("sourceScale", 1)
    for entry in manifest["frames"]:
        with Image.open(args.render_directory / "frames" / entry["file"]) as source:
            if source.size != (size[0] * scale, size[1] * scale):
                raise ValueError(f"Unexpected frame size: {entry['file']}")
            rendered = source.convert("RGB")
            if scale != 1:
                rendered = rendered.resize(size, Image.Resampling.LANCZOS)
            frames.append(rendered.quantize(
                colors=256, method=Image.Quantize.MEDIANCUT,
                dither=Image.Dither.NONE))
        durations.append(entry["durationMs"])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    frames[0].save(args.output, save_all=True, append_images=frames[1:],
                   duration=durations, loop=manifest["repeat"],
                   optimize=True, disposal=1)
    with Image.open(args.output) as result:
        if result.n_frames != len(frames):
            raise ValueError("The GIF lost animation frames")
    print(f"{len(frames)} frames; {sum(durations)} ms; {args.output.stat().st_size} bytes")


if __name__ == "__main__":
    main()
