<script lang="ts">
  import { tick } from 'svelte'
  import { api } from '../lib/api'
  import { framing, mainView, planner, sequenceInbox, stellariumPick } from '../lib/inbox.svelte'
  import { formatDec, formatRa, mosaic, positionAngle, separation, type Sky } from '../lib/sky'
  import { act, mountLock, nick, nova } from '../lib/state.svelte'
  import { eqToHor, eqVec, lstDegrees, vecToAltAz, type ChartView } from '../lib/skyview'
  import type { AltitudeCurve, NightInfo, SolvedFrame, TargetInfo, TonightFilters } from '../lib/types'
  import AltitudeChart from './AltitudeChart.svelte'
  import SkyChart from './SkyChart.svelte'
  import OfflineSky from './OfflineSky.svelte'

  // Target picker (search the catalog, what's well placed tonight, or anything clicked on the sky chart) and
  // framing tool (the camera's frame or a mosaic on the sky, rotated to match the camera), with the mount's
  // live position, feeding slews, centring and the sequence.

  const KEY = 'nova.framing'
  type BoxName = 'picker' | 'controls' | 'altitude'
  interface Box {
    x: number
    y: number
    w: number
    h: number
  }
  interface Saved {
    target: { name: string; ra: number; dec: number; info: TargetInfo | null } | null
    centre: Sky | null
    pa: number
    columns: number
    rows: number
    overlap: number
    chart: ChartView
    follow: boolean
    constellations: boolean
    atmosphere: boolean
    ground: boolean
    /** The patch of sky where guiding calibrates best. */
    calibration: boolean
    survey: string
    camera: string | null
    /** Which panels float over the chart (collapsed ones leave just their button in the toolbar). */
    show: { picker: boolean; controls: boolean; altitude: boolean }
    /** Panels moved or resized over the chart (px from its top-left); a missing one sits in its usual place. */
    boxes: Partial<Record<BoxName, Box>>
    /** Which boxes the chart draws: the planned frame, the mount's, the last plate solve's. */
    layers: { target: boolean; mount: boolean; solve: boolean }
    filters: TonightFilters
  }
  const defaults: Saved = {
    target: null,
    centre: null,
    pa: 0,
    columns: 1,
    rows: 1,
    overlap: 0.15,
    chart: { mode: 'horizon', lon: 180, lat: 40, fov: 100 },
    follow: false,
    constellations: true,
    atmosphere: true,
    ground: true,
    calibration: false,
    survey: 'tiles',
    camera: null,
    show: { picker: true, controls: true, altitude: true },
    boxes: {},
    layers: { target: true, mount: true, solve: true },
    filters: {
      groups: ['galaxy', 'nebula', 'cluster', 'planetary'],
      minSize: 10,
      maxSize: null,
      maxMagnitude: null,
      minAltitude: 30,
      minHours: 2,
      minMoonDistance: null,
      sort: 'best',
    },
  }
  function load(): Saved {
    try {
      const raw = localStorage.getItem(KEY)
      if (raw) {
        const s = JSON.parse(raw)
        // "auto" was the rendered images before the stored survey existed: the stored survey replaces it.
        const survey = !s.survey || s.survey === 'auto' ? 'tiles' : s.survey
        // The frame's rotation starts at 0° each time (match the camera, or turn it, for a target); the rest is kept.
        return { ...defaults, ...s, pa: 0, survey, chart: { ...defaults.chart, ...s.chart }, filters: { ...defaults.filters, ...s.filters }, show: { ...defaults.show, ...s.show }, boxes: { ...s.boxes }, layers: { ...defaults.layers, ...s.layers } }
      }
    } catch {
      // storage unavailable or unreadable: start fresh
    }
    return structuredClone(defaults)
  }
  const s = $state<Saved>(load())

  // The floating panels can be moved (by their heading) and resized (from their bottom-right corner) over the chart.
  let overlay = $state<HTMLElement>()
  let front = $state<BoxName | null>(null) // the last one grabbed, on top of the others
  const smallest: Record<BoxName, [number, number]> = { picker: [220, 320], controls: [220, 160], altitude: [240, 100] }
  function boxStyle(name: BoxName): string | undefined {
    const b = s.boxes[name]
    // Kept inside the chart when the window gets smaller.
    return b
      ? `left: clamp(0px, ${b.x}px, calc(100% - ${b.w}px)); top: clamp(0px, ${b.y}px, calc(100% - ${b.h}px)); width: min(${b.w}px, 100%); height: min(${b.h}px, 100%)`
      : undefined
  }
  function grab(e: PointerEvent, name: BoxName, resizing: boolean) {
    if (!overlay || e.button !== 0) return
    if (!resizing && (e.target as HTMLElement).closest('button, input, select, a, .grip')) return
    const panel = (e.currentTarget as HTMLElement).closest('.floating') as HTMLElement
    front = name
    const o = overlay.getBoundingClientRect()
    const r = panel.getBoundingClientRect()
    const start = { px: e.clientX, py: e.clientY, x: r.left - o.left, y: r.top - o.top, w: r.width, h: r.height }
    const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v))
    let started = false
    const move = (ev: PointerEvent) => {
      const dx = ev.clientX - start.px
      const dy = ev.clientY - start.py
      if (!started) {
        if (Math.abs(dx) + Math.abs(dy) < 3) return // a click, not a drag
        started = true
        s.boxes[name] = { x: Math.round(start.x), y: Math.round(start.y), w: Math.round(start.w), h: Math.round(start.h) }
      }
      const b = s.boxes[name]
      if (!b) return
      if (resizing) {
        const [minW, minH] = smallest[name]
        b.w = Math.round(clamp(start.w + dx, minW, o.width - start.x))
        b.h = Math.round(clamp(start.h + dy, minH, o.height - start.y))
      } else {
        b.x = Math.round(clamp(start.x + dx, 0, o.width - start.w))
        b.y = Math.round(clamp(start.y + dy, 0, o.height - start.h))
      }
    }
    document.body.style.userSelect = 'none' // no text selected while dragging
    const up = () => {
      document.body.style.userSelect = ''
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      window.removeEventListener('pointercancel', up)
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
    window.addEventListener('pointercancel', up)
    e.preventDefault()
  }
  const placeBack = (name: BoxName) => delete s.boxes[name]
  $effect(() => {
    const json = JSON.stringify(s)
    try {
      localStorage.setItem(KEY, json)
    } catch {
      // ignore
    }
  })

  // ---- Camera field of view ----
  const cameras = $derived((nova.status?.optics ?? []).filter((o) => o.imageScale && o.sensorWidth && o.sensorHeight))
  const camera = $derived(cameras.find((c) => c.camera === s.camera) ?? cameras.find((c) => c.connected) ?? cameras[0] ?? null)
  const fieldW = $derived(camera ? (camera.imageScale! * camera.sensorWidth!) / 3600 : 1)
  const fieldH = $derived(camera ? (camera.imageScale! * camera.sensorHeight!) / 3600 : 0.67)
  const fieldText = (deg: number) => (deg >= 1 ? `${deg.toFixed(2)}°` : `${(deg * 60).toFixed(1)}′`)

  // ---- Frame(s) ----
  const centre = $derived<Sky | null>(s.centre ?? (s.target ? { ra: s.target.ra, dec: s.target.dec } : null))
  const panels = $derived(
    centre ? mosaic(centre, fieldW, fieldH, s.pa, Math.max(1, s.columns), Math.max(1, s.rows), Math.min(0.6, Math.max(0, s.overlap))) : [],
  )
  const frames = $derived(panels.map((p) => ({ centre: p.centre, corners: p.corners, label: String(p.index) })))

  const moveFrame = (p: Sky) => (s.centre = p)

  let chart = $state<SkyChart | null>(null)
  // The Layers drop-down: closes when clicking anywhere else.
  let layersOpen = $state(false)
  $effect(() => {
    if (!layersOpen) return
    const close = (e: PointerEvent) => {
      if (!(e.target as HTMLElement).closest('.layers')) layersOpen = false
    }
    window.addEventListener('pointerdown', close, true)
    return () => window.removeEventListener('pointerdown', close, true)
  })

  /** Picks a target; from the lists (not the chart) the chart also turns to it and zooms to frame it. */
  function select(t: TargetInfo, turn = true) {
    const name = t.commonName && t.group !== 'star' ? `${t.label} ${t.commonName}` : t.label
    s.target = { name, ra: t.raHours, dec: t.decDegrees, info: t }
    s.centre = null
    matchNote = null
    if (turn) {
      // Picked from the list: get it out of the way of the chart, and show the framing controls.
      s.show.picker = false
      s.show.controls = true
      fit(t)
    }
  }

  // Turn to the frame and zoom to show it (and the target) with some sky around.
  function fit(t: TargetInfo | null = s.target?.info ?? null) {
    const c = centre ?? (t ? { ra: t.raHours, dec: t.decDegrees } : null)
    if (!c) return
    const span = Math.hypot(fieldW * Math.max(1, s.columns), fieldH * Math.max(1, s.rows))
    const size = (t?.majorArcmin ?? 0) / 60
    s.follow = false
    chart?.goto(c.ra, c.dec, Math.min(60, Math.max(0.5, Math.max(span, size) * 2.2)))
  }

  // ---- Site and mount, for the chart ----
  const site = $derived.by(() => {
    const st = nova.status?.site
    const active = st?.sites.find((x) => x.name === st.active)
    const where = active ?? st?.mountLocation ?? null
    return where ? { latitude: where.latitude, longitude: where.longitude } : null
  })
  const mountInfo = $derived.by(() => {
    const m = nova.status?.mount
    return m?.connected ? { ra: m.raHours, dec: m.decDegrees, slewing: m.state === 'Slewing', name: nick(m.name) } : null
  })
  // Where the selected target is right now.
  let clock = $state(Date.now())
  $effect(() => {
    const timer = setInterval(() => (clock = Date.now()), 30_000)
    return () => clearInterval(timer)
  })
  const targetAltAz = $derived.by(() => {
    if (!site || !s.target) return null
    return vecToAltAz(eqToHor(eqVec(s.target.ra, s.target.dec), lstDegrees(new Date(clock), site.longitude), site.latitude))
  })
  const compass = (az: number) => ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'][Math.round(az / 45) % 8]

  // ---- Search and tonight's list ----
  let query = $state('')
  let results = $state<TargetInfo[]>([])
  let searchTimer: ReturnType<typeof setTimeout> | undefined
  $effect(() => {
    const q = query.trim()
    clearTimeout(searchTimer)
    if (!q) {
      results = []
      return
    }
    searchTimer = setTimeout(async () => {
      try {
        results = await api.searchTargets(q, 30)
      } catch (e) {
        nova.error = (e as Error).message
      }
    }, 200)
  })

  let tonight = $state<TargetInfo[]>([])
  let tonightCount = $state(0)
  let night = $state<NightInfo | null>(null)
  let tonightError = $state<string | null>(null)
  let loading = $state(false)
  let tonightTimer: ReturnType<typeof setTimeout> | undefined
  // Re-rate when the filters or the site change, every 10 minutes (the night moves on), and after a failure.
  const siteKey = $derived(`${nova.status?.site?.active ?? ''}|${nova.status?.mount?.connected ?? false}`)
  let refresh = $state(0)
  $effect(() => {
    const timer = setInterval(() => refresh++, 10 * 60_000)
    return () => clearInterval(timer)
  })
  $effect(() => {
    const filters = $state.snapshot(s.filters)
    void siteKey
    void refresh
    clearTimeout(tonightTimer)
    tonightTimer = setTimeout(async () => {
      loading = true
      try {
        const r = await api.tonight(filters as TonightFilters)
        tonight = r.targets
        tonightCount = r.count
        night = r.night
        tonightError = null
      } catch (e) {
        tonightError = (e as Error).message
        tonightTimer = setTimeout(() => refresh++, 15_000)
      } finally {
        loading = false
      }
    }, 300)
  })
  const toggleGroup = (g: string) =>
    (s.filters.groups = s.filters.groups.includes(g) ? s.filters.groups.filter((x) => x !== g) : [...s.filters.groups, g])
  const optional = (v: string) => (v.trim() === '' ? null : Number(v))

  const time = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '–')
  const size = (t: TargetInfo) =>
    t.majorArcmin == null
      ? ''
      : t.majorArcmin >= 60
        ? `${(t.majorArcmin / 60).toFixed(1)}°`
        : t.majorArcmin < 1
          ? `${Math.round(t.majorArcmin * 60)}″`
          : `${Math.round(t.majorArcmin)}′`

  // ---- Altitude through the night, for the frame centre ----
  let curve = $state<AltitudeCurve | null>(null)
  let curveTimer: ReturnType<typeof setTimeout> | undefined
  $effect(() => {
    const c = centre
    clearTimeout(curveTimer)
    if (!c) return
    curveTimer = setTimeout(async () => {
      try {
        curve = await api.altitudeCurve(formatRa(c.ra), formatDec(c.dec))
      } catch {
        curve = null
      }
    }, 400)
  })

  // ---- The last plate solve, to rotate the camera to match ----
  let solved = $state<SolvedFrame | null>(null)
  const lastSolve = $derived(nova.status?.plateSolve?.last)
  $effect(() => {
    if (!lastSolve?.solved) {
      solved = null
      return
    }
    void lastSolve.finishedUtc
    api.solvedFrame(lastSolve.imageId).then((f) => (solved = f))
  })
  const solvedCorners = $derived(solved ? solved.corners.map((c) => ({ ra: c.raHours, dec: c.decDegrees })) : null)
  const solvedInfo = $derived.by(() => {
    if (!solvedCorners || !solved) return null
    const c = { ra: solved.center.raHours, dec: solved.center.decDegrees }
    // The angle to the middle of the top edge.
    const mid = midpoint(solvedCorners[0], solvedCorners[1])
    return { pa: positionAngle(c, mid), centre: c, offset: centre ? separation(c, centre) * 60 : null }
  })
  function midpoint(a: Sky, b: Sky): Sky {
    const v = (p: Sky) => {
      const ra = p.ra * 15 * (Math.PI / 180)
      const d = (p.dec * Math.PI) / 180
      return [Math.cos(d) * Math.cos(ra), Math.cos(d) * Math.sin(ra), Math.sin(d)]
    }
    const [x, y, z] = v(a).map((k, i) => k + v(b)[i])
    const ra = (Math.atan2(y, x) * 180) / Math.PI / 15
    return { ra: ra < 0 ? ra + 24 : ra, dec: (Math.atan2(z, Math.hypot(x, y)) * 180) / Math.PI }
  }
  // From Stellarium (Mount panel): frame the object picked there. The catalogue's entry, when it's the same object,
  // gives it its usual name and size (for zooming to fit); otherwise Stellarium's name and position.
  $effect(() => {
    const request = stellariumPick.request
    if (!request) return
    stellariumPick.request = null
    void (async () => {
      let info: TargetInfo | null = null
      try {
        const found = await api.searchTargets(request.name, 5)
        info = found.find((t) => separation({ ra: t.raHours, dec: t.decDegrees }, { ra: request.ra, dec: request.dec }) < 0.2) ?? null
      } catch {
        // offline catalogue unavailable: Stellarium's own name will do
      }
      if (info) select(info, false)
      else {
        s.target = { name: request.name, ra: request.ra, dec: request.dec, info: null }
        s.centre = null
        matchNote = null
      }
      s.layers.target = true
      await tick()
      fit(info)
    })()
  })

  // "Match framing" from the image viewer: once that image's solve is here, frame exactly what it shows.
  let matchNote = $state<string | null>(null)
  $effect(() => {
    const request = framing.request
    if (!request) return
    if (solved?.imageId !== request.imageId) {
      // An image from the history: fetch its solved outline (the apply below runs when it arrives).
      api.solvedFrame(request.imageId).then((f) => {
        if (f) solved = f
        else framing.request = null
      })
      return
    }
    if (!solvedInfo) return
    const info = solvedInfo
    const corners = solvedCorners!
    framing.request = null
    s.target = { name: request.name, ra: info.centre.ra, dec: info.centre.dec, info: null }
    s.centre = null
    s.pa = Math.round(info.pa * 10) / 10 % 360
    s.columns = 1
    s.rows = 1
    s.layers.target = true
    s.layers.solve = true
    s.show.controls = true
    s.show.picker = false
    // An image from another camera or telescope won't match this camera's frame size: say so.
    const across = separation(corners[0], corners[1])
    const down = separation(corners[1], corners[2])
    const off = Math.max(Math.abs(across / fieldW - 1), Math.abs(down / fieldH - 1))
    matchNote =
      off > 0.1
        ? `The image covers ${fieldText(across)} × ${fieldText(down)}; ${camera ? `${nick(camera.camera)} covers ${fieldText(fieldW)} × ${fieldText(fieldH)}` : 'this camera differs'}. Pick the camera it was taken with to compare like with like.`
        : null
    fit(null)
  })

  // How far the camera is from the plan's rotation, the short way round.
  const turnBy = $derived(solvedInfo ? ((((s.pa - solvedInfo.pa) % 360) + 540) % 360) - 180 : null)

  // ---- Actions ----
  const mountReady = $derived(nova.status?.mount?.connected ?? false)
  const sequenceRunning = $derived(nova.status?.sequencer?.running ?? false)
  const cameraReady = $derived(nova.status?.camera?.connected ?? false)
  const rotating = $derived(nova.status?.plateSolve?.rotation?.running ?? false)
  const centreRa = $derived(centre ? formatRa(centre.ra) : '')
  const centreDec = $derived(centre ? formatDec(centre.dec) : '')

  // The frame (or mosaic panels) as sequence targets: what "Add to sequence" and a sequence target's
  // "From target planner" use.
  const asTargets = $derived.by(() => {
    if (!centre) return []
    const name = s.target?.name ?? 'Target'
    return panels.map((p) => ({
      name: panels.length > 1 ? `${name} panel ${p.index}` : name,
      ra: formatRa(p.centre.ra),
      dec: formatDec(p.centre.dec),
      rotation: Math.round(s.pa * 10) / 10,
    }))
  })
  $effect(() => {
    planner.frame = asTargets.length
      ? { name: s.target?.name ?? 'Target', panels: asTargets, centre: { ra: centreRa, dec: centreDec } }
      : null
  })

  function addToSequence() {
    if (!centre) return
    sequenceInbox.targets.push(...asTargets)
    mainView.view = 'sequence'
  }

  function frameMount() {
    const m = nova.status?.mount
    if (!m?.connected) return
    s.target = { name: 'Mount position', ra: m.raHours, dec: m.decDegrees, info: null }
    s.centre = null
    fit(null)
  }
