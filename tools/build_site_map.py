"""
Builds the site picker's offline map data (web/src/lib/map/) from public-domain / CC-BY sources:

  * Natural Earth 1:10m state and province borders, lakes and rivers (public domain)
    https://github.com/nvkelso/natural-earth-vector (geojson/), saved in tools/data/
  * GeoNames cities1000 (every place with 1000+ people, CC BY 4.0), with countryInfo.txt and admin1CodesASCII.txt
    https://download.geonames.org/export/dump/, saved in tools/data/

Coastlines and country borders come from the world-atlas package (Natural Earth 1:10m) at run time.

Lines are thinned to what the map can show (points about 200 m apart) and stored as whole numbers of 1/1000°:
the first point, then differences, which keeps the files small.

    python tools/build_site_map.py
"""
import csv
import io
import json
import math
import pathlib
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent
DATA = ROOT / "data"
OUT = ROOT.parent / "web" / "src" / "lib" / "map"

SCALE = 1000          # coordinates in 1/1000 degree (~100 m)
TOLERANCE = 0.002     # Douglas-Peucker tolerance in degrees (~200 m)

# GeoNames feature codes that aren't places to live: sections of a city, historical, abandoned, destroyed.
SKIP_CODES = {"PPLX", "PPLH", "PPLQ", "PPLW", "PPLCH"}


def simplify(points, tolerance):
    """Douglas-Peucker, iterative."""
    if len(points) < 3:
        return points
    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]
    while stack:
        a, b = stack.pop()
        ax, ay = points[a]
        bx, by = points[b]
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        worst, index = 0.0, -1
        for i in range(a + 1, b):
            px, py = points[i]
            d = abs(dy * (px - ax) - dx * (py - ay)) / length if length else math.hypot(px - ax, py - ay)
            if d > worst:
                worst, index = d, i
        if worst > tolerance and index > 0:
            keep[index] = True
            stack.append((a, index))
            stack.append((index, b))
    return [p for p, k in zip(points, keep) if k]


def encode(points):
    """[[lon, lat], …] → [x0, y0, dx1, dy1, …] in 1/1000°, dropping repeats."""
    out = []
    px = py = None
    for lon, lat in points:
        x, y = round(lon * SCALE), round(lat * SCALE)
        if px is None:
            out += [x, y]
        elif (x, y) != (px, py):
            out += [x - px, y - py]
        else:
            continue
        px, py = x, y
    return out if len(out) >= 4 else None


def lines_of(path, polygons=False, keep=lambda props: True):
    geo = json.loads(path.read_text(encoding="utf-8"))
    out = []
    for feature in geo["features"]:
        if not keep(feature.get("properties") or {}):
            continue
        g = feature.get("geometry")
        if not g:
            continue
        kind, coords = g["type"], g["coordinates"]
        if kind == "LineString":
            parts = [coords]
        elif kind == "MultiLineString":
            parts = coords
        elif kind == "Polygon":
            parts = coords
        elif kind == "MultiPolygon":
            parts = [ring for polygon in coords for ring in polygon]
        else:
            continue
        for part in parts:
            pts = simplify([(p[0], p[1]) for p in part], TOLERANCE)
            if polygons and len(pts) < 4:
                continue
            encoded = encode(pts)
            if encoded:
                out.append(encoded)
    return out


def rank(population):
    """0 for the biggest cities … 12 for villages: which zoom level a place first shows at."""
    for r, at_least in enumerate([5_000_000, 2_000_000, 1_000_000, 500_000, 250_000, 100_000, 50_000, 20_000, 10_000, 5_000, 2_500, 1_500]):
        if population >= at_least:
            return r
    return 12


def places():
    countries = {}
    for line in (DATA / "countryInfo.txt").read_text(encoding="utf-8").splitlines():
        if line.startswith("#") or not line.strip():
            continue
        f = line.split("\t")
        countries[f[0]] = f[4]
    admin1 = {}
    for line in (DATA / "admin1CodesASCII.txt").read_text(encoding="utf-8").splitlines():
        f = line.split("\t")
        if len(f) >= 2:
            admin1[f[0]] = f[1]

    regions, region_index, rows = [], {}, []
    with zipfile.ZipFile(DATA / "cities1000.zip") as z:
        text = io.TextIOWrapper(z.open("cities1000.txt"), encoding="utf-8")
        for f in csv.reader(text, delimiter="\t", quoting=csv.QUOTE_NONE):
            if f[6] != "P" or f[7] in SKIP_CODES:
                continue
            country = countries.get(f[8], f[8])
            state = admin1.get(f"{f[8]}.{f[10]}")
            region = f"{state}, {country}" if state and state != f[1] else country
            if region not in region_index:
                region_index[region] = len(regions)
                regions.append(region)
            population = int(f[14] or 0)
            rows.append((population, [f[1], region_index[region], round(float(f[4]), 3), round(float(f[5]), 3), rank(population)]))
    # Biggest first: the map shows the first few hundred in view, and search lists the biggest match first.
    rows.sort(key=lambda r: -r[0])
    return regions, [r[1] for r in rows]


def main():
    OUT.mkdir(parents=True, exist_ok=True)

    regions, rows = places()
    (OUT / "places.json").write_text(json.dumps({
        "source": "GeoNames cities1000 (CC BY 4.0, geonames.org), built by tools/build_site_map.py",
        "fields": ["name", "region (index into regions)", "lat", "lon", "rank (0 biggest … 12 smallest)"],
        "regions": regions,
        "places": rows,
    }, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    features = {
        "source": "Natural Earth 1:10m (public domain), built by tools/build_site_map.py",
        "encoding": "each line: x0, y0 then differences, in 1/1000 degree (x = longitude, y = latitude)",
        "states": lines_of(DATA / "ne_10m_admin_1_states_provinces_lines.geojson"),
        "lakes": lines_of(DATA / "ne_10m_lakes.geojson", polygons=True) + lines_of(DATA / "ne_10m_lakes_north_america.geojson", polygons=True),
        "rivers": lines_of(DATA / "ne_10m_rivers_lake_centerlines.geojson") + lines_of(DATA / "ne_10m_rivers_north_america.geojson"),
    }
    (OUT / "features.json").write_text(json.dumps(features, separators=(",", ":")), encoding="utf-8")

    for name in ("places.json", "features.json"):
        print(f"{name}: {(OUT / name).stat().st_size / 1e6:.1f} MB")
    print(f"{len(rows)} places in {len(regions)} regions; "
          f"{len(features['states'])} border lines, {len(features['lakes'])} lake outlines, {len(features['rivers'])} rivers")


if __name__ == "__main__":
    main()
