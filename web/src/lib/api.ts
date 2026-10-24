import type { TerminalStatus, HotspotSettings, HotspotStatus, WebcamControls, WebcamSettings, WebcamStatus, SystemStats, GuideLogFile, AltitudeCurve, SkyField, SolvedFrame, TargetInfo, TonightFilters, TonightResult, Annotations, AutofocusSettings, MotionDirection, PolarAlignmentSettings, Sequence, CameraSettingsUpdate, DeviceKind, DeviceProperty, FitsHeaderCard, PreviewMode, StarOverlay, FlatsSettings, SolveInfo, DriverSetup, SetupCheck, NotificationSettings, NotificationSettingsView, FolderListing, NovaGuiderSettings } from './types'

import { signedOut } from './auth.svelte'

/** When guiding counts as settled (after Guide or a dither). */
export interface Settle {
  settlePixels: number
  settleSeconds: number
  settleTimeout: number
}

/** The settle part of the mount's guide defaults. */
export const settleOf = (d: { settlePixels: number; settleSeconds: number; settleTimeout: number } | null | undefined): Settle | undefined =>
  d ? { settlePixels: d.settlePixels, settleSeconds: d.settleSeconds, settleTimeout: d.settleTimeout } : undefined

// Commands go over plain REST so they are just as easy to call from curl or scripts.

async function get<T>(path: string): Promise<T> {
  const response = await fetch(path)
  if (response.status === 401) signedOut()
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    try {
      const problem = await response.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      // not a problem+json body
    }
    throw new Error(message)
  }
  return response.json()
}

