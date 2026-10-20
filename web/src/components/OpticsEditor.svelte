<script lang="ts">
  import { ask } from '../lib/confirm.svelte'
  import { api } from '../lib/api'
  import { act, nick, nova } from '../lib/state.svelte'
  import { reveal } from '../lib/reveal.svelte'

  /** The imaging camera, edited by default; any other camera (e.g. the guide camera) can be picked. */
  let { defaultCamera, binning = 1, title = 'Optics', imaging = true }: { defaultCamera: string | null; binning?: number; title?: string; imaging?: boolean } =
    $props()

  const NewPreset = '\u0000new'

  let open = $state(false)
  // The setup guide's "Set the optics" button.
  $effect(() => {
    if (reveal.part !== 'optics' || !imaging) return
    reveal.part = null
    open = true
  })
  let chosenCamera = $state<string | null>(null)
  // The preset being edited: an existing name, or NewPreset.
  let editing = $state<string | null>(null)
  let label = $state('')
  let focalLength = $state<number | null>(null)
  let aperture = $state<number | null>(null)
  let loadedKey = $state<string | null>(null)

  const cameras = $derived(nova.status?.optics ?? [])
  const camera = $derived(chosenCamera ?? defaultCamera ?? cameras[0]?.camera ?? null)
  const info = $derived(cameras.find((c) => c.camera === camera) ?? null)
  const presets = $derived(info?.presets ?? [])
  const isImagingCamera = $derived(imaging && camera === defaultCamera)
  const target = $derived(editing ?? info?.activePreset ?? (presets.length === 0 ? NewPreset : null))
  const existing = $derived(presets.find((p) => p.name === target) ?? null)
  const sharedWith = $derived((existing?.usedBy ?? []).map(nick).join(', '))

  // Load the fields when the camera or preset being shown changes; leave them alone while typing.
  $effect(() => {
    const key = `${camera}|${target}`
    if (key === loadedKey) return
    loadedKey = key
    label = existing?.name ?? ''
    focalLength = existing?.focalLength ?? null
    aperture = existing?.aperture ?? null
  })

  const ratio = $derived(focalLength && aperture ? focalLength / aperture : null)
  const scale = $derived(focalLength && info?.pixelSize ? (206.265 * info.pixelSize) / focalLength : null)
  const unsaved = $derived(
    !existing || label.trim() !== existing.name || focalLength !== existing.focalLength || aperture !== existing.aperture,
  )
  const summary = $derived(
    info?.activePreset
      ? `${info.activePreset}: ${info.focalLength} mm f/${info.focalRatio?.toFixed(1)}` +
          (info.imageScale ? `, ${(info.imageScale * (isImagingCamera ? binning : 1)).toFixed(2)}″/px` : '')
      : 'not set',
  )

  function chooseCamera(name: string) {
    chosenCamera = name
    editing = null
  }

  function choosePreset(value: string) {
    if (value === NewPreset) {
      editing = NewPreset
      return
    }
    editing = null
    // Picking a preset makes it the one in use (and sends it to the driver if connected).
    if (camera) act(() => api.selectOptics(camera, value))
  }

  async function save() {
    if (!camera || !focalLength || !aperture || !label.trim()) return
    const originalName = existing ? existing.name : undefined
    if (await act(() => api.setOptics(camera, label.trim(), focalLength!, aperture!, originalName))) editing = null
  }

  async function remove() {
    if (!camera || !existing) return
    const message = sharedWith ? `It's also used by ${sharedWith}, which will have no optics until you pick another.` : undefined
    if (!(await ask(`Delete the optics preset "${existing.name}"?`, { message, confirm: 'Delete', danger: true }))) return
    if (await act(() => api.deleteOptics(camera, existing!.name))) editing = null
  }
</script>

<div class="optics">
  <button class="toggle" aria-expanded={open} onclick={() => (open = !open)}>
    <span class="chevron" class:closed={!open} aria-hidden="true">▾</span>
    {title} <span class="muted summary">{summary}</span>
  </button>

  {#if open}
    {#if cameras.length > 1}
      <label class="wide">
        For
        <select value={camera} onchange={(e) => chooseCamera(e.currentTarget.value)}>
          {#each cameras as c (c.camera)}
            <option value={c.camera}>{nick(c.camera)}{c.camera === nova.status?.camera?.name ? ' (imaging)' : ''}</option>
          {/each}
        </select>
      </label>
    {/if}

    <label class="wide">
      Preset
      <select value={target ?? ''} onchange={(e) => choosePreset(e.currentTarget.value)} title="Picking a preset makes it the one in use">
        {#each presets as p (p.name)}
          <option value={p.name}
            >{p.name}{p.name === info?.activePreset ? ' ✓' : ''} — {p.focalLength} mm f/{p.focalRatio.toFixed(1)}{p.usedBy?.length
              ? ` (also ${p.usedBy.map(nick).join(', ')})`
              : ''}</option
          >
        {/each}
        <option value={NewPreset}>+ New preset…</option>
      </select>
    </label>

    <div class="fields">
      <label class="name">Label<input type="text" maxlength="60" placeholder="e.g. RedCat 51 + 0.8× reducer" bind:value={label} /></label>
      <label>Focal length (mm)<input type="number" min="1" step="1" bind:value={focalLength} /></label>
      <label>Aperture (mm)<input type="number" min="1" step="1" bind:value={aperture} /></label>
    </div>

    <p class="muted derived mono">
      {ratio ? `f/${ratio.toFixed(1)}` : 'f/—'},
      {info?.pixelSize ? `${info.pixelSize} µm pixels` : 'pixel size unknown until connected'}{#if scale},
        {scale.toFixed(2)}″/px{#if isImagingCamera && binning > 1} ({(scale * binning).toFixed(2)}″/px at {binning}×{binning}){/if}{/if}
    </p>

    <div class="row actions">
      <button disabled={!camera || !label.trim() || !focalLength || !aperture || !unsaved} onclick={save}>
        {existing ? 'Save changes' : 'Add preset'}
      </button>
      {#if existing}<button class="secondary" onclick={remove}>Delete</button>{/if}
    </div>
    {#if existing && sharedWith}
      <p class="muted hint">Shared with {sharedWith}: changes apply there too.</p>
    {/if}
    <p class="muted hint">
      The preset marked ✓ is sent to the driver whenever this camera connects (also when PHD2 connects it). Presets are
      shared by every camera: pick the same one for a camera on the same telescope.
    </p>
  {/if}
</div>

<style>
  .optics {
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
  }
  label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  label input,
  label select {
    width: 100%;
    color: var(--text);
  }
  .wide {
    margin-top: 8px;
  }
  .fields {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
    margin-top: 8px;
  }
  .fields .name {
    grid-column: 1 / -1;
  }
  .derived {
    margin: 6px 0 0;
    font-size: 12px;
  }
  .actions {
    margin-top: 8px;
  }
  .hint {
    margin: 6px 0 0;
    font-size: 11px;
  }
</style>
