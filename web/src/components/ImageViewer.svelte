<script lang="ts">
  import { abortExposure, expose, exposure } from '../lib/exposure.svelte'
  import FileBrowserDialog from './FileBrowserDialog.svelte'
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import type { CompareMode, PreviewMode } from '../lib/types'
  import { preview, previewModes as modes, previewSizes as sizes, setPreviewMode, setPreviewSize, setPreviewStretch } from '../lib/preview.svelte'
  import { liveStack, span } from '../lib/liveStack.svelte'
  import ImageHistory from './ImageHistory.svelte'
  import FitsHeader from './FitsHeader.svelte'
  import ZoomableImage from './ZoomableImage.svelte'
  import HfrTrend from './HfrTrend.svelte'
  import type { Annotations, StarOverlay } from '../lib/types'
  import { setOcularsRotation } from '../lib/stellarium'
  import { ocularsAngle, stellariumSettings } from '../lib/stellariumSettings.svelte'
  import { framing, mainView } from '../lib/inbox.svelte'
  import { imageFloat, setStackView } from '../lib/floats.svelte'

  // The history: which image is on screen (null follows the latest), and comparing two.
  const history = $derived(nova.status?.imageHistory ?? null)
  const entries = $derived(history?.images ?? [])
  let viewing = $state<number | null>(null)
  const image = $derived(viewing != null ? (entries.find((e) => e.info.id === viewing)?.info ?? nova.image) : nova.image)
  const isLatest = $derived(!!image && image.id === nova.image?.id)
  $effect(() => {
    // An image that's dropped out of the history: back to following the latest.
    if (viewing != null && history && !entries.some((e) => e.info.id === viewing)) viewing = null
  })
  // The history column can be hidden to give the image the room; remembered per browser.
  let historyOpen = $state(load('nova.historyOpen') !== '0')
  function setHistoryOpen(open: boolean) {
    historyOpen = open
    try {
      localStorage.setItem('nova.historyOpen', open ? '1' : '0')
    } catch {
      // ignore
    }
  }
  let compare = $state<CompareMode>('single')
  let pair = $state<[number | null, number | null]>([null, null])
  function setCompare(next: CompareMode) {
    compare = next
    if (next === 'single') return
    // Start from what's on screen and the image before it.
    const ids = entries.map((e) => e.info.id)
    const a = pair[0] != null && ids.includes(pair[0]) ? pair[0] : (image?.id ?? ids[0] ?? null)
    const b = pair[1] != null && ids.includes(pair[1]) && pair[1] !== a ? pair[1] : (ids.find((i) => i !== a) ?? null)
    pair = [a, b]
  }
  const pairA = $derived(entries.find((e) => e.info.id === pair[0]) ?? null)
  const pairB = $derived(entries.find((e) => e.info.id === pair[1]) ?? null)
  const comparing = $derived(compare !== 'single' && !!pairA && !!pairB)
  // Blink: A and B in turn, about twice a second.
  let blinkB = $state(false)
  let blinkPaused = $state(false)
  $effect(() => {
    if (compare !== 'blink' || blinkPaused) return
    const timer = setInterval(() => (blinkB = !blinkB), 600)
    return () => clearInterval(timer)
  })
  // Side by side: zoomed and moved together.
  let panX = $state(0)
  let panY = $state(0)
  let zoomCentre = $state<{ u: number; v: number } | null>(null)
  const stats = $derived(image?.statistics)

  // Live stacking (lib/liveStack): the switch stacks Expose and Loop frames; Frame | Stack picks what's shown.
  const live = $derived(liveStack.status)
  const stack = $derived(liveStack.current)
  const showingStack = $derived(liveStack.showing)
  const setLiveStack = (on: boolean) => liveStack.setEnabled(on)
  const saveStack = () => liveStack.save()
  const resetStack = () => liveStack.reset()
  let actualSize = $state(false)
  let magnification = $state(1)
  // The viewer on screen (the single image, the blinking one, or the left of a pair) for the zoom buttons.
  let zoomer = $state<ZoomableImage | undefined>()

  // Colour, stretch and resolution: shared by every viewer (lib/preview), remembered per browser.
  const mode = $derived(preview.mode)
  const stretch = $derived(preview.stretch)
  const size = $derived(preview.size)
  let rendering = $state(false)
  let showHeader = $state(false)

  // ★ overlay: circles on the detected stars, remembered per browser.
  let showStars = $state(load('nova.showStars') === '1')
  let overlay = $state<StarOverlay | null>(null)
  function toggleStars() {
    showStars = !showStars
    try {
      localStorage.setItem('nova.showStars', showStars ? '1' : '0')
    } catch {
      // ignore
    }
  }
  // Keyed by number and time: numbers start again when NOVA restarts.
  let overlayKey = $state<string | null>(null)
  $effect(() => {
    const id = image?.id
    const key = image ? `${image.id} ${image.capturedUtc}` : null
    if (!showStars || id === undefined || overlayKey === key) return
    api.stars(id).then(
      (o) => ((overlay = o), (overlayKey = key)),
      () => ((overlay = null), (overlayKey = null)),
    )
  })
  const starInfo = $derived(image?.stars)
  const overlayForImage = $derived(overlay && image && overlayKey === `${image.id} ${image.capturedUtc}` ? overlay : null)
  const solver = $derived(nova.status?.plateSolve)
  // Only show a solve that belongs to the image on screen.
  const solve = $derived(
    !image ? null : solver?.last?.imageId === image.id ? solver.last : (entries.find((e) => e.info.id === image.id)?.solve ?? null),
  )
  const mountReady = $derived(nova.status?.mount?.connected ?? false)

  // Names: catalog stars placed by the plate solution, remembered per browser. Fetched again for each new solve.
  let showNames = $state(load('nova.showNames') !== '0')
  let names = $state<Annotations | null>(null)
  function toggleNames() {
    showNames = !showNames
    try {
      localStorage.setItem('nova.showNames', showNames ? '1' : '0')
    } catch {
      // ignore
    }
  }
  const solveKey = $derived(solve?.solved ? `${solve.imageId} ${solve.finishedUtc}` : null)
  let namesKey: string | null = null
  $effect(() => {
    const key = solveKey
    if (!showNames || !key || key === namesKey) return
    namesKey = key
    api.annotations(40, image?.id).then(
      (a) => (names = a),
      () => (names = null),
    )
  })
  const namesForImage = $derived(names && image && names.imageId === image.id ? names : null)

  // → Stellarium: turn its Oculars sensor frame to the camera's solved angle (and automatically, if enabled).
  let stellariumNote = $state<string | null>(null)
  async function sendAngle(rotation: number, automatic: boolean) {
    const angle = ocularsAngle(rotation)
    try {
      const how = await setOcularsRotation(angle, stellariumSettings.port)
      stellariumNote = `Stellarium frame ${how === 'confirmed' ? 'set to' : 'sent'} ${angle.toFixed(1)}°`
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e)
      stellariumNote = automatic ? `Stellarium: ${message}` : null
      if (!automatic) nova.error = message
    }
  }
  let sentFor: string | null = null
  $effect(() => {
    const last = solver?.last
    if (!stellariumSettings.autoRotation || !last?.solved || last.rotation == null) return
    const key = `${last.imageId} ${last.finishedUtc}`
    if (key === sentFor) return
    sentFor = key
    sendAngle(last.rotation, true)
  })
  let menuOpen = $state(false)
  // Open… : from this computer (a file picker) or from the mini PC (its image folders).
  let openMenu = $state(false)
  let browsing = $state(false)

  // Open… a FITS file from this computer: it replaces the viewer's image so it can be solved,
  // slewed back to, or have its stars measured.
  let fileInput = $state<HTMLInputElement>()
  let opening = $state<string | null>(null)
  async function openFile(file: File | undefined) {
    if (!file) return
    opening = file.name
    try {
      await act(() => api.openFits(file))
    } finally {
      opening = null
      if (fileInput) fileInput.value = ''
    }
  }
  // Slew / centre on a solved position (J2000, decimal hours and degrees).
  const goTo = (center: boolean) => {
    if (solve?.raHours == null || solve.decDegrees == null) return
    const ra = solve.raHours.toFixed(6)
    const dec = solve.decDegrees.toFixed(5)
    act(() => (center ? api.center(ra, dec) : api.slew(ra, dec)))
  }
  const fromCamera = $derived(!image?.openedFile)

  // A and B's numbers side by side, with what changed (smaller HFR and FWHM are better, more stars are better).
  type Row = { name: string; a: string; b: string; diff: string; better?: boolean; worse?: boolean }
  function compareRows(a: NonNullable<typeof pairA>, b: NonNullable<typeof pairA>): Row[] {
    const n = (v: number | null | undefined, digits = 2) => (v == null ? '—' : v.toFixed(digits))
    const delta = (x: number | null | undefined, y: number | null | undefined, digits = 2, lowerIsBetter = true, unit = '') => {
      if (x == null || y == null) return { diff: '—' }
      const d = y - x
      const small = Math.abs(d) < Math.pow(10, -digits) / 2
      return {
        diff: small ? `0${unit}` : `${d > 0 ? '+' : ''}${d.toFixed(digits)}${unit}`,
        better: !small && (lowerIsBetter ? d < 0 : d > 0),
        worse: !small && (lowerIsBetter ? d > 0 : d < 0),
      }
    }
    const sa = a.info.stars
    const sb = b.info.stars
    const rows: Row[] = [
      { name: 'Taken', a: time(a.info.capturedUtc), b: time(b.info.capturedUtc), diff: `${Math.round((Date.parse(b.info.capturedUtc) - Date.parse(a.info.capturedUtc)) / 1000)} s` },
      { name: 'Exposure', a: `${a.info.durationSeconds} s`, b: `${b.info.durationSeconds} s`, diff: '' },
      { name: 'Stars', a: String(sa.count), b: String(sb.count), ...delta(sa.count, sb.count, 0, false) },
      { name: 'HFR (px)', a: n(sa.medianHfr), b: n(sb.medianHfr), ...delta(sa.medianHfr, sb.medianHfr) },
      { name: 'FWHM (″)', a: n(sa.fwhmArcsec), b: n(sb.fwhmArcsec), ...delta(sa.fwhmArcsec, sb.fwhmArcsec) },
      { name: 'Median', a: fmt(a.info.statistics.median), b: fmt(b.info.statistics.median), ...delta(a.info.statistics.median, b.info.statistics.median, 0) },
      { name: 'Noise (MAD)', a: n(a.info.statistics.mad, 1), b: n(b.info.statistics.mad, 1), ...delta(a.info.statistics.mad, b.info.statistics.mad, 1) },
    ]
    const ra = a.solve?.solved ? a.solve : null
    const rb = b.solve?.solved ? b.solve : null
    if (ra && rb && ra.raHours != null && ra.decDegrees != null && rb.raHours != null && rb.decDegrees != null) {
      // Pointing: how far B's centre is from A's; rotation: the short way round.
      const rad = Math.PI / 180
      const d1 = ra.decDegrees * rad
      const d2 = rb.decDegrees * rad
      const dra = (rb.raHours - ra.raHours) * 15 * rad
      const sep = Math.acos(Math.min(1, Math.sin(d1) * Math.sin(d2) + Math.cos(d1) * Math.cos(d2) * Math.cos(dra))) / rad
      rows.push({ name: 'Centre', a: ra.raText ?? '', b: rb.raText ?? '', diff: `${(sep * 60).toFixed(1)}′ apart` })
      if (ra.rotation != null && rb.rotation != null) {
        const turn = ((((rb.rotation - ra.rotation) % 360) + 540) % 360) - 180
        rows.push({ name: 'Rotation', a: `${ra.rotation.toFixed(1)}°`, b: `${rb.rotation.toFixed(1)}°`, diff: `${turn > 0 ? '+' : ''}${turn.toFixed(1)}°` })
      }
    } else {
      rows.push({ name: 'Solved', a: ra ? '✓' : '—', b: rb ? '✓' : '—', diff: ra && rb ? '' : 'solve both to compare pointing' })
    }
    return rows
  }

  // Match framing: frame this image in the Targets tab (its centre and rotation), solving it first if need be,
  // named from its OBJECT header or the file.
  let matching = $state(false)
  async function matchFraming() {
    const shown = image
    if (!shown) return
    matching = true
    try {
      await act(async () => {
        if (!solve?.solved) {
          const result = await api.solveImage(shown.id)
          if (!result.solved) throw new Error(`Couldn't plate solve this image: ${result.error ?? 'no solution'}`)
        }
        let name =
          shown.openedFile?.replace(/\.(fits?|fts)(\.gz)?$/i, '') ??
          `Image ${new Date(shown.capturedUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`
        try {
          const object = (await api.fitsHeader(shown.id)).find((c) => c.key === 'OBJECT')?.value?.trim()
          if (object) name = object
        } catch {
          // no header: the file name will do
        }
        framing.request = { imageId: shown.id, name }
        mainView.view = 'targets'
      })
    } finally {
      matching = false
    }
  }

  function setSize(value: number) {
    setPreviewSize(value)
    menuOpen = false
  }

  // The actual size of what's displayed, read from the loaded image.
  let shownWidth = $state(0)
  let shownHeight = $state(0)

  function onImageLoad(width: number, height: number) {
    shownWidth = width
    shownHeight = height
    rendering = false
    loadFailed = false
  }

  // Say what's happening while a frame loads (slow on a weak Wi-Fi link), and if it couldn't.
  let loadFailed = $state(false)
  function onImageFailed() {
    rendering = false
    loadFailed = true
  }

  function closeMenuOnOutsideClick(event: MouseEvent) {
    if (menuOpen && !(event.target as Element).closest('.menu-wrap')) menuOpen = false
    if (openMenu && !(event.target as Element).closest('.open-wrap')) openMenu = false
  }

  function load(key: string): string | null {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  }

  const setStretch = setPreviewStretch
  const setMode = setPreviewMode

  const src = $derived(image ? api.previewUrl(image, mode, stretch, size) : null)
  $effect(() => {
    if (src) {
      rendering = true
      loadFailed = false
    }
  })

  // What's actually on screen, for the caption.
  const shown = $derived.by(() => {
    if (!image) return ''
    const linear = stretch ? '' : ', linear'
    if (mode === 'mono') return `mono${linear}`
    if (mode === 'auto')
      return image.bayerPattern ? `colour (${image.bayerPattern}${linear})` : `mono (no Bayer pattern in header${linear})`
    return `colour (${mode}, forced${linear})`
  })

  const fmt = (n: number) => Math.round(n).toLocaleString()
  const time = (iso: string) => new Date(iso).toLocaleTimeString()
