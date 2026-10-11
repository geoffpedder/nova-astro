<script lang="ts">
  import { ask } from '../lib/confirm.svelte'
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'

  // Where frames are saved: the mini PC, or a USB drive (mounted when plugged in, ejected from here). A night saved
  // on the mini PC can be copied to the drive afterwards.
  const storage = $derived(nova.status?.storage ?? null)
  const drive = $derived(storage?.drives.find((d) => d.selected) ?? null)
  const copy = $derived(storage?.copy ?? null)

  let open = $state(false)
  let night = $state<string | null>(null)
  const chosenNight = $derived(night ?? storage?.nights[0]?.night ?? null)

  const size = (bytes: number) =>
    bytes >= 1024 ** 4
      ? `${(bytes / 1024 ** 4).toFixed(1)} TB`
      : bytes >= 1024 ** 3
        ? `${(bytes / 1024 ** 3).toFixed(1)} GB`
        : `${Math.max(bytes > 0 ? 1 : 0, Math.round(bytes / 1024 ** 2))} MB`
  const files = (n: number) => `${n} file${n === 1 ? '' : 's'}`

  const summary = $derived.by(() => {
    if (!storage) return ''
    if (storage.onUsb && drive) return `USB: ${drive.name}${drive.freeBytes != null ? `, ${size(drive.freeBytes)} free` : ''}`
    if (storage.target === 'usb') return 'Mini PC (USB not ready)'
    return 'Mini PC'
  })

  async function eject() {
    if (!drive) return
    if (nova.status?.sequencer?.running && storage?.onUsb) {
      const ok = await ask(`Eject ${drive.name} during the sequence?`, {
        message: 'The frame being saved finishes first; the rest of the night is saved on the mini PC.',
        confirm: 'Eject',
      })
      if (!ok) return
    }
    await act(api.storageEject)
  }
</script>

{#if storage}
  <div class="storage">
    <button class="toggle" aria-expanded={open} onclick={() => (open = !open)}>
      <span class="chevron" class:closed={!open} aria-hidden="true">▾</span>
      Storage <span class="muted summary" class:warn={storage.warning}>{summary}</span>
    </button>

    {#if open}
      <div class="segmented" role="group" aria-label="Save frames to">
        <button class:on={storage.target === 'local'} onclick={() => act(() => api.storageTarget('local'))}>Mini PC</button>
        <button
          class:on={storage.target === 'usb'}
          disabled={!storage.available}
          title={storage.available ? 'Save frames to a USB drive plugged into the mini PC' : "This computer can't use USB drives (nova setup adds udisks2)"}
          onclick={() => act(() => api.storageTarget('usb', drive?.device))}>USB drive</button
        >
      </div>

      {#if storage.warning}
        <p class="warning">{storage.warning}</p>
      {/if}

      {#if storage.drives.length > 1}
        <label class="wide">
          Drive
          <select value={drive?.device} onchange={(e) => act(() => api.storageTarget(storage.target, e.currentTarget.value))}>
            {#each storage.drives as d (d.device)}<option value={d.device}>{d.name} ({size(d.sizeBytes)})</option>{/each}
          </select>
        </label>
      {/if}

      {#if drive}
        <div class="drive">
          <span>
            <b>{drive.name}</b>
            <span class="muted">
              {size(drive.sizeBytes)}{drive.fileSystem ? ` ${drive.fileSystem}` : ''}{drive.mounted
                ? drive.freeBytes != null
                  ? `, ${size(drive.freeBytes)} free`
                  : ''
                : ', not mounted'}
            </span>
          </span>
          <button class="secondary small" disabled={!drive.mounted || copy?.running} onclick={eject} title="Finish writing and unmount it, so it can be unplugged">
            Eject
          </button>
        </div>
      {:else if storage.available}
        <p class="muted hint">No USB drive plugged in.</p>
      {/if}

      <p class="muted hint path" title={storage.savingTo}>Saving to {storage.savingTo}</p>

      {#if drive?.mounted && storage.nights.length > 0}
        <div class="copy">
          <label>
            Copy a night from the mini PC
            <select value={chosenNight} onchange={(e) => (night = e.currentTarget.value)} disabled={copy?.running}>
              {#each storage.nights as n (n.night)}<option value={n.night}>{n.night} ({files(n.files)}, {size(n.bytes)})</option>{/each}
            </select>
          </label>
          {#if copy?.running}
            <button class="secondary small" onclick={() => act(api.storageCopyCancel)}>Cancel</button>
          {:else}
            <button class="secondary small" disabled={!chosenNight} onclick={() => chosenNight && act(() => api.storageCopy(chosenNight))}>Copy</button>
          {/if}
        </div>
        {#if copy}
          <p class="muted hint" class:warn={copy.error}>
            {#if copy.running}
              Copying {copy.night}: {copy.filesDone} of {files(copy.filesTotal)} ({size(copy.bytesDone)} of {size(copy.bytesTotal)})
            {:else if copy.error}
              Copying {copy.night}: {copy.error}
            {:else}
              Copied {copy.night}: {files(copy.filesTotal)}{copy.skipped > 0 ? ` (${copy.skipped} were already there)` : ''}
            {/if}
          </p>
          {#if copy.running}
            <div class="bar" aria-label="Copy progress"><div style:width="{copy.bytesTotal ? (100 * copy.bytesDone) / copy.bytesTotal : 0}%"></div></div>
          {/if}
        {/if}
      {/if}
    {/if}
  </div>
{/if}

<style>
  .storage {
    border-top: 1px solid var(--panel-border);
    padding: 8px 0;
    margin-bottom: 8px;
  }
  .toggle {
    all: unset;
    cursor: pointer;
    display: flex;
    align-items: baseline;
    gap: 6px;
    width: 100%;
    font-size: 13px;
    font-weight: 600;
  }
  .toggle:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
  .chevron {
    font-size: 10px;
    transition: transform 0.15s;
  }
  .chevron.closed {
    transform: rotate(-90deg);
  }
  .summary {
    font-weight: 400;
    font-size: 12px;
    margin-left: auto;
    text-align: right;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }
  .warn {
    color: var(--warn);
  }
  .segmented {
    display: flex;
    margin-top: 8px;
  }
  .segmented button {
    flex: 1;
    border-radius: 0;
    color: var(--muted);
    background: transparent;
    border: 1px solid var(--panel-border);
    padding: 4px 8px;
    font-size: 12px;
  }
  .segmented button:first-child {
    border-radius: var(--radius) 0 0 var(--radius);
  }
  .segmented button:last-child {
    border-radius: 0 var(--radius) var(--radius) 0;
  }
  .segmented button.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .warning {
    margin: 6px 0 0;
    font-size: 12px;
    color: var(--warn);
  }
  label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  label select {
    width: 100%;
    color: var(--text);
  }
  .wide {
    margin-top: 8px;
  }
  .drive {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-top: 8px;
    font-size: 13px;
  }
  .copy {
    display: flex;
    align-items: end;
    gap: 8px;
    margin-top: 8px;
  }
  .copy label {
    flex: 1;
    min-width: 0;
  }
  .hint {
    margin: 6px 0 0;
    font-size: 11px;
  }
  .path {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .small {
    font-size: 12px;
    padding: 2px 8px;
  }
  .bar {
    height: 4px;
    background: var(--input);
    border-radius: 2px;
    margin-top: 4px;
    overflow: hidden;
  }
  .bar div {
    height: 100%;
    background: var(--accent);
  }
</style>
