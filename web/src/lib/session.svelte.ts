import { api } from './api'
import { auth, checkAuth } from './auth.svelte'
import { ask } from './confirm.svelte'
import { exposure, looping } from './exposure.svelte'
import { mainView } from './inbox.svelte'
import { refreshSetup, setupGuide, setupRemaining } from './setup.svelte'
import { act, nova, startHub, stopHub } from './state.svelte'
import type { DeviceKind } from './types'

/**
 * What every layout shares, whichever is showing: signing in and live updates, night mode, full screen, the setup
 * guide's state, Loop, errors, the sequence's progress and which equipment exists. A layout only arranges these;
 * the behaviour lives here once.
 */

// ---------- Night mode ----------

function loadNight(): boolean {
  try {
    return localStorage.getItem('nova.night') === '1'
  } catch {
    return false // storage unavailable (private mode): the day theme
  }
}

export const theme = $state({ night: loadNight() })

export function toggleNight(): void {
  theme.night = !theme.night
}

// ---------- Full screen ----------

// The browser's own, so the page fills a tablet or laptop at the scope (Esc also leaves it). Safari on iPad still uses
// the webkit names; iPhones can't do it at all, so the button only shows where it works.
type WebkitDocument = Document & { webkitFullscreenEnabled?: boolean; webkitFullscreenElement?: Element | null; webkitExitFullscreen?: () => Promise<void> }
type WebkitElement = HTMLElement & { webkitRequestFullscreen?: () => Promise<void> }
const doc = document as WebkitDocument

/** Opened from the Home Screen: already its own full-screen app. */
export const standalone =
  matchMedia('(display-mode: standalone)').matches ||
  matchMedia('(display-mode: fullscreen)').matches ||
  (navigator as Navigator & { standalone?: boolean }).standalone === true

export const screenMode = $state({
  canFullscreen: !standalone && !!(doc.fullscreenEnabled || doc.webkitFullscreenEnabled),
  fullscreen: false,
})

const syncFullscreen = () => (screenMode.fullscreen = !!(doc.fullscreenElement ?? doc.webkitFullscreenElement))

export async function toggleFullscreen(): Promise<void> {
  try {
    if (screenMode.fullscreen) await (doc.exitFullscreen ? doc.exitFullscreen() : doc.webkitExitFullscreen?.())
    else {
      const root = document.documentElement as WebkitElement
      await (root.requestFullscreen ? root.requestFullscreen() : root.webkitRequestFullscreen?.())
    }
  } catch {
    // refused (e.g. not from a click); nothing to undo
  }
  syncFullscreen()
}

// ---------- Derived state both layouts show ----------

class Session {
  readonly sequencer = $derived(nova.status?.sequencer)
  readonly sequence = $derived(this.sequencer?.status ?? null)

  /** The current exposure's progress during a sequence (0–100), or null. */
  readonly sequenceProgress = $derived.by(() => {
    const e = nova.exposure
    if (!this.sequencer?.running || !e) return null
    if (e.state === 'Downloading' || e.totalSeconds <= 0) return 100
    return Math.min(100, (100 * e.elapsedSeconds) / e.totalSeconds)
  })

  readonly setupLeft = $derived(setupRemaining())
  readonly setupProblems = $derived((setupGuide.check?.problems ?? []).filter((p) => !p.optional).length)
  /** The setup guide shows by itself until setup is done (unless put away), or when opened. */
  readonly showGuide = $derived(
    setupGuide.open || (setupGuide.check != null && nova.status != null && this.setupLeft > 0 && !setupGuide.check.hidden),
  )

  /** A warning or error since the Activity view was last looked at. */
  seenProblem = $state('')
  readonly lastProblem = $derived([...nova.log].reverse().find((e) => e.level !== 'info')?.timeUtc ?? '')
  readonly unseenProblem = $derived(this.lastProblem !== '' && this.lastProblem > this.seenProblem && mainView.view !== 'activity')

  readonly serverConnected = $derived(nova.status?.serverConnected ?? false)

  // Optional equipment only gets a panel when a device of that kind exists (connected or not).
  readonly hasFilterWheel = $derived(has('FilterWheel'))
  readonly hasFocuser = $derived(has('Focuser'))
  readonly hasRotator = $derived(has('Rotator'))
  readonly hasFlatPanel = $derived(has('FlatPanel'))
  /** PHD2 isn't a device: its panel shows when it's reachable, or when the user disconnected it (to reconnect). */
  readonly showGuider = $derived(nova.status?.guider ? nova.status.guider.connected || !nova.status.guider.autoConnect : false)
}

function has(kind: DeviceKind): boolean {
  return (nova.status?.devices ?? []).some((d) => d.kinds.includes(kind))
}

export const session = new Session()

export async function abortSequence(): Promise<void> {
  if (await ask('Abort the sequence?', { message: 'The current exposure is cancelled.', confirm: 'Abort', danger: true }))
    act(api.sequenceAbort)
}

// ---------- Starting it all ----------

let started = false

/** Called once by the root of the page: sign-in, live updates, and the effects every layout relies on. */
export function startSession(): () => void {
  if (started) return () => {}
  started = true
  checkAuth()
  document.addEventListener('fullscreenchange', syncFullscreen)
  document.addEventListener('webkitfullscreenchange', syncFullscreen)

  const stopEffects = $effect.root(() => {
    // Signed in (or no password): connect for live updates and check the setup.
    $effect(() => {
      if (!auth.checked) return
      if (!auth.signedIn) {
        stopHub()
        return
      }
      startHub()
      refreshSetup()
    })

    $effect(() => {
      document.documentElement.dataset.theme = theme.night ? 'night' : ''
      try {
        localStorage.setItem('nova.night', theme.night ? '1' : '0')
      } catch {
        // ignore
      }
    })

    // An error notice clears itself after 20 seconds (a new error restarts the wait).
    $effect(() => {
      const message = nova.error
      if (!message) return
      const timer = setTimeout(() => {
        if (nova.error === message) nova.error = null
      }, 20_000)
      return () => clearTimeout(timer)
    })

    // Loop: start the next exposure as soon as a new frame arrives. Shared by every Expose button (lib/exposure).
    $effect(() => {
      const id = nova.image?.id
      if (id === undefined || id === looping.lastImageId) return
      const first = looping.lastImageId === undefined
      looping.lastImageId = id
      if (exposure.loop && !first && nova.status?.camera?.connected) act(() => api.expose(exposure.duration, exposure.save))
    })

    // Finishing the last setup step while the guide is showing keeps it open, so its "All set" is seen.
    let guideWasAuto = false
    $effect(() => {
      if (session.showGuide && !setupGuide.open && session.setupLeft > 0) guideWasAuto = true
      else if (guideWasAuto && session.setupLeft === 0) {
        guideWasAuto = false
        setupGuide.open = true
      }
    })

    $effect(() => {
      if (mainView.view === 'activity') session.seenProblem = session.lastProblem
    })
  })

  // The setup guide's check of the mini PC (drivers, ASTAP, PHD2): often while it's showing, now and then otherwise.
  let ticks = 0
  const timer = setInterval(() => {
    if (auth.signedIn && (session.showGuide || ++ticks % 6 === 0)) refreshSetup()
  }, 10_000)

  return () => {
    stopEffects()
    clearInterval(timer)
    document.removeEventListener('fullscreenchange', syncFullscreen)
    document.removeEventListener('webkitfullscreenchange', syncFullscreen)
    started = false
  }
}
