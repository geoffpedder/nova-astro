// Drawing a HiPS sky survey (the sky as HEALPix tiles: 512-pixel JPEGs, nested scheme, equatorial frame) on the
// sky chart. Tiles come from NOVA's tile store (/api/sky/tiles), which keeps them on the mini PC. Each tile is
// cut into small patches and each patch warped onto the chart as two textured triangles; a tile not loaded yet
// borrows the matching part of a loaded ancestor (or of the whole-sky Allsky image), so there are no holes.

import type { Projection } from './skyview'

const TILE = 512
const ALLSKY_TILE = 64
const ALLSKY_PER_ROW = 27
const FACE_DEGREES = 58.63 // a base face is about this wide (sqrt of 4π/12 steradians)

const JRLL = [2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4]
const JPLL = [1, 3, 5, 7, 0, 2, 4, 6, 1, 3, 5, 7]

/** Takes every other bit (the x or y half of a nested index). */
function compress(v: number): number {
  let out = 0
  for (let bit = 0; v > 0; bit++, v = Math.floor(v / 4)) out |= (v & 1) << bit
  return out
}

/** A tile's face and position on it (in tiles). */
export function tileXY(order: number, npix: number): { face: number; ix: number; iy: number } {
  const perFace = 4 ** order
  const face = Math.floor(npix / perFace)
  const ipf = npix - face * perFace
  return { face, ix: compress(ipf), iy: compress(Math.floor(ipf / 2)) }
}

/** RA (hours) and Dec of fractional face coordinates x, y in [0, 1] (healpix_base's xyf2loc). */
export function facePoint(face: number, x: number, y: number): { ra: number; dec: number } {
  const jr = JRLL[face] - x - y
  let nr: number
  let z: number
  if (jr < 1) {
    nr = jr
    z = 1 - (nr * nr) / 3
  } else if (jr > 3) {
    nr = 4 - jr
    z = (nr * nr) / 3 - 1
  } else {
    nr = 1
    z = ((2 - jr) * 2) / 3
  }
  let t = JPLL[face] * nr + x - y
  if (t < 0) t += 8
  if (t >= 8) t -= 8
  const phi = nr < 1e-15 ? 0 : (Math.PI / 4) * (t / nr)
  return { ra: ((phi * 180) / Math.PI / 15) % 24, dec: (Math.asin(Math.max(-1, Math.min(1, z))) * 180) / Math.PI }
}

/** Where a texture comes from: an image and the face square (x0, y0, span) it covers, in its pixels (ox, oy, size). */
interface Source {
  image: HTMLImageElement
  x0: number
  y0: number
  span: number
  ox: number
  oy: number
  size: number
}

type Entry = { image: HTMLImageElement; state: 'loading' | 'ok' | 'missing'; at: number }

export class HipsLayer {
  private tiles = new Map<string, Entry>()
  private allsky: Entry | null = null

  constructor(
    private readonly onChange: () => void,
    private readonly base = '/api/sky/tiles',
  ) {}

  get hasAllsky(): boolean {
    return this.allsky?.state === 'ok'
  }

  private load(key: string, url: string): Entry {
    const image = new Image()
    const entry: Entry = { image, state: 'loading', at: Date.now() }
    image.onload = () => {
      entry.state = 'ok'
      this.onChange()
    }
    image.onerror = () => {
      entry.state = 'missing'
      entry.at = Date.now()
    }
    image.src = url
    if (key) {
      this.tiles.set(key, entry)
      // Keep memory in check: forget the oldest tiles beyond a few hundred.
      if (this.tiles.size > 500) {
        for (const k of this.tiles.keys()) {
          this.tiles.delete(k)
          if (this.tiles.size <= 400) break
        }
      }
    }
    return entry
  }

  private tile(order: number, npix: number, request: boolean): HTMLImageElement | null {
    const key = `${order}/${npix}`
    let entry = this.tiles.get(key)
    // A missing tile (offline, not stored) is tried again after a minute.
    if (entry?.state === 'missing' && Date.now() - entry.at > 60_000) {
      this.tiles.delete(key)
      entry = undefined
    }
    if (!entry && request) entry = this.load(key, `${this.base}/${order}/${npix}.jpg`)
    return entry?.state === 'ok' ? entry.image : null
  }

