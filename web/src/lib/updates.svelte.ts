import { api } from './api'
import { nova } from './state.svelte'
import type { UpdateStatus } from './types'

/**
 * Whether a newer NOVA release is out. The mini PC asks GitHub once a day; the page reads its answer (when it opens,
 * hourly, and when the Mini PC tab shows). It only tells: updating is "nova update" on the mini PC. "Not now" puts a
 * release away on this browser until the next one.
 */
const DismissedKey = 'nova.updateDismissed'

function stored(): string | null {
  try {
    return localStorage.getItem(DismissedKey)
  } catch {
    return null
  }
}

export const updates = $state({
  status: null as UpdateStatus | null,
  /** The release notes dialog. */
  open: false,
  checking: false,
  /** The version put away with "Not now". */
  dismissed: stored(),
})

export async function refreshUpdates(): Promise<void> {
  try {
    updates.status = await api.update()
  } catch {
    // keep the last answer; the server may be restarting
  }
}

/** The top bar's chip: a newer release, not put away. */
export const updateShown = () => !!updates.status?.available && updates.status.latest?.version !== updates.dismissed

export function dismissUpdate() {
  const version = updates.status?.latest?.version ?? null
  updates.dismissed = version
  updates.open = false
  try {
    if (version) localStorage.setItem(DismissedKey, version)
  } catch {
    // no storage (a private window): put away for this visit
  }
}

export async function checkNow() {
  updates.checking = true
  try {
    await api.checkUpdate()
  } catch (e) {
    nova.error = (e as Error).message
  }
  await refreshUpdates()
  updates.checking = false
}

export async function setDailyCheck(enabled: boolean) {
  try {
    await api.updateSettings(enabled)
  } catch (e) {
    nova.error = (e as Error).message
  }
  await refreshUpdates()
}

let started = false
/** Called once by the dialog (which every layout mounts). */
export function watchUpdates(): () => void {
  if (started) return () => {}
  started = true
  refreshUpdates()
  const timer = setInterval(refreshUpdates, 3600_000)
  return () => {
    clearInterval(timer)
    started = false
  }
}