const query = (params: Record<string, string | number | null | undefined>) =>
  Object.entries(params)
    .filter(([, v]) => v !== null && v !== undefined && v !== '')
    .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`)
    .join('&')

async function post(path: string, body?: unknown): Promise<void> {
  const response = await fetch(path, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (response.status === 401 && !path.startsWith('/api/auth')) signedOut()
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    try {
      const problem = await response.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      // not a problem+json body; keep the status text
    }
    throw new Error(message)
  }
}

export const api = {
  // Targets and framing
  searchTargets: (q: string, limit = 20) => get<TargetInfo[]>(`/api/targets/search?${query({ q, limit })}`),
  tonight: (f: TonightFilters, limit = 150) =>
    get<TonightResult>(
      `/api/targets/tonight?${query({
        groups: f.groups.join(','),
        minSize: f.minSize,
        maxSize: f.maxSize,
        maxMagnitude: f.maxMagnitude,
        minAltitude: f.minAltitude,
        minHours: f.minHours,
        minMoonDistance: f.minMoonDistance,
        sort: f.sort,
        limit,
      })}`,
    ),
  altitudeCurve: (ra: string, dec: string) => get<AltitudeCurve>(`/api/targets/altitude?${query({ ra, dec })}`),
  skyField: (ra: string, dec: string, radius: number, more: { magnitude?: number; maxStars?: number; objects?: 'famous'; maxObjects?: number } = {}) =>
    get<SkyField>(`/api/sky/field?${query({ ra, dec, radius, ...more })}`),
  /** id: an image in the history (the latest when not given). */
  solvedFrame: async (id?: number): Promise<SolvedFrame | null> => {
    const response = await fetch(`/api/images/latest/frame${id != null ? `?id=${id}` : ''}`)
    return response.ok ? response.json() : null
  },
  /** URL of a survey image: square, north up, fov degrees on a side (cached by the mini PC). */
  surveyUrl: (ra: number, dec: number, fov: number, survey: string, size = 1024) =>
    `/api/sky/survey?${query({ ra: ra.toFixed(5), dec: dec.toFixed(5), fov: fov.toFixed(4), survey, size })}`,

  connect: () => post('/api/equipment/connect'),
  /** Empty removes the nickname. */
  setNickname: (device: string, nickname: string) => post('/api/equipment/nickname', { device, nickname }),
  disconnect: () => post('/api/equipment/disconnect'),
  /** Saves what's in use now as an equipment profile (originalName: the one to replace or rename). */
  saveEquipmentProfile: (name: string, originalName?: string) => post('/api/equipment/profiles', { name, originalName }),
  /** Puts a profile's devices, drivers and mount connection back (equipment must be disconnected). */
  loadEquipmentProfile: (name: string) => post('/api/equipment/profiles/load', { name }),
  deleteEquipmentProfile: (name: string) => post('/api/equipment/profiles/delete', { name }),
  /** The mount over Bluetooth (this mount, by name and address) or over USB (enabled false). */
  setMountBluetooth: (enabled: boolean, mount?: string, address?: string) => post('/api/mount/bluetooth', { enabled, mount, address }),
  /** Searches for ZWO mounts for a few seconds; what's found shows in status.mountBluetooth.found. */
  scanMountBluetooth: () => post('/api/mount/bluetooth/scan'),

  /** Adds or updates a site and makes it active; pass originalName to rename. */
  saveSite: (name: string, latitude: number, longitude: number, elevation: number, originalName?: string) =>
    post('/api/site', { name, latitude, longitude, elevation, originalName }),
  selectSite: (name: string) => post('/api/site/select', { name }),
  deleteSite: (name: string) => post('/api/site/delete', { name }),
  /** Degrees around the Sun the telescope won't point while it's up; 0 turns Sun protection off. */
  setSunAvoidance: (degrees: number) => post('/api/site/sun', { degrees }),
  /** Switches Sun protection on or off (its distance is kept). */
  setSunProtection: (enabled: boolean) => post('/api/site/sun', { enabled }),
  setSettleSeconds: (seconds: number) => post('/api/mount/settle', { seconds }),
  /** A null name means automatic: use the first device of that kind. */
  select: (kind: DeviceKind, name: string | null) => post('/api/equipment/select', { kind, name }),

  expose: (duration: number, save: boolean) => post('/api/camera/expose', { duration, save }),
  abortExposure: () => post('/api/camera/abort'),
  cameraSettings: (update: CameraSettingsUpdate) => post('/api/camera/settings', update),
  /** Adds or updates a preset and makes it active; pass originalName to rename an existing one. */
  setOptics: (camera: string, name: string, focalLength: number, aperture: number, originalName?: string) =>
    post('/api/optics', { camera, name, focalLength, aperture, originalName }),
  selectOptics: (camera: string, name: string) => post('/api/optics/select', { camera, name }),
  deleteOptics: (camera: string, name: string) => post('/api/optics/delete', { camera, name }),
  cameraDefaults: (camera: string, gain: number | null, offset: number | null) => post('/api/camera/defaults', { camera, gain, offset }),
  // Where frames are saved
  storageTarget: (target: 'local' | 'usb', device?: string) => post('/api/storage/target', { target, device }),
  storageEject: () => post('/api/storage/eject'),
  storageCopy: (night: string) => post('/api/storage/copy', { night }),
  storageCopyCancel: () => post('/api/storage/copy/cancel'),

  deviceProperties: async (device: string): Promise<DeviceProperty[]> => {
    const response = await fetch(`/api/devices/${encodeURIComponent(device)}/properties`)
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
    return response.json()
  },
  setDeviceProperty: (device: string, property: string, values: Record<string, string>) =>
    post(`/api/devices/${encodeURIComponent(device)}/properties/${encodeURIComponent(property)}`, { values }),

  /** J2000 coordinates. */
  slew: (ra: string, dec: string) => post('/api/mount/slew', { ra, dec }),
  /** Slew, then expose/solve/sync until the target is centred. J2000. */
  center: (ra: string, dec: string) => post('/api/mount/center', { ra, dec }),
  /** Plate solves the latest image, or one in the history. */
  solve: (id?: number) => post(`/api/solve${id != null ? `?id=${id}` : ''}`),
  /** Plate solves the latest image (or one in the history) and returns the result (solved or not). */
  solveImage: async (id?: number): Promise<SolveInfo> => {
    const response = await fetch(`/api/solve${id != null ? `?id=${id}` : ''}`, { method: 'POST' })
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`
      try {
        const problem = await response.json()
        message = problem.detail ?? problem.title ?? message
      } catch {
        // keep the status text
      }
      throw new Error(message)
    }
    return response.json()
  },
  /** Measure the camera's angle and ask for it to be turned to angle (position angle of the frame's top), J2000 centre. */
  matchRotation: (ra: string, dec: string, angle: number) => post('/api/rotation/match', { ra, dec, angle }),
  /** The camera has been turned: check it again. */
  rotationTurned: () => post('/api/rotation/turned'),
  /** Stop asking: keep the camera as it is. */
  rotationKeep: () => post('/api/rotation/keep'),
  rotationCancel: () => post('/api/rotation/cancel'),
  syncToSolve: () => post('/api/solve/sync'),
  /** Move (true, repeat while held) or stop (false) one direction; null + false stops everything. */
  mountMotion: (direction: MotionDirection | null, move: boolean) => post('/api/mount/motion', { direction, move }),
  mountRate: (name: string) => post('/api/mount/rate', { name }),
  park: () => post('/api/mount/park'),
  unpark: () => post('/api/mount/unpark'),
  abortMount: () => post('/api/mount/abort'),

  focuserMove: (position: number) => post('/api/focuser/move', { position }),
  /** Negative steps move in, positive move out. */
  focuserStep: (steps: number) => post('/api/focuser/step', { steps }),
  focuserHalt: () => post('/api/focuser/halt'),
  /** Without a position, saves wherever the focuser is now. */
  saveFocusPreset: (focuser: string, name: string, position?: number, originalName?: string) =>
    post('/api/focuser/presets', { focuser, name, position, originalName }),
  deleteFocusPreset: (focuser: string, name: string) => post('/api/focuser/presets/delete', { focuser, name }),
  systemRestart: (options: { park: boolean; coolerOff: boolean }) => post('/api/system/restart', options),
  systemShutdown: (options: { park: boolean; coolerOff: boolean }) => post('/api/system/shutdown', options),
  sequenceStart: (sequence: Sequence) => post('/api/sequence/start', sequence),
  sequencePause: () => post('/api/sequence/pause'),
  sequenceResume: () => post('/api/sequence/resume'),
  sequenceSkip: () => post('/api/sequence/skip'),
  /** Change a row's frame count while the sequence runs (not below the frames already taken). */
  sequenceCount: (target: number, row: number, count: number) => post('/api/sequence/count', { target, row, count }),
  /** While it runs: the refocus triggers and the end of the night. */
  sequenceLive: (autofocus: Sequence['autofocus'], end: Sequence['end']) => post('/api/sequence/live', { autofocus, end }),
  sequenceSkipTarget: () => post('/api/sequence/skip-target'),
  sequenceAbort: () => post('/api/sequence/abort'),
  sequenceSave: (sequence: Sequence) => post('/api/sequences', sequence),
  sequenceDelete: (name: string) => post('/api/sequences/delete', { name }),
  sequenceLoad: async (name: string): Promise<Sequence> => {
    const response = await fetch(`/api/sequences/${encodeURIComponent(name)}`)
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
    return response.json()
  },
  polarStart: () => post('/api/polar/start'),
  polarContinue: () => post('/api/polar/continue'),
  polarStop: () => post('/api/polar/stop'),
  polarSettings: (settings: PolarAlignmentSettings) => post('/api/polar/settings', settings),
  turnPolarKnobs: (altitude: number, azimuth: number) => post('/api/simulator/polar-knobs', { altitude, azimuth }),
  autofocus: () => post('/api/autofocus/start'),
  autofocusCancel: () => post('/api/autofocus/cancel'),
  autofocusSettings: (settings: AutofocusSettings) => post('/api/autofocus/settings', settings),
  /** Flats with a panel put on by hand; name says what they're for (the folder). Null camera settings: as they are. */
  flatsStart: (name: string, settings: FlatsSettings) => post('/api/flats/start', { name, settings }),
  /** Done what the flats asked (panel on, dimmed, covered…). */
  flatsContinue: () => post('/api/flats/continue'),
  /** Skip what the flats are asking for (all of them, or just the dark flats). */
  flatsSkip: () => post('/api/flats/skip'),
  flatsCancel: () => post('/api/flats/cancel'),

  /** 1-based slot. */
  filterMove: (slot: number) => post('/api/filterwheel/move', { slot }),

  rotatorMove: (angle: number) => post('/api/rotator/move', { angle }),
  /** brightness: null keeps the panel's. */
  flatPanelLight: (on: boolean, brightness: number | null) => post('/api/flatpanel/light', { on, brightness }),
  flatPanelCover: (closed: boolean) => post('/api/flatpanel/cover', { closed }),
  rotatorHalt: () => post('/api/rotator/halt'),

  guiderConnect: () => post('/api/guider/connect'),
  guiderDisconnect: () => post('/api/guider/disconnect'),
  /** settle: the mount's defaults (guider status `defaults`); the server's own when left out. */
  guide: (recalibrate = false, settle?: Settle) => post('/api/guider/guide', { recalibrate, ...settle }),
  dither: (pixels: number, settle?: Settle) => post('/api/guider/dither', { pixels, ...settle }),
  guideProfileSave: (name: string) => post('/api/guider/nova/profiles/save', { name }),
  guideProfileLoad: (name: string) => post('/api/guider/nova/profiles/load', { name }),
  guideProfileDelete: (name: string) => post('/api/guider/nova/profiles/delete', { name }),
  guiderStop: () => post('/api/guider/stop'),
  guiderProfile: (id: number) => post('/api/guider/profile', { id }),
  guiderEquipment: (connected: boolean) => post('/api/guider/equipment', { connected }),
  guiderBackend: (backend: 'phd2' | 'nova') => post('/api/guider/backend', { backend }),
  novaGuiderSettings: (settings: NovaGuiderSettings) => post('/api/guider/nova/settings', settings),
  guiderLoop: () => post('/api/guider/loop'),
  guiderCalibrate: () => post('/api/guider/calibrate'),
  guiderClearCalibration: () => post('/api/guider/calibration/clear'),
  /** NOVA's guider: follow the star unguided for this long and measure seeing, drift and periodic error. */
  guiderAssistant: (seconds: number) => post('/api/guider/assistant', { seconds }),
  guiderApplyAssistant: () => post('/api/guider/assistant/apply'),
  /** Takes a dark library for the guide camera: frames darks at each exposure (s). The scope must be covered. */
  guideDarks: (exposures: number[], frames: number) => post('/api/guider/darks/build', { exposures, frames }),
  clearGuideDarks: () => post('/api/guider/darks/clear'),
  guideLogs: () => get<GuideLogFile[]>('/api/guider/logs'),
  systemStats: () => get<SystemStats>('/api/system/stats'),
  webcam: () => get<WebcamStatus>('/api/webcam'),
  webcamSettings: (settings: WebcamSettings) => post('/api/webcam/settings', settings),
  webcamControls: () => get<WebcamControls>('/api/webcam/controls'),
  setWebcamControl: (name: string, value: number) => post('/api/webcam/controls', { name, value }),
  resetWebcamControls: () => post('/api/webcam/controls/reset'),
  guideLogUrl: (log: GuideLogFile) => `/api/guider/logs/${encodeURIComponent(log.name)}?drive=${log.onDrive}`,
  /** stretch: low (black background), medium or high (full auto stretch). */
  guideFrameUrl: (frame: number, stretch = 'medium') => `/api/guider/frame.png?f=${frame}&stretch=${stretch}`,
  /** Guide on the star nearest this point of the guide frame (its pixels). */
  guideSelectStar: (x: number, y: number) => post('/api/guider/select', { x, y }),

  /** size: longest side in pixels, 0 for full resolution. */
  // Image numbers start again from 1 when NOVA restarts, so the address also carries when the image was taken: a
  // new image must never be given an old one the browser still has.
  previewUrl: (image: { id: number; capturedUtc: string }, mode: PreviewMode, stretch: boolean, size: number) =>
    `/api/images/latest/preview.png?id=${image.id}&t=${Date.parse(image.capturedUtc)}&mode=${mode}&stretch=${stretch}&size=${size}`,
  thumbnailUrl: (image: { id: number; capturedUtc: string }) => `/api/images/${image.id}/thumbnail.png?t=${Date.parse(image.capturedUtc)}`,
  annotations: async (count = 40, id?: number): Promise<Annotations> => {
    const response = await fetch(`/api/images/latest/annotations?count=${count}${id != null ? `&id=${id}` : ''}`)
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
    return response.json()
  },
  stars: async (id?: number): Promise<StarOverlay> => {
    const response = await fetch(`/api/images/latest/stars${id != null ? `?id=${id}` : ''}`)
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
    return response.json()
  },
  /** The image folders on the mini PC (or its USB drive): folders and FITS files in one of them. */
  browseFiles: (root: string, path: string) => get<FolderListing>(`/api/files?${query({ root, path })}`),
  /** Opens a FITS file saved on the mini PC (or its USB drive) in the viewer. */
  openMiniPcFile: (root: string, path: string) => post('/api/images/open-file', { root, path }),
  /** Sends a FITS file from this computer to become the viewer's image (solve it, measure its stars…). */
  openFits: async (file: File): Promise<void> => {
    const response = await fetch(`/api/images/open?name=${encodeURIComponent(file.name)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/octet-stream' },
      body: file,
    })
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`
      try {
        const problem = await response.json()
        message = problem.detail ?? problem.title ?? message
      } catch {
        // keep the status text
      }
      throw new Error(message)
    }
  },
  fitsHeader: async (id?: number): Promise<FitsHeaderCard[]> => {
    const response = await fetch(`/api/images/latest/header${id != null ? `?id=${id}` : ''}`)
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
    return response.json()
  },
  fitsUrl: (id?: number) => `/api/images/latest/fits${id != null ? `?id=${id}` : ''}`,
  notifications: () => get<NotificationSettingsView>('/api/notifications'),
  /** Saves the notification settings; token: undefined keeps the current one, '' removes it. */
  saveNotifications: (settings: NotificationSettings, token?: string) => post('/api/notifications', { settings, token }),
  testNotification: () => post('/api/notifications/test'),
  terminal: () => get<TerminalStatus>('/api/terminal'),
  hotspot: () => get<{ status: HotspotStatus; settings: HotspotSettings }>('/api/hotspot'),
  saveHotspot: (settings: HotspotSettings) => post('/api/hotspot/settings', settings),
  startHotspot: () => post('/api/hotspot/start'),
  stopHotspot: () => post('/api/hotspot/stop'),
  /** Per-filter focus offsets: save (typed in), measure (autofocus on each filter), cancel measuring. */
  saveFocusOffsets: (focuser: string, enabled: boolean, reference: string | null, offsets: Record<string, number>) =>
    post('/api/focuser/offsets', { focuser, enabled, reference, offsets }),
  measureFocusOffsets: (passes: number) => post('/api/focuser/offsets/measure', { passes }),
  cancelFocusOffsets: () => post('/api/focuser/offsets/cancel'),
  /** Signs this browser in with NOVA's password. */
  signIn: (password: string) => post('/api/auth/login', { password }),
  signOut: () => post('/api/auth/logout'),
  /** Sets or changes the password (empty removes it); the current one is needed while one is set. */
  setPassword: (current: string, password: string) => post('/api/auth/password', { current, new: password }),
  /** The first-run guide's checks of the mini PC. */
  setupCheck: () => get<SetupCheck>('/api/setup'),
  /** Puts the first-run guide away (or brings it back). */
  setSetupHidden: (hidden: boolean) => post('/api/setup/hidden', { hidden }),
  drivers: () => get<DriverSetup>('/api/drivers'),
  /** Saves the INDI drivers to run, and starts or stops them now when indiserver allows. */
  setDrivers: async (drivers: string[]): Promise<{ started: string[]; stopped: string[]; missing: string[]; note: string | null }> => {
    const response = await fetch('/api/drivers', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ drivers }),
    })
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`
      try {
        const problem = await response.json()
        message = problem.detail ?? problem.title ?? message
      } catch {
        // keep the status text
      }
      throw new Error(message)
    }
    return response.json()
  },
  /** How many images the history keeps (3–20). */
  historySize: (size: number) => post('/api/images/history', { size }),
  pinImage: (id: number, pinned: boolean) => post(`/api/images/${id}/pin`, { pinned }),
}
