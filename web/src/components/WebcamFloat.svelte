<script lang="ts">
  import { mainView } from '../lib/inbox.svelte'
  import { liveStream, saveWebcamLayout, webcamLayout, webcamStreamUrl } from '../lib/webcam.svelte'

  // The webcam popped out: a small window over every tab (but the Webcam tab itself), dragged by its title bar.
  const visible = $derived(webcamLayout.popped && mainView.view !== 'webcam')
  let src = $state<string | null>(null)
  $effect(() => {
    src = visible ? webcamStreamUrl() : null
  })

  let box = $state<HTMLElement>()
  let drag: { x: number; y: number; right: number; bottom: number } | null = null
  function down(e: PointerEvent) {
    if ((e.target as HTMLElement).closest('button')) return
    drag = { x: e.clientX, y: e.clientY, right: webcamLayout.right, bottom: webcamLayout.bottom }
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  }
  function move(e: PointerEvent) {
    if (!drag || !box) return
    const r = box.getBoundingClientRect()
    webcamLayout.right = Math.max(0, Math.min(window.innerWidth - r.width, drag.right - (e.clientX - drag.x)))
    webcamLayout.bottom = Math.max(0, Math.min(window.innerHeight - r.height, drag.bottom - (e.clientY - drag.y)))
  }
  function up() {
    if (drag) saveWebcamLayout()
    drag = null
  }
  // Resized from its bottom-right corner, keeping the top-left where it is unless that would push it off the window.
  let resize: { x: number; y: number; width: number; ratio: number; right: number; bottom: number } | null = null
  function resizeDown(e: PointerEvent) {
    if (!box) return
    const img = box.querySelector('img')
    const ratio = img && img.clientWidth ? img.clientHeight / img.clientWidth : 0.75
    resize = { x: e.clientX, y: e.clientY, width: webcamLayout.width, ratio, right: webcamLayout.right, bottom: webcamLayout.bottom }
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    e.preventDefault()
  }
  function resizeMove(e: PointerEvent) {
    if (!resize) return
    const dx = e.clientX - resize.x
    const dy = (e.clientY - resize.y) / resize.ratio
    const grow = Math.abs(dx) > Math.abs(dy) ? dx : dy // whichever way the corner moved most; the picture keeps its shape
    const most = Math.min(window.innerWidth - 8, (window.innerHeight - 60) / resize.ratio) // still fits the window
    const width = Math.round(Math.max(200, Math.min(most, resize.width + grow)))
    const change = width - resize.width
    webcamLayout.width = width
    // Once against the window's edge it grows the other way instead.
    webcamLayout.right = Math.max(0, resize.right - change)
    webcamLayout.bottom = Math.max(0, resize.bottom - change * resize.ratio)
  }
  function resizeUp() {
    if (resize) saveWebcamLayout()
    resize = null
  }

  function close() {
    webcamLayout.popped = false
    saveWebcamLayout()
  }
</script>

{#if visible}
  <div class="float panel" bind:this={box} style:right="{webcamLayout.right}px" style:bottom="{webcamLayout.bottom}px" style:width="{webcamLayout.width}px">
    <div class="bar" role="presentation" onpointerdown={down} onpointermove={move} onpointerup={up} onpointercancel={up}>
      <span class="title">Webcam</span>
      <span class="gap"></span>
      <button class="icon" onclick={() => (mainView.view = 'webcam')} title="Open the Webcam tab" aria-label="Open the Webcam tab">⤢</button>
      <button class="icon" onclick={close} title="Close (the webcam stops when nobody watches)" aria-label="Close the webcam view">✕</button>
    </div>
    {#if src}{#key src}<img class="frame" {src} alt="The webcam's live view" use:liveStream />{/key}{/if}
    <div class="grip" role="presentation" title="Drag to resize" onpointerdown={resizeDown} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></div>
  </div>
{/if}

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
  img {
    display: block;
    width: 100%;
    background: #000;
    min-height: 120px;
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
    .grip {
      display: none;
    }
    .bar {
      cursor: default;
    }
  }
</style>
