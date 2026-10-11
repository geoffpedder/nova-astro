"""Builds the sky chart's constellation figures (web/src/lib/constellations.json) from d3-celestial.

    python tools/build_constellations.py tools/data

Inputs (in the given folder), from https://github.com/ofrohn/d3-celestial (data/):
  constellations.lines.json  Constellation lines (IAU charts, with modifications by Olaf Frohn)
  constellations.json        Names and label positions
d3-celestial is Copyright (c) 2015, Olaf Frohn, released under the BSD 3-Clause licence; the notice is kept in
the output and in the README.

Output: {"notice": ..., "lines": {"And": [[[ra_hours, dec], ...], ...]}, "names": [{"id", "name", "ra", "dec", "rank"}]}
GeoJSON longitudes (-180..180 degrees) become RA hours (0..24).
"""
import json
import pathlib
import sys

NOTICE = ("Constellation lines and names from d3-celestial, Copyright (c) 2015, Olaf Frohn, BSD 3-Clause licence "
          "(https://github.com/ofrohn/d3-celestial); lines after the IAU constellation charts.")


def ra_hours(lon: float) -> float:
    return round((lon + 360) % 360 / 15, 5)


def main(folder: pathlib.Path) -> None:
    lines_doc = json.loads((folder / "d3-lines.json").read_text(encoding="utf-8"))
    names_doc = json.loads((folder / "d3-names.json").read_text(encoding="utf-8"))
    lines = {}
    for feature in lines_doc["features"]:
        lines[feature["id"]] = [[[ra_hours(lon), round(lat, 4)] for lon, lat in strand]
                                for strand in feature["geometry"]["coordinates"]]
    names = []
    for feature in names_doc["features"]:
        lon, lat = feature["geometry"]["coordinates"]
        p = feature["properties"]
        names.append({"id": feature["id"], "name": p["name"], "ra": ra_hours(lon), "dec": round(lat, 3), "rank": int(p.get("rank", 3))})
    target = pathlib.Path(__file__).resolve().parent.parent / "web/src/lib/constellations.json"
    target.write_text(json.dumps({"notice": NOTICE, "lines": lines, "names": names}, separators=(",", ":")), encoding="utf-8")
    print(f"{len(lines)} figures, {len(names)} names -> {target} ({target.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main(pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "tools/data"))
