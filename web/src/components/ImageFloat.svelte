<script lang="ts">
  import { api } from '../lib/api'
  import { imageFloat } from '../lib/floats.svelte'
  import { mainView } from '../lib/inbox.svelte'
  import { nova } from '../lib/state.svelte'
  import type { PreviewMode } from '../lib/types'
  import FloatWindow from './FloatWindow.svelte'

  // The latest image popped out: just the picture, over every tab, following each new frame.
  const layout = imageFloat.layout
  const visible = $derived(layout.popped)
  const image = $derived(nova.image)

  // Shown as the Image tab shows it (its colour and stretch choices, read again whenever it reappears or a frame comes).
  function load(key: string): string | null {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  }
  const src = $derived.by(() => {
    if (!visible || !image) return null
    const mode = (load('nova.previewMode') ?? 'auto') as PreviewMode
    return api.previewUrl(image, mode, load('nova.previewStretch') !== '0', layout.width > 480 ? 1600 : 800)
  })
  const hfr = $derived(image?.stars.medianHfr)
  const caption = $derived(
    image
      ? `${image.durationSeconds} s at ${new Date(image.capturedUtc).toLocaleTimeString()}${hfr != null ? `, HFR ${hfr.toFixed(2)}` : ''}`
      : '',
  )
</script>

{#if visible}
  <FloatWindow
    title={image ? `Image, ${caption}` : 'Image'}
    layout={layout}
    save={imageFloat.save}
    expandTitle="Open the Image tab"
    onexpand={() => (mainView.view = 'image')}
  >
    {#if src}
      <img {src} alt="The latest frame" />
    {:else}
      <p class="muted small empty">No image yet</p>
    {/if}
  </FloatWindow>
{/if}

<style>
  img {
    display: block;
    width: 100%;
    background: #000;
    min-height: 120px;
  }
  .empty {
    margin: 0;
    padding: 40px 12px;
    text-align: center;
  }
</style>
