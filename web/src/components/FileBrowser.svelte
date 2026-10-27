<script lang="ts">
  import { onMount, untrack } from 'svelte'
  import { api } from '../lib/api'
  import { auth } from '../lib/auth.svelte'
  import { mainView } from '../lib/inbox.svelte'
  import { portal } from '../lib/portal'
  import { nova } from '../lib/state.svelte'
  import type { FileEntry, FolderListing } from '../lib/types'
  import Icon from './Icon.svelte'

  // The image folders on the mini PC (and a mounted USB drive). Two ways to use it, one browser:
  //   open:  Open… → From the mini PC, FITS files only: a file opens in the Image tab (then onopened).
  //   files: the Mini PC tab, every file: open a FITS file, view a picture, download anything, delete (with the
  //          password).
  let { mode, onopened }: { mode: 'open' | 'files'; onopened?: () => void } = $props()

  const all = $derived(mode === 'files')
  let listing = $state<FolderListing | null>(null)
  let error = $state<string | null>(null)
  let note = $state<string | null>(null)
  let opening = $state<string | null>(null)

  async function browse(root: string, path: string) {
    error = null
    try {
      listing = await api.browseFiles(root, path, all)
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
  }
  onMount(() => browse('local', ''))

  // A USB drive plugged in, pulled out or ejected: look again, so its tab comes and goes. Browsing a drive that's gone
  // (or was ejected) goes back to the mini PC's files.
  const drives = $derived((nova.status?.storage?.drives ?? []).map((d) => `${d.device}:${d.mounted}`).join(','))
  let seenDrives: string | null = null
  $effect(() => {
    const now = drives
    if (seenDrives === null || now === seenDrives) {
      seenDrives = now
      return
    }
    seenDrives = now
    untrack(async () => {
      if (!listing) return
      const was = listing
      await browse(was.root, was.path)
      if (error && was.root === 'usb') {
        const why = error
        await browse('local', '')
        error = null
        note = `Back to the mini PC's files: ${why}`
      }
    })
  })

  const crumbs = $derived(listing?.path ? listing.path.split('/') : [])
  const join = (...parts: string[]) => parts.filter(Boolean).join('/')
  const at = (e: FileEntry) => join(listing!.path, e.name)

  async function open(e: FileEntry) {
    if (!listing) return
    opening = e.name
    error = null
    try {
      await api.openMiniPcFile(listing.root, at(e))
      if (mode === 'files') mainView.view = 'image'
      onopened?.()
    } catch (err) {
      error = err instanceof Error ? err.message : String(err)
    } finally {
      opening = null
    }
  }

  // Thumbnails: made on the mini PC once per file, fetched as rows scroll into view. Off for a slow link; remembered.
  function loadThumbs(): boolean {
    try {
      return localStorage.getItem('nova.fileThumbnails') !== '0'
    } catch {
      return true
    }
  }
  let thumbs = $state(loadThumbs())
  function setThumbs(on: boolean) {
    thumbs = on
    try {
      localStorage.setItem('nova.fileThumbnails', on ? '1' : '0')
    } catch {
      // just for this visit
    }
  }
  // A picture small enough is its own thumbnail; a big one (a full-size stack) isn't worth fetching for one.
  const thumbOf = (e: FileEntry): string | null =>
    !thumbs || !listing
      ? null
      : e.kind === 'fits'
        ? api.fileThumbnailUrl(listing.root, at(e), e.modifiedUtc)
        : e.kind === 'image' && (e.bytes ?? Infinity) < 3 * 1024 ** 2
          ? api.fileUrl(listing.root, at(e), true)
          : null
  let broken = $state<Set<string>>(new Set())

  // Deleting: one file, or a folder and everything in it, with NOVA's password each time.
  let deleting = $state<FileEntry | null>(null)
  let password = $state('')
  let deleteError = $state<string | null>(null)
  let busy = $state(false)
  function askDelete(e: FileEntry) {
    deleting = e
    password = ''
    deleteError = null
    note = null
  }
  async function confirmDelete() {
    if (!listing || !deleting) return
    busy = true
    deleteError = null
    try {
      const done = await api.deleteFile(listing.root, at(deleting), password)
      deleting = null
      note = `Deleted ${done.path} (${done.files} file${done.files === 1 ? '' : 's'}, ${size(done.bytes)})`
      await browse(listing.root, listing.path)
    } catch (err) {
      deleteError = err instanceof Error ? err.message : String(err)
    } finally {
      busy = false
      password = ''
    }
  }
  const focus = (node: HTMLInputElement) => node.focus()

  const size = (bytes: number) =>
    bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(2)} GB` : bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`
  const when = (utc: string) => new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  // What's in this folder, for the line under the list.
  const summary = $derived.by(() => {
    if (!listing) return ''
    const files = listing.entries.filter((e) => !e.folder)
    const folders = listing.entries.length - files.length
    const bytes = files.reduce((n, e) => n + (e.bytes ?? 0), 0)
    const parts = [folders ? `${folders} folder${folders === 1 ? '' : 's'}` : null, files.length ? `${files.length} file${files.length === 1 ? '' : 's'}, ${size(bytes)}` : null]
    return parts.filter(Boolean).join(' and ')
  })
