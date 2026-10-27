<script lang="ts">
  import type { Snippet } from 'svelte'
  import type { FloatLayout } from '../lib/floats.svelte'

  // A small window over every tab: moved by its title bar, resized from its bottom-right corner (the content keeps its
  // shape), ⤢ to open its tab and ✕ to close. Where it sits is kept in `layout`; `save` remembers it.
  let {
    title,
    layout,
    save,
    expandTitle,
    onexpand,
    closeTitle = 'Close',
    children,
  }: {
    title: string
    layout: FloatLayout
    save: () => void
    expandTitle: string
    onexpand: () => void
    closeTitle?: string
    children: Snippet
  } = $props()

  let box = $state<HTMLElement>()
  // Where it's shown: where it was put, but kept inside the browser when the window gets smaller (and back where it
  // was put when the window grows again; layout keeps that).
  let viewWidth = $state(window.innerWidth)
  let viewHeight = $state(window.innerHeight)
  let boxWidth = $state(0)
  let boxHeight = $state(0)
  const shownRight = $derived(Math.max(0, Math.min(layout.right, viewWidth - boxWidth)))
  const shownBottom = $derived(Math.max(0, Math.min(layout.bottom, viewHeight - boxHeight)))
  let drag: { x: number; y: number; right: number; bottom: number } | null = null
  function down(e: PointerEvent) {
    if ((e.target as HTMLElement).closest('button')) return
    drag = { x: e.clientX, y: e.clientY, right: shownRight, bottom: shownBottom }
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  }
  function move(e: PointerEvent) {
    if (!drag || !box) return
    const r = box.getBoundingClientRect()
    layout.right = Math.max(0, Math.min(window.innerWidth - r.width, drag.right - (e.clientX - drag.x)))
    layout.bottom = Math.max(0, Math.min(window.innerHeight - r.height, drag.bottom - (e.clientY - drag.y)))
  }
  function up() {
    if (drag) save()
    drag = null
  }

  // Resized from its bottom-right corner, keeping the top-left where it is unless that would push it off the window.
  let resize: { x: number; y: number; width: number; ratio: number; right: number; bottom: number } | null = null
  function resizeDown(e: PointerEvent) {
    if (!box) return
    const content = box.querySelector('.content')
    const ratio = content && content.clientWidth && content.clientHeight ? content.clientHeight / content.clientWidth : 0.75
    resize = { x: e.clientX, y: e.clientY, width: boxWidth || layout.width, ratio, right: shownRight, bottom: shownBottom }
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    e.preventDefault()
  }
  function resizeMove(e: PointerEvent) {
    if (!resize) return
    const dx = e.clientX - resize.x
    const dy = (e.clientY - resize.y) / resize.ratio
    const grow = Math.abs(dx) > Math.abs(dy) ? dx : dy // whichever way the corner moved most; the content keeps its shape
    const most = Math.min(window.innerWidth - 8, (window.innerHeight - 60) / resize.ratio) // still fits the window
    const width = Math.round(Math.max(200, Math.min(most, resize.width + grow)))
    const change = width - resize.width
    layout.width = width
    // Once against the window's edge it grows the other way instead.
    layout.right = Math.max(0, resize.right - change)
    layout.bottom = Math.max(0, resize.bottom - change * resize.ratio)
  }
  function resizeUp() {
    if (resize) save()
    resize = null
  }

  function close() {
    layout.popped = false
    save()
  }
</script>

<svelte:window bind:innerWidth={viewWidth} bind:innerHeight={viewHeight} />

<!-- The shadow is on the outside, so it follows the cut corner clipped off the window inside. -->
<div class="float" bind:this={box} bind:offsetWidth={boxWidth} bind:offsetHeight={boxHeight}
  style:right="{shownRight}px" style:bottom="{shownBottom}px" style:width="{layout.width}px">
<div class="window panel">
  <div class="bar" role="presentation" onpointerdown={down} onpointermove={move} onpointerup={up} onpointercancel={up}>
    <span class="title">{title}</span>
    <span class="gap"></span>
    <button class="icon" onclick={onexpand} title={expandTitle} aria-label={expandTitle}>⤢</button>
    <button class="icon" onclick={close} title={closeTitle} aria-label={`Close the ${title.toLowerCase()} window`}>✕</button>
  </div>
  <div class="content">{@render children()}</div>
  <div class="grip" role="presentation" title="Drag to resize" onpointerdown={resizeDown} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></div>
</div>
</div>

<style>
  .float {
    position: fixed;
    z-index: 35; /* under dialogs and their backdrop (40 and up) */
    max-width: calc(100vw - 16px);
    filter: drop-shadow(0 8px 18px rgb(0 0 0 / 0.45));
    pointer-events: none; /* the clipped-off corner lets clicks through too */
  }
  /* Over pictures and the sky chart there's no plain background to paint the cut corner in (as panels do), so it's
     clipped off, see-through, with just the line along the cut. */
  .window {
    padding: 0;
    overflow: hidden;
    clip-path: polygon(0 0, calc(100% - var(--cut)) 0, 100% var(--cut), 100% 100%, 0 100%);
    pointer-events: auto;
  }
  .window::after {
    background: linear-gradient(
      to top right,
      transparent calc(50% - 1.2px),
      var(--cut-line) calc(50% - 1.2px),
      var(--cut-line) 50%,
      transparent 50%
    );
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 3px calc(var(--cut) - 1px) 3px 6px; /* the buttons clear of the cut corner */
    cursor: move;
    touch-action: none;
    user-select: none;
    border-bottom: 1px solid var(--panel-border);
  }
  .title {
    font-size: 12px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--muted);
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
  .gap {
    flex: 1;
  }
  .icon {
    all: unset;
    cursor: pointer;
    padding: 0 5px;
    color: var(--text);
    font-size: 13px;
  }
  .icon:hover {
    color: var(--accent);
  }
  .grip {
    position: absolute;
    right: 0;
    bottom: 0;
    width: 16px;
    height: 16px;
    cursor: nwse-resize;
    touch-action: none;
    background: linear-gradient(135deg, transparent 50%, var(--muted) 50%, var(--muted) 58%, transparent 58%, transparent 70%,
        var(--muted) 70%, var(--muted) 78%, transparent 78%);
    opacity: 0.7;
  }
  .grip:hover {
    opacity: 1;
  }
  /* Phones: docked full width above the activity line rather than floating. */
  @media (max-width: 600px) {
    .float {
      left: 8px !important;
      right: 8px !important;
      bottom: 40px !important;
      width: auto !important;
      max-width: none;
    }
    .bar {
      cursor: default;
    }
    .grip {
      display: none;
    }
  }
</style>