</script>

<div class="targets">
  <div class="title">
    {#if s.target}
      <b>{s.target.name}</b>
      {#if s.target.info}
        {@const t = s.target.info}
        <span class="muted">{t.typeName}{#if size(t)}, {size(t)}{/if}{#if t.magnitude != null}, magnitude {t.magnitude}{/if}</span>
      {/if}
      {#if targetAltAz}
        <span class="muted small" class:bad={targetAltAz.alt < 0}
          >{targetAltAz.alt.toFixed(0)}° up in the {compass(targetAltAz.az)}{targetAltAz.alt < 0 ? ' (below the horizon)' : ''}</span
        >
      {/if}
      <span class="mono muted small">frame centre {centreRa} {centreDec}</span>
    {:else}
      <span class="muted">Search, pick from tonight's list, or click anything on the sky to plan a target.</span>
    {/if}
  </div>

  <div class="canvas">
    <SkyChart
      bind:this={chart}
      bind:view={s.chart}
      bind:follow={s.follow}
      constellations={s.constellations}
      atmosphere={s.atmosphere}
      ground={s.ground}
      calibration={s.calibration}
      sunZone={nova.status?.site?.sunProtection
        ? { degrees: nova.status.site.sunAvoidance, active: nova.status.site.sunAltitude == null || nova.status.site.sunAltitude > -3 }
        : null}
      {site}
      survey={s.survey}
      frames={s.layers.target ? frames : []}
      frameCentre={centre}
      onmoveframe={moveFrame}
      solved={s.layers.solve ? solvedCorners : null}
      solvedAngle={solvedInfo?.pa ?? null}
      showMount={s.layers.mount}
      mount={mountInfo}
      mountFrame={camera ? { width: fieldW, height: fieldH, pa: solvedInfo?.pa ?? s.pa } : null}
      selected={s.target && s.layers.target ? { ra: s.target.ra, dec: s.target.dec } : null}
      onselect={(t) => select(t, false)}
    />
    <!-- Everything else floats over the chart; each panel collapses to its button in the toolbar. -->
    <div class="overlay" bind:this={overlay}>
      <div class="toolbar">
        <button class="secondary panel-toggle" class:on={s.show.picker} aria-pressed={s.show.picker} onclick={() => (s.show.picker = !s.show.picker)}
          title="Search and tonight's best targets">☰ Targets</button
        >
        <div class="segmented" role="group" aria-label="Chart orientation">
          <button
            class:on={s.chart.mode === 'horizon'}
            disabled={!site}
            title={site ? 'Horizon level, as you see the sky' : 'Set your site to see the horizon'}
            onclick={() => (s.chart.mode = 'horizon')}>Horizon</button
          >
          <button class:on={s.chart.mode === 'equatorial'} title="Celestial north up" onclick={() => (s.chart.mode = 'equatorial')}>Equatorial</button>
        </div>
        <button class="secondary" onclick={() => chart?.zoomAt(null, 1 / 1.5)} title="Zoom in">＋</button>
        <button class="secondary" onclick={() => chart?.zoomAt(null, 1.5)} title="Zoom out">－</button>
        <button class="secondary" disabled={!centre} onclick={() => fit()} title="Turn to the target and frame it">Target</button>
        <div class="layers">
          <button class="secondary" aria-expanded={layersOpen} onclick={() => (layersOpen = !layersOpen)} title="What the chart draws">Layers ▾</button>
          {#if layersOpen}
            <div class="layers-menu panel" role="group" aria-label="Chart layers">
              <button class="secondary reset-boxes" disabled={!Object.keys(s.boxes).length} onclick={() => (s.boxes = {})}
                title="The Targets, Framing and Altitude panels back where they started, at their usual size">Put panels back</button>
              <hr />
              <label class="check" title="The daytime sky's blue glow and the bright haze round the Sun">
                <input type="checkbox" bind:checked={s.atmosphere} /> Atmosphere
              </label>
              <label class="check" title="The ground below the horizon">
                <input type="checkbox" bind:checked={s.ground} /> Ground
              </label>
              <label class="check" title="Constellation figures and names">
                <input type="checkbox" bind:checked={s.constellations} /> Constellations
              </label>
              <label class="check" title="Where PHD2 calibrates best: near the celestial equator and the meridian">
                <input type="checkbox" bind:checked={s.calibration} /> Calibration area
              </label>
              <hr />
              <label class="check" title="The planned frame (yellow) and the target marker">
                <input type="checkbox" bind:checked={s.layers.target} /> Target
              </label>
              <label class="check" title="The mount's reticle and camera frame (red)">
                <input type="checkbox" bind:checked={s.layers.mount} /> Mount
              </label>
              <label class="check" title="The last plate solve's frame (green)">
                <input type="checkbox" bind:checked={s.layers.solve} /> Solve
              </label>
            </div>
          {/if}
        </div>
        <label class="check follow" title="Keep the mount in the middle of the chart">
          <input type="checkbox" bind:checked={s.follow} disabled={!mountInfo} /> Follow
        </label>
        <select class="survey" bind:value={s.survey} title="Sky imagery: the stored survey is instant (and offline once downloaded); the high-res images are rendered for each view, online only">
          <option value="tiles">Sky: DSS (stored, fast)</option>
          <option value="CDS/P/DSS2/color">Sky: high-res DSS colour (online, slow)</option>
          <option value="CDS/P/DSS2/red">Sky: high-res DSS red (online, slow)</option>
          <option value="CDS/P/Mellinger/color">Sky: wide-field Mellinger (online)</option>
          <option value="none">Sky: stars only</option>
        </select>
        <OfflineSky />
        <span class="spacer"></span>
        <!-- Together, so a narrow window wraps them as a pair. -->
        <span class="panel-buttons">
        {#if curve && centre}
          <button class="secondary panel-toggle" class:on={s.show.altitude} aria-pressed={s.show.altitude} onclick={() => (s.show.altitude = !s.show.altitude)}
            title="The target's altitude through the night">Altitude</button
          >
        {/if}
        {#if centre}
          <button class="secondary panel-toggle" class:on={s.show.controls} aria-pressed={s.show.controls} onclick={() => (s.show.controls = !s.show.controls)}
            title="Camera, rotation, mosaic, slew and centre">Framing</button
          >
        {/if}
        </span>
      </div>
      <div class="side left">
        {#if s.show.picker}
          <section class="picker floating" class:free={!!s.boxes.picker} class:front={front === 'picker'} style={boxStyle('picker')} aria-label="Find a target">
            <div class="floating-head" role="presentation" title="Drag to move, double-click to put back"
              onpointerdown={(e) => grab(e, 'picker', false)} ondblclick={() => placeBack('picker')}>
              <b>Targets</b>
              <button class="collapse" title="Hide (the Targets button brings it back)" aria-label="Hide the target list" onclick={() => (s.show.picker = false)}>✕</button>
            </div>
            <input class="search" type="search" placeholder="Search: M 31, NGC 7000, Heart, Sh2-129…" bind:value={query} aria-label="Search targets" />

            {#if query.trim()}
              <div class="list">
                {#each results as t (t.label)}
                  <button class="item" class:selected={s.target?.info?.label === t.label} onclick={() => select(t)}>
                    <span class="name"><b>{t.label}</b>{#if t.commonName}<span class="common muted">{t.commonName}</span>{/if}</span>
                    <span class="meta muted">{t.typeName}{#if size(t)}, {size(t)}{/if}{#if t.magnitude != null}, magnitude {t.magnitude}{/if}</span>
                  </button>
                {:else}
                  <p class="muted small">Nothing by that name.</p>
                {/each}
              </div>
            {:else}
              <div class="tonight-head">
                <b>Tonight</b>
                {#if night}
                  <span class="muted small">
                    Dark {time(night.darkStart)}–{time(night.darkEnd)}, Moon {Math.round(night.moonIllumination * 100)}%
                    {night.moonWaxing ? 'waxing' : 'waning'}
                  </span>
                {/if}
              </div>
              <div class="filters">
                <div class="chips">
                  {#each [['galaxy', 'Galaxies'], ['nebula', 'Nebulae'], ['cluster', 'Clusters'], ['planetary', 'Planetary']] as [g, text] (g)}
                    <button class="chip" class:on={s.filters.groups.includes(g)} aria-pressed={s.filters.groups.includes(g)} onclick={() => toggleGroup(g)}
                      >{text}</button
                    >
                  {/each}
                </div>
                <label title="Lowest altitude that counts as 'up'">Above <input type="number" min="0" max="80" bind:value={s.filters.minAltitude} />°</label>
                <label title="At least this many hours of darkness above that altitude">for <input type="number" min="0" max="12" step="0.5" bind:value={s.filters.minHours} /> h</label>
                <label title="Smallest size, arcminutes"
                  >Size ≥ <input
                    type="number"
                    min="0"
                    value={s.filters.minSize ?? ''}
                    onchange={(e) => (s.filters.minSize = optional(e.currentTarget.value))}
                  />′</label
                >
                <label title="Faintest magnitude (objects without one are kept, except galaxies)"
                  >Mag ≤ <input
                    type="number"
                    step="0.5"
                    value={s.filters.maxMagnitude ?? ''}
                    onchange={(e) => (s.filters.maxMagnitude = optional(e.currentTarget.value))}
                  /></label
                >
                <label>
                  Sort
                  <select bind:value={s.filters.sort}>
                    <option value="best">Best</option>
                    <option value="transit">Highest at</option>
                    <option value="altitude">Altitude</option>
                    <option value="size">Size</option>
                    <option value="brightness">Brightness</option>
                  </select>
                </label>
              </div>
              {#if tonightError}
                <p class="bad small">{tonightError}</p>
              {:else}
                <p class="muted small">{loading ? 'Rating…' : `${tonightCount} match${tonightCount === 1 ? '' : 'es'}${tonightCount > tonight.length ? `, best ${tonight.length} shown` : ''}`}</p>
              {/if}
              <div class="list">
                {#each tonight as t (t.label)}
                  <button class="item" class:selected={s.target?.info?.label === t.label} onclick={() => select(t)}>
                    <span class="name"><b>{t.label}</b>{#if t.commonName}<span class="common muted">{t.commonName}</span>{/if}</span>
                    <span class="meta muted">
                      {t.typeName}{#if size(t)}, {size(t)}{/if}{#if t.visibility}.
                        Up {t.visibility.hoursAbove.toFixed(1)} h, highest {Math.round(t.visibility.maxAltitude)}° at {time(t.visibility.bestUtc)}{/if}
                    </span>
                  </button>
                {/each}
              </div>
            {/if}
            <div class="row">
              <button class="secondary" disabled={!mountReady} onclick={frameMount} title="Frame wherever the mount points now">Frame the mount position</button>
            </div>
            <div class="grip" role="presentation" title="Drag to resize" onpointerdown={(e) => grab(e, 'picker', true)}></div>
          </section>
        {/if}
      </div>
      <div class="bottom" class:free={!!s.boxes.altitude}>
        {#if curve && centre && s.show.altitude}
          <div class="altitude floating" class:free={!!s.boxes.altitude} class:front={front === 'altitude'} style={boxStyle('altitude')} role="presentation"
            title="Drag to move, double-click to put back" onpointerdown={(e) => grab(e, 'altitude', false)} ondblclick={() => placeBack('altitude')}>
            <button class="collapse" title="Hide (the Altitude button brings it back)" aria-label="Hide the altitude chart" onclick={() => (s.show.altitude = false)}>✕</button>
            <AltitudeChart {curve} minAltitude={s.filters.minAltitude} label={s.target?.name} fill={!!s.boxes.altitude} />
            <div class="grip" role="presentation" title="Drag to resize" onpointerdown={(e) => grab(e, 'altitude', true)}></div>
          </div>
        {/if}
      </div>
      <div class="side right">
        {#if centre && s.show.controls}
          <div class="controls floating" class:free={!!s.boxes.controls} class:front={front === 'controls'} style={boxStyle('controls')} aria-label="Framing">
            <div class="floating-head" role="presentation" title="Drag to move, double-click to put back"
              onpointerdown={(e) => grab(e, 'controls', false)} ondblclick={() => placeBack('controls')}>
              <b>Framing</b>
              <button class="collapse" title="Hide (the Framing button brings it back)" aria-label="Hide the framing controls" onclick={() => (s.show.controls = false)}>✕</button>
            </div>
            <label>
              Camera
              <select
                value={camera?.camera ?? ''}
                onchange={(e) => (s.camera = e.currentTarget.value)}
                title="Field of view from the camera's sensor and its active optics preset"
              >
                {#each cameras as c (c.camera)}<option value={c.camera}>{nick(c.camera)} ({c.activePreset})</option>{/each}
              </select>
            </label>
            {#if camera}
              <span class="muted small">{fieldText(fieldW)} × {fieldText(fieldH)} at {camera.imageScale?.toFixed(2)}″/px</span>
            {:else}
              <span class="bad small">Connect the camera once and set its optics (focal length) to get its field of view.</span>
            {/if}

            <label title="Position angle of the top of the frame: degrees from north through east">
              Rotation
              <span class="inline">
                <input type="range" min="0" max="359" step="1" bind:value={s.pa} />
                <input type="number" min="0" max="359" step="0.5" bind:value={s.pa} />°
              </span>
            </label>

            <fieldset>
              <legend>Mosaic</legend>
              <span class="inline">
                <input type="number" min="1" max="8" bind:value={s.columns} aria-label="Columns" /> ×
                <input type="number" min="1" max="8" bind:value={s.rows} aria-label="Rows" /> panels,
                <input
                  type="number"
                  min="0"
                  max="60"
                  step="5"
                  value={Math.round(s.overlap * 100)}
                  onchange={(e) => (s.overlap = Number(e.currentTarget.value) / 100)}
                  aria-label="Overlap percent"
                />% overlap
              </span>
            </fieldset>

            <span class="inline">
              <button class="secondary" disabled={!s.centre} onclick={() => (s.centre = null)} title="Put the frame back on the target">Frame on target</button>
            </span>

            {#if matchNote}<p class="bad small">{matchNote}</p>{/if}
            {#if solvedInfo}
              <div class="solve">
                <label class="check"><input type="checkbox" bind:checked={s.layers.solve} /> Last plate solve (green)</label>
                <span class="small">
                  rotation {solvedInfo.pa.toFixed(1)}°{#if turnBy != null && Math.abs(turnBy) >= 0.5}, turn the camera {Math.abs(turnBy).toFixed(1)}°
                    {turnBy > 0 ? 'anticlockwise' : 'clockwise'} on the sky{:else}, matches the plan{/if}
                  {#if solvedInfo.offset != null}, {solvedInfo.offset.toFixed(1)}′ from the frame centre{/if}
                </span>
                <button class="link" onclick={() => (s.pa = Math.round(solvedInfo.pa * 10) / 10)} title="Plan with the camera as it is now"
                  >Use the camera's rotation</button
                >
              </div>
            {/if}

            <div class="actions">
              <button
                disabled={!mountReady || !!mountLock()}
                onclick={() => act(() => api.slew(centreRa, centreDec))}
                title={mountLock() ?? 'GoTo the frame centre'}>Slew</button
              >
              <button
                disabled={!mountReady || !!mountLock()}
                onclick={() => act(() => api.center(centreRa, centreDec))}
                title={mountLock() ?? 'GoTo, then plate solve and correct until the frame centre is centred'}>Centre</button
              >
              <button
                class="secondary"
                disabled={!cameraReady || rotating || !!mountLock()}
                onclick={() => act(() => api.matchRotation(centreRa, centreDec, s.pa))}
                title={mountLock() ??
                  `Take a frame and measure the camera's angle, then turn it to ${s.pa.toFixed(1)}°: with the rotator if one is connected, otherwise it tells you which way to turn the camera. Checks again after each turn`}
                >Match rotation</button
              >
              <button
                disabled={sequenceRunning}
                onclick={addToSequence}
                title={sequenceRunning ? 'Stop the sequence to change its targets' : 'Add as a sequence target (one per mosaic panel)'}
                >Add to sequence{panels.length > 1 ? ` (${panels.length})` : ''}</button
              >
            </div>
            <div class="grip sticky" role="presentation" title="Drag to resize" onpointerdown={(e) => grab(e, 'controls', true)}></div>
          </div>
        {/if}
      </div>
    </div>
  </div>
</div>

<style>
  /* The chart fills the tab; the target list, framing controls and altitude chart float over it. */
  .targets {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    gap: 8px;
    min-height: 0;
  }
  section {
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-height: 0;
  }
  .overlay {
    position: absolute;
    inset: 8px;
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    grid-template-rows: auto minmax(0, 1fr);
    grid-template-areas:
      'toolbar toolbar toolbar'
      'left . right';
    gap: 8px;
    pointer-events: none; /* the chart underneath still drags and clicks between the panels */
  }
  .side {
    display: flex;
    min-height: 0;
  }
  .side.left {
    grid-area: left;
  }
  .side.right {
    grid-area: right;
    align-items: flex-start; /* only as tall as its controls */
  }
  .side.right .controls {
    max-height: 100%;
  }
  /* A fixed-size altitude chart, centred along the bottom of the sky chart whatever panels are open. */
  .bottom {
    position: absolute;
    left: 50%;
    bottom: 0;
    transform: translateX(-50%);
    width: min(440px, 100%);
  }
  /* Over the chart there's no plain background to paint a cut corner in, so these are clipped instead (they open no
     dialogs that could be cut off). */
  .floating {
    pointer-events: auto;
    background: color-mix(in srgb, var(--panel) 92%, transparent);
    backdrop-filter: blur(4px);
    border: 1px solid var(--panel-border);
    border-radius: 2px;
    clip-path: polygon(0 0, calc(100% - var(--cut)) 0, 100% var(--cut), 100% 100%, 0 100%);
    padding: 8px 10px;
    box-shadow: 0 6px 18px rgb(0 0 0 / 0.35);
    min-height: 0;
  }
  .floating {
    position: relative;
  }
  /* Moved or resized: placed over the chart where it was put. */
  .floating.free {
    position: absolute;
    max-height: none !important;
  }
  .floating.front {
    z-index: 2;
  }
  .bottom.free {
    display: contents;
  }
  .altitude.free {
    display: flex;
    flex-direction: column;
  }
  .altitude {
    cursor: move;
    touch-action: none;
  }
  .floating-head {
    cursor: move;
    touch-action: none;
    user-select: none;
    display: flex;
    align-items: center;
    justify-content: space-between;
    font-size: 13px;
  }
  .collapse {
    background: none;
    border: none;
    color: var(--muted);
    padding: 0 4px;
    cursor: pointer;
  }
  .picker {
    width: min(320px, 42vw);
    overflow: hidden;
  }
  .altitude {
    position: relative;
    padding: 4px 6px;
  }
  .altitude .collapse {
    position: absolute;
    top: 4px;
    right: 4px;
    z-index: 1;
  }
  .grip {
    position: absolute;
    right: 0;
    bottom: 0;
    width: 18px;
    height: 18px;
    cursor: nwse-resize;
    touch-action: none;
    background: linear-gradient(135deg, transparent 50%, var(--muted) 50%, var(--muted) 58%, transparent 58%, transparent 70%,
        var(--muted) 70%, var(--muted) 78%, transparent 78%);
    opacity: 0.6;
  }
  .grip:hover {
    opacity: 1;
  }
  /* In a panel that scrolls: kept in its bottom corner as it scrolls. */
  .grip.sticky {
    position: sticky;
    bottom: -8px;
    align-self: flex-end;
    flex-shrink: 0;
    margin: auto -10px -8px 0; /* at the bottom, however tall the panel is made */
  }
  .reset-boxes {
    font-size: 12px;
  }
  .panel-toggle.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .spacer {
    flex: 1;
    pointer-events: none !important;
  }
  .search {
    width: 100%;
  }
  .list {
    display: flex;
    flex-direction: column;
    gap: 2px;
    overflow-y: auto;
    min-height: 0;
    flex: 1;
  }
  .item {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    text-align: left;
    background: none;
    border: 1px solid transparent;
    color: var(--text);
    padding: 5px 8px;
    border-radius: var(--radius);
  }
  .item:hover {
    background: var(--input);
  }
  .item.selected {
    border-color: var(--accent);
  }
  .name {
    font-size: 13px;
  }
  .common {
    margin-left: 6px;
    font-weight: 400;
  }
  .meta {
    font-size: 11px;
  }
  .tonight-head {
    display: flex;
    gap: 8px;
    align-items: baseline;
    flex-wrap: wrap;
  }
  .filters {
    display: flex;
    flex-wrap: wrap;
    gap: 6px 10px;
    font-size: 12px;
    align-items: center;
  }
  .filters input {
    width: 4.5em;
  }
  .chips {
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
    width: 100%;
  }
  .chip {
    background: none;
    border: 1px solid var(--panel-border);
    color: var(--muted);
    padding: 2px 10px;
    border-radius: 999px;
    font-size: 12px;
  }
  .chip.on {
    border-color: var(--accent);
    color: var(--accent);
  }
  .title {
    display: flex;
    gap: 10px;
    align-items: baseline;
    flex-wrap: wrap;
  }
  .canvas {
    position: relative;
    min-width: 0;
    min-height: 0;
  }
  .controls {
    width: min(260px, 42vw);
    overflow-y: auto;
  }
  .toolbar {
    grid-area: toolbar;
    display: flex;
    flex-wrap: wrap;
    gap: 5px;
    align-items: center;
    pointer-events: none;
  }
  /* Clicks pass through the gaps to the chart, but not through the controls (including other components' ones). */
  .toolbar > :global(*) {
    pointer-events: auto;
  }
  .toolbar button,
  .toolbar select {
    padding: 3px 9px;
    font-size: 12px;
    background: rgb(22 22 22 / 0.85);
  }
  /* Sized for the chosen sky, not the longest choice, so the toolbar fits on one line on a tablet. */
  .survey {
    max-width: 140px;
    text-overflow: ellipsis;
  }
  .panel-buttons {
    display: flex;
    gap: 6px;
  }
  .segmented {
    display: flex;
  }
  .segmented button {
    border-radius: 0;
    color: var(--muted);
    border: 1px solid var(--panel-border);
  }
  .segmented button:first-child {
    border-radius: var(--radius) 0 0 var(--radius);
  }
  .segmented button:last-child {
    border-radius: 0 var(--radius) var(--radius) 0;
  }
  .segmented button.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .layers {
    position: relative;
  }
  .layers-menu {
    position: absolute;
    top: calc(100% + 4px);
    left: 0;
    z-index: 20;
    display: grid;
    gap: 6px;
    padding: 8px 10px;
    min-width: 160px;
    font-size: 13px;
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
  }
  .layers-menu hr {
    width: 100%;
    border: none;
    border-top: 1px solid var(--panel-border);
    margin: 2px 0;
  }
  .layers-menu label {
    display: flex;
    align-items: center;
    gap: 6px;
    white-space: nowrap;
    cursor: pointer;
  }
  .follow {
    display: flex;
    gap: 5px;
    align-items: center;
    font-size: 12px;
    background: rgb(22 22 22 / 0.85);
    padding: 3px 8px;
    border-radius: var(--radius);
  }
  /* Phones: one panel at a time is about all that fits; they cover the chart until collapsed. */
  @media (max-width: 700px) {
    .picker,
    .controls {
      width: min(300px, calc(100vw - 48px));
    }
  }
  .controls {
    display: flex;
    flex-direction: column;
    gap: 8px;
    font-size: 13px;
  }
  .controls label {
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .controls label.check {
    flex-direction: row;
    align-items: center;
    gap: 6px;
  }
  .inline {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
  }
  .inline input[type='number'] {
    width: 4.5em;
  }
  .inline input[type='range'] {
    flex: 1;
    min-width: 120px;
  }
  fieldset {
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 6px 8px;
    margin: 0;
  }
  legend {
    font-size: 12px;
    color: var(--muted);
  }
  .solve {
    display: flex;
    flex-direction: column;
    gap: 3px;
    border-left: 3px solid var(--ok);
    padding-left: 8px;
  }
  .actions {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
  }
  .row {
    display: flex;
    gap: 6px;
  }
  .small {
    font-size: 12px;
  }
  .bad {
    color: var(--danger);
  }
</style>
