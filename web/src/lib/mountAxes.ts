// Where a German equatorial mount's two axes are for a place in the sky. No drawing here, so it's cheap to import.
//
// World: X east, Y up, Z south. The mount's own frame has Y along the polar axis (towards the pole that's up), and in
// it the RA angle `a` is 0 with the telescope on top (the home position: where a counterweight would hang straight
// down) and the Dec angle `b` is 0 pointing at the pole. In the southern hemisphere the mount faces south.

export interface MountAxes {
  /** The RA axis's angle (degrees). */
  a: number
  /** The Dec axis's angle (degrees). */
  b: number
  /** Whether the telescope is on the east side of the pier (so, normally, looking west). */
  east: boolean
  /** The pointing's altitude (degrees). */
  altitude: number
}

const rad = (d: number) => (d * Math.PI) / 180
const deg = (r: number) => (r * 180) / Math.PI

/**
 * @param hourAngle hours from the meridian (negative east of it)
 * @param declination degrees
 * @param latitude degrees, south negative
 * @param side the mount's side of the pier (which way round it is: see below); null picks the way that has the
 * telescope above the Dec axis's other end
 */
export function mountAxes(hourAngle: number, declination: number, latitude: number, side: 'East' | 'West' | null): MountAxes {
  const H = rad(hourAngle * 15), D = rad(declination), L = rad(latitude)
  // Where it points, in the world.
  const sinAlt = Math.sin(L) * Math.sin(D) + Math.cos(L) * Math.cos(D) * Math.cos(H)
  const east = -Math.cos(D) * Math.sin(H)
  const north = Math.sin(D) * Math.cos(L) - Math.cos(D) * Math.cos(H) * Math.sin(L)
  // ...and in the mount's frame: turned to face the other pole in the south, then tipped back by the polar axis's tilt.
  const south = latitude < 0
  const wx = south ? -east : east
  const wz = south ? north : -north
  const tilt = Math.PI / 2 - Math.abs(L)
  const x = wx
  const y = sinAlt * Math.cos(tilt) - wz * Math.sin(tilt)
  const z = sinAlt * Math.sin(tilt) + wz * Math.cos(tilt)

  const reach = Math.acos(Math.max(-1, Math.min(1, y))) // from the pole
  const altitude = deg(Math.asin(Math.max(-1, Math.min(1, sinAlt))))
  if (reach < 1e-4) return { a: 0, b: 0, east: false, altitude }

  // Two ways to point there, half a turn of RA apart with Dec leaning the other way.
  const ways = [
    { a: deg(Math.atan2(z, -x)), b: deg(reach) },
    { a: deg(Math.atan2(-z, x)), b: -deg(reach) },
  ].map((w) => {
    // The telescope's end of the Dec axis: how far east of the pier, and how far up.
    const eastward = Math.sin(rad(w.a)) * (south ? -1 : 1)
    const up = Math.cos(rad(w.a)) * Math.sin(tilt)
    return { ...w, eastward, up }
  })
  // A mount's "side of the pier" says which of the two ways round it is, not where the telescope physically sits: East
  // is the way it is while looking west of the meridian above the pole, and it stays East on round under the pole,
  // where the telescope is then on the west side. (Taking it for the physical side hung the telescope under the mount
  // for targets below the pole.)
  const normal = ways[south ? 1 : 0]
  const flipped = ways[south ? 0 : 1]
  let chosen = side === 'East' ? normal : side === 'West' ? flipped : ways[0].up >= ways[1].up ? ways[0] : ways[1]
  // Hanging well under the mount can't be right, whatever the mount says (a driver with another convention, or its
  // side and position out of step for a moment during a slew): the other way round is the one it's in.
  if (Math.cos(rad(chosen.a)) < -0.5) chosen = chosen === normal ? flipped : normal
  return { a: chosen.a, b: chosen.b, east: chosen.eastward > 0, altitude }
}
