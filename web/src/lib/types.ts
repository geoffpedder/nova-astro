// Mirrors the C# records in src/Nova.Server/Services (serialized camelCase, enums as strings).

export type DeviceKind =
  | 'Camera' | 'Mount' | 'Focuser' | 'FilterWheel' | 'Rotator' | 'Guider' | 'Dome' | 'Weather' | 'FlatPanel' | 'Other'
export type CameraState = 'Idle' | 'Exposing' | 'Downloading' | 'Error'
export type MountState = 'Idle' | 'Tracking' | 'Slewing' | 'Parking' | 'Parked' | 'Error'
export interface NetworkInterfaceInfo {
  name: string
  kind: 'wifi' | 'wired'
  /** Wi-Fi: 'client' (joined a network) or 'hotspot' (the mini PC is the access point). */
  mode: 'client' | 'hotspot' | null
  ssid: string | null
  signalPercent: number | null
  addresses: string[]
  /** Has the route to the router: the connection actually in use. */
  gateway: boolean
}

export type MotionDirection = 'North' | 'South' | 'East' | 'West'

export type FrameType = 'Light' | 'Bias' | 'Dark' | 'Flat'

export interface DeviceInfo {
  name: string
  kinds: DeviceKind[]
  isConnected: boolean
}

export interface NumberSetting {
  value: number
  min: number
  max: number
  step: number
}

/** Temperatures in °C. canCool is false for uncooled cameras that only report temperature. */
export interface CoolerStatus {
  canCool: boolean
  on: boolean
  temperature: number | null
  targetTemperature: number | null
  powerPercent: number | null
}

/** Null members are not supported by this camera. */
export interface CameraControls {
  cooler: CoolerStatus | null
  gain: NumberSetting | null
  offset: NumberSetting | null
  binning: NumberSetting | null
  frameType: FrameType | null
}

export interface CameraSettingsUpdate {
  coolerOn?: boolean
  targetTemperature?: number
  gain?: number
  offset?: number
  binning?: number
  frameType?: FrameType
}

export interface CameraStatus {
  name: string
  connected: boolean
  state: CameraState
  controls: CameraControls | null
}

/** Coordinates are J2000 (converted from the mount's JNow, which is also given). */
export interface MountStatus {
  name: string
  connected: boolean
  state: MountState
  raHours: number
  decDegrees: number
  raText: string
  decText: string
  jNowRaText: string
  jNowDecText: string
  /** Side of the pier, if the mount reports it. */
  sideOfPier: 'East' | 'West' | null
  /** Hours from the meridian: negative = still east of it. Null without a site. */
  hourAngle: number | null
  /** Manual-motion speeds; the current one; directions moving now. */
  slewRates: { name: string; label: string }[] | null
  slewRate: string | null
  moving: MotionDirection[] | null
}

/** A plate solve of one image; the centre is J2000. */
export interface SolveInfo {
  imageId: number
  solved: boolean
  raHours: number | null
  decDegrees: number | null
  raText: string | null
  decText: string | null
  rotation: number | null
  imageScale: number | null
  flipped: boolean
  fieldWidth: number | null
  fieldHeight: number | null
  error: string | null
  seconds: number
  finishedUtc: string
}

export interface CenteringStatus {
  running: boolean
  attempt: number
  maxAttempts: number
  errorArcmin: number | null
  message: string
}

/** Turning the camera by hand to the planned framing angle (no rotator). Angles: position angle of the frame's top. */
export interface RotationStatus {
  running: boolean
  /** Waiting for the camera to be turned (Check again) or kept as it is. */
  waiting: boolean
  plannedAngle: number
  cameraAngle: number | null
  /** Degrees on the sky, + towards east. */
  turnDegrees: number | null
  /** 'clockwise' | 'anticlockwise', seen from behind the camera. */
  direction: string | null
  /** The quickest turn frames the plan upside down (the same sky). */
  upsideDown: boolean
  checks: number
  message: string
  /** The sequencer is waiting on this. */
  fromSequence: boolean
  tolerance: number
  /** The rotator doing the turning, if there is one. */
  rotator: string | null
}

