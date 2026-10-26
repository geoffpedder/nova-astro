<script lang="ts">
  /**
   * Shows an image fitted to its box, or zoomed: 1:1 (one image pixel per screen pixel) and on up to 16x in
   * doublings, with the scroll wheel (around the pointer) or zoomAt(). Zoomed, the image is dragged around with the
   * mouse or a finger, and a navigator in the top-left shows which part of the whole image is in view. The box
   * itself never changes size.
   */
  import type { Snippet } from 'svelte'

  let {
    src,
    alt,
    zoomed = $bindable(false),
    panX = $bindable(0),
    panY = $bindable(0),
    zoomCentre = $bindable(null),
    magnification = $bindable(1),
    onloaded,
    onfailed,
    dimmed = false,
    overlay,
    ontap,
  }: {
    src: string
    alt: string
    zoomed?: boolean
    /** Where the image's top-left sits when zoomed: bind two viewers to the same values to move them together. */
    panX?: number
    panY?: number
    /**
     * Where to centre when zoom is switched on, as fractions of the image (a click sets it; null is the middle).
     * Bind it too for a linked pair, so a click in either puts both on the same spot.
     */
    zoomCentre?: { u: number; v: number } | null
    /** Screen pixels per image pixel when zoomed: 1 (1:1), 2, 4 ... 16. Bind it too for a linked pair. */
    magnification?: number
    /** Natural size of each image once loaded. */
    onloaded?: (width: number, height: number) => void
    /** The image couldn't be loaded. */
    onfailed?: () => void
    dimmed?: boolean
    /** Drawn over the image, sized and moved with it (fill 100% × 100%, e.g. an SVG). */
    overlay?: Snippet
    /**
     * A single tap does this instead of zooming (the compact layout hides its controls); a double tap then zooms in on
     * that spot, and out again.
     */
    ontap?: () => void
  } = $props()

  const NavigatorMax = 160 // longest side of the navigator thumbnail, px
  const DragThreshold = 3 // px of movement before a press counts as a drag

  let viewport = $state<HTMLDivElement>()
  let vw = $state(0)
  let vh = $state(0)
  let nw = $state(0)
  let nh = $state(0)

  let dragging = $state(false)

  const fitScale = $derived(nw && nh && vw && vh ? Math.min(vw / nw, vh / nh, 1) : 1)
  const MaxMagnification = 16
  const scale = $derived(zoomed ? magnification : fitScale)
  const width = $derived(nw * scale)
  const height = $derived(nh * scale)

  // Keep the image covering the box when it's bigger, centred when it's smaller.
  const clamp = (pos: number, size: number, box: number) =>
    size <= box ? (box - size) / 2 : Math.min(0, Math.max(box - size, pos))
  const x = $derived(zoomed ? clamp(panX, width, vw) : (vw - width) / 2)
  const y = $derived(zoomed ? clamp(panY, height, vh) : (vh - height) / 2)

  /** Puts image point (ix, iy) in the middle of the box, zoomed. */
  function centreOn(ix: number, iy: number) {
    panX = vw / 2 - ix * magnification
    panY = vh / 2 - iy * magnification
  }

  /**
   * Zooms by a factor (2 in, 0.5 out) keeping the point under the pointer (or the middle) where it is: from fitted
   * to 1:1, then doubling up to 16x; zooming out of 1:1 fits again.
   */
  export function zoomAt(factor: number, clientX?: number, clientY?: number) {
    if (!viewport || !nw) return
    const rect = viewport.getBoundingClientRect()
    const ax = clientX != null ? clientX - rect.left : vw / 2
    const ay = clientY != null ? clientY - rect.top : vh / 2
    if (!zoomed) {
      if (factor <= 1) return
      zoomCentre = { u: (ax - x) / fitScale / nw, v: (ay - y) / fitScale / nh }
      magnification = 1
      zoomed = true
      return
    }
    if (factor < 1 && magnification <= 1) {
      zoomed = false
      return
    }
    const next = Math.min(MaxMagnification, Math.max(1, magnification * factor))
    if (next === magnification) return
    const ix = (ax - x) / magnification
    const iy = (ay - y) / magnification
    magnification = next
    panX = ax - ix * next
    panY = ay - iy * next
  }

  // The scroll wheel zooms around the pointer, a doubling per notch (trackpads add up their small steps).
  let wheel = 0
  function onwheel(event: WheelEvent) {
    if (!nw) return
    event.preventDefault()
    wheel += event.deltaY
    if (Math.abs(wheel) < 80) return
    zoomAt(wheel < 0 ? 2 : 0.5, event.clientX, event.clientY)
    wheel = 0
  }
  // Not passive (Svelte's onwheel is), so scrolling over the image zooms it rather than the page.
  $effect(() => {
    const element = viewport
    if (!element) return
    element.addEventListener('wheel', onwheel, { passive: false })
    return () => element.removeEventListener('wheel', onwheel)
  })

  // Entering 1:1 (from the toolbar or a click): centre on the clicked point, or the middle of the image.
  let wasZoomed = false
  $effect(() => {
    if (zoomed && !wasZoomed) {
      const target = zoomCentre ?? { u: 0.5, v: 0.5 }
      centreOn(target.u * nw, target.v * nh)
    }
    // Cleared only when zooming out, so a linked viewer still sees where the click was.
    if (!zoomed && wasZoomed) {
      zoomCentre = null
      magnification = 1
    }
    wasZoomed = zoomed
  })

  function onload(event: Event) {
    const img = event.currentTarget as HTMLImageElement
    // A new frame or preview size: keep looking at the same part of the sky.
    if (zoomed && nw && img.naturalWidth !== nw) {
      const ratio = img.naturalWidth / nw
      centreOn(((vw / 2 - x) / magnification) * ratio, ((vh / 2 - y) / magnification) * ratio)
    }
    nw = img.naturalWidth
    nh = img.naturalHeight
    onloaded?.(nw, nh)
  }

  // Capturing keeps a drag going when the pointer leaves the box; if a browser refuses, dragging still works.
  function capture(element: Element | undefined, pointerId: number, on: boolean) {
    try {
      if (on) element?.setPointerCapture(pointerId)
      else element?.releasePointerCapture(pointerId)
    } catch {
      // not capturable (e.g. a pointer the browser no longer tracks)
    }
  }

  // ---- Pinching (two fingers) ----
  // Smoothly from fitted up to 16x, keeping the image point between the fingers under them; moving both fingers pans.
  // Pinched back to the fitted size, it fits again.
  const fingers = new Map<number, { x: number; y: number }>()
  let pinch: { gap: number; scale: number; ix: number; iy: number } | null = null
  let pinched = false // the click that follows a pinch isn't a "zoom to 1:1 here"
  const fingerPair = () => {
    const [a, b] = [...fingers.values()]
    return { gap: Math.hypot(a.x - b.x, a.y - b.y), mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2 }
  }

  function startPinch() {
    if (!viewport || !nw) return
    const { gap, mx, my } = fingerPair()
    const rect = viewport.getBoundingClientRect()
    pinch = { gap, scale, ix: (mx - rect.left - x) / scale, iy: (my - rect.top - y) / scale }
    dragging = false
    pinched = true
  }

  function movePinch() {
    if (!pinch || !viewport || pinch.gap <= 0) return
    const { gap, mx, my } = fingerPair()
    const rect = viewport.getBoundingClientRect()
    const next = Math.min(MaxMagnification, pinch.scale * (gap / pinch.gap))
    if (next <= fitScale * 1.02) {
      zoomed = false
      return
    }
    if (!zoomed) {
      wasZoomed = true // placed under the fingers below, not centred on zoomCentre
      zoomed = true
    }
    magnification = next
    panX = mx - rect.left - pinch.ix * next
    panY = my - rect.top - pinch.iy * next
  }

  // ---- Dragging the image ----
  let start = { px: 0, py: 0, x: 0, y: 0, moved: false }

  function pointerDown(event: PointerEvent) {
    start.moved = false
    if (event.pointerType === 'touch') {
      fingers.set(event.pointerId, { x: event.clientX, y: event.clientY })
      if (fingers.size === 1) pinched = false
      if (fingers.size === 2) {
        capture(viewport, event.pointerId, true)
        startPinch()
        return
      }
      if (fingers.size > 2) return
    }
    if (!zoomed || event.button !== 0) return
    capture(viewport, event.pointerId, true)
    start = { px: event.clientX, py: event.clientY, x, y, moved: false }
    dragging = true
  }

  function pointerMove(event: PointerEvent) {
    if (fingers.has(event.pointerId)) {
      fingers.set(event.pointerId, { x: event.clientX, y: event.clientY })
      if (pinch && fingers.size >= 2) {
        movePinch()
        return
      }
    }
    if (!dragging) return
    const dx = event.clientX - start.px
    const dy = event.clientY - start.py
    if (Math.abs(dx) + Math.abs(dy) > DragThreshold) start.moved = true
    panX = start.x + dx
    panY = start.y + dy
  }

  function pointerUp(event: PointerEvent) {
    if (fingers.delete(event.pointerId) && fingers.size < 2) pinch = null
    if (!dragging) return
    dragging = false
    capture(viewport, event.pointerId, false)
  }

  let tapTimer: ReturnType<typeof setTimeout> | undefined
  function click(event: MouseEvent) {
    if (pinched) {
      pinched = false
      return
    }
    if (ontap) {
      if (start.moved) return // the end of a drag, not a tap
      // Wait a moment: a second tap makes it a double tap (zoom), not two taps.
      clearTimeout(tapTimer)
      tapTimer = setTimeout(ontap, 260)
      return
    }
    zoomIn(event)
  }

  // Zoom to 1:1 centred on the clicked spot.
  function zoomIn(event: MouseEvent) {
    if (zoomed || !nw || !viewport) return
    const rect = viewport.getBoundingClientRect()
    zoomCentre = {
      u: (event.clientX - rect.left - x) / fitScale / nw,
      v: (event.clientY - rect.top - y) / fitScale / nh,
    }
    zoomed = true
  }

  function doubleClick(event: MouseEvent) {
    clearTimeout(tapTimer)
    if (zoomed) zoomed = false
    else if (ontap) zoomIn(event)
  }

  // ---- Navigator ----
  const nav = $derived.by(() => {
    if (!nw || !nh) return null
    const ratio = nw / nh
    const w = ratio >= 1 ? NavigatorMax : NavigatorMax * ratio
    const h = ratio >= 1 ? NavigatorMax / ratio : NavigatorMax
    const k = w / nw
    return {
      w,
      h,
      left: (-x / scale) * k,
      top: (-y / scale) * k,
      width: (Math.min(vw, width) / scale) * k,
      height: (Math.min(vh, height) / scale) * k,
    }
  })
  const showNavigator = $derived(zoomed && nav !== null && (width > vw || height > vh))

  let navDragging = false

  function navigate(event: PointerEvent) {
    if (!nav) return
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
    centreOn(((event.clientX - rect.left) / nav.w) * nw, ((event.clientY - rect.top) / nav.h) * nh)
  }

  function navDown(event: PointerEvent) {
    event.stopPropagation() // don't start dragging the main image
    capture(event.currentTarget as HTMLElement, event.pointerId, true)
    navDragging = true
    navigate(event)
  }

  function navMove(event: PointerEvent) {
    if (navDragging) navigate(event)
  }

  function navUp(event: PointerEvent) {
    navDragging = false
    capture(event.currentTarget as HTMLElement, event.pointerId, false)
  }