  /** The best texture for a tile: its own image, else a loaded ancestor's, else the Allsky image (order 3). */
  private source(order: number, npix: number): Source | null {
    const own = this.tile(order, npix, true)
    const { ix, iy } = tileXY(order, npix)
    if (own) return { image: own, x0: ix / 2 ** order, y0: iy / 2 ** order, span: 1 / 2 ** order, ox: 0, oy: 0, size: TILE }
    for (let a = order - 1; a >= 0; a--) {
      const shift = order - a
      const parent = Math.floor(npix / 4 ** shift)
      const image = this.tile(a, parent, false)
      if (image) {
        const p = tileXY(a, parent)
        return { image, x0: p.ix / 2 ** a, y0: p.iy / 2 ** a, span: 1 / 2 ** a, ox: 0, oy: 0, size: TILE }
      }
    }
    // The Allsky image: every level-3 tile at 64 pixels, 27 to a row.
    if (order >= 3 && this.allsky?.state === 'ok') {
      const parent = Math.floor(npix / 4 ** (order - 3))
      const p = tileXY(3, parent)
      return {
        image: this.allsky.image,
        x0: p.ix / 8,
        y0: p.iy / 8,
        span: 1 / 8,
        ox: (parent % ALLSKY_PER_ROW) * ALLSKY_TILE,
        oy: Math.floor(parent / ALLSKY_PER_ROW) * ALLSKY_TILE,
        size: ALLSKY_TILE,
      }
    }
    return null
  }

  /**
   * Draws the survey for this view. Returns true if anything was drawn (the whole view is then covered once the
   * Allsky image has loaded).
   */
  draw(g: CanvasRenderingContext2D, p: Projection, dpr: number): boolean {
    if (!this.allsky) this.allsky = this.load('', `${this.base}/allsky.jpg`)

    // The level whose pixels match the screen's.
    const degreesPerPixel = p.fov / p.width / dpr
    const order = Math.max(3, Math.min(9, Math.ceil(Math.log2(FACE_DEGREES / (TILE * degreesPerPixel)))))

    const centre = p.unproject(p.width / 2, p.height / 2)
    const viewRadius = (2 * Math.atan(Math.hypot(p.width, p.height) / 2 / p.scale / 2) * 180) / Math.PI
    const visible: number[] = []
    // Down the tree from the 12 base faces, keeping tiles that can touch the view.
    const walk = (o: number, npix: number) => {
      const { face, ix, iy } = tileXY(o, npix)
      const n = 2 ** o
      const mid = facePoint(face, (ix + 0.5) / n, (iy + 0.5) / n)
      const radius = (FACE_DEGREES / n) * 0.85 + 0.5
      if (separation(mid, centre) > viewRadius + radius) return
      if (o === order) visible.push(npix)
      else for (let c = 0; c < 4; c++) walk(o + 1, npix * 4 + c)
    }
    for (let f = 0; f < 12; f++) walk(0, f)
    if (visible.length > 400) return false // too wide a view for this many tiles

    let drawn = false
    for (const npix of visible) {
      const source = this.source(order, npix)
      if (!source) continue
      drawn = true
      this.drawTile(g, p, order, npix, source)
    }
    return drawn
  }

