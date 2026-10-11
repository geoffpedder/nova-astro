<script lang="ts">
  /**
   * Offline world map for picking a location, drawn as SVG in a plain longitude/latitude projection. Wheel or
   * buttons to zoom, drag to pan, click to drop the pin. Zoomed out: Natural Earth 1:50m coasts and borders.
   * Zoomed in: 1:10m coasts and borders, states and provinces, lakes and rivers, drawn only where you're looking.
   * Towns: GeoNames' 160,000 places of 1000+ people. The data ships with NOVA and loads only when the picker
   * opens, so it works with no internet (built by tools/build_site_map.py).
   */
  import { onMount, untrack } from 'svelte'

  let {
    initial,
    onpick,
    onclose,
  }: {
    initial: { lat: number; lon: number } | null
    onpick: (lat: number, lon: number) => void
    onclose: () => void
  } = $props()

  type Place = [name: string, region: string, lat: number, lon: number, rank: number]
  /** Part of a layer: its path in "degree space" (x = lon + 180, y = 90 − lat) and its bounding box there. */
  type Piece = { x0: number; y0: number; x1: number; y1: number; d: string }
  type Detail = { land: Piece[]; lakes: Piece[]; rivers: Piece[]; states: Piece[]; borders: Piece[] }

  let land = $state('')
  let borders = $state('')
  let places = $state.raw<Place[]>([])
  let lowerNames: string[] = []
  let loadError = $state<string | null>(null)
  // The detailed layers load after the overview is up, and take over when zoomed in this far (degrees across).
  const DETAIL_WIDTH = 25
  let detail = $state.raw<Detail | null>(null)

  // View in degrees: centre and width (height follows the box's shape). Opens on the starting point, then
  // follows the user, so the initial value is read once.
  const start = untrack(() => initial)
  let cx = $state(start?.lon ?? 0)
  let cy = $state(start?.lat ?? 20)
  let w = $state(start ? 8 : 360)
  let pin = $state<{ lat: number; lon: number } | null>(start)

  let box = $state<HTMLDivElement>()
  let bw = $state(1)
  let bh = $state(1)
  const h = $derived((w * bh) / Math.max(bw, 1))
  const viewBox = $derived(`${cx + 180 - w / 2} ${90 - cy - h / 2} ${w} ${h}`)

  onMount(async () => {
    try {
      const [topo, world, towns] = await Promise.all([
        import('topojson-client'),
        import('world-atlas/countries-50m.json'),
        import('../lib/map/places.json'),
      ])
      // JSON modules: the parsed file is the default export.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const atlas = (world as any).default
      land = toPath(topo.feature(atlas, atlas.objects.land))
      borders = toPath(topo.mesh(atlas, atlas.objects.countries, (a: unknown, b: unknown) => a !== b))
      const t = (towns as unknown as { default: { regions: string[]; places: [string, number, number, number, number][] } }).default
      places = t.places.map(([name, region, lat, lon, rank]) => [name, t.regions[region], lat, lon, rank])
      lowerNames = places.map((p) => p[0].toLowerCase())
    } catch (e) {
      loadError = e instanceof Error ? e.message : String(e)
      return
    }
    try {
      const [topo, world, extra] = await Promise.all([
        import('topojson-client'),
        import('world-atlas/countries-10m.json'),
        import('../lib/map/features.json'),
      ])
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const atlas = (world as any).default
      const f = (extra as unknown as { default: { states: number[][]; lakes: number[][]; rivers: number[][] } }).default
      detail = {
        land: pieces(topo.feature(atlas, atlas.objects.land)),
        borders: pieces(topo.mesh(atlas, atlas.objects.countries, (a: unknown, b: unknown) => a !== b)),
        states: decode(f.states, false),
        lakes: decode(f.lakes, true),
        rivers: decode(f.rivers, false),
      }
    } catch {
      // The overview still works; zooming in just stays at its detail.
    }
  })

  /** Lines stored as x0, y0 then differences, in 1/1000° (tools/build_site_map.py). */
  function decode(lines: number[][], close: boolean): Piece[] {
    return lines.map((l) => {
      let lon = l[0]
      let lat = l[1]
      const out: string[] = []
      let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity
      for (let i = 0; i < l.length; i += 2) {
        if (i > 0) {
          lon += l[i]
          lat += l[i + 1]
        }
        const x = lon / 1000 + 180
        const y = 90 - lat / 1000
        out.push(`${i === 0 ? 'M' : 'L'}${x.toFixed(3)} ${y.toFixed(3)}`)
        if (x < x0) x0 = x
        if (x > x1) x1 = x
        if (y < y0) y0 = y
        if (y > y1) y1 = y
      }
      if (close) out.push('Z')
      return { x0, y0, x1, y1, d: out.join('') }
    })
  }

  /** A GeoJSON shape split into its rings, each with its box, so only what's in view gets drawn. */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  function pieces(geo: any): Piece[] {
    const out: Piece[] = []
    const ring = (points: number[][], close: boolean) => {
      const d: string[] = []
      let previous: number | null = null
      let shift = 0
      let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity
      points.forEach(([lon, lat], i) => {
        if (previous !== null) {
          if (lon - previous > 180) shift -= 360
          else if (lon - previous < -180) shift += 360
        }
        const x = lon + shift + 180
        const y = 90 - lat
        d.push(`${i === 0 ? 'M' : 'L'}${x.toFixed(3)} ${y.toFixed(3)}`)
        if (x < x0) x0 = x
        if (x > x1) x1 = x
        if (y < y0) y0 = y
        if (y > y1) y1 = y
        previous = lon
      })
      if (close) d.push('Z')
      out.push({ x0, y0, x1, y1, d: d.join('') })
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const geometry = (g: any) => {
      if (!g) return
      if (g.type === 'Polygon') g.coordinates.forEach((r: number[][]) => ring(r, true))
      else if (g.type === 'MultiPolygon') g.coordinates.forEach((p: number[][][]) => p.forEach((r) => ring(r, true)))
      else if (g.type === 'LineString') ring(g.coordinates, false)
      else if (g.type === 'MultiLineString') g.coordinates.forEach((r: number[][]) => ring(r, false))
    }
    if (geo.type === 'FeatureCollection') geo.features.forEach((f: { geometry: unknown }) => geometry(f.geometry))
    else if (geo.type === 'Feature') geometry(geo.geometry)
    else geometry(geo)
    return out
  }

  // GeoJSON → SVG path in "degree space": x = lon + 180, y = 90 - lat.
  // Shapes crossing the date line are kept continuous by letting longitude run past ±180°; the map is drawn
  // three times side by side (see <use> below), so the part beyond the edge shows up on the other side.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  function toPath(geo: any): string {
    const out: string[] = []
    const ring = (points: number[][], close: boolean) => {
      let previous: number | null = null
      let shift = 0
      points.forEach(([lon, lat], i) => {
        if (previous !== null) {
          if (lon - previous > 180) shift -= 360
          else if (lon - previous < -180) shift += 360
        }
        out.push(`${i === 0 ? 'M' : 'L'}${(lon + shift + 180).toFixed(3)} ${(90 - lat).toFixed(3)}`)
        previous = lon
      })
      if (close) out.push('Z')
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const geometry = (g: any) => {
      if (!g) return
      if (g.type === 'Polygon') g.coordinates.forEach((r: number[][]) => ring(r, true))
      else if (g.type === 'MultiPolygon') g.coordinates.forEach((p: number[][][]) => p.forEach((r) => ring(r, true)))
      else if (g.type === 'LineString') ring(g.coordinates, false)
      else if (g.type === 'MultiLineString') g.coordinates.forEach((r: number[][]) => ring(r, false))
    }
    if (geo.type === 'FeatureCollection') geo.features.forEach((f: { geometry: unknown }) => geometry(f.geometry))
    else if (geo.type === 'Feature') geometry(geo.geometry)
    else geometry(geo)
    return out.join('')
  }

  // ---- View helpers ----
  const clampView = () => {
    w = Math.min(400, Math.max(0.02, w))
    cx = Math.min(180, Math.max(-180, cx))
    cy = Math.min(90, Math.max(-90, cy))
  }

  /** Screen point → {lon, lat}. */
  function toGeo(clientX: number, clientY: number) {
    const r = box!.getBoundingClientRect()
    return {
      lon: cx - w / 2 + ((clientX - r.left) / r.width) * w,
      lat: cy + h / 2 - ((clientY - r.top) / r.height) * h,
    }
  }

  function zoomAround(factor: number, clientX?: number, clientY?: number) {
    const r = box!.getBoundingClientRect()
    const anchor = toGeo(clientX ?? r.left + r.width / 2, clientY ?? r.top + r.height / 2)
    const newW = Math.min(400, Math.max(0.02, w / factor))
    // Keep the point under the cursor in place.
    cx = anchor.lon - ((anchor.lon - cx) * newW) / w
    cy = anchor.lat - ((anchor.lat - cy) * newW) / w
    w = newW
    clampView()
  }

  function onwheel(event: WheelEvent) {
    event.preventDefault()
    zoomAround(event.deltaY < 0 ? 1.4 : 1 / 1.4, event.clientX, event.clientY)
  }

  let drag: { x: number; y: number; cx: number; cy: number; moved: boolean } | null = null

  function pointerDown(event: PointerEvent) {
    if (event.button !== 0) return
    drag = { x: event.clientX, y: event.clientY, cx, cy, moved: false }
    try {
      box?.setPointerCapture(event.pointerId)
    } catch {
      // not capturable; dragging still works inside the map
    }
  }

  function pointerMove(event: PointerEvent) {
    if (!drag) return
    const dx = event.clientX - drag.x
    const dy = event.clientY - drag.y
    if (Math.abs(dx) + Math.abs(dy) > 3) drag.moved = true
    cx = drag.cx - (dx / bw) * w
    cy = drag.cy + (dy / bh) * h
    clampView()
  }

  function pointerUp(event: PointerEvent) {
    if (drag && !drag.moved) {
      const p = toGeo(event.clientX, event.clientY)
      pin = { lat: Math.round(p.lat * 10000) / 10000, lon: Math.round(p.lon * 10000) / 10000 }
    }
    drag = null
  }

  // ---- What to draw at this zoom ----
  // Zoomed in: the detailed layers, only the pieces in (or near) the view, also one world to either side when the
  // view crosses the date line.
  const zoomedIn = $derived(!!detail && w <= DETAIL_WIDTH)
  const detailShown = $derived.by(() => {
    if (!detail || w > DETAIL_WIDTH) return null
    const left = cx + 180 - w
    const right = cx + 180 + w
    const top = 90 - cy - h
    const bottom = 90 - cy + h
    const offsets = [0, ...(left < 0 ? [-360] : []), ...(right > 360 ? [360] : [])]
    const pick = (list: Piece[]) =>
      offsets.map((o) => ({
        offset: o,
        d: list
          .filter((q) => q.x1 + o >= left && q.x0 + o <= right && q.y1 >= top && q.y0 <= bottom)
          .map((q) => q.d)
          .join(''),
      }))
    return { land: pick(detail.land), lakes: pick(detail.lakes), rivers: pick(detail.rivers), states: pick(detail.states), borders: pick(detail.borders) }
  })
  const gridStep = $derived(w > 180 ? 30 : w > 60 ? 10 : w > 20 ? 5 : w > 6 ? 1 : w > 1.5 ? 0.5 : w > 0.4 ? 0.1 : 0.05)
  const grid = $derived.by(() => {
    const lines: string[] = []
    for (let lon = -180; lon <= 180; lon += gridStep) {
      if (lon < cx - w / 2 - gridStep || lon > cx + w / 2 + gridStep) continue
      lines.push(`M${lon + 180} 0V180`)
    }
    for (let lat = -90; lat <= 90; lat += gridStep) {
      if (lat < cy - h / 2 - gridStep || lat > cy + h / 2 + gridStep) continue
      lines.push(`M0 ${90 - lat}H360`)
    }
    return lines.join('')
  })

  // Bigger places (lower rank) show first; more appear as you zoom in, down to villages. The list is biggest first,
  // so a crowded view keeps the biggest.
  const maxRank = $derived(w > 150 ? 0 : w > 60 ? 2 : w > 25 ? 4 : w > 10 ? 6 : w > 4 ? 8 : w > 1.5 ? 10 : 12)
  const visiblePlaces = $derived(
    places
      .filter(
        (p) => p[4] <= maxRank && Math.abs(p[3] - cx) <= w / 2 && Math.abs(p[2] - cy) <= h / 2,
      )
      .slice(0, 250),
  )
  const unit = $derived(w / Math.max(bw, 1)) // one screen pixel in degrees

  // ---- Search ----
  let query = $state('')
  const matches = $derived.by(() => {
    const q = query.trim().toLowerCase()
    if (q.length < 2) return []
    const starts: Place[] = []
    const contains: Place[] = []
    for (let i = 0; i < places.length && starts.length < 8; i++) {
      const name = lowerNames[i]
      if (name.startsWith(q)) starts.push(places[i])
      else if (contains.length < 8 && name.includes(q)) contains.push(places[i])
    }
    return [...starts, ...contains].slice(0, 8)
  })

  function goTo(place: Place) {
    cx = place[3]
    cy = place[2]
    w = 1.5
    pin = { lat: place[2], lon: place[3] }
    query = ''
  }

  const fmt = (v: number, pos: string, neg: string) => `${Math.abs(v).toFixed(4)}° ${v >= 0 ? pos : neg}`

  function onkeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') onclose()
  }