/** A named focuser position, e.g. with and without a reducer. */
export interface FocusPreset {
  name: string
  position: number
}

/** One line of an exposure plan. Null gain/offset/binning keep the camera's current setting. */
export interface SequenceRow {
  filter: string | null
  exposureSeconds: number
  count: number
  gain: number | null
  offset: number | null
  binning: number | null
  frameType: FrameType
  enabled: boolean
}

export interface SequenceTarget {
  name: string
  /** J2000, "hh:mm:ss" or decimal hours */
  ra: string
  /** J2000, "±dd:mm:ss" or decimal degrees */
  dec: string
  /** Planned camera angle (position angle of the frame's top, degrees); null/absent: any. */
  rotation?: number | null
}

/** One target of a night's plan: where, which frames, and when. */
export interface SequenceTargetPlan {
  target: SequenceTarget | null
  rows: SequenceRow[]
  order: 'ByRow' | 'Cycle'
  /** Local "22:30": don't start before. */
  startAfter: string | null
  /** Local "01:15": move on then. */
  stopAt: string | null
  /** Degrees: wait until this high, move on when lower. Null = the horizon. */
  minAltitude: number | null
}

/** A night's imaging plan (J2000). Older single-target plans use target/rows/order instead of targets. */
export interface Sequence {
  name: string
  target: { name: string; ra: string; dec: string } | null
  start: { coolCamera: boolean; coolTo: number; centerTarget: boolean; autofocus: boolean; startGuiding: boolean; matchRotation: boolean }
  rows: SequenceRow[]
  order: 'ByRow' | 'Cycle'
  dither: { everyFrames: number; pixels: number; raOnly: boolean; settlePixels: number; settleSeconds: number; settleTimeout: number }
  autofocus: {
    afterFilterChange: boolean
    temperatureChange: number | null
    hfrIncreasePercent: number | null
    everyFrames: number | null
  }
  /** stopAt: when the night ends for every target: AstronomicalDawn, NauticalDawn, Sunrise, a local time ("05:30") or Never. */
  end: { stopGuiding: boolean; warmCamera: boolean; park: boolean; stopAt?: string | null }
  targets?: SequenceTargetPlan[] | null
  meridianFlip: {
    enabled: boolean
    minutesAfterMeridian: number
    maxMinutesAfterMeridian: number
    recenter: boolean
    autofocusAfter: boolean
    settleSeconds: number
  }
  /** Flats with a hand-placed panel: before the camera is turned for the next target, and at the end. */
  flats?: { enabled: boolean } | null
}

/** How flats are taken (the flats wizard's settings, also used by sequences). */
export interface FlatsSettings {
  /** Median brightness to aim for, % of full scale. */
  targetPercent: number
  /** Flats, and as many dark flats. */
  count: number
  darkFlats: boolean
  minExposure: number
  maxExposure: number
  /** With a flat panel connected, NOVA switches it, sets its brightness and closes its cover. */
  usePanel: boolean
}

export interface FlatsStatus {
  running: boolean
  phase: 'Panel' | 'Calibrating' | 'Flats' | 'Cover' | 'DarkFlats' | 'Done' | 'Skipped' | 'Failed' | 'Cancelled'
  message: string
  /** What NOVA is waiting for you to do. */
  ask: 'PanelOn' | 'Dimmer' | 'Brighter' | 'Cover' | null
  name: string | null
  targetAdu: number
  exposure: number | null
  median: number | null
  flats: number
  darkFlats: number
  count: number
  takeDarkFlats: boolean
  tests: { exposure: number; median: number }[]
  fromSequence: boolean
  folder: string | null
  /** The flat panel NOVA is driving, if any. */
  panel: string | null
  panelBrightness: number | null
}

export interface FlatsInfo {
  running: boolean
  settings: FlatsSettings
  status: FlatsStatus | null
}

