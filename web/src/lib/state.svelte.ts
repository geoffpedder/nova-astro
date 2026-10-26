import * as signalR from '@microsoft/signalr'
import type { ExposureStatus, ImageInfo, LogEntry, StatusSnapshot } from './types'
import { signedOut } from './auth.svelte'

const MaxLogEntries = 300

/** Everything the UI shows. The backend owns the truth; this is a live mirror fed by the hub. */
export const nova = $state({
  hub: 'connecting' as 'connecting' | 'connected' | 'reconnecting',
  status: null as StatusSnapshot | null,
  exposure: null as ExposureStatus | null,
  image: null as ImageInfo | null,
  log: [] as LogEntry[],
  error: null as string | null,
  /** Device whose driver settings panel is open, if any. */
  settingsDevice: null as string | null,
})

/** Runs a command, surfacing any failure in the header instead of throwing. */
/** A device's nickname if it has one, else its own name. */
export function nick(name: string | null | undefined): string {
  if (!name) return ''
  return nova.status?.nicknames?.[name] ?? name
}

/**
 * Why the user can't move the mount right now (the same rule the server applies), or null: an exposure or a
 * sequence would be spoiled. Stop and Park stay available.
 */
export function mountLock(): string | null {
  if (nova.status?.sequencer?.running) return 'Locked while a sequence is running, paused too (Stop and Park still work)'
  if (nova.exposure) return 'Locked while the camera is exposing (Stop and Park still work)'
  return null
}

export async function act(command: () => Promise<unknown>): Promise<boolean> {
  try {
    await command()
    nova.error = null
    return true
  } catch (e) {
    nova.error = e instanceof Error ? e.message : String(e)
    return false
  }
}

let hub: signalR.HubConnection | null = null

/** Disconnects live updates (signing out). */
export async function stopHub(): Promise<void> {
  const connection = hub
  hub = null
  await connection?.stop()
}

export function startHub(): void {
  if (hub) return
  // Event names match NovaHub.Events in src/Nova.Server/Hubs/NovaHub.cs.
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub')
    // Never give up: the mini PC may reboot or Wi-Fi may drop mid-session.
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => 2000 })
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  connection.on('status', (status: StatusSnapshot) => {
    nova.status = status
    nova.exposure = status.exposure
    if (status.latestImage && status.latestImage.id !== nova.image?.id) nova.image = status.latestImage
  })
  // Handlers return nothing: SignalR takes a returned value as a reply the server didn't ask for, and logs an error.
  connection.on('exposureProgress', (exposure: ExposureStatus | null) => {
    nova.exposure = exposure
  })
  connection.on('imageReady', (image: ImageInfo) => {
    nova.image = image
  })
  connection.on('logHistory', (entries: LogEntry[]) => {
    nova.log = entries
  })
  connection.on('log', (entry: LogEntry) => {
    nova.log.push(entry)
    if (nova.log.length > MaxLogEntries) nova.log.splice(0, nova.log.length - MaxLogEntries)
  })

  connection.onreconnecting(() => (nova.hub = 'reconnecting'))
  connection.onreconnected(() => (nova.hub = 'connected'))

  // Automatic reconnect only covers drops after the first successful start.
  const start = async () => {
    if (hub !== connection) return // stopped (signed out) meanwhile
    try {
      await connection.start()
      nova.hub = 'connected'
    } catch (e) {
      // Refused for want of a password: the sign-in screen takes over (and starts a new connection after).
      if (e instanceof signalR.HttpError && e.statusCode === 401) {
        signedOut()
        await stopHub()
        return
      }
      nova.hub = 'reconnecting'
      setTimeout(start, 3000)
    }
  }
  hub = connection
  start()
}
