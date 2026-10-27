<script lang="ts">
  import { onMount, untrack } from 'svelte'
  import { api } from '../lib/api'
  import { HipsLayer } from '../lib/hips'
  import figures from '../lib/constellations.json'
  import { formatDec, formatRa, frameCorners, type Sky } from '../lib/sky'
  import { Projection, type ChartView, altAzVec, eqToHor, eqVec, horToEq, lstDegrees, norm, vecToAltAz, vecToEq, type Mode, type Vec } from '../lib/skyview'
  import type { TargetInfo } from '../lib/types'

  // A Stellarium-like sky chart: the whole sky, dragged and zoomed freely. Stars, deep-sky objects, the Sun and
  // Moon, a grid and the horizon; survey imagery when zoomed in; the planned frame(s), the last solved frame and
  // the mount's live position (with its camera frame). Click anything to pick it, or empty sky for that point.

  let {
    view = $bindable(),
    site,
    survey,
    frames,
    frameCentre,
    onmoveframe,
    frameAngle = null,
    onrotateframe,
    solved = null,
    solvedAngle = null,
    mount = null,
    showMount = true,
    mountFrame = null,
    follow = $bindable(false),
    constellations = true,
    atmosphere = true,
    ground = true,
    calibration = false,
    sunZone = null,
    selected = null,
    onselect,
  }: {
    view: ChartView
    site: { latitude: number; longitude: number } | null
    /** 'tiles' (the stored survey, fast), a hips2fits survey id (rendered to order: slow, sharpest), or 'none'. */
    survey: string
    frames: { centre: Sky; corners: Sky[]; label: string }[]
    frameCentre: Sky | null
    onmoveframe: (centre: Sky) => void
    /** The planned frame's rotation (position angle of its top, degrees), for its label. */
    frameAngle?: number | null
    /** With this, the frame gets a handle above it: drag it round to turn the frame (to a new position angle). */
    onrotateframe?: (pa: number) => void
    solved?: Sky[] | null
    /** The solved frame's rotation (position angle of its top, degrees), for its label. */
    solvedAngle?: number | null
    mount?: { ra: number; dec: number; slewing: boolean; name: string } | null
    /** Draw the mount's reticle and frame (Follow mount works either way). */
    showMount?: boolean
    /** Width, height (degrees) and rotation of the camera's frame, drawn at the mount. */
    mountFrame?: { width: number; height: number; pa: number } | null
    follow?: boolean
    /** Draw the constellation figures and names. */
    constellations?: boolean
    /** The daytime sky's blue glow and the haze round the Sun. */
    atmosphere?: boolean
    /** The ground below the horizon. */
    ground?: boolean
    /** Where guiding calibrates best: near the celestial equator and the meridian. */
    calibration?: boolean
    /** Sun protection: its radius (degrees) and whether it's in force now (the Sun is up). */
    sunZone?: { degrees: number; active: boolean } | null
    selected?: { ra: number; dec: number } | null
    onselect: (target: TargetInfo) => void
  } = $props()

  // ---- Catalog data ----
  interface Star {
    ra: number
    dec: number
    mag: number
    label: string | null
  }
  let bright = $state<Star[]>([])
  let famous = $state<TargetInfo[]>([])
  let deepStars = $state<Star[]>([])
  let deepObjects = $state<TargetInfo[]>([])
  interface Planet {
    name: string
    ra: number
    dec: number
    magnitude: number
    diameter: number // arcseconds
  }
  let bodies = $state<{ sun: Sky; moon: Sky; illumination: number; planets: Planet[] } | null>(null)

  const toStar = (s: { raHours: number; decDegrees: number; magnitude: number; label: string | null }): Star => ({
    ra: s.raHours,
    dec: s.decDegrees,
    mag: s.magnitude,
    label: s.label,
  })

  onMount(() => {
    // The bright sky once (naked-eye stars, Messier/Caldwell/named objects); fainter detail per view below.
    fetch('/api/sky/field?ra=0&dec=0&radius=180&magnitude=6.5&maxStars=12000&objects=famous&maxObjects=3000')
      .then((r) => r.json())
      .then((f) => {
        bright = f.stars.map(toStar)
        famous = f.objects
      })
      .catch(() => {})
    const loadBodies = () =>
      fetch('/api/sky/bodies')
        .then((r) => r.json())
        .then((b) => {
          bodies = {
            sun: { ra: b.sun.raHours, dec: b.sun.decDegrees },
            moon: { ra: b.moon.raHours, dec: b.moon.decDegrees },
            illumination: b.moon.illumination,
            planets: (b.planets ?? []).map((pl: { name: string; raHours: number; decDegrees: number; magnitude: number; diameterArcsec: number }) => ({
              name: pl.name,
              ra: pl.raHours,
              dec: pl.decDegrees,
              magnitude: pl.magnitude,
              diameter: pl.diameterArcsec,
            })),
          }
        })
        .catch(() => {})
    loadBodies()
    const bodiesTimer = setInterval(loadBodies, 60_000)
    const clock = setInterval(() => (now = Date.now()), 5_000)
    // Day/night theme: the chart's colours change with it.
    const themeWatch = new MutationObserver(() => themeTick++)
    themeWatch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
    return () => {
      clearInterval(bodiesTimer)
      clearInterval(clock)
      themeWatch.disconnect()
    }
  })

  // ---- The projection for this frame ----
  let now = $state(Date.now())
  let themeTick = $state(0)
  let width = $state(800)
  let height = $state(600)
  const horizonOk = $derived(site !== null)
  const mode = $derived<Mode>(view.mode === 'horizon' && horizonOk ? 'horizon' : 'equatorial')
  const lst = $derived(site ? lstDegrees(new Date(now), site.longitude) : 0)
  const latitude = $derived(site?.latitude ?? 0)
  const centreVec = $derived<Vec>(mode === 'horizon' ? altAzVec(view.lat, view.lon) : eqVec(view.lon, view.lat))
  const projection = $derived(new Projection(mode, centreVec, view.fov, width, height, lst, latitude))
  // Hidden (another tab showing) the chart has no size: nothing to draw or fetch.
  const ready = $derived(width > 20 && height > 20)
  // What's being looked at; in horizon mode the sky turns underneath, so it also changes every 2 minutes.
  const lookKey = $derived(
    `${mode}|${view.lon.toFixed(4)}|${view.lat.toFixed(4)}|${view.fov.toFixed(4)}|${width}|${height}|${mode === 'horizon' ? Math.floor(now / 120_000) : 0}`,
  )

  /** Puts the view centre on an equatorial position (and optionally zooms). */
  export function goto(ra: number, dec: number, fov?: number) {
    const v = eqVec(ra, dec)
    setCentre(mode === 'horizon' ? eqToHor(v, lst, latitude) : v)
    if (fov) view.fov = clampFov(fov)
  }

  function setCentre(frameVec: Vec) {
    if (mode === 'horizon') {
      const { alt, az } = vecToAltAz(frameVec)
      view.lat = Math.max(-89.5, Math.min(89.5, alt))
      view.lon = az
    } else {
      const { ra, dec } = vecToEq(frameVec)
      view.lat = Math.max(-89.5, Math.min(89.5, dec))
      view.lon = ra
    }
  }
  const clampFov = (f: number) => Math.min(200, Math.max(0.15, f))

  // Switching mode (by the user) keeps looking at the same place. Only the user's choice counts: until the site
  // is known the chart shows equatorial, and that's not a switch.
  let lastMode: Mode | null = null
  $effect(() => {
    const m = view.mode
    if (lastMode === null || !site) {
      lastMode = m
      return
    }
    if (m === lastMode) return
    const previous = lastMode === 'horizon' ? horToEq(altAzVec(view.lat, view.lon), lst, latitude) : eqVec(view.lon, view.lat)
    lastMode = m
    const next = m === 'horizon' ? eqToHor(previous, lst, latitude) : previous
    if (m === 'horizon') {
      const { alt, az } = vecToAltAz(next)
      view.lat = Math.max(-89.5, Math.min(89.5, alt))
      view.lon = az
    } else {
      const { ra, dec } = vecToEq(next)
      view.lat = Math.max(-89.5, Math.min(89.5, dec))
      view.lon = ra
    }
  })

  // Follow the mount: keep it in the middle.
  $effect(() => {
    if (follow && mount) goto(mount.ra, mount.dec)
  })

  // ---- Fainter stars and all objects for the current view (debounced) ----
  let deepTimer: ReturnType<typeof setTimeout> | undefined
  $effect(() => {
    void lookKey
    if (!ready) return
    const fov = view.fov
    const centre = untrack(() => projection.unproject(width / 2, height / 2))
    clearTimeout(deepTimer)
    if (fov > 30) {
      deepStars = []
      deepObjects = []
      return
    }
    const radius = (fov * Math.hypot(width, height)) / width / 2 + 0.5
    deepTimer = setTimeout(async () => {
      try {
        const f = await api.skyField(formatRa(centre.ra), formatDec(centre.dec), radius, { maxStars: 8000, maxObjects: 800 })
        deepStars = f.stars.filter((s) => s.magnitude >= 6.5).map(toStar)
        deepObjects = f.objects
      } catch {
        // keep what we have
      }
    }, 250)
    return () => clearTimeout(deepTimer)
  })

  // ---- Survey imagery (zoomed in), re-requested when the view settles ----
  // Each image remembers where on the sky it is and is drawn there, so it stays aligned as the sky turns and while
  // the next one loads.
  interface SurveyImage {
    image: HTMLImageElement
    ra: number
    dec: number
    /** Degrees on a side. */
    side: number
    survey: string
  }
  // The stored survey's tiles; a loaded tile redraws the chart.
  let tilesLoaded = $state(0)
  const hips = new HipsLayer(() => tilesLoaded++)
  const useTiles = $derived(survey === 'tiles' || survey === 'auto')

  let surveyImage = $state<SurveyImage | null>(null)
  let surveyFailed = $state(false)
  let surveyTimer: ReturnType<typeof setTimeout> | undefined
  // A rendered (hips2fits) image: only when one of those surveys is chosen.
  const surveyId = $derived(survey)
  const showSurvey = $derived(surveyId.startsWith('CDS/') && view.fov <= (surveyId === 'CDS/P/Mellinger/color' ? 90 : 25))
  // A square covering the whole view however it's turned (its diagonal).
  const surveySide = $derived(Math.min(180, (view.fov * Math.hypot(width, height)) / Math.max(1, width)))
  $effect(() => {
    void lookKey
    const id = surveyId
    clearTimeout(surveyTimer)
    if (!ready || !showSurvey) return
    const centre = untrack(() => projection.unproject(width / 2, height / 2))
    const side = surveySide
    const pixels = Math.min(2048, Math.max(512, Math.round((Math.max(width, height) * (window.devicePixelRatio || 1)) / 256) * 256))
    surveyTimer = setTimeout(() => {
      surveyFailed = false
      const image = new Image()
      image.onload = () => (surveyImage = { image, ra: centre.ra, dec: centre.dec, side, survey: id })
      image.onerror = () => (surveyFailed = true)
      image.src = `/api/sky/survey?ra=${centre.ra.toFixed(5)}&dec=${centre.dec.toFixed(5)}&fov=${side.toFixed(4)}&size=${pixels}&survey=${encodeURIComponent(id)}&projection=STG`
    }, 500)
    return () => clearTimeout(surveyTimer)
  })
  // Shown while it's this survey at about this zoom.
  const surveyShown = $derived(
    showSurvey && !!surveyImage && surveyImage.survey === surveyId && Math.abs(surveyImage.side / surveySide - 1) < 0.02,
  )

  // ---- Drawing ----
  let canvas = $state<HTMLCanvasElement | null>(null)
  let frameRequest = 0
  $effect(() => {
    // Everything drawn is read here, so any change redraws (once per animation frame).
    void [projection, bright, famous, deepStars, deepObjects, bodies, frames, solved, mount, mountFrame, selected, surveyShown, now, tilesLoaded, useTiles, constellations, sunZone, themeTick, atmosphere, ground, width, height]
    cancelAnimationFrame(frameRequest)
    // A new size (the window resized, or the tab shown again after one) is drawn straight away, before the browser
    // paints: waiting a frame would show the old picture stretched to the new shape.
    const dpr = window.devicePixelRatio || 1
    if (canvas && ready && (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr))) untrack(draw)
    else frameRequest = requestAnimationFrame(draw)
  })

  // Off-screen canvases for the ground and the day sky, one cell per few screen pixels.
  const CELL = 5
  let groundCanvas: HTMLCanvasElement | null = null
  let skyCanvas: HTMLCanvasElement | null = null

  function atmosphereLayers(p: Projection, night: boolean): { ground: HTMLCanvasElement; sky: HTMLCanvasElement | null } {
    const w = Math.ceil(width / CELL)
    const h = Math.ceil(height / CELL)
    groundCanvas ??= document.createElement('canvas')
    skyCanvas ??= document.createElement('canvas')
    for (const c of [groundCanvas, skyCanvas]) {
      if (c.width !== w || c.height !== h) {
        c.width = w
        c.height = h
      }
    }
    const ground = new ImageData(w, h)
    const sky = new ImageData(w, h)
    // How much daylight: none below the Sun at -8°, full from +4°.
    const sunAltitude = bodies && site ? vecToAltAz(eqToHor(eqVec(bodies.sun.ra, bodies.sun.dec), lst, latitude)).alt : -90
    const daylight = night ? 0 : Math.max(0, Math.min(1, (sunAltitude + 8) / 12))
    const day = atmosphere ? daylight : 0 // the sky's glow (switchable); the ground follows the daylight regardless
    const [gr, gg, gb] = night ? [42, 4, 4] : [Math.round(22 + 22 * daylight), Math.round(46 + 30 * daylight), Math.round(20 + 14 * daylight)]
    // The Sun's position in the chart's frame, for the bright haze round it.
    const sunFrame = bodies ? p.toFrame(eqVec(bodies.sun.ra, bodies.sun.dec)) : null
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++) {
        const v = p.unprojectFrame(x * CELL + CELL / 2, y * CELL + CELL / 2)
        const up = p.mode === 'horizon' ? v[2] : eqToHor(v, lst, latitude)[2]
        const altitude = (Math.asin(Math.max(-1, Math.min(1, up))) * 180) / Math.PI
        const i = (y * w + x) * 4
        if (altitude < 0.4) {
          // Mostly opaque a little below the horizon (stars faintly through); a soft edge.
          ground.data[i] = gr
          ground.data[i + 1] = gg
          ground.data[i + 2] = gb
          ground.data[i + 3] = Math.round(174 * Math.min(1, (0.4 - altitude) / 1.2))
        }
        if (day > 0 && altitude > -1) {
          const t = Math.min(1, Math.max(0, altitude) / 70) // 0 at the horizon, 1 high up
          let r = 150 - 105 * t
          let gg2 = 190 - 95 * t
          let b = 235 - 60 * t
          let a = day * (95 - 50 * t)
          if (sunFrame) {
            // Haze round the Sun: a bright core and a wide soft halo, warm white, blended over the blue.
            const cos = Math.max(-1, Math.min(1, v[0] * sunFrame[0] + v[1] * sunFrame[1] + v[2] * sunFrame[2]))
            const d = (Math.acos(cos) * 180) / Math.PI
            const glow = day * Math.min(1, 0.9 * Math.exp(-d / 5) + 0.45 * Math.exp(-d / 22))
            if (glow > 0.01) {
              const k = glow / (glow + a / 255 + 1e-6)
              r = r + (255 - r) * k
              gg2 = gg2 + (244 - gg2) * k
              b = b + (215 - b) * k
              a = Math.min(240, a + glow * 200)
            }
          }
          sky.data[i] = Math.round(r)
          sky.data[i + 1] = Math.round(gg2)
          sky.data[i + 2] = Math.round(b)
          sky.data[i + 3] = Math.round(a)
        }
      }
    groundCanvas.getContext('2d')!.putImageData(ground, 0, 0)
    if (day > 0) skyCanvas.getContext('2d')!.putImageData(sky, 0, 0)
    return { ground: groundCanvas, sky: day > 0 ? skyCanvas : null }
  }

  const PLANET_COLOURS: Record<string, string> = {
    Mercury: '#c9c3b8',
    Venus: '#fff1c4',
    Mars: '#ff7a52',
    Jupiter: '#f0d3a8',
    Saturn: '#ecd99e',
    Uranus: '#a9e8ee',
    Neptune: '#7fa6ff',
    Pluto: '#cdbfae',
  }

  function limitMagnitude(fov: number): number {
    return fov >= 120 ? 5.2 : fov >= 60 ? 5.8 : fov >= 25 ? 6.5 : fov >= 10 ? 8.5 : fov >= 4 ? 10 : fov >= 1.5 ? 11.5 : 12.5
  }

  function draw() {
    if (!canvas || !ready) return
    const dpr = window.devicePixelRatio || 1
    if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
      canvas.width = Math.round(width * dpr)
      canvas.height = Math.round(height * dpr)
    }
    const g = canvas.getContext('2d')!
    g.setTransform(dpr, 0, 0, dpr, 0, 0)
    const night = document.documentElement.dataset.theme === 'night'
    const c = night
      ? { meridian: 'rgba(200,60,60,0.7)', figure: 'rgba(170,40,40,0.55)', figureName: 'rgba(200,50,50,0.8)', bg: '#050000', star: '#ff5050', grid: 'rgba(160,30,30,0.35)', horizon: '#a02020', text: '#ff5050', object: '#c03030', frame: '#ff3030', solved: '#ff7070', mount: '#ff2020', below: 0.3 }
      : { meridian: 'rgba(122,170,150,0.75)', figure: 'rgba(120,150,200,0.45)', figureName: 'rgba(150,175,215,0.75)', bg: '#04060c', star: '#e8ecf5', grid: 'rgba(110,140,190,0.22)', horizon: '#6a8a4a', text: '#c8d0e0', object: '#8fb0cf', frame: '#dcbc6e', solved: '#8fc29a', mount: '#d26a5a', below: 0.35 }
    const p = projection
    g.fillStyle = c.bg
    g.fillRect(0, 0, width, height)

    // The stored survey, tile by tile (it covers the whole view once its Allsky backdrop has loaded).
    let tilesCover = false
    if (useTiles && view.fov <= 120) {
      g.save()
      if (night) g.filter = 'grayscale(1) sepia(1) hue-rotate(-50deg) saturate(6) brightness(0.55)'
      tilesCover = hips.draw(g, p, dpr) && hips.hasAllsky
      g.restore()
    }

    // Survey image, at its place on the sky, north turned to where north is there.
    const imageAt = surveyShown && surveyImage ? p.sky(surveyImage.ra, surveyImage.dec) : null
    if (surveyShown && surveyImage && imageAt) {
      const size = p.pixels(surveyImage.side)
      const north = p.sky(surveyImage.ra, Math.min(89.99, surveyImage.dec + 0.01))
      g.save()
      g.translate(imageAt.x, imageAt.y)
      g.rotate(north ? Math.atan2(north.x - imageAt.x, -(north.y - imageAt.y)) : p.northAngle())
      if (night) g.filter = 'grayscale(1) sepia(1) hue-rotate(-50deg) saturate(6) brightness(0.55)'
      g.drawImage(surveyImage.image, -size / 2, -size / 2, size, size)
      g.restore()
    }

    const RAD = Math.PI / 180
    const sinLat = Math.sin(latitude * RAD)
    const cosLat = Math.cos(latitude * RAD)
    /** Sine of the altitude (only its sign matters for dimming what's below the horizon). */
    const altitudeOf = (ra: number, dec: number) =>
      site ? sinLat * Math.sin(dec * RAD) + cosLat * Math.cos(dec * RAD) * Math.cos((lst - ra * 15) * RAD) : 1
    const visible = (pt: { x: number; y: number } | null, margin = 20): pt is { x: number; y: number } =>
      !!pt && pt.x > -margin && pt.y > -margin && pt.x < width + margin && pt.y < height + margin

    // The sky by day: a subtle blue, lighter towards the horizon (fading out through twilight).
    const layers = site ? atmosphereLayers(p, night) : null
    if (layers?.sky) {
      g.save()
      g.imageSmoothingEnabled = true
      g.drawImage(layers.sky, 0, 0, width, height)
      g.restore()
    }

    // Grid: RA hours and Dec circles, spaced for the zoom.
    const fov = view.fov
    const [raStep, decStep] = fov > 90 ? [2, 30] : fov > 30 ? [1, 10] : fov > 10 ? [1 / 3, 5] : fov > 3 ? [1 / 12, 1] : [1 / 60, 1 / 6]
    g.strokeStyle = c.grid
    g.lineWidth = 1
    const sample = Math.max(0.2, Math.min(3, fov / 40))
    const polyline = (points: ({ x: number; y: number } | null)[]) => {
      g.beginPath()
      let pen = false
      let last: { x: number; y: number } | null = null
      for (const pt of points) {
        if (!pt || (last && Math.hypot(pt.x - last.x, pt.y - last.y) > Math.max(width, height))) {
          pen = false
          last = pt
          continue
        }
        if (pen) g.lineTo(pt.x, pt.y)
        else g.moveTo(pt.x, pt.y)
        pen = true
        last = pt
      }
      g.stroke()
    }
    const here = p.unproject(width / 2, height / 2)
    const reach = Math.min(180, (2 * Math.atan(Math.hypot(width, height) / 2 / p.scale / 2) * 180) / Math.PI + 1)
    const decLow = Math.max(-89.9, here.dec - reach)
    const decHigh = Math.min(89.9, here.dec + reach)
    // RA span near the view (all of it near a pole or when zoomed out).
    const nearPole = Math.abs(here.dec) + reach >= 89
    const raSpan = nearPole || reach > 60 ? 12 : Math.min(12, reach / 15 / Math.max(0.05, Math.cos(Math.max(Math.abs(decLow), Math.abs(decHigh)) * RAD)))
    const raFrom = here.ra - raSpan
    const raTo = here.ra + raSpan
    // RA lines crowd together towards the poles. Like Stellarium, each one stops (fading out) where it would come
    // closer than a few pixels to its neighbours; every second, fourth, eighth… line carries on further in, so the
    // spacing on screen stays even right up to the pole instead of turning into a smear of lines.
    const pxPerDeg = p.pixels(1)
    const MinGap = 14 // px between neighbouring RA lines
    const linesAround = Math.round(24 / raStep)
    const raLine = (ra: number, from: number, to: number) => {
      if (to <= from) return
      const pts = []
      for (let dec = from; dec < to + 1e-9; dec = Math.min(to, dec + sample)) {
        pts.push(p.sky(((ra % 24) + 24) % 24, dec))
        if (dec >= to) break
      }
      polyline(pts)
    }
    const gridAlpha = g.globalAlpha
    for (let ra = Math.ceil(raFrom / raStep) * raStep; ra <= raTo; ra += raStep) {
      // How far apart this line is from the next one that's still drawn where it ends: lines at multiples of 2, 4,
      // 8… steps keep going (up to 4 lines, 6 h apart, which meet at the pole).
      const k = ((Math.round(ra / raStep) % linesAround) + linesAround) % linesAround
      let spread = 1
      while (spread * 2 <= linesAround / 4 && k % (spread * 2) === 0) spread *= 2
      const gapDeg = raStep * 15 * spread
      const at = (pixels: number) => (gapDeg * pxPerDeg <= pixels ? 0 : (Math.acos(Math.min(1, pixels / (gapDeg * pxPerDeg))) * 180) / Math.PI)
      const end = Math.min(89.9, at(MinGap)) // the line stops here…
      const solid = Math.min(end, at(MinGap * 2)) // …fading out from here
      raLine(ra, Math.max(decLow, -solid), Math.min(decHigh, solid))
      const steps = 4
      for (let i = 0; i < steps; i++) {
        const a0 = solid + ((end - solid) * i) / steps
        const a1 = solid + ((end - solid) * (i + 1)) / steps
        g.globalAlpha = gridAlpha * (1 - (i + 0.5) / steps)
        raLine(ra, Math.max(decLow, a0), Math.min(decHigh, a1))
        raLine(ra, Math.max(decLow, -a1), Math.min(decHigh, -a0))
      }
      g.globalAlpha = gridAlpha
    }
    for (let dec = Math.ceil(decLow / decStep) * decStep; dec <= decHigh; dec += decStep) {
      if (Math.abs(dec) >= 89.99) continue
      const pts = []
      for (let ra = raFrom; ra <= raTo + 1e-9; ra += sample / 15 / Math.max(0.05, Math.cos(dec * RAD))) pts.push(p.sky(((ra % 24) + 24) % 24, dec))
      polyline(pts)
    }

    // The meridian: north point, zenith, south point (and on round under the horizon), where targets cross from
    // east to west and a German mount flips.
    if (site) {
      // The upper meridian (hour angle 0: south horizon, zenith, pole) is where targets cross and the mount flips;
      // the lower one (12 h, under the pole) only circumpolar targets pass, with no flip: drawn faint.
      const raUpper = (((lst / 15) % 24) + 24) % 24
      const raLower = (raUpper + 12) % 24
      const line = (ra: number) => {
        const pts: ({ x: number; y: number } | null)[] = []
        for (let dec = -90; dec <= 90; dec += sample) pts.push(p.sky(ra, dec))
        return pts
      }
      const upper = line(raUpper)
      g.strokeStyle = c.meridian
      g.lineWidth = 1.5
      g.setLineDash([8, 6])
      polyline(upper)
      g.globalAlpha = 0.3
      g.lineWidth = 1
      polyline(line(raLower))
      g.globalAlpha = 1
      g.setLineDash([])
      // Label it where it's on screen, nearest the middle of the view.
      let best: { x: number; y: number } | null = null
      for (const pt of upper)
        if (visible(pt, -20) && (!best || Math.abs(pt.y - height / 2) < Math.abs(best.y - height / 2))) best = pt
      if (best) {
        g.fillStyle = c.meridian
        g.font = '12px system-ui, sans-serif'
        g.textAlign = 'left'
        g.fillText('Meridian', best.x + 6, best.y - 6)
      }
    }

    // Where to calibrate guiding: near Dec 0 (RA moves the star fastest, so the measured rates are good) and near the
    // meridian (the mount isn't about to flip and the star is high). ±1.5 h of hour angle, ±20° of Dec.
    if (site && calibration) {
      const raMid = lst / 15
      const edge: ({ x: number; y: number } | null)[] = []
      const ra = (h: number) => (((raMid + h) % 24) + 24) % 24
      for (let h = -1.5; h <= 1.5 + 1e-9; h += sample / 15) edge.push(p.sky(ra(h), 20))
      for (let d = 20; d >= -20 - 1e-9; d -= sample) edge.push(p.sky(ra(1.5), d))
      for (let h = 1.5; h >= -1.5 - 1e-9; h -= sample / 15) edge.push(p.sky(ra(h), -20))
      for (let d = -20; d <= 20 + 1e-9; d += sample) edge.push(p.sky(ra(-1.5), d))
      edge.push(edge[0])
      const zone = night ? 'rgba(200,60,60,' : 'rgba(120,200,255,'
      if (edge.every((pt) => pt) && edge.some((pt) => visible(pt, 0))) {
        g.fillStyle = zone + '0.16)'
        g.beginPath()
        edge.forEach((pt, i) => (i ? g.lineTo(pt!.x, pt!.y) : g.moveTo(pt!.x, pt!.y)))
        g.fill()
      }
      g.strokeStyle = zone + '0.85)'
      g.lineWidth = 1.5
      g.setLineDash([6, 4])
      polyline(edge)
      g.setLineDash([])
      // The label just inside the box's top edge (Dec +20° or −20°, whichever is drawn higher), in the middle and
      // turned along that edge as the frame labels are (flipped rather than upside down).
      const north = p.sky(ra(0), 20)
      const south = p.sky(ra(0), -20)
      if (north && south) {
        const topDec = north.y <= south.y ? 20 : -20
        const [top, other] = topDec === 20 ? [north, south] : [south, north]
        const a = p.sky(ra(-0.1), topDec)
        const b = p.sky(ra(0.1), topDec)
        if (a && b && visible(top, -10)) {
          let angle = Math.atan2(b.y - a.y, b.x - a.x)
          if (Math.cos(angle) < 0) angle += Math.PI
          // Into the box from its top edge; the text sits on that side of the edge.
          const inward = { x: other.x - top.x, y: other.y - top.y }
          const textUp = { x: Math.sin(angle), y: -Math.cos(angle) }
          const upIsOut = textUp.x * inward.x + textUp.y * inward.y < 0
          g.save()
          g.translate(top.x, top.y)
          g.rotate(angle)
          g.fillStyle = zone + '0.85)'
          g.font = '12px system-ui, sans-serif'
          g.textAlign = 'center'
          g.textBaseline = upIsOut ? 'top' : 'bottom'
          g.fillText('Calibrate guiding here', 0, upIsOut ? 4 : -4)
          g.restore()
        }
      }
    }

    // Constellation figures: each segment drawn as the great-circle arc it is (sampled, so it curves correctly
    // across a wide view); names thin out as the view widens (by the figure's prominence).
    if (constellations && fov > 2) {
      g.strokeStyle = c.figure
      g.lineWidth = 1.2
      const step = Math.max(0.25, Math.min(3, fov / 40))
      for (const strands of Object.values(figures.lines)) {
        for (const strand of strands) {
          const pts: ({ x: number; y: number } | null)[] = []
          for (let i = 0; i < strand.length - 1; i++) {
            const a = eqVec(strand[i][0], strand[i][1])
            const b = eqVec(strand[i + 1][0], strand[i + 1][1])
            const angle = Math.acos(Math.max(-1, Math.min(1, a[0] * b[0] + a[1] * b[1] + a[2] * b[2]))) / RAD
            const n = Math.max(1, Math.ceil(angle / step))
            for (let k = i === 0 ? 0 : 1; k <= n; k++) {
              const t = k / n
              const v = norm([0, 1, 2].map((j) => a[j] * (1 - t) + b[j] * t) as Vec)
              pts.push(p.frame(p.toFrame(v)))
            }
          }
          polyline(pts)
        }
      }
      const maxRank = fov > 90 ? 1 : fov > 45 ? 2 : 3
      if (fov >= 8) {
        g.fillStyle = c.figureName
        g.font = '13px system-ui, sans-serif'
        g.textAlign = 'center'
        for (const name of figures.names) {
          if (name.rank > maxRank) continue
          const pt = p.sky(name.ra, name.dec)
          if (visible(pt)) g.fillText(name.name.toUpperCase(), pt.x, pt.y)
        }
      }
    }

    // Deep-sky objects.
    const unit = p.pixels(1 / 60) // pixels per arcminute
    const deepLabels = new Set(deepObjects.map((d) => d.label))
    const objects = fov <= 30 ? [...famous.filter((o) => !deepLabels.has(o.label)), ...deepObjects] : famous
    g.font = '12px system-ui, sans-serif'
    g.textAlign = 'center'
    for (const o of objects) {
      const pt = p.sky(o.raHours, o.decDegrees)
      if (!visible(pt, 200)) continue
      const below = altitudeOf(o.raHours, o.decDegrees) < 0
      g.globalAlpha = below ? c.below : 1
      g.strokeStyle = c.object
      g.lineWidth = 1.2
      const rx = Math.max(3, ((o.majorArcmin ?? 2) / 2) * unit)
      const ry = o.angleDegrees == null ? rx : Math.max(3, ((o.minorArcmin ?? o.majorArcmin ?? 2) / 2) * unit)
      // The major axis's direction on screen: towards the object's position angle from its local north.
      const north = p.sky(o.raHours, Math.min(89.99, o.decDegrees + 0.05))
      const up = north ? Math.atan2(north.x - pt.x, -(north.y - pt.y)) : 0
      const t = ((o.angleDegrees ?? 0) * Math.PI) / 180
      g.beginPath()
      g.setLineDash(rx > 6 ? [5, 4] : [])
      // East is left of north on the sky: the axis turns anticlockwise from north on screen.
      g.ellipse(pt.x, pt.y, ry, rx, up - t, 0, Math.PI * 2)
      g.stroke()
      g.setLineDash([])
      const famousOne = o.names.some((n) => n.startsWith('M ') || n.startsWith('Caldwell '))
      // Labels thin out as the view widens: big named objects always, Messier/Caldwell from 60°, the rest close in.
      const bigNamed = !!o.commonName && (o.majorArcmin ?? 0) >= 30
      if (bigNamed || (famousOne && fov <= 60) || (o.commonName && fov <= 30) || (rx > 12 && fov <= 15) || fov <= 3) {
        g.fillStyle = c.object
        const label = fov <= 40 && o.commonName ? `${o.label} ${o.commonName}` : o.label
        g.fillText(label, pt.x, pt.y - Math.max(rx, ry) - 4)
      }
    }
    g.globalAlpha = 1

    // Stars (hidden under the survey image, which already shows them; their names still show).
    const limit = limitMagnitude(fov)
    const stars = fov <= 30 ? [...bright, ...deepStars] : bright
    g.textAlign = 'left'
    for (const s of stars) {
      if (s.mag > limit) continue
      const pt = p.sky(s.ra, s.dec)
      if (!visible(pt, 4)) continue
      const below = altitudeOf(s.ra, s.dec) < 0
      const r = Math.max(0.6, Math.min(5.5, 0.8 + (limit - s.mag) * 0.6))
      if (!imageAt && !tilesCover) {
        g.globalAlpha = below ? c.below : 1
        g.fillStyle = c.star
        g.beginPath()
        g.arc(pt.x, pt.y, r, 0, Math.PI * 2)
        g.fill()
      }
      const proper = s.label && !/^[0-9α-ωA-Z]{1,3}[0-9¹²³⁴⁵⁶⁷⁸⁹]* /.test(s.label) && !/^(HD|HIP|Gl|GJ) /.test(s.label)
      if (s.label && ((proper && s.mag < (fov > 90 ? 1.6 : fov > 40 ? 2.6 : 4.5)) || (fov < 12 && s.mag < 5.5))) {
        g.globalAlpha = below ? c.below : 0.85
        g.fillStyle = c.text
        g.font = '12px system-ui, sans-serif'
        g.fillText(s.label, pt.x + r + 3, pt.y + 4)
      }
    }
    g.globalAlpha = 1

    // Sun protection: the circle the telescope is kept out of (filled while it's in force).
    if (bodies && sunZone && sunZone.degrees > 0) {
      const centre = eqVec(bodies.sun.ra, bodies.sun.dec)
      // Two directions at right angles to the Sun, to walk round it.
      const helper: Vec = Math.abs(centre[2]) < 0.9 ? [0, 0, 1] : [1, 0, 0]
      const u = norm([
        helper[1] * centre[2] - helper[2] * centre[1],
        helper[2] * centre[0] - helper[0] * centre[2],
        helper[0] * centre[1] - helper[1] * centre[0],
      ] as Vec)
      const v: Vec = [centre[1] * u[2] - centre[2] * u[1], centre[2] * u[0] - centre[0] * u[2], centre[0] * u[1] - centre[1] * u[0]]
      const r = sunZone.degrees * RAD
      const ring: ({ x: number; y: number } | null)[] = []
      for (let t = 0; t <= 360; t += 3) {
        const a = t * RAD
        const k = (i: number) => Math.cos(r) * centre[i] + Math.sin(r) * (Math.cos(a) * u[i] + Math.sin(a) * v[i])
        ring.push(p.frame(p.toFrame([k(0), k(1), k(2)])))
      }
      const whole = ring.every(Boolean)
      if (sunZone.active && whole) {
        g.beginPath()
        ring.forEach((pt, i) => (i ? g.lineTo(pt!.x, pt!.y) : g.moveTo(pt!.x, pt!.y)))
        g.closePath()
        g.fillStyle = night ? 'rgba(160,0,0,0.12)' : 'rgba(255,70,50,0.10)'
        g.fill()
      }
      g.strokeStyle = sunZone.active ? 'rgba(255,80,60,0.9)' : 'rgba(255,80,60,0.35)'
      g.lineWidth = sunZone.active ? 2 : 1
      g.setLineDash(sunZone.active ? [] : [6, 6])
      polyline(ring)
      g.setLineDash([])
      const top = p.sky(bodies.sun.ra, Math.min(89.9, bodies.sun.dec + sunZone.degrees * 0.7))
      if (visible(top, -10)) {
        g.fillStyle = sunZone.active ? 'rgba(255,90,70,0.95)' : 'rgba(255,90,70,0.5)'
        g.font = '12px system-ui, sans-serif'
        g.textAlign = 'center'
        g.fillText(sunZone.active ? `Sun protection, ${sunZone.degrees}°` : 'Sun protection (Sun down)', top.x, top.y)
      }
    }

    // The planets: coloured, sized by brightness (or their real disc when zoomed in that far), labelled.
    if (bodies) {
      g.font = '12px system-ui, sans-serif'
      g.textAlign = 'left'
      for (const pl of bodies.planets) {
        const pt = p.sky(pl.ra, pl.dec)
        if (!visible(pt, 20)) continue
        const disc = p.pixels(pl.diameter / 3600) / 2
        const r = Math.max(disc, Math.max(1.8, Math.min(6, 3.5 - pl.magnitude * 0.45)))
        g.globalAlpha = altitudeOf(pl.ra, pl.dec) < 0 ? c.below : 1
        g.fillStyle = night ? '#d02020' : (PLANET_COLOURS[pl.name] ?? '#ddd')
        g.beginPath()
        g.arc(pt.x, pt.y, r, 0, Math.PI * 2)
        g.fill()
        if (pl.name === 'Saturn' && disc > 4) {
          // A hint of the rings once Saturn shows as a disc.
          g.strokeStyle = g.fillStyle
          g.lineWidth = Math.max(1, disc * 0.18)
          g.beginPath()
          g.ellipse(pt.x, pt.y, disc * 2.2, disc * 0.8, -0.35, 0, Math.PI * 2)
          g.stroke()
        }
        g.fillStyle = night ? '#ff5050' : (PLANET_COLOURS[pl.name] ?? c.text)
        g.fillText(pl.name, pt.x + r + 4, pt.y + 4)
      }
      g.globalAlpha = 1
    }

    // Sun and Moon.
    if (bodies) {
      for (const [body, colour, name] of [
        [bodies.sun, '#ffd34d', 'Sun'],
        [bodies.moon, '#d8d8d0', `Moon ${Math.round(bodies.illumination * 100)}%`],
      ] as [Sky, string, string][]) {
        const pt = p.sky(body.ra, body.dec)
        if (!visible(pt, 30)) continue
        const r = Math.max(7, p.pixels(0.5) / 2)
        g.globalAlpha = altitudeOf(body.ra, body.dec) < 0 ? 0.45 : 1
        g.fillStyle = night ? '#a01010' : colour
        g.beginPath()
        g.arc(pt.x, pt.y, r, 0, Math.PI * 2)
        g.fill()
        g.fillStyle = c.text
        g.fillText(name, pt.x + r + 4, pt.y + 4)
      }
      g.globalAlpha = 1
    }

    // The ground: opaque green below the horizon (dark red at night), so it's clear what's out of reach.
    if (layers && ground) {
      g.save()
      g.imageSmoothingEnabled = true
      g.drawImage(layers.ground, 0, 0, width, height)
      g.restore()
    }

    // Horizon and the cardinal points.
    if (site) {
      g.strokeStyle = c.horizon
      g.lineWidth = 2
      const pts = []
      for (let az = 0; az <= 360; az += sample) pts.push(p.frame(p.toFrame(horToEq(altAzVec(0, az), lst, latitude))))
      polyline(pts)
      g.fillStyle = c.horizon
      g.font = 'bold 15px system-ui, sans-serif'
      g.textAlign = 'center'
      for (const [az, name] of [
        [0, 'N'],
        [45, 'NE'],
        [90, 'E'],
        [135, 'SE'],
        [180, 'S'],
        [225, 'SW'],
        [270, 'W'],
        [315, 'NW'],
      ] as [number, string][]) {
        const pt = p.frame(p.toFrame(horToEq(altAzVec(0, az), lst, latitude)))
        if (visible(pt)) g.fillText(name, pt.x, pt.y + 18)
      }
    }


    // Reference points: the celestial poles, and (with a site) the zenith and nadir.
    {
      const marks: [{ x: number; y: number } | null, string][] = [
        [p.sky(0, 90), 'NCP'],
        [p.sky(0, -90), 'SCP'],
      ]
      if (site) {
        marks.push([p.frame(p.toFrame(horToEq(altAzVec(90, 0), lst, latitude))), 'Zenith'])
        marks.push([p.frame(p.toFrame(horToEq(altAzVec(-90, 0), lst, latitude))), 'Nadir'])
      }
      g.strokeStyle = c.text
      g.fillStyle = c.text
      g.lineWidth = 1.5
      g.font = '12px system-ui, sans-serif'
      g.textAlign = 'left'
      for (const [pt, name] of marks) {
        if (!visible(pt, -6)) continue
        g.globalAlpha = 0.85
        g.beginPath()
        g.moveTo(pt.x - 6, pt.y)
        g.lineTo(pt.x + 6, pt.y)
        g.moveTo(pt.x, pt.y - 6)
        g.lineTo(pt.x, pt.y + 6)
        g.stroke()
        g.fillText(name, pt.x + 8, pt.y - 6)
      }
      g.globalAlpha = 1
    }

    const polygon = (corners: Sky[], colour: string, dash: number[], fill?: string): { x: number; y: number }[] | null => {
      const pts = corners.map((k) => p.sky(k.ra, k.dec))
      if (!pts.every(Boolean)) return null
      g.beginPath()
      pts.forEach((pt, i) => (i ? g.lineTo(pt!.x, pt!.y) : g.moveTo(pt!.x, pt!.y)))
      g.closePath()
      if (fill) {
        g.fillStyle = fill
        g.fill()
      }
      g.setLineDash(dash)
      g.strokeStyle = colour
      g.lineWidth = 2
      g.stroke()
      g.setLineDash([])
      // A dot on the top edge: which way is up on the camera.
      g.fillStyle = colour
      g.beginPath()
      g.arc((pts[0]!.x + pts[1]!.x) / 2, (pts[0]!.y + pts[1]!.y) / 2, 3.5, 0, Math.PI * 2)
      g.fill()
      return pts as { x: number; y: number }[]
    }

    // A name by a box, written along the box's own edges (turned with it, but never upside down): above its top-left
    // or top-right corner, or centred under its bottom edge, so the three boxes' names don't land on each other.
    // pts are corners as polygon() drew them (top-left, top-right, bottom-right, bottom-left), several boxes' for a mosaic.
    const boxLabel = (pts: { x: number; y: number }[] | null, text: string, colour: string, where: 'top-left' | 'top-right' | 'bottom') => {
      if (!pts || pts.length < 4) return
      const unit = (x: number, y: number) => {
        const l = Math.hypot(x, y) || 1
        return { x: x / l, y: y / l }
      }
      // The box's axes on screen: along its top edge, and towards its top.
      const along = unit(pts[1].x - pts[0].x, pts[1].y - pts[0].y)
      const up = unit(pts[0].x + pts[1].x - pts[2].x - pts[3].x, pts[0].y + pts[1].y - pts[2].y - pts[3].y)
      const a = (q: { x: number; y: number }) => q.x * along.x + q.y * along.y
      const u = (q: { x: number; y: number }) => q.x * up.x + q.y * up.y
      const at = (alongPos: number, upPos: number) => ({
        x: along.x * alongPos + up.x * upPos,
        y: along.y * alongPos + up.y * upPos,
      })
      const minA = Math.min(...pts.map(a))
      const maxA = Math.max(...pts.map(a))
      const top = Math.max(...pts.map(u))
      const bottom = Math.min(...pts.map(u))
      const gap = 5
      const anchor =
        where === 'top-left' ? at(minA, top + gap) : where === 'top-right' ? at(maxA, top + gap) : at((minA + maxA) / 2, bottom - gap)

      // Turn the text with the box, flipped half a turn when that would leave it upside down.
      let angle = Math.atan2(along.y, along.x)
      let align: CanvasTextAlign = where === 'top-left' ? 'left' : where === 'top-right' ? 'right' : 'center'
      if (Math.cos(angle) < 0) {
        angle += Math.PI
        if (align !== 'center') align = align === 'left' ? 'right' : 'left'
      }
      // Which way the text's "up" points, so it sits outside the box rather than over its edge.
      const textUp = { x: Math.sin(angle), y: -Math.cos(angle) }
      const outward = where === 'bottom' ? -1 : 1
      const textUpIsOut = (textUp.x * up.x + textUp.y * up.y) * outward > 0

      g.save()
      g.translate(anchor.x, anchor.y)
      g.rotate(angle)
      g.font = 'bold 12px system-ui, sans-serif'
      g.fillStyle = colour
      g.textAlign = align
      g.textBaseline = textUpIsOut ? 'bottom' : 'top'
      g.fillText(text, 0, 0)
      g.restore()
    }

    if (solved) boxLabel(polygon(solved, c.solved, [8, 5]), solvedAngle != null ? `Solve ${((Math.round(solvedAngle * 10) / 10) % 360).toFixed(1)}°` : 'Solve', c.solved, 'bottom')
    const framePoints = frames.flatMap((f) => polygon(f.corners, c.frame, [], 'rgba(220, 188, 110, 0.07)') ?? [])
    boxLabel(framePoints, frameAngle != null ? `Target ${(Math.round(frameAngle * 10) / 10).toFixed(1)}°` : 'Target', c.frame, 'top-left')
    rotateHandle = null
    if (onrotateframe && frameCentre && framePoints.length >= 4) {
      // A small right angle just outside the frame's bottom-right corner (the whole mosaic's), along the frame's own
      // axes: the corner to grab to turn it.
      const unit = (x: number, y: number) => {
        const l = Math.hypot(x, y) || 1
        return { x: x / l, y: y / l }
      }
      const pts = framePoints
      const along = unit(pts[1].x - pts[0].x, pts[1].y - pts[0].y)
      const up = unit(pts[0].x + pts[1].x - pts[2].x - pts[3].x, pts[0].y + pts[1].y - pts[2].y - pts[3].y)
      const a = (q: { x: number; y: number }) => q.x * along.x + q.y * along.y
      const u = (q: { x: number; y: number }) => q.x * up.x + q.y * up.y
      const right = Math.max(...pts.map(a))
      const bottom = Math.min(...pts.map(u))
      const at = (alongPos: number, upPos: number) => ({ x: along.x * alongPos + up.x * upPos, y: along.y * alongPos + up.y * upPos })
      const gapOut = 6 // outside the box
      const arm = 11
      const corner = at(right + gapOut, bottom - gapOut)
      if (visible(corner)) {
        g.save()
        g.strokeStyle = c.frame
        g.lineWidth = 2.5
        g.lineCap = 'round'
        g.lineJoin = 'round'
        g.beginPath()
        const startPt = at(right + gapOut - arm, bottom - gapOut)
        const endPt = at(right + gapOut, bottom - gapOut + arm)
        g.moveTo(startPt.x, startPt.y)
        g.lineTo(corner.x, corner.y)
        g.lineTo(endPt.x, endPt.y)
        g.stroke()
        g.restore()
        // Picked up anywhere around the angle.
        rotateHandle = at(right + gapOut - arm / 3, bottom - gapOut + arm / 3)
      }
    }
    if (frames.length > 1) {
      g.font = 'bold 14px system-ui, sans-serif'
      g.textAlign = 'center'
      g.fillStyle = c.frame
      for (const f of frames) {
        const pt = p.sky(f.centre.ra, f.centre.dec)
        if (visible(pt)) g.fillText(f.label, pt.x, pt.y + 5)
      }
    }

    // Selection.
    if (selected) {
      const pt = p.sky(selected.ra, selected.dec)
      if (visible(pt)) {
        g.strokeStyle = c.frame
        g.lineWidth = 1.5
        for (const a of [0, 1, 2, 3]) {
          const ang = (a * Math.PI) / 2 + Math.PI / 4
          g.beginPath()
          g.moveTo(pt.x + Math.cos(ang) * 10, pt.y + Math.sin(ang) * 10)
          g.lineTo(pt.x + Math.cos(ang) * 18, pt.y + Math.sin(ang) * 18)
          g.stroke()
        }
      }
    }

    // The mount, live: a reticle, and its camera frame.
    if (mount && showMount) {
      // The mount's name (or nickname) labels its camera frame; by the reticle only when there's no frame to label.
      const mountName = mount.slewing ? `${mount.name} (slewing)` : mount.name
      const mountBox =
        mountFrame && mountFrame.width > 0
          ? polygon(frameCorners({ ra: mount.ra, dec: mount.dec }, mountFrame.width, mountFrame.height, mountFrame.pa), c.mount, [4, 4])
          : null
      boxLabel(mountBox, mountName, c.mount, 'top-right')
      const pt = p.sky(mount.ra, mount.dec)
      if (visible(pt, 40)) {
        g.strokeStyle = c.mount
        g.lineWidth = 2
        g.beginPath()
        g.arc(pt.x, pt.y, 14, 0, Math.PI * 2)
        g.stroke()
        for (const [dx, dy] of [
          [1, 0],
          [-1, 0],
          [0, 1],
          [0, -1],
        ]) {
          g.beginPath()
          g.moveTo(pt.x + dx * 8, pt.y + dy * 8)
          g.lineTo(pt.x + dx * 22, pt.y + dy * 22)
          g.stroke()
        }
        if (!mountBox) {
          g.fillStyle = c.mount
          g.font = 'bold 12px system-ui, sans-serif'
          g.textAlign = 'left'
          g.fillText(mountName, pt.x + 18, pt.y - 16)
        }
      } else {
        // Off screen: an arrow at the edge pointing to it.
        const target = p.sky(mount.ra, mount.dec)
        const dx = target ? target.x - width / 2 : 0
        const dy = target ? target.y - height / 2 : 0
        if (target && (dx || dy)) {
          const k = Math.min((width / 2 - 24) / Math.abs(dx || 1e-9), (height / 2 - 24) / Math.abs(dy || 1e-9))
          const x = width / 2 + dx * k
          const y = height / 2 + dy * k
          const a = Math.atan2(dy, dx)
          g.fillStyle = c.mount
          g.beginPath()
          g.moveTo(x + Math.cos(a) * 12, y + Math.sin(a) * 12)
          g.lineTo(x + Math.cos(a + 2.5) * 10, y + Math.sin(a + 2.5) * 10)
          g.lineTo(x + Math.cos(a - 2.5) * 10, y + Math.sin(a - 2.5) * 10)
          g.closePath()
          g.fill()
        }
      }
    }
  }

  // ---- Interaction: drag to pan (or move the frame), wheel to zoom, click to pick ----
  type Drag = {
    pointer: number
    x: number
    y: number
    moved: boolean
    start: Projection
    kind: 'pan' | 'frame' | 'rotate'
    frameAt: { x: number; y: number } | null
    /** Turning the frame: its centre on screen, the pointer's angle round it and the frame's angle at the start, and
     * which way a position angle turns on screen here (+1 or -1: the chart can show the sky either way round). */
    turn?: { cx: number; cy: number; from: number; pa: number; sense: number }
  }
  let drag: Drag | null = null
  /** Where the rotation handle was drawn, for picking it up. */
  let rotateHandle: { x: number; y: number } | null = null
  let overHandle = $state(false)

  // Which way the position angle turns on screen at a point: from north towards east, clockwise or anticlockwise.
  function turnSense(at: Sky): number {
    const c = projection.sky(at.ra, at.dec)
    const n = projection.sky(at.ra, Math.min(89.9, at.dec + 0.1))
    const e = projection.sky(at.ra + 0.1 / 15 / Math.max(0.05, Math.cos((at.dec * Math.PI) / 180)), at.dec)
    if (!c || !n || !e) return 1
    const cross = (n.x - c.x) * (e.y - c.y) - (n.y - c.y) * (e.x - c.x)
    return cross >= 0 ? 1 : -1
  }

  function local(e: PointerEvent | WheelEvent) {
    const r = canvas!.getBoundingClientRect()
    return { x: e.clientX - r.left, y: e.clientY - r.top }
  }

  function insideFrames(x: number, y: number): boolean {
    return frames.some((f) => {
      const pts = f.corners.map((k) => projection.sky(k.ra, k.dec))
      if (!pts.every(Boolean)) return false
      let inside = false
      for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
        const a = pts[i]!
        const b = pts[j]!
        if (a.y > y !== b.y > y && x < ((b.x - a.x) * (y - a.y)) / (b.y - a.y) + a.x) inside = !inside
      }
      return inside
    })
  }

  // Two fingers pinch to zoom (around the point between them); a pinch cancels the first finger's pan or tap.
  const fingers = new Map<number, { x: number; y: number }>()
  let pinchGap: number | null = null
  const gap = () => {
    const [a, b] = [...fingers.values()]
    return Math.hypot(a.x - b.x, a.y - b.y)
  }
  const between = () => {
    const [a, b] = [...fingers.values()]
    return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
  }
  function releaseFinger(e: PointerEvent) {
    if (!fingers.delete(e.pointerId)) return false
    if (fingers.size < 2) pinchGap = null
    return true
  }

  function pointerdown(e: PointerEvent) {
    if (!canvas || e.button !== 0) return
    const at = local(e)
    if (e.pointerType === 'touch') {
      fingers.set(e.pointerId, at)
      if (fingers.size === 2) {
        drag = null
        pinchGap = gap()
        try {
          canvas.setPointerCapture(e.pointerId)
        } catch {
          // fine
        }
        return
      }
      if (fingers.size > 2) return
    }
    if (rotateHandle && frameCentre && onrotateframe && Math.hypot(at.x - rotateHandle.x, at.y - rotateHandle.y) <= 18) {
      const centre = projection.sky(frameCentre.ra, frameCentre.dec)
      if (centre) {
        drag = {
          pointer: e.pointerId,
          ...at,
          moved: false,
          start: projection,
          kind: 'rotate',
          frameAt: null,
          turn: { cx: centre.x, cy: centre.y, from: Math.atan2(at.y - centre.y, at.x - centre.x), pa: frameAngle ?? 0, sense: turnSense(frameCentre) },
        }
        overHandle = true // the turning cursor while it's held
        try {
          canvas.setPointerCapture(e.pointerId)
        } catch {
          // synthetic pointer: fine
        }
        return
      }
    }
    const onFrame = frameCentre && insideFrames(at.x, at.y)
    drag = {
      pointer: e.pointerId,
      ...at,
      moved: false,
      start: projection,
      kind: onFrame ? 'frame' : 'pan',
      frameAt: onFrame && frameCentre ? projection.sky(frameCentre.ra, frameCentre.dec) : null,
    }
    try {
      canvas.setPointerCapture(e.pointerId)
    } catch {
      // synthetic pointer: fine
    }
  }
  function pointermove(e: PointerEvent) {
    if (fingers.has(e.pointerId)) {
      fingers.set(e.pointerId, local(e))
      if (pinchGap !== null && fingers.size >= 2) {
        const now = gap()
        if (now > 0 && Math.abs(now - pinchGap) > 1) {
          zoomAt(between(), pinchGap / now)
          pinchGap = now
        }
        return
      }
    }
    if (!drag) {
      const at = local(e)
      overHandle = !!rotateHandle && Math.hypot(at.x - rotateHandle.x, at.y - rotateHandle.y) <= 18
    }
    if (!drag || e.pointerId !== drag.pointer) return
    const at = local(e)
    const dx = at.x - drag.x
    const dy = at.y - drag.y
    if (!drag.moved && Math.hypot(dx, dy) < 4) return
    drag.moved = true
    if (drag.kind === 'rotate' && drag.turn) {
      // The frame turns as far as the pointer has gone round its centre, to the nearest half degree.
      const t = drag.turn
      const turned = ((Math.atan2(at.y - t.cy, at.x - t.cx) - t.from) * 180) / Math.PI
      const pa = (((t.pa + t.sense * turned) % 360) + 360) % 360
      onrotateframe?.((Math.round(pa * 2) / 2) % 360)
    } else if (drag.kind === 'frame' && drag.frameAt) {
      onmoveframe(drag.start.unproject(drag.frameAt.x + dx, drag.frameAt.y + dy))
    } else {
      follow = false
      setCentre(drag.start.unprojectFrame(width / 2 - dx, height / 2 - dy))
    }
  }
  function pointerup(e: PointerEvent) {
    releaseFinger(e)
    if (!drag || e.pointerId !== drag.pointer) return
    const wasClick = !drag.moved && drag.kind !== 'rotate'
    const at = local(e)
    drag = null
    if (wasClick) pick(at.x, at.y)
  }

  function wheel(e: WheelEvent) {
    e.preventDefault()
    zoomAt(local(e), e.deltaY > 0 ? 1.2 : 1 / 1.2)
  }
  /** Zooms keeping the sky under a screen point where it is. */
  export function zoomAt(at: { x: number; y: number } | null, factor: number) {
    const fov = clampFov(view.fov * factor)
    if (!at) {
      view.fov = fov
      return
    }
    const before = projection.unprojectFrame(at.x, at.y)
    const next = new Projection(mode, centreVec, fov, width, height, lst, latitude)
    const after = next.unprojectFrame(at.x, at.y)
    view.fov = fov
    if (!follow) setCentre(norm([0, 1, 2].map((i) => centreVec[i] + before[i] - after[i]) as Vec))
  }

  function pick(x: number, y: number) {
    let best: { d: number; target: TargetInfo } | null = null
    const consider = (d: number, target: () => TargetInfo) => {
      if (d < 14 && (!best || d < best.d)) best = { d, target: target() }
    }
    const fov = view.fov
    const objects = fov <= 30 ? [...famous, ...deepObjects] : famous
    for (const o of objects) {
      const pt = projection.sky(o.raHours, o.decDegrees)
      if (!pt) continue
      const r = Math.max(3, ((o.majorArcmin ?? 2) / 2) * projection.pixels(1 / 60))
      const d = Math.hypot(pt.x - x, pt.y - y)
      consider(d <= r ? Math.min(d, 6) : d, () => o) // a click inside an object's outline picks it
    }
    const limit = limitMagnitude(fov)
    for (const s of fov <= 30 ? [...bright, ...deepStars] : bright) {
      if (s.mag > limit) continue
      const pt = projection.sky(s.ra, s.dec)
      if (!pt) continue
      consider(Math.hypot(pt.x - x, pt.y - y) + 2, () => starTarget(s))
    }
    for (const pl of bodies?.planets ?? []) {
      const pt = projection.sky(pl.ra, pl.dec)
      if (pt) consider(Math.max(0, Math.hypot(pt.x - x, pt.y - y) - 4), () => planetTarget(pl))
    }
    const chosen = best as { d: number; target: TargetInfo } | null
    if (chosen) onselect(chosen.target)
    else {
      const sky = projection.unproject(x, y)
      onselect(pointTarget(sky.ra, sky.dec))
    }
  }

  function starTarget(s: Star): TargetInfo {
    const name = s.label ?? `Star ${formatRa(s.ra)} ${formatDec(s.dec)}`
    return {
      label: name,
      commonName: null,
      names: [name],
      type: '*',
      typeName: 'Star',
      group: 'star',
      raHours: s.ra,
      decDegrees: s.dec,
      ra: formatRa(s.ra),
      dec: formatDec(s.dec),
      majorArcmin: null,
      minorArcmin: null,
      angleDegrees: null,
      magnitude: s.mag,
      surfaceBrightness: null,
      visibility: null,
    }
  }

  function planetTarget(pl: Planet): TargetInfo {
    return {
      ...starTarget({ ra: pl.ra, dec: pl.dec, mag: pl.magnitude, label: pl.name }),
      type: 'Planet',
      typeName: pl.name === 'Pluto' ? 'Dwarf planet' : 'Planet',
      group: 'planet',
      majorArcmin: pl.diameter / 60,
    }
  }

  function pointTarget(ra: number, dec: number): TargetInfo {
    const label = `RA ${formatRa(ra).slice(0, 5)} Dec ${formatDec(dec).slice(0, 6)}`
    return { ...starTarget({ ra, dec, mag: 0, label }), label, names: [label], type: 'point', typeName: 'Sky position', magnitude: null }
  }

  const centreText = $derived.by(() => {
    const c = projection.unproject(width / 2, height / 2)
    if (mode === 'horizon') return `Az ${view.lon.toFixed(0)}° Alt ${view.lat.toFixed(0)}°`
    return `RA ${formatRa(c.ra).slice(0, 5)} Dec ${formatDec(c.dec).slice(0, 6)}`
  })
  const fovText = $derived(view.fov >= 1 ? `${view.fov.toFixed(view.fov < 10 ? 1 : 0)}°` : `${(view.fov * 60).toFixed(0)}′`)
