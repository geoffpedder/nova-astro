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
  let drag: { x: number; y: number; right: number; bottom: number } | null = null
  function down(e: PointerEvent) {
    if ((e.target as HTMLElement).closest('button')) return
    drag = { x: e.clientX, y: e.clientY, right: layout.right, bottom: layout.bottom }
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
    resize = { x: e.clientX, y: e.clientY, width: layout.width, ratio, right: layout.right, bottom: layout.bottom }
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

<div class="float panel" bind:this={box} style:right="{layout.right}px" style:bottom="{layout.bottom}px" style:width="{layout.width}px">
  <div class="bar" role="presentation" onpointerdown={down} onpointermove={move} onpointerup={up} onpointercancel={up}>
    <span class="title">{title}</span>
    <span class="gap"></span>
    <button class="icon" onclick={onexpand} title={expandTitle} aria-label={expandTitle}>⤢</button>
    <button class="icon" onclick={close} title={closeTitle} aria-label={`Close the ${title.toLowerCase()} window`}>✕</button>
  </div>
  <div class="content">{@render children()}</div>
  <div class="grip" role="presentation" title="Drag to resize" onpointerdown={resizeDown} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></div>
</div>

<style>
  .float {
    position: fixed;
    z-index: 40;
    max-width: calc(100vw - 16px);
    padding: 0;
    overflow: hidden;
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 3px 6px;
    cursor: move;
    touch-action: none;
    user-select: none;
    border-bottom: 1px solid var(--panel-border);
  }
  .title {
    font-size: 11px;
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
