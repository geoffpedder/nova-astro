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

/** The Wi-Fi hotspot fallback's name and password, and when it starts and stops by itself. */
export interface HotspotSettings {
  ssid: string
  password: string
  /** Start it when no Wi-Fi (or cable with a route to the router) is found for 90 s. */
  fallback: boolean
  /** While nobody is on it, try joining Wi-Fi again every 5 minutes. */
  retry: boolean
}

export interface HotspotStatus {
  state: 'Off' | 'Starting' | 'On' | 'Stopping' | 'Error'
  ssid: string
  /** A password is set, so it can start. */
  configured: boolean
  fallback: boolean
  retry: boolean
  /** Phones and laptops on it now. */
  stations: number | null
  /** How the mini PC runs it: networkd, networkmanager, iwd, simulator or none. */
  method: string
  /** Why it can't run here, or null. */
  problem: string | null
  error: string | null
  address: string
  /** Why it's on: no Wi-Fi found, or started from the page. */
  reason: string | null
  onSinceUtc: string | null
  /** Started from the page: back to Wi-Fi if nobody joins within 3 minutes. */
  manual: boolean
  /** No connection and the fallback on: seconds until it starts. */
  startsInSeconds: number | null
}

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
  /** Recent guide steps; settling ones (after starting or a dither) aren't counted in the RMS. */
  history: { ra: number; dec: number; settling: boolean; dither: boolean }[]
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
  /** Which guider is in use: PHD2 or NOVA's own. */
  backend: 'phd2' | 'nova'
  /** NOVA's own guider (also while PHD2 is in use, for switching). */
  nova: NovaGuiderStatus | null
  /** Whether the recent guiding is seeing-limited or settings could improve it (after 30 steady steps). */
  diagnosis: GuideDiagnosis | null
  /** Dither and settle defaults for the mount (smaller and tighter for ZWO's AM mounts). */
  defaults: GuideDefaults | null
}

export interface GuideDefaults {
  /** "ZWO AM" when the mount is one, else "". */
  mount: string
  ditherPixels: number
  ditherEveryFrames: number
  settlePixels: number
  settleSeconds: number
  settleTimeout: number
}

export interface WebcamSettings {
  /** Null: the first webcam found. */
  device: string | null
  width: number
  height: number
  fps: number
}

/** One of the webcam's controls (V4L2): a slider, a switch or a choice; inactive ones are set automatically for now. */
export interface WebcamControl {
  name: string
  label: string
  group: string
  type: 'int' | 'bool' | 'menu'
  min: number
  max: number
  step: number
  default: number
  value: number
  inactive: boolean
  menu: { value: number; label: string }[]
}

export interface WebcamControls {
  device: string | null
  controls: WebcamControl[]
  /** Why they can't be shown (v4l-utils missing, no webcam). */
  error: string | null
}

export interface WebcamStatus {
  devices: { path: string; name: string }[]
  settings: WebcamSettings
  /** The webcam in use (or that would be). */
  device: string | null
  /** Waiting: watched, but no webcam plugged in yet (it starts by itself when one is). Error: it tries again every couple of seconds. */
  state: 'Idle' | 'Waiting' | 'Starting' | 'Streaming' | 'Error'
  viewers: number
  fps: number | null
  error: string | null
  /** e.g. that it has to convert the webcam's frames. */
  note: string | null
}

/** The mini PC at a glance (null members aren't available on this system). */
export interface SystemStats {
  /** All cores together, 0–100. */
  cpuPercent: number | null
  cores: number
  /** 1-minute load average. */
  load: number | null
  /** °C, the hottest thermal zone. */
  cpuTemperature: number | null
  memoryUsedBytes: number | null
  memoryTotalBytes: number | null
  novaMemoryBytes: number
  disks: { label: string; path: string; totalBytes: number; freeBytes: number }[]
  uptimeSeconds: number | null
  host: string
}

export interface GuideDiagnosis {
  /** One line for under the graph. */
  summary: string
  axes: { axis: string; rms: number; lag1: number; verdict: 'seeing-limited' | 'chasing' | 'lagging' | 'random'; text: string }[]
  /** How much guiding widens the stars (FWHM, ″): 2.355 × total RMS. */
  blurArcsec: number | null
}

/** What the guiding assistant measured with corrections off (distances in `units`; min moves in pixels). */
export interface GuidingAssistantResult {
  utc: string
  seconds: number
  frames: number
  axes: 'RA/Dec' | 'X/Y'
  units: 'arcsec' | 'px'
  jitterRa: number
  jitterDec: number
  driftRa: number
  driftDec: number
  periodicRa: number
  polarErrorArcmin: number | null
  recommendedRaMinMove: number
  recommendedDecMinMove: number
  recommendedExposure: number | null
  summary: string
  /** RA's periodic error period(s), seconds (when the run saw two cycles). */
  pePeriods: number[] | null
}

export type DecGuideMode = 'Auto' | 'North' | 'South' | 'Off'