</script>

<div class="chart" bind:clientWidth={width} bind:clientHeight={height}>
  <canvas
    bind:this={canvas}
    class:turning={overHandle}
    onpointerdown={pointerdown}
    onpointermove={pointermove}
    onpointerup={pointerup}
    onpointercancel={(e) => {
      releaseFinger(e)
      drag = null
    }}
    onwheel={wheel}
    aria-label="Sky chart: drag to look around, scroll or pinch to zoom, click to pick a target"
  ></canvas>
  <div class="hud">
    <span class="mono">{centreText}, field {fovText}</span>
    {#if showSurvey && surveyFailed}<span>no sky image (offline?)</span>{/if}
  </div>
</div>

<style>
  .chart {
    position: relative;
    width: 100%;
    height: 100%;
    min-height: 300px;
    overflow: hidden;
    border-radius: var(--radius);
    background: #04060c;
  }
  canvas {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    display: block;
    touch-action: none;
    cursor: grab;
  }
  canvas:active {
    cursor: grabbing;
  }
  canvas.turning {
    cursor: crosshair;
  }
  .hud {
    position: absolute;
    left: 8px;
    bottom: 8px;
    display: flex;
    gap: 8px;
    font-size: 12px;
    color: #c8d0e0;
    pointer-events: none;
  }
  .hud span {
    background: rgba(0, 0, 0, 0.55);
    padding: 2px 6px;
    border-radius: 4px;
  }
  :global(:root[data-theme='night']) .hud {
    color: #ff5050;
  }
</style>