export interface SequencerStatus {
  state: 'Running' | 'Paused' | 'Done' | 'Failed' | 'Aborted'
  sequenceName: string
  target: string | null
  step: string
  message: string
  pauseRequested: boolean
  framesDone: number
  framesTotal: number
  rows: { done: number; count: number; skipped: boolean }[]
  currentRow: number | null
  lastHfr: number | null
  autofocuses: number
  dithers: number
  failures: number
  startedUtc: string
  etaUtc: string | null
  finishedUtc: string | null
  flips: number
  /** When the next meridian flip is due, while one is. */
  flipDueUtc: string | null
  /** Every target's progress; state is Waiting, Running, Done, Skipped or Stopped. */
  targets: { name: string; state: string; rows: { done: number; count: number; skipped: boolean }[]; note: string | null }[] | null
  currentTarget: number
  /** When the night ends for the sequence (dawn by default), and how to say it ("astronomical dawn (05:12)"). */
  nightEndUtc: string | null
  nightEnd: string | null
}

export interface SequencerInfo {
  running: boolean
  status: SequencerStatus | null
  saved: string[]
}

export interface PolarAlignmentSettings {
  /** ThreePoint: three solves anywhere, turning RA between them (N.I.N.A.'s TPPA). Pole: the camera on the pole with Polaris in view, RA turned about 90° between two solves (as SharpCap). */
  method: 'ThreePoint' | 'Pole'
  /** Three-point: how far RA turns between the points. */
  stepDegrees: number
  direction: 'West' | 'East'
  exposureSeconds: number
  refraction: boolean
  manual: boolean
  /** GoTo the start first (the mount roughly polar aligned, e.g. at home); false: wherever it points. */
  startAutomatically: boolean
  /** Three-point start: degrees above the pole, and east of it. */
  altitudeOffset: number
  azimuthOffset: number
  /** Pole method: how far RA turns between the two frames. */
  poleTurnDegrees: number
}

/** + altitude: axis above the pole (lower it). + azimuth: axis right of the pole as you face it (move it left). */
export interface PolarError {
  altitudeArcmin: number
  azimuthArcmin: number
  totalArcmin: number
  axisAltitude: number
  axisAzimuth: number
  /** Why the numbers may be unreliable right now (field due east/west or overhead, very large error). */
  warning: string | null
}

export interface PolarAlignmentRun {
  running: boolean
  phase: 'Measuring' | 'Adjusting' | 'Done' | 'Failed' | 'Cancelled'
  message: string
  point: number
  waitingForRotation: boolean
  northernHemisphere: boolean
  error: PolarError | null
  initialError: PolarError | null
  history: { utc: string; altitudeArcmin: number; azimuthArcmin: number; totalArcmin: number }[]
  frames: number
  startedUtc: string
  method: 'ThreePoint' | 'Pole'
  /** How many points this method measures (3 or 2). */
  points: number
  /** Pole method: where the axis, the pole and Polaris are in the latest frame (full-size pixels; may lie outside). */
  overlay: {
    imageId: number
    width: number
    height: number
    arcsecPerPixel: number
    axis: [number, number] | null
    pole: [number, number] | null
    polaris: [number, number] | null
    upFromAxis: [number, number] | null
  } | null
  /** The last frame polar alignment took (the tab shows only its own frames, not an image opened meanwhile). */
  imageId: number | null
}

export interface PolarAlignmentInfo {
  running: boolean
  settings: PolarAlignmentSettings
  status: PolarAlignmentRun | null
  /** Simulator only: the true error and virtual knobs. */
  knobs: { altitudeErrorArcmin: number; azimuthErrorArcmin: number } | null
}

export interface AutofocusSettings {
  /** Focuser steps between measurements. */
  stepSize: number
  /** Measurements either side of the start (2 × this + 1 in all). */
  stepsPerSide: number
  exposureSeconds: number
  /** Overshoot for outward moves so every position is reached moving inward; 0 = off. */
  backlash: number
}