  private drawTile(g: CanvasRenderingContext2D, p: Projection, order: number, npix: number, s: Source) {
    const { face, ix, iy } = tileXY(order, npix)
    const n = 2 ** order
    // Finer patches for tiles that are big on screen (curved edges), coarser for small ones.
    const corner = (fx: number, fy: number) => {
      const pt = facePoint(face, (ix + fx) / n, (iy + fy) / n)
      return p.sky(pt.ra, pt.dec)
    }
    const a = corner(0, 0)
    const b = corner(1, 1)
    const across = a && b ? Math.hypot(a.x - b.x, a.y - b.y) : 600
    const cuts = Math.max(1, Math.min(8, Math.ceil(across / 140)))
    const points: ({ x: number; y: number } | null)[][] = []
    for (let i = 0; i <= cuts; i++) {
      points.push([])
      for (let j = 0; j <= cuts; j++) points[i].push(corner(i / cuts, j / cuts))
    }
    // Texture position (pixels) of fractional tile coordinates: columns run along y, rows along x.
    const tex = (fx: number, fy: number) => {
      const x = (ix + fx) / n
      const y = (iy + fy) / n
      return { u: s.ox + ((y - s.y0) / s.span) * s.size, v: s.oy + ((x - s.x0) / s.span) * s.size }
    }
    for (let i = 0; i < cuts; i++)
      for (let j = 0; j < cuts; j++) {
        const p00 = points[i][j]
        const p10 = points[i + 1][j]
        const p11 = points[i + 1][j + 1]
        const p01 = points[i][j + 1]
        if (!p00 || !p10 || !p11 || !p01) continue
        const t00 = tex(i / cuts, j / cuts)
        const t10 = tex((i + 1) / cuts, j / cuts)
        const t11 = tex((i + 1) / cuts, (j + 1) / cuts)
        const t01 = tex(i / cuts, (j + 1) / cuts)
        triangle(g, s.image, p00, p10, p11, t00, t10, t11)
        triangle(g, s.image, p00, p11, p01, t00, t11, t01)
      }
  }
}

function separation(a: { ra: number; dec: number }, b: { ra: number; dec: number }): number {
  const r = Math.PI / 180
  const c =
    Math.sin(a.dec * r) * Math.sin(b.dec * r) + Math.cos(a.dec * r) * Math.cos(b.dec * r) * Math.cos((a.ra - b.ra) * 15 * r)
  return Math.acos(Math.max(-1, Math.min(1, c))) / r
}

type Pt = { x: number; y: number }
type Tx = { u: number; v: number }

/** Draws the image's texture triangle t0 t1 t2 onto the screen triangle p0 p1 p2 (an affine warp, clipped). */
function triangle(g: CanvasRenderingContext2D, image: HTMLImageElement, p0: Pt, p1: Pt, p2: Pt, t0: Tx, t1: Tx, t2: Tx) {
  // Grow the triangle a little so neighbouring patches overlap (no hairline seams).
  const cx = (p0.x + p1.x + p2.x) / 3
  const cy = (p0.y + p1.y + p2.y) / 3
  const grow = (pt: Pt) => {
    const d = Math.hypot(pt.x - cx, pt.y - cy) || 1
    return { x: pt.x + ((pt.x - cx) / d) * 0.6, y: pt.y + ((pt.y - cy) / d) * 0.6 }
  }
  const [q0, q1, q2] = [grow(p0), grow(p1), grow(p2)]
  const det = (t1.u - t0.u) * (t2.v - t0.v) - (t2.u - t0.u) * (t1.v - t0.v)
  if (Math.abs(det) < 1e-9) return
  // Solve x = a·u + c·v + e, y = b·u + d·v + f from the three corners.
  const a = ((p1.x - p0.x) * (t2.v - t0.v) - (p2.x - p0.x) * (t1.v - t0.v)) / det
  const c = ((p2.x - p0.x) * (t1.u - t0.u) - (p1.x - p0.x) * (t2.u - t0.u)) / det
  const b = ((p1.y - p0.y) * (t2.v - t0.v) - (p2.y - p0.y) * (t1.v - t0.v)) / det
  const d = ((p2.y - p0.y) * (t1.u - t0.u) - (p1.y - p0.y) * (t2.u - t0.u)) / det
  const e = p0.x - a * t0.u - c * t0.v
  const f = p0.y - b * t0.u - d * t0.v
  g.save()
  g.beginPath()
  g.moveTo(q0.x, q0.y)
  g.lineTo(q1.x, q1.y)
  g.lineTo(q2.x, q2.y)
  g.closePath()
  g.clip()
  g.transform(a, b, c, d, e, f)
  g.drawImage(image, 0, 0)
  g.restore()
}
