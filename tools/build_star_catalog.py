"""Builds NOVA's offline star catalog (src/Nova.Core/Catalogs/stars.tsv.gz) from the HYG database.

    python tools/build_star_catalog.py path/to/hygdata_v41.csv

HYG database v4.1 by David Nash, https://github.com/astronexus/HYG-Database — CC BY-SA 4.0.
The extract is a derived database, so it carries the same licence.

Output: gzipped, tab-separated, sorted by declination, one star per line:
    ra_hours  dec_degrees  magnitude  label  detail
label is the most familiar designation (proper name, Bayer, Flamsteed, HD, HIP, Gliese);
detail lists the others, e.g. "ζ Ori · 50 Ori · HD 37742 · HIP 26727".
"""
import csv
import gzip
import io
import pathlib
import sys

GREEK = {
    "Alp": "α", "Bet": "β", "Gam": "γ", "Del": "δ", "Eps": "ε", "Zet": "ζ", "Eta": "η", "The": "θ",
    "Iot": "ι", "Kap": "κ", "Lam": "λ", "Mu": "μ", "Nu": "ν", "Xi": "ξ", "Omi": "ο", "Pi": "π",
    "Rho": "ρ", "Sig": "σ", "Tau": "τ", "Ups": "υ", "Phi": "φ", "Chi": "χ", "Psi": "ψ", "Ome": "ω",
}
SUPERSCRIPT = str.maketrans("0123456789", "⁰¹²³⁴⁵⁶⁷⁸⁹")


def bayer(code: str, con: str) -> str | None:
    if not code or not con:
        return None
    letter, _, index = code.partition("-")
    if letter not in GREEK:
        return None
    return f"{GREEK[letter]}{index.translate(SUPERSCRIPT)} {con}"


def main(source: str) -> None:
    out = pathlib.Path(__file__).resolve().parent.parent / "src" / "Nova.Core" / "Catalogs" / "stars.tsv.gz"
    rows = []
    with open(source, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if r["id"] == "0":  # the Sun
                continue
            names = [
                r["proper"] or None,
                bayer(r["bayer"], r["con"]),
                f"{r['flam']} {r['con']}" if r["flam"] and r["con"] else None,
                f"HD {r['hd']}" if r["hd"] else None,
                f"HIP {r['hip']}" if r["hip"] else None,
                r["gl"] or None,
            ]
            names = [n.replace("\t", " ") for n in names if n]
            if not names:
                continue
            rows.append((float(r["dec"]), float(r["ra"]), float(r["mag"]), names[0], " · ".join(names[1:])))

    rows.sort()
    text = io.StringIO()
    text.write("# HYG database v4.1 (David Nash, astronexus) - CC BY-SA 4.0 - ra_h dec_deg mag label detail\n")
    for dec, ra, mag, label, detail in rows:
        text.write(f"{ra:.6f}\t{dec:.5f}\t{mag:.2f}\t{label}\t{detail}\n")
    out.parent.mkdir(parents=True, exist_ok=True)
    with gzip.GzipFile(out, "wb", compresslevel=9, mtime=0) as gz:
        gz.write(text.getvalue().encode("utf-8"))
    print(f"{len(rows)} stars -> {out} ({out.stat().st_size / 1e6:.1f} MB)")


if __name__ == "__main__":
    main(sys.argv[1])
