// The sky chart's geometry: a stereographic projection (as Stellarium uses) of the celestial sphere, viewed
// either with the horizon level (horizon mode, alt-az) or with celestial north up (equatorial mode).
// Positions are J2000 RA (hours) / Dec (degrees); precession is ignored for drawing (a fraction of a degree).

export type Vec = [number, number, number]
export type Mode = 'horizon' | 'equatorial'

/** What the sky chart is looking at. */
export interface ChartView {
  mode: Mode
  /** The view centre: azimuth/altitude (horizon mode) or RA hours/Dec (equatorial mode). */
  lon: number
  lat: number
  /** Degrees across the width. */
  fov: number
}

const RAD = Math.PI / 180

export const dot = (a: Vec, b: Vec) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
export const cross = (a: Vec, b: Vec): Vec => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
export const norm = (a: Vec): Vec => {
  const n = Math.hypot(a[0], a[1], a[2]) || 1
  return [a[0] / n, a[1] / n, a[2] / n]
}

/** Equatorial unit vector: x towards RA 0h, y towards RA 6h, z the north celestial pole. */
export function eqVec(ra: number, dec: number): Vec {
  const a = ra * 15 * RAD
  const d = dec * RAD
  return [Math.cos(d) * Math.cos(a), Math.cos(d) * Math.sin(a), Math.sin(d)]
}

export function vecToEq(v: Vec): { ra: number; dec: number } {
  const ra = Math.atan2(v[1], v[0]) / RAD / 15
  return { ra: ra < 0 ? ra + 24 : ra, dec: Math.asin(Math.max(-1, Math.min(1, v[2]))) / RAD }
}

/** Local sidereal time in degrees (Meeus 12.4), east longitude positive. */
export function lstDegrees(date: Date, longitude: number): number {
  const jd = date.getTime() / 86400000 + 2440587.5
  const t = (jd - 2451545) / 36525
  const gmst = 280.46061837 + 360.98564736629 * (jd - 2451545) + 0.000387933 * t * t - (t * t * t) / 38710000
  return (((gmst + longitude) % 360) + 360) % 360
}

/**
 * Horizon unit vector (x north, y west, z zenith — a right-handed frame) from an equatorial one.
 * Azimuth runs from north through east.
 */
export function eqToHor(v: Vec, lst: number, latitude: number): Vec {
  const { ra, dec } = vecToEq(v)
  const h = (lst - ra * 15) * RAD
  const d = dec * RAD
  const f = latitude * RAD
  return [
    Math.cos(f) * Math.sin(d) - Math.sin(f) * Math.cos(d) * Math.cos(h),
    Math.cos(d) * Math.sin(h),
    Math.sin(f) * Math.sin(d) + Math.cos(f) * Math.cos(d) * Math.cos(h),
  ]
}

export function horToEq(v: Vec, lst: number, latitude: number): Vec {
  const f = latitude * RAD
  const sinD = Math.sin(f) * v[2] + Math.cos(f) * v[0]
  const cosDcosH = Math.cos(f) * v[2] - Math.sin(f) * v[0]
  const cosDsinH = v[1]
  const h = Math.atan2(cosDsinH, cosDcosH) / RAD
  const dec = Math.asin(Math.max(-1, Math.min(1, sinD))) / RAD
  return eqVec(((((lst - h) / 15) % 24) + 24) % 24, dec)
}

export const altAzVec = (alt: number, az: number): Vec => [
  Math.cos(alt * RAD) * Math.cos(az * RAD),
  -Math.cos(alt * RAD) * Math.sin(az * RAD),
  Math.sin(alt * RAD),
]

export function vecToAltAz(v: Vec): { alt: number; az: number } {
  const az = Math.atan2(-v[1], v[0]) / RAD
  return { alt: Math.asin(Math.max(-1, Math.min(1, v[2]))) / RAD, az: (az + 360) % 360 }
}

/** Everything needed to turn sky positions into screen pixels for one frame of the chart. */
export class Projection {
  readonly toFrame: (eq: Vec) => Vec
  readonly fromFrame: (v: Vec) => Vec
  readonly right: Vec
  readonly up: Vec
  /** Pixels per stereographic unit. */
  readonly scale: number

  constructor(
    readonly mode: Mode,
    /** View centre in the mode's frame (horizon or equatorial). */
    readonly centre: Vec,
    /** Field of view across the width, degrees. */
    readonly fov: number,
    readonly width: number,
    readonly height: number,
    readonly lst: number,
    readonly latitude: number,
  ) {
    this.toFrame = mode === 'horizon' ? (v) => eqToHor(v, lst, latitude) : (v) => v
    this.fromFrame = mode === 'horizon' ? (v) => horToEq(v, lst, latitude) : (v) => v
    // Up towards the frame's pole (zenith or celestial north); seen from inside the sphere, left is up × centre.
    let up: Vec = [0, 0, 1]
    up = norm([up[0] - dot(up, centre) * centre[0], up[1] - dot(up, centre) * centre[1], up[2] - dot(up, centre) * centre[2]])
    this.up = up
    const left = cross(up, centre)
    this.right = [-left[0], -left[1], -left[2]]
    this.scale = width / 2 / (2 * Math.tan((fov / 4) * RAD))
  }

  /** Screen position of a frame vector, or null if too far behind to draw. */
  frame(v: Vec): { x: number; y: number } | null {
    const z = dot(v, this.centre)
    if (z < -0.6) return null
    const k = 2 / (1 + z)
    return { x: this.width / 2 + k * dot(v, this.right) * this.scale, y: this.height / 2 - k * dot(v, this.up) * this.scale }
  }

  /** Screen position of an equatorial (J2000) position. */
  sky(ra: number, dec: number): { x: number; y: number } | null {
    return this.frame(this.toFrame(eqVec(ra, dec)))
  }

  /** The frame vector at a screen point. */
  unprojectFrame(x: number, y: number): Vec {
    const X = (x - this.width / 2) / this.scale
    const Y = (this.height / 2 - y) / this.scale
    const r = Math.hypot(X, Y)
    if (r === 0) return this.centre
    const theta = 2 * Math.atan(r / 2)
    const s = Math.sin(theta) / r
    return norm([0, 1, 2].map((i) => Math.cos(theta) * this.centre[i] + s * (X * this.right[i] + Y * this.up[i])) as Vec)
  }

  /** The equatorial (J2000) position at a screen point. */
  unproject(x: number, y: number): { ra: number; dec: number } {
    return vecToEq(this.fromFrame(this.unprojectFrame(x, y)))
  }

  /** Screen angle (radians, clockwise from up) of celestial north at the view centre. */
  northAngle(): number {
    const pole = this.toFrame([0, 0, 1])
    const t: Vec = [0, 1, 2].map((i) => pole[i] - dot(pole, this.centre) * this.centre[i]) as Vec
    return Math.atan2(dot(t, this.right), dot(t, this.up))
  }

  /** Stereographic units → screen pixels for a small angular size (degrees) near the centre. */
  pixels(degrees: number): number {
    return 2 * Math.tan((degrees / 4) * RAD) * 2 * this.scale
  }
}