</script>

<!-- svelte-ignore a11y_no_static_element_interactions, a11y_click_events_have_key_events -->
<div
  class="viewport"
  class:zoomed
  class:dragging
  bind:this={viewport}
  bind:clientWidth={vw}
  bind:clientHeight={vh}
  onpointerdown={pointerDown}
  onpointermove={pointerMove}
  onpointerup={pointerUp}
  onpointercancel={pointerUp}
  onclick={click}
  ondblclick={doubleClick}
  title={zoomed
    ? `${magnification === 1 ? '1:1' : `${Math.round(magnification * 10) / 10}×`}. Drag to move around, scroll or pinch to zoom, double-click to fit.`
    : 'Click to zoom to 1:1 here, or scroll or pinch to zoom.'}
>
  <img
    {src}
    {alt}
    class:dimmed
    class:pixels={zoomed}
    draggable="false"
    style:width="{width}px"
    style:height="{height}px"
    style:transform="translate({x}px, {y}px)"
    style:visibility={nw ? 'visible' : 'hidden'}
    {onload}
    onerror={() => onfailed?.()}
  />
  {#if overlay && nw}
    <div
      class="overlay"
      style:width="{width}px"
      style:height="{height}px"
      style:transform="translate({x}px, {y}px)"
    >
      {@render overlay()}
    </div>
  {/if}

  {#if showNavigator && nav}
    <div
      class="navigator"
      style:width="{nav.w}px"
      style:height="{nav.h}px"
      onpointerdown={navDown}
      onpointermove={navMove}
      onpointerup={navUp}
      onpointercancel={navUp}
      role="slider"
      aria-label="Position in the full image"
      aria-valuenow={Math.round(-x)}
      tabindex="-1"
    >
      <img class="frame" {src} alt="" draggable="false" />
      <div
        class="view"
        style:left="{nav.left}px"
        style:top="{nav.top}px"
        style:width="{nav.width}px"
        style:height="{nav.height}px"
      ></div>
    </div>
  {/if}
</div>

<style>
  .viewport {
    position: relative;
    width: 100%;
    height: 100%;
    overflow: hidden;
    cursor: zoom-in;
    user-select: none;
  }
  .viewport {
    touch-action: none; /* fingers pinch and drag the image, not the page */
  }
  .viewport.zoomed {
    cursor: grab;
  }
  .viewport.dragging {
    cursor: grabbing;
  }
  .viewport > img {
    position: absolute;
    left: 0;
    top: 0;
    max-width: none;
    transform-origin: 0 0;
  }
  .overlay {
    position: absolute;
    left: 0;
    top: 0;
    pointer-events: none;
  }
  img.pixels {
    image-rendering: pixelated;
  }
  img.dimmed {
    opacity: 0.6;
  }
  .navigator {
    position: absolute;
    left: 8px;
    top: 8px;
    border: 1px solid var(--panel-border);
    background: #000;
    box-shadow: 0 2px 10px rgb(0 0 0 / 0.6);
    cursor: pointer;
    overflow: hidden;
  }
  .navigator img {
    width: 100%;
    height: 100%;
    display: block;
    opacity: 0.85;
  }
  .view {
    position: absolute;
    border: 1.5px solid var(--accent);
    background: color-mix(in srgb, var(--accent) 15%, transparent);
    pointer-events: none;
  }
</style>