</script>

<svelte:window onclick={closeMenuOnOutsideClick} />

{#if showHeader && image}
  <FitsHeader imageId={image.id} onclose={() => (showHeader = false)} />
{/if}

<div class="panel viewer">
  <h2>Image</h2>

  <div class="toolbar">
    <!-- The exposure time is the Camera panel's: change it in either place. -->
    <span class="shoot-group">
    <input
      class="mono seconds"
      type="number"
      min="0"
      max="3600"
      step="0.5"
      bind:value={exposure.duration}
      disabled={!!nova.exposure || !nova.status?.camera?.connected}
      aria-label="Exposure (seconds)"
      title="Exposure in seconds (the same as the Camera panel's)"
    /><span class="unit muted">s</span>
    {#if nova.exposure}
      <button
        class="danger small shoot"
        onclick={abortExposure}
        title="Stop the exposure (and Loop)"
      >
        Abort {nova.exposure.state === 'Downloading' ? '(downloading)' : `${nova.exposure.elapsedSeconds.toFixed(0)}/${nova.exposure.totalSeconds} s`}
      </button>
    {:else}
      <button
        class="small shoot"
        disabled={!nova.status?.camera?.connected}
        onclick={expose}
        title="Takes a frame with the Camera panel's settings (exposure, Save FITS, Loop)"
      >
        Expose
      </button>
    {/if}
    </span>
    <label class="mode">
      Colour
      <select
        value={mode}
        onchange={(e) => setMode(e.currentTarget.value as PreviewMode)}
        title="Debayer one-shot-colour frames for the preview (the saved FITS is unchanged)"
      >
        {#each modes as m (m.value)}<option value={m.value}>{m.label}</option>{/each}
      </select>
    </label>
    <label class="check" title="Off shows the data linearly: 0 is black, the sensor's full scale is white">
      <input type="checkbox" checked={stretch} onchange={(e) => setStretch(e.currentTarget.checked)} />
      Stretch
    </label>
    <label
      class="switch live"
      title="Stack Expose and Loop frames as they come: each lined up on its stars and averaged in, so the picture builds up. The frames are saved as usual. A sequence target has its own Live stack tick."
    >
      <input type="checkbox" role="switch" checked={live?.enabled ?? false} disabled={!live} onchange={(e) => setLiveStack(e.currentTarget.checked)} />
      <span class="track" aria-hidden="true"><span class="knob"></span></span>
      Live stack
    </label>
    {#if stack && stack.frames > 0}
      <span class="segmented" role="group" aria-label="Show">
        <button class="secondary small" class:on={!showingStack} aria-pressed={!showingStack} onclick={() => setStackView(false)} title="The latest frame">Frame</button>
        <button class="secondary small" class:on={showingStack} aria-pressed={showingStack} onclick={() => setStackView(true)} title="The live stack: {stack.label}">Stack</button>
      </span>
    {/if}
    <div class="spacer"></div>
    <input
      bind:this={fileInput}
      type="file"
      accept=".fits,.fit,.fts,.gz"
      hidden
      onchange={(e) => openFile(e.currentTarget.files?.[0])}
    />
    <div class="open-wrap">
      <button
        class="secondary small"
        disabled={opening !== null}
        aria-haspopup="menu"
        aria-expanded={openMenu}
        onclick={() => (openMenu = !openMenu)}
        title="Open a FITS file to plate solve it (and slew back there) or check its HFR"
      >
        {opening !== null ? 'Opening…' : 'Open ▾'}
      </button>
      {#if openMenu}
        <div class="menu panel open-menu" role="menu">
          <button role="menuitem" class="item" onclick={() => ((openMenu = false), fileInput?.click())}>From this computer…</button>
          <button role="menuitem" class="item" onclick={() => ((openMenu = false), (browsing = true))} title="A frame saved on the mini PC or its USB drive, e.g. from an earlier night">
            From the mini PC…
          </button>
        </div>
      {/if}
    </div>
    {#if browsing}<FileBrowserDialog onclose={() => (browsing = false)} />{/if}
    <button
      class="secondary small"
      disabled={!image || showingStack || !solver?.available || solver?.solving}
      title={solver?.available ? `Plate solve this image with ${solver.name}` : (solver?.problem ?? 'No plate solver')}
      onclick={() => act(() => api.solve(image?.id))}
    >
      {solver?.solving ? 'Solving…' : 'Solve'}
    </button>
    <button
      class="secondary small"
      disabled={!image || showingStack || !solver?.available || solver?.solving || matching}
      title="Frame exactly what this image shows in the Targets tab: its centre and rotation (plate solves it first if needed), to slew, centre, match the camera's rotation or add to a sequence"
      onclick={matchFraming}
    >
      {matching ? 'Matching…' : 'Match framing'}
    </button>
    <button
      class="secondary small"
      class:on={showStars}
      disabled={!image || showingStack}
      onclick={toggleStars}
      title="Circle the detected stars"
      aria-pressed={showStars}>★</button
    >
    <button
      class="secondary small"
      class:on={showNames && !!solve?.solved}
      disabled={!solve?.solved || showingStack}
      onclick={toggleNames}
      title={solve?.solved ? 'Label catalog stars by name (from the plate solve)' : 'Plate solve the image to name its stars'}
      aria-pressed={showNames}>Names</button
    >
    <button class="secondary small" disabled={!image || showingStack} onclick={() => (showHeader = true)} title="Show the FITS header">
      Header
    </button>
    <button
      class="secondary small"
      class:on={imageFloat.layout.popped}
      aria-pressed={imageFloat.layout.popped}
      onclick={() => ((imageFloat.layout.popped = !imageFloat.layout.popped), imageFloat.save())}
      title="A small floating view of the latest image (or the live stack) that stays on screen over the other tabs"
    >
      Pop out
    </button>
    <button class="secondary small" disabled={!image && !showingStack} onclick={() => (actualSize = !actualSize)} title={actualSize ? 'Fit the whole image' : 'One image pixel per screen pixel'}>
      {actualSize ? 'Fit' : '1:1'}
    </button>
    <span class="zoom-buttons">
      <button class="secondary small" disabled={(!image && !showingStack) || !actualSize} onclick={() => zoomer?.zoomAt(0.5)} title="Zoom out (or scroll down)" aria-label="Zoom out">−</button>
      <span class="zoom-level mono" title="Zoom (the scroll wheel, or a pinch, zooms around that spot)">{actualSize ? (magnification === 1 ? '1:1' : `${Math.round(magnification * 10) / 10}×`) : 'fit'}</span>
      <button class="secondary small" disabled={(!image && !showingStack) || magnification >= 16} onclick={() => zoomer?.zoomAt(2)} title="Zoom in (or scroll up)" aria-label="Zoom in">+</button>
    </span>
    {#if image && !showingStack}
      <a class="small" href={api.fitsUrl(image.id)} download>FITS ↓</a>
    {/if}
    <div class="menu-wrap">
      <button
        class="secondary small gear"
        aria-haspopup="menu"
        aria-expanded={menuOpen}
        aria-label="Viewer settings"
        title="Viewer settings"
        onclick={() => (menuOpen = !menuOpen)}>⚙</button
      >
      {#if menuOpen}
        <div class="menu panel" role="menu">
          <div class="menu-title muted">Preview resolution</div>
          {#each sizes as s (s.value)}
            <button role="menuitemradio" aria-checked={size === s.value} class="item" onclick={() => setSize(s.value)}>
              <span class="check">{size === s.value ? '●' : ''}</span>
              <span>{s.label}</span>
              {#if s.note}<span class="muted note">{s.note}</span>{/if}
            </button>
          {/each}
          <p class="muted menu-hint">Larger previews show more detail with 1:1 zoom but take longer to load.</p>
        </div>
      {/if}
    </div>
  </div>

  <div class="body" class:collapsed={!historyOpen}>
  <div class="main">
  {#if comparing && pairA && pairB}
    {@const label = (h: typeof pairA, which: string) => `${which}: ${time(h.info.capturedUtc)}, ${h.info.durationSeconds} s`}
    {#if compare === 'side'}
      <div class="pair">
        <figure>
          <ZoomableImage src={api.previewUrl(pairA.info, mode, stretch, size)} alt="Image A" bind:zoomed={actualSize} bind:magnification bind:panX bind:panY bind:zoomCentre bind:this={zoomer} />
          <figcaption class="a">{label(pairA, 'A')}</figcaption>
        </figure>
        <figure>
          <ZoomableImage
            src={api.previewUrl(pairB.info, mode, stretch, size)}
            alt="Image B"
            bind:zoomed={actualSize}
            bind:magnification
            bind:panX
            bind:panY
            bind:zoomCentre
          />
          <figcaption class="b">{label(pairB, 'B')}</figcaption>
        </figure>
      </div>
    {:else}
      <div class="frame-wrap">
        <ZoomableImage src={api.previewUrl((blinkB ? pairB : pairA).info, mode, stretch, size)} alt="Blinking A and B" bind:zoomed={actualSize} bind:magnification bind:this={zoomer} />
        <span class="blink-label {blinkB ? 'b' : 'a'}">{blinkB ? label(pairB, 'B') : label(pairA, 'A')}</span>
        <button class="secondary small blink-pause" onclick={() => (blinkPaused = !blinkPaused)} title="Pause on one image, or carry on blinking"
          >{blinkPaused ? '▶ Blink' : '⏸ Pause'}</button
        >
        <!-- Both loaded up front, so the switch is instant. -->
        <img class="preload" src={api.previewUrl(pairA.info, mode, stretch, size)} alt="" />
        <img class="preload" src={api.previewUrl(pairB.info, mode, stretch, size)} alt="" />
      </div>
    {/if}
    <table class="compare mono">
      <thead><tr><th></th><th class="a">A</th><th class="b">B</th><th>B − A</th></tr></thead>
      <tbody>
        {#each compareRows(pairA, pairB) as row (row.name)}
          <tr><th>{row.name}</th><td>{row.a}</td><td>{row.b}</td><td class:worse={row.worse} class:better={row.better}>{row.diff}</td></tr>
        {/each}
      </tbody>
    </table>
  {:else if showingStack && stack}
    <div class="frame-wrap">
      <ZoomableImage src={api.liveStackUrl(stack, size)} alt="The live stack" bind:zoomed={actualSize} bind:magnification bind:this={zoomer} />
    </div>
    <div class="stats mono">
      <span class="fact"><span class="k">Stack</span>{stack.label}</span>
      <span class="fact"><span class="k">Frames</span>{stack.frames}{#if live?.busy}{' '}<span class="muted">(stacking the next)</span>{/if}</span>
      <span class="fact"><span class="k">Total</span>{span(stack.integrationSeconds)}</span>
      <span
        class="fact"
        class:warn={stack.rejected > 0}
        title={stack.lastRejected ? `Last left out: ${stack.lastRejected}` : 'Frames with too few stars lined up, trailed or out of focus are left out'}
      >
        <span class="k">Left out</span>{stack.rejected}
      </span>
      <span class="fact"><span class="k">Size</span>{stack.width}×{stack.height}</span>
      <span class="fact"><span class="k">Updated</span>{time(stack.updatedUtc)}</span>
      <span class="fact stack-actions">
        <button class="secondary small" disabled={liveStack.saving} onclick={saveStack} title="Save the stack as it is now (FITS and PNG) in the night's Stacked folder">
          {liveStack.saving ? 'Saving…' : 'Save'}
        </button>
        <button class="secondary small" onclick={resetStack} title="Start this stack again from the next frame">Reset</button>
      </span>
      <!-- Always its own line, so the image above doesn't jump as frames come. -->
      <div class="solve-line">
        {#if stack.lastRejected && stack.rejected > 0}<span class="muted">Last left out: {stack.lastRejected}</span>{/if}
      </div>
      {#if liveStack.note ?? stack.savedPath}
        {@const note = liveStack.note ?? `Saved ${stack.savedPath}`}
        <span class="muted path" title={note}>{note}</span>
      {/if}
    </div>
  {:else}
  <div class="frame-wrap">
    {#if image && src}
      <ZoomableImage {src} alt="Latest frame" bind:zoomed={actualSize} bind:magnification bind:this={zoomer} onloaded={onImageLoad} onfailed={onImageFailed} dimmed={rendering}>
        {#snippet overlay()}
          {#if showNames && namesForImage}
            <div class="names">
              {#each namesForImage.stars as s (s.label + s.x)}
                <div
                  class="name"
                  style:left="{((s.x + 0.5) / namesForImage.width) * 100}%"
                  style:top="{((s.y + 0.5) / namesForImage.height) * 100}%"
                  title="{s.label}{s.detail ? ` (${s.detail.split(' · ').join(', ')})` : ''}, magnitude {s.magnitude.toFixed(2)}"
                >
                  <span class="ring"></span>
                  <span class="text">{s.label} <small>{s.magnitude.toFixed(1)}</small></span>
                </div>
              {/each}
            </div>
          {/if}
          {#if showStars && overlayForImage}
            <svg class="stars" viewBox="0 0 {overlayForImage.width} {overlayForImage.height}" preserveAspectRatio="none">
              {#each overlayForImage.stars as s, i (i)}
                <circle cx={s.x} cy={s.y} r={Math.max(4, s.hfr * 2.5)} />
              {/each}
            </svg>
          {/if}
        {/snippet}
      </ZoomableImage>
      {#if rendering}
        <div class="loading" role="status">Loading image…</div>
      {:else if loadFailed}
        <div class="loading failed" role="status">Couldn't load the image: is the connection to the mini PC weak? The next frame tries again.</div>
      {/if}
    {:else}
      <p class="muted empty">No image yet. Take an exposure, or Open a FITS file.</p>
    {/if}
  </div>

  {#if image && stats}
    <div class="stats mono">
      {#if image.openedFile}<span class="opened" title="Opened from your computer"><span class="emoji">📂</span> {image.openedFile}</span>{/if}
      <!-- Labelled numbers, spaced rather than strung together. -->
      <span class="fact"><span class="k">Image</span>#{image.id}</span>
      <span class="fact"><span class="k">Taken</span>{time(image.capturedUtc)}</span>
      <span class="fact"><span class="k">Exposure</span>{image.durationSeconds} s</span>
      <span class="fact"><span class="k">Shown</span>{shown}</span>
      {#if starInfo?.medianHfr != null}
        <span class="fact"><span class="k">Stars</span>{starInfo.count}</span>
        <span class="fact hfr" title="HFR: half-flux radius, the focus measure. Smaller is sharper; FWHM is about 2 × HFR.">
          <span class="k">HFR</span><b>{starInfo.medianHfr.toFixed(2)}</b> px{#if starInfo.hfrArcsec != null}{' '}({starInfo.hfrArcsec.toFixed(2)}″){/if}
          {#if fromCamera && isLatest}<HfrTrend points={nova.status?.hfrHistory ?? []} />{/if}
        </span>
        <span class="fact"><span class="k">FWHM</span>{starInfo.fwhmArcsec != null ? `${starInfo.fwhmArcsec.toFixed(2)}″` : `${starInfo.medianFwhm?.toFixed(2)} px`}</span>
      {:else}
        <span class="fact hfr">
          <span class="k">Stars</span>none measured
          {#if fromCamera && isLatest}<HfrTrend points={nova.status?.hfrHistory ?? []} />{/if}
        </span>
      {/if}
      <span class="fact">
        <span class="k">Size</span>{image.width}×{image.height}{#if shownWidth && shownWidth !== image.width}{' '}(preview {shownWidth}×{shownHeight}){/if}
      </span>
      <span class="fact"><span class="k">Median</span>{fmt(stats.median)}</span>
      <span class="fact"><span class="k">Mean</span>{fmt(stats.mean)}</span>
      <span class="fact"><span class="k">MAD</span>{fmt(stats.mad)}</span>
      <span class="fact"><span class="k">Min</span>{fmt(stats.min)}</span>
      <span class="fact"><span class="k">Max</span>{fmt(stats.max)}</span>
      <!-- Always its own line, even before the solve, so the image above doesn't jump as frames loop. -->
      <div class="solve-line">
      {#if solve?.solved}
        <span class="solved">
          ✓ Solved: {solve.raText} {solve.decText}, rotation {solve.rotation?.toFixed(1)}°{solve.flipped ? ' (mirrored)' : ''},
          {solve.imageScale?.toFixed(2)}″/px, in {solve.seconds} s.
          {#if mountReady}
            {#if fromCamera && isLatest}<button class="link" onclick={() => act(api.syncToSolve)} title="Tell the mount it's pointing here">Sync mount</button>{/if}
            <button class="link" onclick={() => goTo(false)} title="Slew the mount to this image's centre">Slew here</button>
            <button
              class="link"
              onclick={() => goTo(true)}
              title="Slew, then take frames and plate solve until the mount is centred on this image's centre">Center here</button
            >
          {/if}
          {#if solve.rotation != null}
            <button
              class="link"
              onclick={() => sendAngle(solve.rotation!, false)}
              title="Turn Stellarium's Oculars sensor frame to this angle ({ocularsAngle(solve.rotation)}° after calibration)"
              >→ Stellarium</button
            >
          {/if}
        </span>
      {:else if solve}
        <span class="unsolved" title={solve.error}>✗ {solve.error}</span>
      {:else if solver?.solving}
        <span class="muted">Plate solving…</span>
      {/if}
      </div>
      {#if stellariumNote}<span class="muted">{stellariumNote}</span>{/if}
      {#if image.savedPath}<span class="muted path" title={image.savedPath}>{image.savedPath}</span>{/if}
    </div>
  {/if}
  {/if}
  </div>
  {#if historyOpen}
  <ImageHistory
    oncollapse={() => setHistoryOpen(false)}
    images={entries}
    size={history?.size ?? 5}
    latestId={nova.image?.id ?? null}
    {viewing}
    mode={compare}
    {pair}
    onview={(id) => (viewing = id)}
    onmode={setCompare}
    onpair={(p) => (pair = p)}
  />
  {:else}
    <button class="history-tab" onclick={() => setHistoryOpen(true)} title="Show the image history" aria-label="Show the image history">
      ⟨ <span>History{#if entries.length} ({entries.length}){/if}</span>
    </button>
  {/if}
  </div>
</div>

<style>
  .viewer {
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0; /* a big image must never widen the layout */
  }
  .zoom-buttons {
    display: inline-flex;
    align-items: center;
    gap: 4px;
  }
  .zoom-level {
    min-width: 30px;
    text-align: center;
    font-size: 11px;
    color: var(--muted);
  }
  .body {
    flex: 1;
    min-height: 0;
    display: grid;
    grid-template-columns: minmax(0, 1fr) 170px;
    gap: 10px;
  }
  .main {
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
  }
  .body.collapsed {
    grid-template-columns: minmax(0, 1fr) auto;
  }
  .history-tab {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    width: 26px;
    padding: 8px 0;
    font-size: 12px;
    background: var(--input);
    color: var(--muted);
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
  }
  .history-tab span {
    writing-mode: vertical-rl;
  }
  @media (max-width: 700px) {
    .body {
      grid-template-columns: 1fr;
      grid-template-rows: minmax(0, 1fr) auto;
    }
  }
  .pair {
    flex: 1;
    min-height: 0;
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 6px;
  }
  .pair figure {
    margin: 0;
    position: relative;
    min-height: 0;
    display: flex;
    background: #000;
    border-radius: var(--radius);
    overflow: hidden;
  }
  figcaption,
  .blink-label {
    position: absolute;
    left: 6px;
    bottom: 6px;
    font-size: 12px;
    padding: 1px 6px;
    border-radius: 4px;
    background: rgb(0 0 0 / 0.6);
    pointer-events: none;
  }
  .a {
    color: var(--ok);
  }
  .b {
    color: var(--warn);
  }
  .blink-pause {
    position: absolute;
    right: 6px;
    bottom: 6px;
  }
  .preload {
    position: absolute;
    width: 1px;
    height: 1px;
    opacity: 0;
    pointer-events: none;
  }
  .compare {
    margin-top: 8px;
    font-size: 12px;
    border-collapse: collapse;
  }
  .compare th,
  .compare td {
    padding: 2px 10px 2px 0;
    text-align: left;
    font-weight: normal;
  }
  .compare tbody th {
    color: var(--muted);
  }
  .compare .better {
    color: var(--ok);
  }
  .compare .worse {
    color: var(--danger);
  }
  .loading {
    position: absolute;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    padding: 6px 12px;
    border-radius: var(--radius);
    background: rgb(0 0 0 / 0.65);
    color: var(--text);
    font-size: 13px;
    pointer-events: none;
    /* Only when it takes a moment: a quick load doesn't flash it. */
    animation: appear 0.15s ease-out 0.35s both;
  }
  .loading.failed {
    color: var(--warn);
    animation: none;
  }
  @keyframes appear {
    from {
      opacity: 0;
    }
    to {
      opacity: 1;
    }
  }
  .frame-wrap {
    position: relative;
    flex: 1;
    min-height: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    background: #000;
    border-radius: var(--radius);
    overflow: hidden;
  }
  /* The exposure time and Expose as one control. */
  .shoot-group {
    display: inline-flex;
    align-items: stretch;
  }
  .shoot-group .seconds {
    width: 4.2em;
    padding: 2px 6px;
    font-size: 12px;
    border-radius: var(--radius) 0 0 var(--radius);
    border-right: none;
    text-align: right;
  }
  .shoot-group .unit {
    display: flex;
    align-items: center;
    padding: 0 6px 0 3px;
    font-size: 12px;
    background: var(--input);
    border: 1px solid var(--panel-border);
    border-left: none;
    border-right: none;
  }
  .shoot-group .shoot {
    border-radius: 0 var(--radius) var(--radius) 0;
  }
  /* Abort with the countdown: one width throughout ("Abort 9/300 s", "Abort 120/300 s", "Abort (downloading)"), so the
     toolbar doesn't shift as it counts. */
  .shoot-group .shoot.danger {
    min-width: 11.8em;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .toolbar {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 8px;
  }
  .mode {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: var(--muted);
  }
  .mode select {
    color: var(--text);
  }
  .menu-wrap,
  .open-wrap {
    position: relative;
  }
  .open-menu {
    right: auto;
    left: 0;
    min-width: 180px;
  }
  .gear {
    padding: 2px 7px;
  }
  .menu {
    position: absolute;
    right: 0;
    top: calc(100% + 4px);
    z-index: 5;
    min-width: 230px;
    padding: 6px;
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.4);
  }
  .menu-title {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    padding: 4px 6px;
  }
  .item {
    all: unset;
    box-sizing: border-box;
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    padding: 5px 6px;
    border-radius: 4px;
    font-size: 13px;
    cursor: pointer;
  }
  .item:hover,
  .item:focus-visible {
    background: var(--input);
  }
  .item .check {
    width: 10px;
    font-size: 9px;
    color: var(--accent);
  }
  .note {
    margin-left: auto;
    font-size: 11px;
  }
  .menu-hint {
    font-size: 11px;
    margin: 6px 6px 2px;
  }
  .check {
    display: flex;
    align-items: center;
    gap: 4px;
    font-size: 12px;
    color: var(--muted);
    cursor: pointer;
  }
  .spacer {
    flex: 1;
  }
  .live {
    font-size: 12px;
    color: var(--muted);
  }
  .segmented {
    display: inline-flex;
  }
  .segmented button {
    border-radius: 0;
  }
  .segmented button:first-child {
    border-radius: var(--radius) 0 0 var(--radius);
  }
  .segmented button:last-child {
    border-radius: 0 var(--radius) var(--radius) 0;
    margin-left: -1px;
  }
  .warn {
    color: var(--warn);
  }
  .stack-actions {
    gap: 6px;
  }
  .empty {
    padding: 40px;
  }
  .stats {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 18px;
    margin-top: 8px;
    font-size: 12px;
  }
  .fact {
    display: inline-flex;
    align-items: baseline;
    gap: 6px;
  }
  .fact .k {
    font-family: system-ui, sans-serif;
    font-size: 11px;
    color: var(--muted);
  }
  .hfr {
    display: inline-flex;
    align-items: center;
    gap: 8px;
  }
  .on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .stars {
    width: 100%;
    height: 100%;
    display: block;
  }
  .stars circle {
    fill: none;
    stroke: #3ccf8e;
    stroke-width: 1.5;
    vector-effect: non-scaling-stroke;
  }
  :global(:root[data-theme='night']) .stars circle {
    stroke: #ff3030;
  }
  .solve-line {
    flex-basis: 100%;
    min-height: 1.5em;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .solved {
    color: var(--ok);
  }
  .names {
    position: absolute;
    inset: 0;
  }
  .name {
    position: absolute;
    width: 0;
    height: 0;
    --label: #ffd479;
  }
  :global(:root[data-theme='night']) .name {
    --label: #ff4040;
  }
  .name .ring {
    position: absolute;
    width: 16px;
    height: 16px;
    left: -8px;
    top: -8px;
    border: 1.5px solid var(--label);
    border-radius: 50%;
    opacity: 0.85;
  }
  .name .text {
    position: absolute;
    left: 10px;
    bottom: 6px;
    white-space: nowrap;
    font-size: 11px;
    color: var(--label);
    text-shadow:
      0 0 3px #000,
      0 0 2px #000;
    pointer-events: auto; /* hover shows the other designations */
  }
  .name small {
    opacity: 0.75;
    font-size: 9px;
  }
  .opened {
    color: var(--accent);
  }
  .unsolved {
    color: var(--danger);
  }
  .link {
    all: unset;
    cursor: pointer;
    text-decoration: underline;
    margin-left: 6px;
  }
  .path {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 100%;
  }
  .small {
    font-size: 12px;
    padding: 2px 8px;
    text-transform: none;
    letter-spacing: 0;
  }
  a {
    color: var(--accent);
  }
</style>
