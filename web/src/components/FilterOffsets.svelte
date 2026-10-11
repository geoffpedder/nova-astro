<script lang="ts">
  // Per-filter focus offsets (Focuser panel): typed in or measured; with them on, every filter change moves the
  // focuser by the difference, instead of refocusing.
  import { ask } from '../lib/confirm.svelte'
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import type { FocuserStatus } from '../lib/types'

  let { focuser }: { focuser: FocuserStatus } = $props()

  const wheel = $derived(nova.status?.filterWheel)
  const filters = $derived((wheel?.names ?? []).map((name, i) => name || `Slot ${i + 1}`))
  const info = $derived(focuser.offsets)
  const measuring = $derived(info?.measuring?.running ?? false)
  const cameraReady = $derived(!!nova.status?.camera?.connected)

  let open = $state(false)
  let editing = $state(false)
  let enabled = $state(false)
  let reference = $state<string | null>(null)
  let draft = $state<Record<string, number | null>>({})
  let passes = $state(3)

  function edit() {
    enabled = info?.enabled ?? false
    reference = info?.reference ?? filters[0] ?? null
    draft = Object.fromEntries(filters.map((f) => [f, info?.offsets[f] ?? null]))
    editing = true
  }

  async function save() {
    const offsets: Record<string, number> = {}
    for (const [filter, value] of Object.entries(draft)) if (value != null && Number.isFinite(value)) offsets[filter] = Math.round(value)
    if (reference) offsets[reference] = offsets[reference] ?? 0
    if (await act(() => api.saveFocusOffsets(focuser.name, enabled, reference, offsets))) editing = false
  }

  async function toggle(on: boolean) {
    if (!info) return
    await act(() => api.saveFocusOffsets(focuser.name, on, info.reference, info.offsets))
  }

  async function measure() {
    const ref = info?.reference ?? filters[0]
    if (
      !(await ask('Measure the filter offsets?', {
        message: `NOVA autofocuses with ${ref}, then with each other filter and ${ref} again, ${passes} time${passes > 1 ? 's' : ''} over, and averages (${1 + passes * filters.length} autofocus runs). Point at a field with plenty of stars, high up, on a steady night; longer autofocus exposures give steadier results.`,
        confirm: 'Measure',
      }))
    )
      return
    open = true
    act(() => api.measureFocusOffsets(passes))
  }

  const known = $derived(filters.filter((f) => info?.offsets[f] != null))
  const summary = $derived(
    !info || known.length === 0
      ? 'none yet'
      : `${info.enabled ? 'on' : 'off'}: ${known.map((f) => `${f} ${signed(info.offsets[f])}`).join(', ')}`,
  )
  const signed = (n: number) => (n > 0 ? `+${n}` : `${n}`)
</script>

<div class="offsets">
  <button class="toggle" aria-expanded={open} onclick={() => (open = !open)}>
    <span class="chevron" class:closed={!open} aria-hidden="true">▾</span>
    Filter offsets <span class="muted summary">{summary}</span>
  </button>

  {#if open}
    <p class="muted hint">
      Filters focus at slightly different positions. With offsets on, changing filter (by hand or in a sequence) moves the
      focuser by the difference, so a sequence needn't refocus after every filter change.
    </p>

    {#if info?.measuring}
      <p class="message" class:bad={!info.measuring.running && info.measuring.message !== 'Cancelled' && known.length === 0}>
        {info.measuring.running ? '⏳ ' : ''}{info.measuring.message}
      </p>
      {#if info.measuring.results.length}
        <ul class="results mono">
          {#each info.measuring.results as r, i (i)}
            <li>{r.filter}: {r.message}</li>
          {/each}
        </ul>
      {/if}
    {/if}

    {#if editing}
      <table>
        <thead><tr><th>Filter</th><th>Offset (steps)</th><th title="The filter the others are measured from">Reference</th></tr></thead>
        <tbody>
          {#each filters as f (f)}
            <tr>
              <td>{f}</td>
              <td><input type="number" step="1" bind:value={draft[f]} disabled={reference === f} placeholder="none" /></td>
              <td><input type="radio" name="offset-reference" checked={reference === f} onchange={() => ((reference = f), (draft[f] = 0))} /></td>
            </tr>
          {/each}
        </tbody>
      </table>
      <label class="check"><input type="checkbox" bind:checked={enabled} /> Move the focuser on filter changes</label>
      <div class="row">
        <span class="spacer"></span>
        <button class="secondary" onclick={() => (editing = false)}>Cancel</button>
        <button onclick={save}>Save</button>
      </div>
    {:else}
      {#if known.length > 0}
        <label class="check">
          <input type="checkbox" checked={info?.enabled ?? false} disabled={measuring} onchange={(e) => toggle(e.currentTarget.checked)} />
          Move the focuser on filter changes
        </label>
      {/if}
      <div class="row">
        {#if measuring}
          <button class="danger" onclick={() => act(api.cancelFocusOffsets)}>Cancel measuring</button>
        {:else}
          <button disabled={!cameraReady || !wheel?.connected} title={cameraReady ? 'Autofocus on every filter and work out the offsets' : 'Connect a camera first'}
            onclick={measure}>Measure…</button
          >
          <label class="passes" title="How many times every filter is measured: more passes average out the scatter of single autofocus runs">
            <input type="number" min="1" max="5" bind:value={passes} /> passes
          </label>
          <button class="secondary" onclick={edit}>Edit</button>
        {/if}
      </div>
    {/if}
  {/if}
</div>

<style>
  .offsets {
    margin-top: 12px;
  }
  .toggle {
    all: unset;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
  }
  .toggle:focus-visible {
    outline: 2px solid var(--accent);
  }
  .chevron {
    font-size: 10px;
    transition: transform 0.15s;
  }
  .chevron.closed {
    transform: rotate(-90deg);
  }
  .summary {
    font-size: 11px;
  }
  .hint,
  .message {
    font-size: 11px;
    margin: 6px 0;
  }
  .bad {
    color: var(--danger);
  }
  .results {
    font-size: 11px;
    margin: 0 0 6px;
    padding-left: 16px;
  }
  table {
    width: 100%;
    font-size: 12px;
    border-collapse: collapse;
    margin: 6px 0;
  }
  th {
    text-align: left;
    font-weight: 500;
    color: var(--muted);
  }
  td input[type='number'] {
    width: 7em;
  }
  .check {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    margin: 6px 0;
  }
  .row {
    display: flex;
    gap: 8px;
    align-items: center;
    margin-top: 6px;
  }
  .spacer {
    flex: 1;
  }
  .passes {
    display: flex;
    align-items: center;
    gap: 4px;
    font-size: 12px;
  }
  .passes input {
    width: 3.5em;
  }
</style>
