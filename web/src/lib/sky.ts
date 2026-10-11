// Sky geometry for the framing tool: gnomonic (TAN) projection, frames at a position angle, mosaics.
// Angles in degrees, RA in hours, J2000. Position angles run from north through east, the astronomers' way.
// Views are drawn north up, east left (as the sky looks), matching the survey images.

export interface Sky {
  ra: number
  dec: number
}

type Vec = [number, number, number]
const RAD = Math.PI / 180

function vec(p: Sky): Vec {
  const a = p.ra * 15 * RAD
  const d = p.dec * RAD
  return [Math.cos(d) * Math.cos(a), Math.cos(d) * Math.sin(a), Math.sin(d)]
}

function sky(v: Vec): Sky {
  const n = Math.hypot(v[0], v[1], v[2])
  const ra = Math.atan2(v[1], v[0]) / RAD / 15
  return { ra: ra < 0 ? ra + 24 : ra, dec: Math.asin(v[2] / n) / RAD }
}

const dot = (a: Vec, b: Vec) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

/** East and north unit vectors of the tangent plane at a point. */
function basis(c: Sky): { centre: Vec; east: Vec; north: Vec } {
  const a = c.ra * 15 * RAD
  const d = c.dec * RAD
  return {
    centre: vec(c),
    east: [-Math.sin(a), Math.cos(a), 0],
    north: [-Math.sin(d) * Math.cos(a), -Math.sin(d) * Math.sin(a), Math.cos(d)],
  }
}

/** Tangent-plane offsets (tan of angle, east and north) of a point as seen from a centre; null if behind. */
export function project(centre: Sky, p: Sky): { x: number; y: number } | null {
  const b = basis(centre)
  const v = vec(p)
  const d = dot(v, b.centre)
  if (d <= 0.05) return null
  return { x: dot(v, b.east) / d, y: dot(v, b.north) / d }
}

/** The point at tangent-plane offsets (east, north) from a centre. */
export function unproject(centre: Sky, east: number, north: number): Sky {
  const b = basis(centre)
  return sky([0, 1, 2].map((i) => b.centre[i] + east * b.east[i] + north * b.north[i]) as Vec)
}

/** Angular separation in degrees. */
export function separation(a: Sky, b: Sky): number {
  return Math.acos(Math.min(1, Math.max(-1, dot(vec(a), vec(b))))) / RAD
}

/** Position angle of b as seen from a (degrees, 0–360, north through east). */
export function positionAngle(a: Sky, b: Sky): number {
  const da = (b.ra - a.ra) * 15 * RAD
  const d1 = a.dec * RAD
  const d2 = b.dec * RAD
  const pa = Math.atan2(Math.sin(da), Math.cos(d1) * Math.tan(d2) - Math.sin(d1) * Math.cos(da)) / RAD
  return (pa + 360) % 360
}

/** Up and right of a frame whose top points at position angle pa, as (east, north) unit offsets. */
function axes(pa: number) {
  const t = pa * RAD
  // Up is towards pa; on a normal (unmirrored) image, right is a quarter turn clockwise on the sky view.
  return { up: { e: Math.sin(t), n: Math.cos(t) }, right: { e: -Math.cos(t), n: Math.sin(t) } }
}

/** A frame's corners (top-left, top-right, bottom-right, bottom-left), width and height in degrees. */
export function frameCorners(centre: Sky, width: number, height: number, pa: number): Sky[] {
  const { up, right } = axes(pa)
  const w = Math.tan((width / 2) * RAD)
  const h = Math.tan((height / 2) * RAD)
  return [
    [-1, 1],
    [1, 1],
    [1, -1],
    [-1, -1],
  ].map(([sx, sy]) => unproject(centre, sx * w * right.e + sy * h * up.e, sx * w * right.n + sy * h * up.n))
}

export interface Panel {
  index: number
  column: number
  row: number
  centre: Sky
  corners: Sky[]
}

/**
 * Mosaic panels around a centre: columns run left to right and rows top to bottom as the frame is
 * oriented, overlapping by the given fraction. Panel 1 is top left.
 */
export function mosaic(centre: Sky, width: number, height: number, pa: number, columns: number, rows: number, overlap: number): Panel[] {
  const { up, right } = axes(pa)
  const stepX = Math.tan(width * (1 - overlap) * RAD)
  const stepY = Math.tan(height * (1 - overlap) * RAD)
  const panels: Panel[] = []
  for (let r = 0; r < rows; r++)
    for (let c = 0; c < columns; c++) {
      const sx = (c - (columns - 1) / 2) * stepX
      const sy = ((rows - 1) / 2 - r) * stepY
      const p = unproject(centre, sx * right.e + sy * up.e, sx * right.n + sy * up.n)
      // An equatorial mount keeps the camera at the same angle to its own north wherever it points.
      panels.push({ index: panels.length + 1, column: c, row: r, centre: p, corners: frameCorners(p, width, height, pa) })
    }
  return panels
}

/** "05:35:17.3" from hours, "+22:00:52" from degrees: what the sequencer and slews take. */
export function formatRa(hours: number): string {
  const total = Math.round((((hours % 24) + 24) % 24) * 36000) // tenths of a second
  const h = Math.floor(total / 36000) % 24
  const m = Math.floor((total % 36000) / 600)
  const s = (total % 600) / 10
  return `${pad(h)}:${pad(m)}:${s.toFixed(1).padStart(4, '0')}`
}

export function formatDec(degrees: number): string {
  const sign = degrees < 0 ? '-' : '+'
  const total = Math.round(Math.abs(degrees) * 3600)
  const d = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  return `${sign}${pad(d)}:${pad(m)}:${pad(total % 60)}`
}

const pad = (n: number) => String(n).padStart(2, '0')