export interface FocusPoint {
  position: number
  hfr: number
  stars: number
}

/** HFR(x) = √(a + b·(x − c)²); c is best focus. */
export interface FocusFit {
  a: number
  b: number
  c: number
  rSquared: number
  bestPosition: number
  bestHfr: number
}

export interface AutofocusRun {
  running: boolean
  message: string
  succeeded: boolean | null
  startPosition: number
  points: FocusPoint[]
  fit: FocusFit | null
  bestPosition: number | null
  finalHfr: number | null
  /** HFR at the start position; the result must not be worse. */
  startHfr: number | null
  temperature: number | null
  filter: string | null
  startedUtc: string
}

export interface AutofocusInfo {
  running: boolean
  settings: AutofocusSettings
  last: AutofocusRun | null
}

export interface SolverStatus {
  name: string
  available: boolean
  problem: string | null
  solving: boolean
  last: SolveInfo | null
  centering: CenteringStatus | null
  rotation: RotationStatus | null
}

export interface ExposureStatus {
  elapsedSeconds: number
  totalSeconds: number
  state: CameraState
}

export interface ImageStatistics {
  min: number
  max: number
  mean: number
  median: number
  mad: number
}

/** Star measurements for one frame. HFR (half-flux radius) is the focus measure: smaller is sharper. */
export interface StarSummary {
  count: number
  medianHfr: number | null
  hfrArcsec: number | null
  medianFwhm: number | null
  fwhmArcsec: number | null
}

export interface HfrPoint {
  imageId: number
  capturedUtc: string
  hfr: number | null
  stars: number
}

/** Named catalog stars placed on the image by its plate solution (full-resolution pixels). */
export interface Annotations {
  imageId: number
  width: number
  height: number
  stars: { x: number; y: number; label: string; detail: string; magnitude: number }[]
}

/** Detected stars in the frame's full-resolution pixels. */
export interface StarOverlay {
  imageId: number
  width: number
  height: number
  stars: { x: number; y: number; hfr: number }[]
}

export interface ImageInfo {
  id: number
  capturedUtc: string
  durationSeconds: number
  width: number
  height: number
  previewWidth: number
  previewHeight: number
  statistics: ImageStatistics
  savedPath: string | null
  /** Set when this image is a FITS file opened from the user's computer rather than a capture. */
  openedFile: string | null
  /** From the FITS header (e.g. "RGGB"); null for mono cameras. */
  bayerPattern: string | null
  stars: StarSummary
}

export interface FitsHeaderCard {
  key: string
  value: string
  comment: string | null
}

/** How the preview is rendered: mono, auto (debayer if the header has a pattern) or a forced pattern. */
export type PreviewMode = 'mono' | 'auto' | 'RGGB' | 'BGGR' | 'GRBG' | 'GBRG'

/** Chosen device names per role; null means automatic (first found). */
export interface EquipmentSelection {
  camera: string | null
  mount: string | null
  focuser: string | null
  filterWheel: string | null
  rotator: string | null
  flatPanel: string | null
}

export interface OpticsPreset {
  name: string
  focalLength: number
  aperture: number
  focalRatio: number
  imageScale: number | null
}

/** Telescope optics for one camera; the top-level values are the active preset's. imageScale is arcsec per unbinned pixel. */
export interface CameraOpticsInfo {
  camera: string
  connected: boolean
  activePreset: string | null
  presets: OpticsPreset[]
  focalLength: number | null
  aperture: number | null
  pixelSize: number | null
  focalRatio: number | null
  imageScale: number | null
  /** Full sensor in unbinned pixels, as last seen while the camera was connected. */
  sensorWidth: number | null
  sensorHeight: number | null
  /** Set whenever the camera connects; null leaves the driver's own. */
  defaultGain: number | null
  defaultOffset: number | null
  /** The defaults are NOVA's own (100/50 for a ZWO imaging camera), not ones chosen for this camera. */
  builtInDefaults: boolean
}

