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
    /* One column that scrolls as a whole: storage at its full height (not squeezed into a box of its own to scroll),
       then the files, whose list scrolls in a box most of the screen high. */
    .page {
      grid-template-rows: auto auto;
    }
    .two {
      grid-template-columns: minmax(0, 1fr);
      grid-template-rows: auto max(360px, 70dvh);
    }
    .side {
      min-height: auto;
      overflow: clip visible;
    }
  }
</style>
