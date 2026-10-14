<script lang="ts">
  import { mainView } from '../lib/inbox.svelte'
  import { liveStream, saveWebcamLayout, webcamLayout, webcamStreamUrl } from '../lib/webcam.svelte'
  import FloatWindow from './FloatWindow.svelte'

  // The webcam popped out: a small window over every tab (but the Webcam tab itself).
  const visible = $derived(webcamLayout.popped && mainView.view !== 'webcam')
  let src = $state<string | null>(null)
  $effect(() => {
    src = visible ? webcamStreamUrl() : null
  })
</script>

{#if visible}
  <FloatWindow
    title="Webcam"
    layout={webcamLayout}
    save={saveWebcamLayout}
    expandTitle="Open the Webcam tab"
    onexpand={() => (mainView.view = 'webcam')}
    closeTitle="Close (the webcam stops when nobody watches)"
  >
    {#if src}{#key src}<img {src} alt="The webcam's live view" use:liveStream />{/key}{/if}
  </FloatWindow>
{/if}

<style>
  img {
    display: block;
    width: 100%;
    background: #000;
    min-height: 120px;
  }
</style>