export interface FilterWheelStatus {
  name: string
  connected: boolean
  /** 1-based slot. */
  position: number
  /** Index 0 = slot 1. */
  names: string[]
  isMoving: boolean
}

/** A flat panel: its light and, on a flip-flat, its cover. */
export interface FlatPanelStatus {
  name: string
  connected: boolean
  hasLight: boolean
  lightOn: boolean
  brightness: { value: number; min: number; max: number; step: number } | null
  hasCover: boolean
  /** Null without a cover. */
  coverClosed: boolean | null
  coverMoving: boolean
}

export interface RotatorStatus {
  name: string
  connected: boolean
  angle: number
  isMoving: boolean
}

export type GuiderState =
  | 'Disconnected' | 'Stopped' | 'Selected' | 'Calibrating' | 'Guiding' | 'LostLock' | 'Paused' | 'Looping'

export interface GuideStats {
  rmsRa: number
  rmsDec: number
  rmsTotal: number
  peakRa: number
  peakDec: number
  samples: number
  /** "arcsec" once PHD2 knows the pixel scale, else "px". */
  units: string
}

export interface GuiderStatus {
  host: string
  connected: boolean
  state: GuiderState
  settling: boolean
  version: string | null
  pixelScale: number | null
  snr: number | null
  hfd: number | null
  stats: GuideStats
  history: { ra: number; dec: number }[]
  profiles: { id: number; name: string }[]
  profile: { id: number; name: string } | null
  /** PHD2 has its own camera and mount connected. */
  equipmentConnected: boolean
  /** noVNC port for PHD2's screen, or null. */
  screenPort: number | null
  /** NOVA keeps trying to reach PHD2 (false after the user pressed Disconnect). */
  autoConnect: boolean
  /** A steady Dec drift guiding keeps correcting, once there are 5 minutes of guiding to tell from. */
  drift: DecDrift | null
}

export interface DecDrift {
  arcsecPerMinute: number
  /** Which way guiding keeps pushing. */
  direction: 'North' | 'South'
  /** Share of Dec corrections that went that way (0–1). */
  oneWay: number
  minutes: number
  /** The least polar error that drifts this fast. */
  polarErrorArcmin: number
  /** Steady and fast enough that polar aligning again would help. */
  significant: boolean
}

/** Positions in steps; "in" is decreasing, "out" increasing. */
export interface FocuserStatus {
  name: string
  connected: boolean
  canMoveAbsolute: boolean
  position: number
  minPosition: number
  maxPosition: number
  isMoving: boolean
  temperature: number | null
  presets: FocusPreset[]
  /** Per-filter focus offsets (steps from the reference filter), and a measurement in progress or just done. */
  offsets: {
    enabled: boolean
    reference: string | null
    offsets: Record<string, number>
    measuring: { running: boolean; message: string; results: { filter: string; position: number | null; message: string }[]; drift: number | null } | null
  } | null
}

export interface GeoLocation {
  latitude: number
  longitude: number
  elevation: number
}

/** Latitude +N, longitude +E (-180..180), elevation in metres. */
/** Messages to your phone through ntfy (GET/POST /api/notifications). The token never comes back: hasToken says if one is set. */
export interface NotificationSettings {
  enabled: boolean
  server: string
  topic: string
  sequenceDone: boolean
  sequenceFailed: boolean
  targetSkipped: boolean
  needsYou: boolean
  guidingLost: boolean
  guidingLostMinutes: number
  /** Frames had to go to the mini PC although a USB drive is chosen. */
  storage: boolean
}
export interface NotificationSettingsView extends NotificationSettings {
  hasToken: boolean
  lastError: string | null
}

/** Something on the mini PC that stops part of NOVA working, and the command that fixes it. */
export interface SetupProblem {
  text: string
  fix: string | null
  /** NOVA works without it (e.g. PHD2). */
  optional: boolean
}