</script>

{#snippet thumb(e: FileEntry)}
  {@const src = broken.has(at(e)) ? null : thumbOf(e)}
  {#if src}
    <img class="thumb" {src} alt="" loading="lazy" decoding="async" onerror={() => (broken = new Set(broken).add(at(e)))} />
  {:else}
    <span class="glyph" class:big={thumbs}><Icon name={e.kind === 'image' ? 'image' : 'file'} size={16} /></span>
  {/if}
{/snippet}

<div class="browser" class:files={mode === 'files'}>
  <div class="bar">
    {#if listing && listing.roots.length > 1}
      <div class="segmented" role="group" aria-label="Where">
        {#each listing.roots as r (r.id)}
          <button class:on={listing.root === r.id} onclick={() => browse(r.id, '')}>{r.label}</button>
        {/each}
      </div>
    {/if}
    <span class="spacer"></span>
    <label class="check" title="Small previews of the frames (made on the mini PC once, then kept). Off on a slow link.">
      <input type="checkbox" checked={thumbs} onchange={(e) => setThumbs(e.currentTarget.checked)} /> Thumbnails
    </label>
  </div>

  {#if listing}
    <nav class="crumbs" aria-label="Folder">
      <button class="link" onclick={() => browse(listing!.root, '')}>{listing.roots.find((r) => r.id === listing!.root)?.label ?? 'Top'}</button>
      {#each crumbs as c, i (i)}
        <span class="muted">/</span>
        <button class="link" onclick={() => browse(listing!.root, crumbs.slice(0, i + 1).join('/'))}>{c}</button>
      {/each}
    </nav>

    <div class="list">
      {#if listing.path}
        <button class="entry" onclick={() => browse(listing!.root, crumbs.slice(0, -1).join('/'))}>
          <span class="glyph muted" aria-hidden="true">↰</span><span class="name muted">Up</span>
        </button>
      {/if}
      {#each listing.entries as e (e.name)}
        {#if e.folder}
          <div class="entry row folder-row">
            <button class="go" onclick={() => browse(listing!.root, at(e))}>
              <span class="glyph folder"><Icon name="folder" size={16} /></span><span class="name">{e.name}</span>
              {#if e.modifiedUtc && mode === 'files'}<span class="muted meta mono">{when(e.modifiedUtc)}</span>{/if}
            </button>
            {#if mode === 'files'}
              <span class="actions">
                <button class="secondary small icon-only danger-hover" onclick={() => askDelete(e)} title="Delete this folder and everything in it" aria-label="Delete {e.name}">
                  <Icon name="trash" size={15} />
                </button>
              </span>
            {/if}
          </div>
        {:else if mode === 'open'}
          <button class="entry file" disabled={opening !== null} onclick={() => open(e)} title="Open in the Image tab">
            {@render thumb(e)}
            <span class="name">{e.name}</span>
            <span class="muted meta mono">{opening === e.name ? 'Opening…' : `${e.bytes != null ? size(e.bytes) : ''} ${e.modifiedUtc ? when(e.modifiedUtc) : ''}`}</span>
          </button>
        {:else}
          <div class="entry row">
            {@render thumb(e)}
            <span class="name" title={e.name}>{e.name}</span>
            <span class="muted meta mono">{e.bytes != null ? size(e.bytes) : ''} {e.modifiedUtc ? when(e.modifiedUtc) : ''}</span>
            <span class="actions">
              {#if e.kind === 'fits'}
                <button class="secondary small" disabled={opening !== null} onclick={() => open(e)} title="Open in the Image tab: solve it, measure its stars">
                  {opening === e.name ? 'Opening…' : 'Open'}
                </button>
              {:else if e.kind === 'image'}
                <a class="button small" href={api.fileUrl(listing.root, at(e), true)} target="_blank" rel="noopener" title="Show it in a new browser tab">View</a>
              {/if}
              <a class="button small icon-only" href={api.fileUrl(listing.root, at(e))} download={e.name} title="Download to this device" aria-label="Download {e.name}">
                <Icon name="download" size={15} />
              </a>
              <button class="secondary small icon-only danger-hover" onclick={() => askDelete(e)} title="Delete this file" aria-label="Delete {e.name}">
                <Icon name="trash" size={15} />
              </button>
            </span>
          </div>
        {/if}
      {:else}
        <p class="muted empty">{mode === 'open' ? 'No folders or FITS files here.' : 'Nothing here yet.'}</p>
      {/each}
    </div>
    {#if mode === 'files' && (note || summary)}<p class="muted summary">{note ?? summary}</p>{/if}
  {/if}

  {#if error}<p class="error">{error}</p>{:else if !listing}<p class="muted">Loading…</p>{/if}
</div>

{#if deleting}
  <div use:portal class="backdrop" role="presentation" onclick={() => !busy && (deleting = null)}></div>
  <div use:portal class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="delete-title">
    <h3 id="delete-title">Delete {deleting.name}?</h3>
    <p>
      {deleting.folder ? 'This folder and everything in it are' : 'This file is'} removed from
      {listing?.root === 'usb' ? 'the USB drive' : 'the mini PC'} for good: there's no bin to get {deleting.folder ? 'them' : 'it'} back from.
    </p>
    {#if auth.passwordSet}
      <form onsubmit={(ev) => (ev.preventDefault(), confirmDelete())}>
        <label class="pw">
          NOVA's password
          <input type="password" autocomplete="current-password" bind:value={password} use:focus disabled={busy} />
        </label>
        {#if deleteError}<p class="error">{deleteError}</p>{/if}
        <div class="buttons">
          <button type="button" class="secondary" disabled={busy} onclick={() => (deleting = null)}>Cancel</button>
          <button type="submit" class="danger" disabled={busy || !password}>{busy ? 'Deleting…' : 'Delete'}</button>
        </div>
      </form>
    {:else}
      <p class="warn">
        Deleting needs NOVA's password, so nobody else on the network can. Set one first: the power menu → Set a password…
      </p>
      <div class="buttons"><button class="secondary" onclick={() => (deleting = null)}>Close</button></div>
    {/if}
  </div>
{/if}

<style>
  .browser {
    display: grid;
    grid-template-rows: auto auto minmax(0, 1fr) auto auto;
    gap: 10px;
    min-height: 0;
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 10px;
  }
  .spacer {
    flex: 1;
  }
  .check {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: var(--muted);
    cursor: pointer;
  }
  .segmented {
    display: flex;
  }
  .segmented button {
    flex: none;
    white-space: nowrap;
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
  .crumbs {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    align-items: baseline;
    font-size: 13px;
  }
  .link {
    all: unset;
    cursor: pointer;
    color: var(--accent);
  }
  .link:hover {
    text-decoration: underline;
  }
  .list {
    overflow: auto;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 4px;
    min-height: 120px;
  }
  .entry {
    all: unset;
    box-sizing: border-box;
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    padding: 5px 6px;
    border-radius: 4px;
    font-size: 13px;
    cursor: pointer;
  }
  .entry.row {
    cursor: default;
  }
  .entry:hover,
  .entry:focus-visible {
    background: var(--input);
  }
  .entry[disabled] {
    cursor: wait;
  }
  .go {
    all: unset;
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
  }
  .glyph {
    width: 16px;
    display: grid;
    place-items: center;
    color: var(--muted);
    flex: none;
  }
  /* With thumbnails on, a file without one keeps the same space, so the names line up. */
  .glyph.big {
    width: 66px;
    height: 46px;
  }
  .glyph.folder {
    color: var(--accent);
  }
  .thumb {
    width: 66px;
    height: 46px;
    object-fit: cover;
    border-radius: 3px;
    background: #000;
    flex: none;
    display: block;
  }
  :global(:root[data-theme='night']) .thumb {
    filter: grayscale(1) sepia(1) saturate(6) hue-rotate(-50deg) brightness(0.55);
  }
  .name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .meta {
    font-size: 11px;
    white-space: nowrap;
  }
  .actions {
    display: flex;
    gap: 4px;
    flex: none;
  }
  .small {
    font-size: 12px;
    padding: 2px 8px;
  }
  .icon-only {
    padding: 2px 6px;
    display: inline-flex;
    align-items: center;
  }
  .danger-hover:hover {
    color: var(--danger);
    border-color: var(--danger);
  }
  /* Links that look like the page's buttons. */
  a.button {
    display: inline-flex;
    align-items: center;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    background: var(--button);
    color: var(--text);
    text-decoration: none;
  }
  a.button:hover {
    border-color: var(--accent);
  }
  .summary {
    margin: 0;
    font-size: 12px;
  }
  .empty {
    margin: 8px;
    font-size: 13px;
  }
  .error {
    margin: 0;
    color: var(--danger);
    font-size: 13px;
  }
  .warn {
    color: var(--warn);
    margin: 0;
  }
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
    top: 25%;
    transform: translateX(-50%);
    width: min(440px, calc(100vw - 32px));
    display: grid;
    gap: 10px;
    padding: 16px;
  }
  .dialog h3 {
    margin: 0;
    overflow-wrap: anywhere;
  }
  .dialog p {
    margin: 0;
    font-size: 13px;
  }
  .dialog form {
    display: grid;
    gap: 10px;
  }
  .pw {
    display: grid;
    gap: 4px;
    font-size: 12px;
    color: var(--muted);
  }
  .buttons {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
  }
  /* A narrow screen: the size and date under the name, so the buttons keep their room. */
  @media (max-width: 600px) {
    .files .row:not(.folder-row) {
      flex-wrap: wrap;
    }
    .files .row:not(.folder-row) .meta {
      order: 3;
      flex-basis: 100%;
      padding-left: 74px;
    }
  }
</style>
