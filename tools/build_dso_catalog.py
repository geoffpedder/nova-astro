"""Builds NOVA's offline deep-sky catalog (src/Nova.Core/Catalogs/dso.tsv.gz).

    python tools/build_dso_catalog.py tools/data

Inputs (in the given folder):
  NGC.csv, addendum.csv  OpenNGC by Mattia Verga, https://github.com/mattiaverga/OpenNGC — CC BY-SA 4.0
                         (database_files/NGC.csv and database_files/addendum.csv)
  sh2.tsv                Sharpless (1959) catalogue of HII regions, VizieR VII/20, as tab-separated
                         _RAJ2000, _DEJ2000 (degrees), Sh2, Diam (arcmin):
                         https://vizier.cds.unistra.fr/viz-bin/asu-tsv?-source=VII/20&-out=Sh2&-out=Diam&-out.add=_RAJ2000,_DEJ2000&-out.form=dec&-out.max=1000

The extract is a derived database of OpenNGC, so it carries the same licence.

Output: gzipped, tab-separated, sorted by declination, one object per line:
    ra_hours  dec_degrees  major_arcmin  minor_arcmin  angle_degrees  magnitude  surface_brightness  type  label  names
Empty fields are blank. label is the most familiar designation (M 31, NGC 7000, IC 434, Sh2-129, …);
names lists every designation and common name, " · "-separated, for searching and display.
"""
import csv
import gzip
import pathlib
import re
import sys

# Stars, duplicates and objects that don't exist are no use as imaging targets.
SKIP_TYPES = {"*", "**", "Dup", "NonEx", "Nova", "Other"}

# Well-known Sharpless regions with no NGC/IC entry to carry a name.
SH2_NAMES = {
    101: "Tulip Nebula", 108: "Sadr Region", 112: "", 115: "", 119: "", 124: "", 126: "", 129: "Flying Bat Nebula",
    132: "Lion Nebula", 136: "Ghost Nebula", 157: "Lobster Claw Nebula",
    240: "Spaghetti Nebula (Simeis 147)", 261: "Lower's Nebula", 264: "Lambda Orionis Ring", 274: "Medusa Nebula",
    276: "Barnard's Loop", 308: "Dolphin Head Nebula",
}
SH2_NAMES = {k: v for k, v in SH2_NAMES.items() if v}

# Popular imaging targets whose OpenNGC entry lacks the familiar name (or the whole extent): extra names
# (a Sharpless number here means that region is this object) and a better size in arcminutes.
OVERRIDES = {
    "IC 1396": (["Elephant's Trunk Nebula", "Sh2-131"], (170, 140)),
    "IC 1805": (["Heart Nebula", "Sh2-190"], (150, 150)),
    "IC 1848": (["Soul Nebula", "Sh2-199"], (150, 75)),
    "NGC 2237": (["Rosette Nebula", "Sh2-275"], (80, 80)),
    "IC 2177": (["Seagull Nebula", "Sh2-292"], (120, 40)),
    "IC 410": (["Tadpoles Nebula", "Sh2-236"], None),
    "NGC 281": (["Pacman Nebula", "Sh2-184"], None),
    "IC 63": (["Ghost of Cassiopeia"], None),
    "M 101": (["Pinwheel Galaxy"], None),
    "IC 1318": (["Butterfly Nebula", "Sadr Region"], (180, 120)),
    "IC 2118": (["Witch Head Nebula"], (180, 60)),
    "IC 443": (["Jellyfish Nebula", "Sh2-248"], None),
    "NGC 6888": (["Sh2-105"], None),
    "IC 5070": (["Sh2-117"], None),
    "NGC 7000": (["Sh2-117"], None),
    "M 42": (["Sh2-281"], None),
    "M 8": (["Sh2-25"], None),
    "M 16": (["Sh2-49"], None),
    "M 17": (["Sh2-45"], None),
    "M 20": (["Sh2-30"], None),
}

KEEP_IDENTIFIERS = re.compile(r"^(C|LBN|Mel|Cr|Cl|B|SH 2|PN|PK|UGC|HCG|Abell|Sh2)\b", re.IGNORECASE)


def designation(raw: str) -> str:
    """NGC0224 -> NGC 224, IC0434 -> IC 434, B033 -> B 33, Mel022 -> Mel 22, C009 -> C 9."""
    m = re.match(r"^([A-Za-z]+)(\d+)(.*)$", raw.strip())
    if not m:
        return raw.strip()
    prefix, number, rest = m.groups()
    return f"{prefix} {int(number)}{rest.strip()}"


