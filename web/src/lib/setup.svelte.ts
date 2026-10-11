import { api } from './api'
import { nick, nova } from './state.svelte'
import type { SetupCheck } from './types'

/**
 * The first-run guide: its steps are worked out from the status (site, connection, optics, solving, PHD2), plus the
 * server's check of the mini PC (drivers, programs; GET /api/setup), refreshed every few seconds.
 */
export const setupGuide = $state({
  /** Shown because the user opened it (it also shows by itself until setup is done, unless put away). */
  open: false,
  check: null as SetupCheck | null,
})

export async function refreshSetup(): Promise<void> {
  try {
    setupGuide.check = await api.setupCheck()
  } catch {
    // keep the last answer; the server may be restarting
  }
}

export type SetupStepKey = 'site' | 'drivers' | 'connect' | 'optics' | 'solve' | 'guiding'

export interface SetupStep {
  key: SetupStepKey
  title: string
  done: boolean
  optional: boolean
  detail: string
}

export function setupSteps(): SetupStep[] {
  const s = nova.status
  const check = setupGuide.check
  if (!s) return []

  const site = s.site.sites.find((x) => x.name === s.site.active)
  const indi = s.backend.toUpperCase().startsWith('INDI')
  const camera = s.camera
  const mount = s.mount
  const optics = s.optics.find((o) => o.camera === camera?.name)
  const solve = s.plateSolve
  const guider = s.guider

  const driversDone = !indi || (check != null && check.driversChosen > 0 && check.driversInstalled === check.driversChosen)
  const connected = s.equipmentConnected && !!camera?.connected && (!mount || mount.connected)
  const solved = (check?.solvedOnce ?? false) || !!solve.last?.solved

  return [
    {
      key: 'site',
      title: 'Site',
      done: !!site,
      optional: false,
      detail: site
        ? `${site.name}, ${site.latitude.toFixed(3)}° ${site.longitude.toFixed(3)}°`
        : "Where the telescope is: for targets' altitudes, the mount's clock and Sun protection.",
    },
    {
      key: 'drivers',
      title: 'Drivers',
      done: driversDone,
      optional: false,
      detail: !indi
        ? "NOVA's own simulator needs no drivers."
        : check == null
          ? 'Checking…'
          : check.driversChosen === 0
            ? 'Pick the INDI drivers for your mount, cameras and other equipment.'
            : driversDone
              ? `${check.driversChosen} chosen, all installed.`
              : `${check.driversChosen - check.driversInstalled} of ${check.driversChosen} chosen drivers aren't installed (see below).`,
    },
    {
      key: 'connect',
      title: 'Connect',
      done: connected,
      optional: false,
      detail: connected
        ? `${nick(camera!.name)}${mount ? ` and ${nick(mount.name)}` : ''} connected.`
        : s.devices.length === 0
          ? 'No devices yet: once the drivers run, they appear in the Equipment panel.'
          : 'Choose your camera and mount in their panels, then connect.',
    },
    {
      key: 'optics',
      title: 'Camera optics',
      done: !!optics?.focalLength,
      optional: false,
      detail: optics?.focalLength
        ? `${optics.focalLength} mm${optics.imageScale ? `, ${optics.imageScale.toFixed(2)}″ per pixel` : ''}${optics.activePreset ? ` (${optics.activePreset})` : ''}.`
        : camera
          ? "Your telescope's focal length, so plate solving knows how big the field is."
          : 'Connect a camera first.',
    },
    {
      key: 'solve',
      title: 'Test plate solve',
      done: solved,
      optional: false,
      detail: solved
        ? 'Plate solving works.'
        : solve.solving
          ? 'Solving…'
          : solve.last && !solve.last.solved
            ? `The last solve failed: ${solve.last.error ?? 'no match'}. It needs stars, so try at night with the scope pointed at the sky.`
            : "A short frame of the night sky, solved: proves the camera, optics and ASTAP work together.",
    },
    {
      key: 'guiding',
      title: 'Guiding',
      done: guider.connected && guider.profile != null,
      optional: true,
      detail: !guider.connected
        ? "PHD2 isn't reachable: is it installed and running, with Tools → Enable Server ticked?"
        : guider.profile == null
          ? 'In PHD2, create a profile for your guide camera and mount.'
          : `PHD2${guider.version ? ` ${guider.version}` : ''}, profile ${guider.profile.name}.`,
    },
  ]
}

/** Required steps still to do. */
export function setupRemaining(): number {
  return setupSteps().filter((step) => !step.optional && !step.done).length
}
