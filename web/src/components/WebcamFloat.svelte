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
  function close() {
    webcamLayout.popped = false
    saveWebcamLayout()
  }
</script>

{#if visible}
  <div class="float panel" bind:this={box} style:right="{webcamLayout.right}px" style:bottom="{webcamLayout.bottom}px">
    <div class="bar" role="presentation" onpointerdown={down} onpointermove={move} onpointerup={up} onpointercancel={up}>
      <span class="title">Webcam</span>
      <span class="gap"></span>
      <button class="icon" onclick={() => (mainView.view = 'webcam')} title="Open the Webcam tab" aria-label="Open the Webcam tab">⤢</button>
      <button class="icon" onclick={close} title="Close (the webcam stops when nobody watches)" aria-label="Close the webcam view">✕</button>
    </div>
    {#if src}{#key src}<img class="frame" {src} alt="The webcam's live view" use:liveStream />{/key}{/if}
  </div>
{/if}

<style>
  .float {
    position: fixed;
    z-index: 40;
    width: 280px;
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
  /* Phones: docked full width above the activity line rather than floating. */
  @media (max-width: 600px) {
    .float {
      left: 8px !important;
      right: 8px !important;
      bottom: 40px !important;
      width: auto;
    }
    .bar {
      cursor: default;
    }
  }
</style>