def tidy_identifier(raw: str) -> str:
    raw = raw.strip()
    m = re.match(r"^SH 2-0*(\d+)$", raw, re.IGNORECASE)
    if m:
        return f"Sh2-{int(m.group(1))}"
    m = re.match(r"^C 0*(\d+)$", raw)
    if m:
        return f"Caldwell {int(m.group(1))}"
    m = re.match(r"^([A-Za-z]+) 0*(\d+)(.*)$", raw)
    if m:
        return f"{m.group(1)} {int(m.group(2))}{m.group(3)}"
    return raw


def hours(text: str) -> float:
    h, m, s = (float(x) for x in text.split(":"))
    return h + m / 60 + s / 3600


def degrees(text: str) -> float:
    sign = -1 if text.strip().startswith("-") else 1
    d, m, s = (abs(float(x)) for x in text.strip().lstrip("+-").split(":"))
    return sign * (d + m / 60 + s / 3600)


def number(text: str) -> str:
    text = (text or "").strip()
    if not text:
        return ""
    return f"{float(text):g}"


def read_openngc(folder: pathlib.Path):
    for file in ("NGC.csv", "addendum.csv"):
        with open(folder / file, encoding="utf-8") as f:
            for row in csv.DictReader(f, delimiter=";"):
                if row["Type"] in SKIP_TYPES or not row["RA"] or not row["Dec"]:
                    continue
                yield row


def main(folder: pathlib.Path) -> None:
    out = []
    sharpless_taken: set[int] = set()

    for row in read_openngc(folder):
        name = designation(row["Name"])
        names = []
        if row["M"]:
            names.append(f"M {int(row['M'])}")
        names.append(name)
        for other in ("NGC", "IC"):
            for n in filter(None, (row.get(other) or "").split(",")):
                names.append(f"{other} {designation(other + n).split(' ', 1)[1]}")
        if name.startswith("C "):
            names.append("Caldwell " + name[2:])
        common = [c.strip() for c in (row.get("Common names") or "").split(",") if c.strip()]
        for ident in filter(None, (row.get("Identifiers") or "").split(",")):
            ident = ident.strip()
            if KEEP_IDENTIFIERS.match(ident):
                tidy = tidy_identifier(ident)
                names.append(tidy)
                if tidy.startswith("Sh2-"):
                    sharpless_taken.add(int(tidy[4:]))
        names.extend(common)
        major, minor = number(row["MajAx"]), number(row["MinAx"])
        if names[0] in OVERRIDES or name in OVERRIDES:
            extra, size = OVERRIDES.get(names[0]) or OVERRIDES[name]
            names.extend(extra)
            sharpless_taken.update(int(x[4:]) for x in extra if x.startswith("Sh2-"))
            if size:
                major, minor = str(size[0]), str(size[1])
        if names[0].startswith("C ") and not row["M"]:
            names[0] = "Caldwell " + names[0][2:]  # addendum entries named by Caldwell number
        unique = list(dict.fromkeys(names))
        label = unique[0]
        magnitude = number(row["V-Mag"]) or number(row["B-Mag"])
        out.append((
            hours(row["RA"]), degrees(row["Dec"]), major, minor, number(row["PosAng"]),
            magnitude, number(row["SurfBr"]), row["Type"], label, " · ".join(unique),
        ))

    with open(folder / "sh2.tsv", encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 4 or not re.match(r"^\s*\d", parts[2] or ""):
                continue
            ra, dec, number_, diameter = parts[0], parts[1], int(parts[2]), parts[3].strip()
            if number_ in sharpless_taken:
                continue
            names = [f"Sh2-{number_}"]
            if number_ in SH2_NAMES:
                names.append(SH2_NAMES[number_])
            out.append((float(ra) / 15, float(dec), diameter, diameter, "", "", "", "HII", names[0], " · ".join(names)))

    out.sort(key=lambda r: r[1])
    target = pathlib.Path(__file__).resolve().parent.parent / "src/Nova.Core/Catalogs/dso.tsv.gz"
    with gzip.open(target, "wt", encoding="utf-8", newline="\n") as f:
        f.write("# OpenNGC (Mattia Verga) CC BY-SA 4.0 + Sharpless VII/20 - ra_h dec_deg maj min pa mag sb type label names\n")
        for r in out:
            f.write(f"{r[0]:.6f}\t{r[1]:.5f}\t" + "\t".join(r[2:]) + "\n")
    print(f"{len(out)} objects -> {target} ({target.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main(pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "tools/data"))