/** The first-run guide's view of the mini PC (GET /api/setup). */
export interface SetupCheck {
  solvedOnce: boolean
  hidden: boolean
  driversApplicable: boolean
  driversChosen: number
  driversInstalled: number
  problems: SetupProblem[]
}

export interface Site {
  name: string
  latitude: number
  longitude: number
  elevation: number
}

export interface SiteStatus {
  sites: Site[]
  active: string | null
  mountLocation: GeoLocation | null
  mountMismatch: boolean
  /** The mini PC's clock (ISO, UTC). */
  serverUtc: string
  /** Degrees from the Sun the telescope is kept while the Sun is up (0 = off). */
  sunAvoidance: number
  /** The Sun's altitude now, when the site is known. */
  sunAltitude: number | null
  /** Whether Sun protection is switched on (sunAvoidance is kept while it's off). */
  sunProtection: boolean
  /** Seconds the mount is left to settle after every GoTo. */
  settleSeconds: number
}

/** The Simulators switch (INDI only). */
export interface SimulatorStatus {
  applicable: boolean
  enabled: boolean
  /** NOVA can stop/start the drivers; otherwise simulators are only hidden. */
  driverControl: boolean
}

export interface StatusSnapshot {
  backend: string
  /** NOVA is talking to the INDI server (always true for the simulator). */
  serverConnected: boolean
  /** The user pressed Connect: the chosen camera and mount should be connected. */
  equipmentConnected: boolean
  /** Raw driver settings are available (INDI only). */
  hasDriverSettings: boolean
  simulators: SimulatorStatus
  devices: DeviceInfo[]
  selection: EquipmentSelection
  camera: CameraStatus | null
  mount: MountStatus | null
  focuser: FocuserStatus | null
  filterWheel: FilterWheelStatus | null
  rotator: RotatorStatus | null
  flatPanel: FlatPanelStatus | null
  guider: GuiderStatus
  optics: CameraOpticsInfo[]
  site: SiteStatus
  plateSolve: SolverStatus
  autofocus: AutofocusInfo
  polarAlignment: PolarAlignmentInfo
  sequencer: SequencerInfo
  /** Restart / shut down the mini PC: whether allowed, why not, and whether it's under way. */
  power: { available: boolean; problem: string | null; pending: string | null }
  /** How the mini PC is connected (refreshed every 15 s). */
  network: { primary: NetworkInterfaceInfo | null; interfaces: NetworkInterfaceInfo[]; hostname: string | null }
  /** NOVA as a telescope for Stellarium's Telescope Control plugin; null when turned off. */
  stellarium: { port: number; clients: number } | null
  hfrHistory: HfrPoint[]
  exposure: ExposureStatus | null
  latestImage: ImageInfo | null
  /** Friendly names the user gave devices (device name → nickname). */
  nicknames: Record<string, string>
  flats: FlatsInfo | null
  imageHistory: { size: number; images: HistoryImage[] } | null
  /** NOVA's version: "0.2.0", or "0.0.0-dev" for a local build. */
  version: string | null
  /** Where frames are saved: the mini PC or a USB drive. */
  storage: StorageStatus | null
}

/** A folder or FITS file in the mini PC's image folders (Open… → From the mini PC). */
export interface FileEntry {
  name: string
  folder: boolean
  bytes: number | null
  modifiedUtc: string | null
}

export interface FolderListing {
  /** 'local' (the mini PC's image folder) or 'usb' (the mounted USB drive). */
  root: string
  /** Relative to the root, '' at its top, '/' between folders. */
  path: string
  entries: FileEntry[]
  roots: { id: string; label: string }[]
}

export interface DriveView {
  device: string
  name: string
  model: string | null
  sizeBytes: number
  /** While it's mounted. */
  freeBytes: number | null
  mounted: boolean
  /** The drive frames go to when saving to USB. */
  selected: boolean
  fileSystem: string | null
}

