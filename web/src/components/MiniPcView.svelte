<script lang="ts">
  import FileBrowser from './FileBrowser.svelte'
  import MachineStats from './MachineStats.svelte'

  // The Mini PC tab: its health, terminal and diagnostics (the Mini PC box), and the files in its image folders, to
  // open, view or download.
  let { active = true }: { active?: boolean } = $props()
</script>

<div class="page">
  <MachineStats {active} />
  <section class="panel files" aria-label="Files">
    <h2>Files</h2>
    <FileBrowser mode="files" />
  </section>
</div>

<style>
  .page {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    gap: 12px;
    min-height: 0;
  }
  .files {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    min-height: 0;
  }
</style>
