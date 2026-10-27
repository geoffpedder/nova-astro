<script lang="ts">
  import FileBrowser from './FileBrowser.svelte'
  import MachineStats from './MachineStats.svelte'
  import StorageEditor from './StorageEditor.svelte'

  // The Mini PC tab: its health, terminal and diagnostics (the Mini PC box); where frames are saved (Storage: the mini
  // PC or a USB drive, ejecting, copying a night); and the files on both, to open, view, download or delete.
  let { active = true }: { active?: boolean } = $props()
</script>

<div class="page">
  <MachineStats {active} />
  <div class="two">
    <div class="side"><StorageEditor /></div>
    <section class="panel files" aria-label="Files">
      <h2>Files</h2>
      <FileBrowser mode="files" />
    </section>
  </div>
</div>

<style>
  .page {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    gap: 12px;
    min-height: 0;
  }
  /* Storage beside the files on a wide screen, above them on a narrow one. */
  .two {
    display: grid;
    grid-template-columns: minmax(260px, 360px) minmax(0, 1fr);
    gap: 12px;
    min-height: 0;
  }
  .side {
    min-height: 0;
    overflow: hidden auto; /* a panel's cut corner reaches 1 px past its edge: no sideways scrolling for that */
  }
  .files {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    min-height: 0;
  }
  @media (max-width: 900px) {
    .two {
      grid-template-columns: minmax(0, 1fr);
      grid-template-rows: auto minmax(360px, 1fr);
    }
  }
</style>
