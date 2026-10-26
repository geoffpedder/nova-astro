<script lang="ts">
  import { api } from '../lib/api'
  import { imageFloat } from '../lib/floats.svelte'
  import { liveStack } from '../lib/liveStack.svelte'
  import { previewSrc } from '../lib/preview.svelte'
  import { mainView } from '../lib/inbox.svelte'
  import { nova } from '../lib/state.svelte'
  import FloatWindow from './FloatWindow.svelte'

  // The latest image popped out: just the picture, over every tab, following each new frame. Or the live stack,
  // when the Image tab shows that.
  const layout = imageFloat.layout
  const visible = $derived(layout.popped)
  const image = $derived(nova.image)
  const stack = $derived(liveStack.current)
  const showingStack = $derived(liveStack.showing)

  // Shown as the Image tab shows it (its colour and stretch choices, lib/preview).
  const src = $derived.by(() => {
    if (visible && showingStack && stack) return api.liveStackUrl(stack, layout.width > 480 ? 1600 : 800)
    if (!visible || !image) return null
    return previewSrc(image, layout.width > 480 ? 1600 : 800)
  })
  const hfr = $derived(image?.stars.medianHfr)
  const caption = $derived(
    showingStack && stack
      ? `${stack.label}, ${stack.frames} stacked`
      : image
      ? `${image.durationSeconds} s at ${new Date(image.capturedUtc).toLocaleTimeString()}${hfr != null ? `, HFR ${hfr.toFixed(2)}` : ''}`
      : '',
  )
</script>

{#if visible}
  <FloatWindow
    title={showingStack ? `Stack, ${caption}` : image ? `Image, ${caption}` : 'Image'}
    layout={layout}
    save={imageFloat.save}
    expandTitle="Open the Image tab"
    onexpand={() => (mainView.view = 'image')}
  >
    {#if src}
      <img class="frame" {src} alt={showingStack ? 'The live stack' : 'The latest frame'} />
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