export interface StorageStatus {
  target: 'local' | 'usb'
  /** Whether this system can use USB drives at all (udisks2 on the mini PC). */
  available: boolean
  drives: DriveView[]
  /** The folder new frames go to right now. */
  savingTo: string
  onUsb: boolean
  localFolder: string
  /** Why frames aren't going to the chosen drive. */
  warning: string | null
  copy: { night: string; running: boolean; filesDone: number; filesTotal: number; bytesDone: number; bytesTotal: number; error: string | null; skipped: number } | null
  /** Nights saved on the mini PC, newest first. */
  nights: { night: string; files: number; bytes: number }[]
}

/** An INDI driver, as Equipment → Drivers… lists it. */
export interface DriverEntry {
  exec: string
  labels: string[]
  manufacturer: string | null
  group: string
  installed: boolean
  selected: boolean
  devices: string[]
  package: string | null
}

export interface DriverSetup {
  applicable: boolean
  liveControl: boolean
  envFile: string
  writable: boolean
  problem: string | null
  drivers: DriverEntry[]
}

/** An image kept to look back at and compare. */
export interface HistoryImage {
  info: ImageInfo
  pinned: boolean
  solve: SolveInfo | null
}

/** The image viewer: one image, two side by side, or two blinking. */
export type CompareMode = 'single' | 'side' | 'blink'

export interface LogEntry {
  timeUtc: string
  level: 'info' | 'warn' | 'error'
  message: string
}

export type DevicePropertyKind = 'Number' | 'Switch' | 'Text' | 'Light' | 'Blob'

export interface DevicePropertyElement {
  name: string
  label: string
  /** Raw value ("On"/"Off" for switches). */
  value: string
  /** Formatted as the driver asks, e.g. sexagesimal. */
  display: string
  min: number | null
  max: number | null
  step: number | null
}

export interface DeviceProperty {
  name: string
  label: string
  group: string
  kind: DevicePropertyKind
  permission: 'ro' | 'wo' | 'rw'
  state: 'Idle' | 'Ok' | 'Busy' | 'Alert'
  rule: 'OneOfMany' | 'AtMostOne' | 'AnyOfMany' | null
  elements: DevicePropertyElement[]
}

// ---- Targets and framing ----

export interface Visibility {
  maxAltitude: number
  bestUtc: string
  /** Hours of darkness above the minimum altitude. */
  hoursAbove: number
  moonSeparation: number
}

/** A deep-sky object from NOVA's catalog (J2000). */
export interface TargetInfo {
  label: string
  commonName: string | null
  names: string[]
  type: string
  typeName: string
  group: 'galaxy' | 'nebula' | 'cluster' | 'planetary' | 'star' | 'planet'
  raHours: number
  decDegrees: number
  ra: string
  dec: string
  majorArcmin: number | null
  minorArcmin: number | null
  angleDegrees: number | null
  magnitude: number | null
  surfaceBrightness: number | null
  visibility: Visibility | null
}

export interface NightInfo {
  sunset: string
  sunrise: string
  nauticalDusk: string | null
  nauticalDawn: string | null
  astronomicalDusk: string | null
  astronomicalDawn: string | null
  darkStart: string
  darkEnd: string
  moonIllumination: number
  moonWaxing: boolean
}

export interface TonightResult {
  night: NightInfo
  /** How many matched (more than are listed). */
  count: number
  targets: TargetInfo[]
}

export interface AltitudeCurve {
  night: NightInfo
  points: { utc: string; altitude: number; moonAltitude: number; sunAltitude: number }[]
}

export interface SkyField {
  stars: { raHours: number; decDegrees: number; magnitude: number; label: string | null }[]
  objects: TargetInfo[]
}

export interface SolvedFrame {
  imageId: number
  corners: { raHours: number; decDegrees: number }[]
  center: { raHours: number; decDegrees: number }
}

export interface TonightFilters {
  groups: string[]
  minSize: number | null
  maxSize: number | null
  maxMagnitude: number | null
  minAltitude: number
  minHours: number
  minMoonDistance: number | null
  sort: 'best' | 'size' | 'brightness' | 'transit' | 'altitude'
}
