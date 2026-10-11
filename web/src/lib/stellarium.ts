// Grab the object selected in Stellarium (its Remote Control plugin, on the PC running this browser).

export interface PlanetariumTarget {
  name: string
  /** J2000 */
  raHours: number
  decDegrees: number
  type: string | null
  magnitude: number | null
}

export const DEFAULT_STELLARIUM_PORT = 8090

class NothingSelected extends Error {}

/**
 * First straight from the browser (Stellarium must allow this page via its CORS setting), then through
 * NOVA's server, which asks Stellarium at this PC's address (Stellarium must be let through the firewall).
 */
export async function fromStellarium(port = DEFAULT_STELLARIUM_PORT): Promise<PlanetariumTarget> {
  let direct: string
  try {
    return parse(await get(`http://localhost:${port}/api/objects/info?format=json`))
  } catch (e) {
    if (e instanceof NothingSelected) throw e
    direct = e instanceof Error ? e.message : String(e)
  }
  try {
    return parse(await get(`/api/stellarium/selection?port=${port}`))
  } catch (e) {
    if (e instanceof NothingSelected) throw e
    const relayed = e instanceof Error ? e.message : String(e)
    throw new Error(
      `Couldn't reach Stellarium (${relayed}; directly: ${direct}). In Stellarium: Configuration → Plugins → ` +
        `Remote Control → "Load at startup", then its settings: enable the server on port ${port} and either ` +
        `tick "Enable CORS" with origin ${location.origin}, or allow Stellarium through this PC's firewall.`,
    )
  }
}

async function get(url: string): Promise<unknown> {
  const response = await fetch(url, { signal: AbortSignal.timeout(4000) })
  if (response.status === 404) throw new NothingSelected('Nothing is selected in Stellarium: click an object first')
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    try {
      const problem = await response.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      // keep the status
    }
    throw new Error(message)
  }
  return response.json()
}

/** Stellarium's info map: angles in degrees; raJ2000/decJ2000 are what NOVA's slews take. */
function parse(data: unknown): PlanetariumTarget {
  const o = data as Record<string, unknown>
  const ra = Number(o.raJ2000)
  const dec = Number(o.decJ2000)
  if (!Number.isFinite(ra) || !Number.isFinite(dec)) throw new Error("Stellarium didn't send J2000 coordinates")
  const text = (key: string) => (typeof o[key] === 'string' && (o[key] as string).trim()) || null
  const magnitude = Number(o.vmag)
  return {
    name: text('localized-name') ?? text('name') ?? 'Stellarium target',
    raHours: (((ra / 15) % 24) + 24) % 24,
    decDegrees: dec,
    type: text('object-type') ?? text('type'),
    magnitude: Number.isFinite(magnitude) && magnitude < 50 ? magnitude : null,
  }
}

/**
 * Turns Stellarium's Oculars sensor frame to an angle. First through NOVA's server, which can confirm it
 * worked; if the server can't reach this PC (firewall), straight from the browser: Stellarium takes a plain
 * form POST without CORS, but the browser can't see the answer, so that path is "sent", not "confirmed".
 */
export async function setOcularsRotation(angle: number, port = DEFAULT_STELLARIUM_PORT): Promise<'confirmed' | 'sent'> {
  const relayed = await fetch('/api/stellarium/rotation', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ angle, port }),
  })
  if (relayed.ok) return 'confirmed'
  let problem = `${relayed.status} ${relayed.statusText}`
  try {
    const p = await relayed.json()
    problem = p.detail ?? p.title ?? problem
  } catch {
    // keep the status
  }
  // Stellarium answered but refused (e.g. Oculars not loaded): going direct won't help.
  if (problem.startsWith('Stellarium said')) throw new Error(problem)
  try {
    await fetch(`http://localhost:${port}/api/stelproperty/set`, {
      method: 'POST',
      mode: 'no-cors',
      body: new URLSearchParams({ id: 'Oculars.selectedCCDRotationAngle', value: String(angle) }),
      signal: AbortSignal.timeout(4000),
    })
    return 'sent'
  } catch {
    throw new Error(`Couldn't reach Stellarium (${problem}). Is it running with the Remote Control plugin on port ${port}?`)
  }
}

/** 05:35:17.3 / +22:00:52 style, as the RA/Dec boxes take. */
export function formatRa(hours: number): string {
  const total = Math.round(hours * 36000) % (24 * 36000) // tenths of a second
  const h = Math.floor(total / 36000)
  const m = Math.floor((total % 36000) / 600)
  const s = (total % 600) / 10
  return `${pad(h)}:${pad(m)}:${s.toFixed(1).padStart(4, '0')}`
}

export function formatDec(degrees: number): string {
  const sign = degrees < 0 ? '-' : '+'
  const total = Math.round(Math.abs(degrees) * 3600)
  const d = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  return `${sign}${pad(d)}:${pad(m)}:${pad(s)}`
}

const pad = (n: number) => String(n).padStart(2, '0')
