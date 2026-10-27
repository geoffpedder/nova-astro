<script lang="ts">
  import { portal } from '../lib/portal'
  import FileBrowser from './FileBrowser.svelte'

  // Open… → From the mini PC: the shared file browser (FITS files only), in a dialog. A file opens in the viewer and
  // closes this.
  let { onclose }: { onclose: () => void } = $props()

  function onkeydown(e: KeyboardEvent) {
    if (e.key === 'Escape') onclose()
  }
</script>

<svelte:window {onkeydown} />
<div use:portal class="backdrop" role="presentation" onclick={onclose}></div>
<div use:portal class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="files-title">
  <div class="head">
    <h3 id="files-title">Open from the mini PC</h3>
    <button class="secondary small" onclick={onclose} aria-label="Close">✕</button>
  </div>
  <FileBrowser mode="open" onopened={onclose} />
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.5);
    z-index: 40;
  }
  .dialog {
    position: fixed;
    z-index: 41;
    left: 50%;
    top: 10%;
    transform: translateX(-50%);
    width: min(620px, calc(100vw - 32px));
    max-height: 80vh;
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    gap: 10px;
    padding: 16px;
  }
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  h3 {
    margin: 0;
  }
  .small {
    font-size: 12px;
    padding: 2px 8px;
  }
</style>
