<script lang="ts">
  // The mount in 3D, where it is now: its axes turned as the mount reports (hour angle, declination, side of the pier),
  // the polar axis tilted to the site's latitude. Drag to look round it, scroll or pinch to zoom. three.js and the
  // model are fetched when this is first shown.
  import { onMount } from 'svelte'
  import { mountAxes } from '../lib/mountAxes'
  import type { MountScene } from '../lib/mountScene'
  import { nova } from '../lib/state.svelte'

  const mount = $derived(nova.status?.mount)
  const site = $derived(nova.status?.site)
  const latitude = $derived(site?.sites.find((s) => s.name === site.active)?.latitude ?? site?.mountLocation?.latitude ?? null)
  const live = $derived(!!mount?.connected && mount.hourAngle != null && latitude != null)
  // Not connected, or no site to work the hour angle out from: shown at home, at a middling latitude.
  const target = $derived(
    live ? mountAxes(mount!.hourAngle!, mount!.decDegrees, latitude!, mount!.sideOfPier) : { a: 0, b: 0, east: false, altitude: 45 })
  const note = $derived(
    !mount?.connected ? 'Not connected: shown at home' : latitude == null ? 'No site set: shown at home' : target.altitude < 0 ? 'Below the horizon' : null)

  let view = $state<HTMLElement>()
  let canvas = $state<HTMLCanvasElement>()
  let failed = $state<string | null>(null)
  let scene: MountScene | null = null
  const shown = { a: 0, b: 0 }
  let first = true
  let frame = 0
  let last = 0

  // Drawn only while something is changing: the axes catching up with the mount, or the view being turned.
  function redraw() {
    if (!frame && scene) frame = requestAnimationFrame(draw)
  }
  function draw(now: number) {
    frame = 0
    if (!scene) return
    const dt = Math.min(0.1, last ? (now - last) / 1000 : 0.016)
    last = now
    // The mount reports once a second: ease towards it, so a slew turns smoothly rather than in steps.
    const ease = first ? 1 : 1 - Math.exp(-dt / 0.35)
    first = false
    let moving = false
    for (const k of ['a', 'b'] as const) {
      const d = target[k] - shown[k]
      if (Math.abs(d) < 0.02) shown[k] = target[k]
      else {
        shown[k] += d * ease
        moving = true
      }
    }
    scene.setLatitude(latitude ?? 45)
    scene.setAxes(shown)
    scene.setBelowHorizon(live && target.altitude < 0)
    scene.render()
    if (moving) redraw()
    else last = 0
  }
  $effect(() => {
    void target.a, target.b, latitude, live
    redraw()
  })

  onMount(() => {
    let gone = false
    let observer: ResizeObserver | undefined
    import('../lib/mountScene')
      .then(({ createMountScene }) => {
        if (gone || !canvas || !view) return
        scene = createMountScene(canvas)
        observer = new ResizeObserver(() => {
          if (!view || !scene) return
          scene.resize(view.clientWidth, view.clientHeight)
          redraw()
        })
        observer.observe(view)
        redraw()
      })
      .catch((e) => (failed = e instanceof Error ? e.message : String(e)))
    return () => {
      gone = true
      observer?.disconnect()
      cancelAnimationFrame(frame)
      scene?.dispose()
      scene = null
    }
  })

  // One finger (or the mouse) turns the view; two pinch to zoom; the wheel zooms.
  const pointers = new Map<number, { x: number; y: number }>()
  let pinch = 0
  const zoom = (factor: number) => {
    if (!scene) return
    scene.look.distance = Math.max(0.9, Math.min(6, scene.look.distance * factor))
    redraw()
  }
  function down(e: PointerEvent) {
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    pinch = 0
  }
  function move(e: PointerEvent) {
    const was = pointers.get(e.pointerId)
    if (!was || !scene) return
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
    if (pointers.size === 1) {
      scene.look.azimuth += (e.clientX - was.x) * 0.008
      scene.look.elevation = Math.max(-0.09, Math.min(1.48, scene.look.elevation + (e.clientY - was.y) * 0.006))
      redraw()
    } else if (pointers.size === 2) {
      const [p, q] = [...pointers.values()]
      const apart = Math.hypot(p.x - q.x, p.y - q.y)
      if (pinch > 0 && apart > 0) zoom(pinch / apart)
      pinch = apart
    }
  }
  function up(e: PointerEvent) {
    pointers.delete(e.pointerId)
    pinch = 0
  }
  function wheel(e: WheelEvent) {
    e.preventDefault()
    zoom(e.deltaY > 0 ? 1.1 : 0.9)
  }
</script>

<div
  class="view"
  bind:this={view}
  role="img"
  aria-label="The mount and telescope in 3D, where they are now"
  title="Drag to look round it, scroll or pinch to zoom"
  onpointerdown={down}
  onpointermove={move}
  onpointerup={up}
  onpointercancel={up}
  onwheel={wheel}
>
  <canvas bind:this={canvas}></canvas>
  {#if failed}
    <span class="note bad">The 3D view couldn't start here ({failed})</span>
  {:else if note}
    <span class="note" class:bad={note === 'Below the horizon'}>{note}</span>
  {/if}
</div>

<style>
  .view {
    position: relative;
    width: 100%;
    aspect-ratio: 4 / 3;
    background: #2a2a2a;
    border: 1px solid var(--panel-border);
    touch-action: none;
    cursor: grab;
    overflow: hidden;
  }
  .view:active {
    cursor: grabbing;
  }
  canvas {
    display: block;
    width: 100%;
    height: 100%;
  }
  .note {
    position: absolute;
    left: 8px;
    top: 6px;
    z-index: 1;
    font-size: 11px;
    color: var(--muted);
    pointer-events: none;
  }
  .note.bad {
    color: var(--danger);
  }
  /* Night: the picture in red only, like the rest of the page. */
  :global(:root[data-theme='night']) .view {
    background: #000;
  }
  :global(:root[data-theme='night']) canvas {
    filter: grayscale(1) brightness(1.7);
  }
  :global(:root[data-theme='night']) .view::after {
    content: '';
    position: absolute;
    inset: 0;
    background: #d02020;
    mix-blend-mode: multiply;
    pointer-events: none;
  }
</style>