</script>

<svelte:window {onkeydown} />

<div class="backdrop" role="presentation" onclick={onclose}></div>
<div class="dialog panel" role="dialog" aria-modal="true" aria-label="Pick a location on the map">
  <header>
    <div class="title">Pick a location</div>
    <div class="search">
      <input type="search" placeholder="Search towns…" bind:value={query} aria-label="Search towns" />
      {#if matches.length}
        <ul class="results">
          {#each matches as m (m[0] + m[1] + m[2])}
            <li><button onclick={() => goTo(m)}>{m[0]} <span class="muted">({m[1]})</span></button></li>
          {/each}
        </ul>
      {/if}
    </div>
    <button class="secondary" onclick={onclose} aria-label="Close">✕</button>
  </header>

  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="map"
    bind:this={box}
    bind:clientWidth={bw}
    bind:clientHeight={bh}
    {onwheel}
    onpointerdown={pointerDown}
    onpointermove={pointerMove}
    onpointerup={pointerUp}
    onpointercancel={() => (drag = null)}
  >
    {#if loadError}
      <p class="error">Couldn't load the map: {loadError}</p>
    {:else if !land}
      <p class="muted loading">Loading map…</p>
    {:else}
      <svg {viewBox} preserveAspectRatio="none" role="img" aria-label="World map">
        <defs>
          <path id="nova-map-land" d={land} />
          <path id="nova-map-borders" d={borders} />
        </defs>
        <rect x="-360" y="0" width="1080" height="180" class="sea" />
        <path d={grid} class="grid" />
        {#if zoomedIn && detailShown}
          {#each detailShown.land as l (l.offset)}<path d={l.d} transform="translate({l.offset} 0)" class="land" />{/each}
          {#each detailShown.lakes as l (l.offset)}<path d={l.d} transform="translate({l.offset} 0)" class="lake" />{/each}
          {#each detailShown.rivers as l (l.offset)}<path d={l.d} transform="translate({l.offset} 0)" class="river" />{/each}
          {#each detailShown.states as l (l.offset)}<path d={l.d} transform="translate({l.offset} 0)" class="states" />{/each}
          {#each detailShown.borders as l (l.offset)}<path d={l.d} transform="translate({l.offset} 0)" class="borders" />{/each}
        {:else}
          {#each [-360, 0, 360] as offset (offset)}
            <use href="#nova-map-land" x={offset} class="land" />
            <use href="#nova-map-borders" x={offset} class="borders" />
          {/each}
        {/if}
        {#each visiblePlaces as p (p[0] + p[2] + p[3])}
          <circle cx={p[3] + 180} cy={90 - p[2]} r={unit * 2} class="town" />
          <text x={p[3] + 180 + unit * 4} y={90 - p[2] + unit * 4} font-size={unit * 11} class="label">{p[0]}</text>
        {/each}
        {#if pin}
          <g class="pin">
            <circle cx={pin.lon + 180} cy={90 - pin.lat} r={unit * 6} />
            <path d={`M${pin.lon + 180 - unit * 12} ${90 - pin.lat}h${unit * 24}M${pin.lon + 180} ${90 - pin.lat - unit * 12}v${unit * 24}`} />
          </g>
        {/if}
      </svg>
    {/if}
    <div class="zoom">
      <button class="secondary" onclick={() => zoomAround(2)} aria-label="Zoom in">+</button>
      <button class="secondary" onclick={() => zoomAround(0.5)} aria-label="Zoom out">−</button>
      <button class="secondary" onclick={() => ((cx = 0), (cy = 20), (w = 360))} title="Whole world" aria-label="Whole world">⌂</button>
    </div>
  </div>

  <footer>
    <span class="mono">
      {#if pin}{fmt(pin.lat, 'N', 'S')}, {fmt(pin.lon, 'E', 'W')}{:else}<span class="muted">Click the map to drop a pin</span>{/if}
    </span>
    <span class="muted credit">Map from Natural Earth, towns from GeoNames (CC BY 4.0)</span>
    <button disabled={!pin} onclick={() => pin && onpick(pin.lat, pin.lon)}>Use this location</button>
  </footer>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.55);
    z-index: 20;
  }
  .dialog {
    position: fixed;
    z-index: 21;
    inset: 4vh max(12px, calc(50vw - 520px));
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  header,
  footer {
    display: flex;
    align-items: center;
    gap: 10px;
  }
  .title {
    font-weight: 600;
  }
  .search {
    position: relative;
    flex: 1;
  }
  .search input {
    width: 100%;
  }
  .results {
    position: absolute;
    z-index: 2;
    top: calc(100% + 2px);
    left: 0;
    right: 0;
    list-style: none;
    margin: 0;
    padding: 4px;
    background: var(--panel);
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.4);
  }
  .results button {
    all: unset;
    display: block;
    width: 100%;
    box-sizing: border-box;
    padding: 4px 6px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 13px;
  }
  .results button:hover,
  .results button:focus-visible {
    background: var(--input);
  }
  .map {
    position: relative;
    flex: 1;
    min-height: 0;
    border-radius: var(--radius);
    overflow: hidden;
    cursor: crosshair;
    touch-action: none;
    user-select: none;
    background: #0b1626;
  }
  svg {
    width: 100%;
    height: 100%;
    display: block;
  }
  .sea {
    fill: #0b1626;
  }
  .land {
    fill: #2a3a2f;
    stroke: #5c7a63;
    stroke-width: 1;
    vector-effect: non-scaling-stroke;
  }
  .borders {
    fill: none;
    stroke: #7d958a;
    stroke-width: 0.6;
    vector-effect: non-scaling-stroke;
  }
  .lake {
    fill: #0b1626;
    stroke: #3d6c9a;
    stroke-width: 0.8;
    vector-effect: non-scaling-stroke;
  }
  .river {
    fill: none;
    stroke: #3d6c9a;
    stroke-width: 1;
    vector-effect: non-scaling-stroke;
  }
  .states {
    fill: none;
    stroke: #7d958a;
    stroke-width: 0.6;
    stroke-dasharray: 4 3;
    vector-effect: non-scaling-stroke;
  }
  :global(:root[data-theme='night']) .lake,
  :global(:root[data-theme='night']) .river {
    stroke: #6a0000;
  }
  :global(:root[data-theme='night']) .lake {
    fill: #000;
  }
  :global(:root[data-theme='night']) .states {
    stroke: #5a0000;
  }
  .grid {
    fill: none;
    stroke: #1d2c42;
    stroke-width: 1;
    vector-effect: non-scaling-stroke;
  }
  .town {
    fill: #d7dde8;
  }
  .label {
    fill: #d7dde8;
    paint-order: stroke;
    stroke: #0b1626;
    stroke-width: 0.25em;
    stroke-linejoin: round;
  }
  .pin circle {
    fill: none;
    stroke: #ff5d5d;
    stroke-width: 2;
    vector-effect: non-scaling-stroke;
  }
  .pin path {
    stroke: #ff5d5d;
    stroke-width: 1.5;
    vector-effect: non-scaling-stroke;
  }
  :global(:root[data-theme='night']) .land {
    fill: #1a0000;
    stroke: #5a0000;
  }
  :global(:root[data-theme='night']) .town,
  :global(:root[data-theme='night']) .label {
    fill: #b01010;
  }
  .zoom {
    position: absolute;
    right: 8px;
    top: 8px;
    display: grid;
    gap: 4px;
  }
  .zoom button {
    width: 30px;
    padding: 2px 0;
    background: var(--panel);
  }
  .loading,
  .error {
    padding: 20px;
  }
  .error {
    color: var(--danger);
  }
  footer .mono {
    flex: 1;
  }
  .credit {
    font-size: 11px;
  }
</style>