export interface GuideAlgorithmSettings {
  raHysteresis: number
  raAggressiveness: number
  raMinMove: number
  decAggressiveness: number
  decMinMove: number
  decFastSwitch: boolean
  decMode: DecGuideMode
  maxRaPulseMs: number
  maxDecPulseMs: number
  /** RA: Hysteresis, or Predictive PEC (also corrects the learned periodic error ahead of time). */
  raAlgorithm: 'Hysteresis' | 'PredictivePec'
  /** Predictive PEC: the period if known (s); 0 finds it. */
  pecPeriodSeconds: number
  /** Predictive PEC: how much of the predicted motion to correct ahead (0–1). */
  pecPredictionGain: number
  /** Predictive PEC: the reactive part's aggressiveness. */
  pecReactiveAggressiveness: number
  /** Add a pulse to take up Dec backlash when a Dec correction reverses (measured at calibration, adjusted as it guides). */
  decBacklashCompensation: boolean
}

/** Predictive PEC's model: learning or predicting, the period(s), the share of RA motion it explains, its size (px). */
export interface PecStatus {
  state: 'Learning' | 'Predicting'
  periods: number[]
  quality: number
  amplitudePixels: number
  minutes: number
  /** Guiding needed before it predicts (two cycles of the longest period), when known. */
  minutesNeeded: number | null
}

export interface NovaGuiderSettings {
  /** Null: the first camera that isn't the imaging camera. */
  camera: string | null
  /** Null: the mount; otherwise a camera's ST4 port. */
  output: string | null
  exposureSeconds: number
  binning: number
  /** 0: worked out from the image scale and guide rate. */
  calibrationStepMs: number
  reverseDecAfterFlip: boolean
  algorithms: GuideAlgorithmSettings
  /** Subtract the guide camera's dark library from every guide frame. */
  useDarks: boolean
  /** The guide camera's gain, set before every frame; null leaves it as the driver has it. */
  gain: number | null
  /** Write a guide log (PHD2's format) into each night's image folder. */
  guideLog: boolean
}

/** A guide log file of NOVA's guider (one per night, in the night's image folder). */
export interface GuideLogFile {
  name: string
  night: string
  /** On the USB drive rather than the mini PC. */
  onDrive: boolean
  bytes: number
  modifiedUtc: string
}

/** The guide camera's dark library at the current binning. */
export interface GuideDarkInfo {
  exposures: number[]
  frames: number
  utc: string
  gain: number | null
  /** Taken at the camera's gain now (darks at another gain aren't used). */
  gainMatches: boolean
  hotPixels: number
  inUse: boolean
}

export interface GuideCalibrationInfo {
  raAngle: number
  decAngle: number
  raRate: number
  decRate: number
  raArcsecPerSecond: number | null
  decArcsecPerSecond: number | null
  orthogonality: number
  decDegrees: number
  pierSide: 'East' | 'West' | null
  utc: string
  warning: string | null
  /** Where the star went after each calibration step (pixels from where the West steps began), for the graph. */
  points: { phase: 'West' | 'East' | 'North' | 'South'; x: number; y: number }[] | null
  /** Dec backlash measured at calibration: how far short the South steps came of the North ones. */
  decBacklashPixels: number
  decBacklashMs: number
}

/** Dec backlash compensation now: measured, the pulse added on a reversal now, and reversals compensated. */
export interface DecBacklashStatus {
  enabled: boolean
  measuredPixels: number
  measuredMs: number
  pulseMs: number
  applied: number
}

export interface NovaGuiderStatus {
  settings: NovaGuiderSettings
  cameras: string[]
  outputs: { name: string | null; label: string }[]
  camera: string | null
  activity: string | null
  calibration: GuideCalibrationInfo | null
  star: { x: number; y: number; lockX: number; lockY: number; snr: number; hfr: number; found: number; tracked: number } | null
  /** Counts guide frames, so the view knows when to fetch the next. */
  frame: number
  frameWidth: number | null
  frameHeight: number | null
  /** The stars it follows (frame pixels): the guide star (lock) and the ones averaged with it. */
  stars: { x: number; y: number; lock: boolean; found: boolean }[]
  darks: GuideDarkInfo | null
  /** While taking darks: which one ("Dark 2 s, 3 of 5"). */
  darkProgress: string | null
  /** The guide camera's gain now and its range (when connected). */
  cameraGain: { value: number; min: number; max: number; step: number } | null
  /** The guiding assistant's last result for this guide camera. */
  assistant: GuidingAssistantResult | null
  cameraConnected: boolean
  /** Saved profiles (the user's, everything included) and built-in presets (exposure, gain, algorithms). */
  profiles: { name: string; builtIn: boolean; description: string | null }[]
  /** The profile the current settings are exactly, or null when changed since. */
  profile: string | null
  /** Predictive PEC's state, when it's the RA algorithm. */
  pec: PecStatus | null
  /** Once a calibration has measured some Dec backlash. */
  backlash: DecBacklashStatus | null
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
  /** The Wi-Fi hotspot fallback. */
  hotspot: HotspotStatus | null
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
