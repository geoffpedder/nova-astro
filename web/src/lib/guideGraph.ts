/** One guide step, as the server sends it. */
export interface GuideStep {
  ra: number
  dec: number
  /** Taken while guiding settled (after starting or a dither): not counted in the RMS, not drawn. */
  settling?: boolean
  /** The first step after a dither. */
  dither?: boolean
}

export interface GuideGraph {
  /** Polyline point lists, one per stretch of steady guiding (settling breaks the line). */
  ra: string[]
  dec: string[]
  /** The scale (± units), from the steady steps only. */
  limit: number
  /** Where dithers happened (x). */
  dithers: number[]
  /** Settling stretches to shade, [x from, x to]. */
  settling: [number, number][]
  y: (value: number) => number
}

/**
 * The guiding graph: RA and Dec error over the last `steps` steps, newest on the right, a fixed width per step so it
 * scrolls left as it grows. A dither moves the star on purpose, so the steps while guiding settles again aren't drawn
 * and don't set the scale: a dashed line marks the dither and a light band the settling, instead of a spike that
 * squashes everything else.
 */
export function guideGraph(points: GuideStep[], steps: number, width: number, height: number): GuideGraph | null {
  const shown = points.slice(-steps)
  if (shown.length < 2) return null
  const steady = shown.filter((p) => !p.settling)
  const limit = Math.ceil(Math.max(2, ...steady.map((p) => Math.max(Math.abs(p.ra), Math.abs(p.dec)))))
  const x = (i: number) => ((i + steps - shown.length) / (steps - 1)) * width
  const margin = height * 0.05
  const y = (v: number) => Math.min(height, Math.max(0, height / 2 - (v / limit) * (height / 2 - margin)))

  const ra: string[] = []
  const dec: string[] = []
  const settling: [number, number][] = []
  let raSeg: string[] = []
  let decSeg: string[] = []
  let settleFrom: number | null = null
  const flush = () => {
    if (raSeg.length > 1) {
      ra.push(raSeg.join(' '))
      dec.push(decSeg.join(' '))
    }
    raSeg = []
    decSeg = []
  }
  shown.forEach((p, i) => {
    if (p.settling) {
      flush()
      settleFrom ??= x(i)
      return
    }
    if (settleFrom != null) {
      settling.push([settleFrom, x(i)])
      settleFrom = null
    }
    raSeg.push(`${x(i).toFixed(1)},${y(p.ra).toFixed(1)}`)
    decSeg.push(`${x(i).toFixed(1)},${y(p.dec).toFixed(1)}`)
  })
  flush()
  if (settleFrom != null) settling.push([settleFrom, width])

  const dithers = shown.flatMap((p, i) => (p.dither ? [x(i)] : []))
  return { ra, dec, limit, dithers, settling, y }
}
