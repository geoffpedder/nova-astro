// Per-browser Stellarium settings (Stellarium runs on this PC, so they belong to the browser, not the server).

import { DEFAULT_STELLARIUM_PORT } from './stellarium'

interface StellariumSettings {
  /** Remote Control plugin port. */
  port: number
  /** Send the camera angle to Oculars after every plate solve. */
  autoRotation: boolean
  /** Calibration: Oculars angle = (mirror ? −1 : 1) × solved rotation + offset. */
  offset: number
  mirror: boolean
}

const KEY = 'nova.stellarium'

function load(): StellariumSettings {
  const defaults: StellariumSettings = { port: DEFAULT_STELLARIUM_PORT, autoRotation: false, offset: 0, mirror: false }
  try {
    const saved = JSON.parse(localStorage.getItem(KEY) ?? 'null')
    // Earlier versions kept only the port, under its own key.
    const legacyPort = Number(localStorage.getItem('nova.stellariumPort'))
    return { ...defaults, ...(legacyPort > 0 ? { port: legacyPort } : {}), ...(saved ?? {}) }
  } catch {
    return defaults
  }
}

export const stellariumSettings = $state<StellariumSettings>(load())

export function saveStellariumSettings() {
  try {
    localStorage.setItem(KEY, JSON.stringify(stellariumSettings))
  } catch {
    // storage unavailable
  }
}

/** The Oculars sensor-frame angle for a plate solve's rotation, after calibration, 0–360°. */
export function ocularsAngle(solvedRotation: number): number {
  const angle = (stellariumSettings.mirror ? -solvedRotation : solvedRotation) + stellariumSettings.offset
  return Math.round((((angle % 360) + 360) % 360) * 100) / 100
}
