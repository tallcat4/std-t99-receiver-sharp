"""Builds the offline map of Japan embedded in the plugin (src/SDRSharp.T99/japan-map.bin) from Natural Earth
1:10m GeoJSON (public domain, https://www.naturalearthdata.com, nvkelso/natural-earth-vector).

  python tools/make-map.py build/naturalearth src/SDRSharp.T99/japan-map.bin

Layers: prefectures (land, borders, names), urban areas, lakes, rivers, railroads, roads, populated places.
Only features inside Japan are kept (a vertex inside, or within ~3 km of, a prefecture polygon).
Every part is stored twice: as given, and simplified (Douglas-Peucker 0.005 deg) for the small zooms.

Format (gzip): "T99M", u8 version=1, u8 layer count; per layer: u8 id, u8 geometry (1 polygon, 2 line, 3 point),
u32 features; per feature: u8 rank, u16 name length + UTF-8 name, i32 label lon, i32 label lat (1e-5 deg),
u16 parts, per part: u32 n + n x (i32 lon, i32 lat), u32 m + m x (i32 lon, i32 lat) simplified.
"""
import argparse
import gzip
import json
import struct
from pathlib import Path

LAYERS = [  # id, file, geometry
    (0, "ne_10m_admin_1_states_provinces", 1),
    (1, "ne_10m_urban_areas", 1),
    (2, "ne_10m_lakes", 1),
    (3, "ne_10m_rivers_lake_centerlines", 2),
    (4, "ne_10m_railroads", 2),
    (5, "ne_10m_roads", 2),
    (6, "ne_10m_populated_places", 3),
]
COARSE = 0.005


def parts_of(geometry):
    t, c = geometry["type"], geometry["coordinates"]
    if t == "Polygon":
        return [r for r in c if r]
    if t == "MultiPolygon":
        return [r for poly in c for r in poly if r]
    if t == "LineString":
        return [c] if c else []
    if t == "MultiLineString":
        return [l for l in c if l]
    if t == "Point":
        return [[c]]
    return []


def simplify(points, tolerance):
    if len(points) < 3:
        return points
    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]
    while stack:
        a, b = stack.pop()
        (x1, y1), (x2, y2) = points[a][:2], points[b][:2]
        dx, dy = x2 - x1, y2 - y1
        norm = (dx * dx + dy * dy) ** 0.5
        best, index = -1.0, -1
        for i in range(a + 1, b):
            x, y = points[i][:2]
            d = abs(dy * (x - x1) - dx * (y - y1)) / norm if norm else ((x - x1) ** 2 + (y - y1) ** 2) ** 0.5
            if d > best:
                best, index = d, i
        if best > tolerance:
            keep[index] = True
            stack += [(a, index), (index, b)]
    return [p for p, k in zip(points, keep) if k]


class Japan:
    """Point-in-Japan test against the prefecture rings (with a small margin for coast and border features)."""

    def __init__(self, rings):
        self.rings = [(r, min(p[0] for p in r), max(p[0] for p in r), min(p[1] for p in r), max(p[1] for p in r)) for r in rings]

    def inside(self, x, y, margin=0.03):
        for ring, x0, x1, y0, y1 in self.rings:
            if not (x0 - margin <= x <= x1 + margin and y0 - margin <= y <= y1 + margin):
                continue
            hit = False
            for (ax, ay, *_), (bx, by, *_) in zip(ring, ring[1:] + ring[:1]):
                if (ay > y) != (by > y) and x < (bx - ax) * (y - ay) / (by - ay) + ax:
                    hit = not hit
            if hit:
                return True
            if margin and any(abs(px - x) <= margin and abs(py - y) <= margin for px, py, *_ in ring[:: max(1, len(ring) // 400)]):
                return True
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("source", type=Path)
    ap.add_argument("output", type=Path)
    a = ap.parse_args()
    load = lambda name: json.loads((a.source / f"{name}.geojson").read_text(encoding="utf-8"))["features"]
    prefectures = [f for f in load(LAYERS[0][1]) if f["properties"].get("iso_a2") == "JP" and f["geometry"]]
    japan = Japan([r for f in prefectures for r in parts_of(f["geometry"])])
    q = lambda v: int(round(v * 1e5))
    out = [b"T99M", struct.pack("<BB", 1, len(LAYERS))]
    for layer, name, geometry in LAYERS:
        features = prefectures if layer == 0 else load(name)
        kept = []
        for f in features:
            p, g = f["properties"], f["geometry"]
            if not g:
                continue
            parts = parts_of(g)
            if layer == 6 and (p.get("adm0name") or p.get("ADM0NAME")) != "Japan":
                continue
            if layer not in (0, 6):
                probe = [pt for part in parts for pt in part[:: max(1, len(part) // 20)]]
                if not any(japan.inside(x, y) for x, y, *_ in probe):
                    continue
            if layer == 5 and p.get("type") == "Ferry Route":
                continue
            if layer == 0:
                rank, label = 0, (p.get("longitude"), p.get("latitude"))
            elif layer == 5:
                rank = 1 if p.get("expressway") == 1 or p.get("type") == "Major Highway" else 2
                label = (0, 0)
            elif layer == 6:
                rank, label = int(p.get("scalerank") or p.get("SCALERANK") or 0), g["coordinates"]
            else:
                rank, label = int(float(p.get("scalerank") or 0)), (0, 0)
            nm = (p.get("name_ja") or p.get("NAME_JA") or p.get("name") or p.get("NAME") or "") if layer in (0, 6) else ""
            kept.append((rank, nm, label, parts))
        out.append(struct.pack("<BBI", layer, geometry, len(kept)))
        points = 0
        for rank, nm, (lx, ly), parts in kept:
            raw = nm.encode("utf-8")
            out.append(struct.pack("<BH", rank, len(raw)) + raw + struct.pack("<iiH", q(lx or 0), q(ly or 0), len(parts)))
            for part in parts:
                coarse = part if geometry == 3 else simplify(part, COARSE)
                for pts in (part, coarse):
                    out.append(struct.pack("<I", len(pts)) + b"".join(struct.pack("<ii", q(x), q(y)) for x, y, *_ in pts))
                points += len(part)
        print(f"layer {layer} {name}: {len(kept)} features, {points} points")
    data = gzip.compress(b"".join(out), 9, mtime=0)
    a.output.write_bytes(data)
    print(f"{len(data)} bytes -> {a.output}")


if __name__ == "__main__":
    main()
