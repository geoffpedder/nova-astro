<script lang="ts">
  import { act, nova } from '../lib/state.svelte'
  import { planner, sequenceInbox } from '../lib/inbox.svelte'
  import { formatDec, formatRa, fromStellarium } from '../lib/stellarium'
  import { stellariumSettings } from '../lib/stellariumSettings.svelte'
  import type { FrameType, SequenceRow, SequenceTargetPlan } from '../lib/types'

  /** One target of the night's plan: what, when, and which frames. */
  let {
    plan = $bindable(),
    index,
    count,
    running,
    progress,
    current,
    filters,
    onremove,
    onmove,
    oncount,
  }: {
    plan: SequenceTargetPlan
    index: number
    count: number
    running: boolean
    /** This target's progress from the running (or last) sequence. */
    progress: { name: string; state: string; rows: { done: number; count: number; skipped: boolean }[]; note: string | null } | null
    current: boolean
    filters: string[]
    onremove: () => void
    onmove: (by: -1 | 1) => void
    oncount: (row: number, count: number) => void
  } = $props()

  const frameTypes: FrameType[] = ['Light', 'Dark', 'Flat', 'Bias']
  const optional = (value: string) => (value.trim() === '' ? null : Number(value))
  const text = (value: string) => (value.trim() === '' ? null : value.trim())

  const totals = $derived.by(() => {
    const rows = plan.rows.filter((r) => r.enabled)
    const frames = rows.reduce((n, r) => n + (r.count || 0), 0)
    const seconds = rows.reduce((n, r) => n + (r.count || 0) * (r.exposureSeconds || 0), 0)
    return { frames, seconds }
  })

  function duration(seconds: number): string {
    if (seconds < 60) return `${Math.round(seconds)} s`
    const h = Math.floor(seconds / 3600)
    const m = Math.round((seconds % 3600) / 60)
    return h > 0 ? `${h} h ${String(m).padStart(2, '0')} min` : `${m} min`
  }

  function blankRow(filter: string | null = null): SequenceRow {
    return { filter, exposureSeconds: 300, count: 25, gain: null, offset: null, binning: null, frameType: 'Light', enabled: true }
  }
  function addRow() {
    const last = plan.rows.at(-1)
    // Next filter in the wheel after the last row's, for quick LRGB entry.
    const next = last?.filter && filters.length ? filters[(filters.indexOf(last.filter) + 1) % filters.length] : (filters[0] ?? null)
    plan.rows.push(last ? { ...last, filter: next } : blankRow(next))
  }
  function moveRow(i: number, by: -1 | 1) {
    const j = i + by
    if (j < 0 || j >= plan.rows.length) return
    ;[plan.rows[i], plan.rows[j]] = [plan.rows[j], plan.rows[i]]
  }

  let fetching = $state(false)
  async function fromStellariumTarget() {
    fetching = true
    try {
      await act(async () => {
        const t = await fromStellarium(stellariumSettings.port)
        plan.target = { name: t.name, ra: formatRa(t.raHours), dec: formatDec(t.decDegrees), rotation: null }
      })
    } finally {
      fetching = false
    }
  }
  // The target planner's frame centre; a mosaic fills this target with panel 1 and adds the other panels after it.
  const frame = $derived(planner.frame)
  function fromPlanner() {
    if (!frame) return
    const [first, ...rest] = frame.panels
    plan.target = { ...first }
    if (rest.length) sequenceInbox.targets.push(...rest.map((t) => ({ ...t })))
  }
  function fromMount() {
    const m = nova.status?.mount
    if (m) plan.target = { name: plan.target?.name || 'Here', ra: formatRa(m.raHours), dec: formatDec(m.decDegrees), rotation: plan.target?.rotation ?? null }
  }

  // This target's live stack while it builds (it's saved and cleared when the target ends).
  const stack = $derived(
    plan.liveStack && progress?.state === 'Running' ? (nova.status?.liveStack?.stacks.find((s) => s.target === progress.name) ?? null) : null,
  )

  const stateClass = (s: string | undefined) =>
    s === 'Running' ? 'warn' : s === 'Done' ? 'ok' : s === 'Skipped' || s === 'Stopped' ? 'danger' : 'muted'
</script>

<section class="card" class:current>
  <header>
    <span class="number">{index + 1}</span>
    {#if plan.target}
      <input class="name" placeholder="Target name, e.g. M 31" bind:value={plan.target.name} disabled={running} />
    {:else}
      <span class="muted">Wherever the scope points</span>
    {/if}
    {#if progress && progress.state !== 'Waiting'}
      <span class="badge {stateClass(progress.state)}" title={progress.note ?? ''}>{progress.state}</span>
    {/if}
    <span class="spacer"></span>
    {#if count > 1}
      <button class="icon" title="Move up" disabled={running || index === 0} onclick={() => onmove(-1)}>↑</button>
      <button class="icon" title="Move down" disabled={running || index === count - 1} onclick={() => onmove(1)}>↓</button>
      <button class="icon" title="Remove this target" disabled={running} onclick={onremove}>✕</button>
    {/if}
  </header>
  {#if progress?.note}<p class="note muted">{progress.note}</p>{/if}

  {#if plan.target}
    <div class="grid where">
      <label>RA (J2000) <input class="mono" bind:value={plan.target.ra} placeholder="hh:mm:ss" disabled={running} /></label>
      <label>Dec (J2000) <input class="mono" bind:value={plan.target.dec} placeholder="±dd:mm:ss" disabled={running} /></label>
      <label
        class="rot"
        title="Camera angle for this framing (position angle of the frame's top, degrees from north through east). After centring, NOVA asks you to turn the camera to it. Empty: any angle."
        >Rotation (°)
        <input
          class="rotation"
          type="number"
          min="0"
          max="360"
          step="0.1"
          placeholder="any"
          value={plan.target.rotation ?? ''}
          disabled={running}
          oninput={(e) => (plan.target!.rotation = optional(e.currentTarget.value))}
        /></label
      >
      <div class="buttons">
        <button class="secondary small" disabled={running || fetching} onclick={fromStellariumTarget}>From Stellarium</button>
        <button class="secondary small" disabled={running || !nova.status?.mount?.connected} onclick={fromMount}>Mount position</button>
        <button
          class="secondary small"
          disabled={running || !frame}
          onclick={fromPlanner}
          title={frame
            ? `${frame.name}${frame.panels.length > 1 ? ` (${frame.panels.length}-panel mosaic: the other panels are added as targets)` : ''}, as framed in the Targets tab`
            : 'Frame a target in the Targets tab first'}>From target planner</button
        >
      </div>
    </div>
  {/if}

  <div class="grid when">
    <label title="Local time: wait until then before starting this target (empty: no wait)">
      Start after
      <input
        type="time"
        value={plan.startAfter ?? ''}
        disabled={running}
        onchange={(e) => (plan.startAfter = text(e.currentTarget.value))}
      />
    </label>
    <label title="Local time: move on to the next target then (empty: when its frames are done)">
      Stop at
      <input type="time" value={plan.stopAt ?? ''} disabled={running} onchange={(e) => (plan.stopAt = text(e.currentTarget.value))} />
    </label>
    <label title="Wait until the target is this high, and move on when it sinks below (empty: the horizon)">
      Min altitude (°)
      <input
        type="number"
        min="-10"
        max="89"
        value={plan.minAltitude ?? ''}
        disabled={running}
        oninput={(e) => (plan.minAltitude = optional(e.currentTarget.value))}
      />
    </label>
  </div>

  <h4>
    Frames
    <span class="muted totals">{totals.frames} frames, {duration(totals.seconds)} of exposure</span>
    <span class="spacer"></span>
    {#if stack}<span class="muted totals" title={stack.lastRejected ? `Last left out: ${stack.lastRejected}` : ''}>{stack.frames} stacked{stack.rejected ? `, ${stack.rejected} left out` : ''}</span>{/if}
    <label
      class="stack-tick"
      title="Stack this target's light frames as they come, to watch it build up in the Image tab (Stack). Saved as FITS and PNG in its Stacked folder when the target ends; the frames are saved as usual."
    >
      <input type="checkbox" checked={plan.liveStack ?? false} disabled={running} onchange={(e) => (plan.liveStack = e.currentTarget.checked)} />
      Live stack
    </label>
  </h4>
  <div class="table-wrap">
  <table>
    <thead>
      <tr>
        <th title="Include this row">On</th>
        <th>Filter</th>
        <th>Exposure (s)</th>
        <th>Count</th>
        <th title="Empty: keep the camera's setting">Gain</th>
        <th title="Empty: keep the camera's setting">Offset</th>
        <th title="Empty: keep the camera's setting">Bin</th>
        <th>Type</th>
        <th>Time</th>
        <th>Done</th>
        <th></th>
      </tr>
    </thead>
    <tbody>
      {#each plan.rows as row, i (i)}
        {@const p = progress?.rows[i]}
        <tr class:off={!row.enabled}>
          <td><input type="checkbox" bind:checked={row.enabled} disabled={running} aria-label="Row {i + 1} on" /></td>
          <td>
            <select bind:value={row.filter} disabled={running} aria-label="Filter">
              <option value={null}>—</option>
              {#each filters as f (f)}<option value={f}>{f}</option>{/each}
              {#if row.filter && !filters.includes(row.filter)}<option value={row.filter}>{row.filter} (not in wheel)</option>{/if}
            </select>
          </td>
          <td><input type="number" min="0" step="any" bind:value={row.exposureSeconds} disabled={running} aria-label="Exposure" /></td>
          <td>
            {#if running && p && progress && !['Done', 'Skipped', 'Stopped'].includes(progress.state)}
              <input
                class="live"
                type="number"
                step="1"
                min={p.done}
                value={p.count}
                disabled={!row.enabled}
                title="Change while it runs: more frames, or fewer (not below the {p.done} already taken)"
                onchange={(e) => oncount(i, Math.round(Number(e.currentTarget.value)))}
                aria-label="Count"
              />
            {:else}
              <input type="number" min="0" step="1" bind:value={row.count} disabled={running} aria-label="Count" />
            {/if}
          </td>
          <td>
            <input
              type="number"
              value={row.gain ?? ''}
              disabled={running}
              oninput={(e) => (row.gain = optional(e.currentTarget.value))}
              aria-label="Gain"
            />
          </td>
          <td>
            <input
              type="number"
              value={row.offset ?? ''}
              disabled={running}
              oninput={(e) => (row.offset = optional(e.currentTarget.value))}
              aria-label="Offset"
            />
          </td>
          <td>
            <input
              type="number"
              min="1"
              max="8"
              value={row.binning ?? ''}
              disabled={running}
              oninput={(e) => (row.binning = optional(e.currentTarget.value))}
              aria-label="Binning"
            />
          </td>
          <td>
            <select bind:value={row.frameType} disabled={running} aria-label="Frame type">
              {#each frameTypes as t (t)}<option>{t}</option>{/each}
            </select>
          </td>
          <td class="mono muted">{duration((row.count || 0) * (row.exposureSeconds || 0))}</td>
          <td class="mono">{#if p}{p.done}/{p.count}{p.skipped ? ' skipped' : ''}{/if}</td>
          <td class="row-buttons">
            <button class="icon" title="Move up" disabled={running} onclick={() => moveRow(i, -1)}>↑</button>
            <button class="icon" title="Move down" disabled={running} onclick={() => moveRow(i, 1)}>↓</button>
            <button class="icon" title="Remove" disabled={running} onclick={() => plan.rows.splice(i, 1)}>✕</button>
          </td>
        </tr>
      {/each}
    </tbody>
  </table>
  </div>
  <div class="row">
    <button class="secondary small" disabled={running} onclick={addRow}>+ Row</button>
    <label class="inline">
      Order
      <select bind:value={plan.order} disabled={running}>
        <option value="ByRow">Row by row</option>
        <option value="Cycle">Cycle (one of each in turn)</option>
      </select>
    </label>
  </div>
</section>

<style>
  /* A little brighter than the page, so each target reads as one block. */
  .card {
    min-width: 0;
    border: 1px solid color-mix(in srgb, var(--text) 8%, var(--panel-border));
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--text) 4%, transparent);
    padding: 10px 12px;
    display: grid;
    gap: 8px;
  }
  .card.current {
    border-color: var(--warn);
  }
  header {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .number {
    width: 22px;
    height: 22px;
    border-radius: 50%;
    display: grid;
    place-items: center;
    font-size: 12px;
    background: var(--input);
    color: var(--muted);
    flex: none;
  }
  .name {
    flex: 1;
    min-width: 120px;
    max-width: 320px;
    font-weight: 600;
  }
  .note {
    margin: 0;
    font-size: 12px;
  }
  .grid {
    display: grid;
    gap: 8px;
    align-items: end;
  }
  .where,
  .when {
    display: flex;
    flex-wrap: wrap;
    align-items: end;
  }
  .where label {
    flex: 1 1 140px;
  }
  .where label.rot {
    flex: 0 1 100px;
  }
  .when label {
    flex: 0 1 150px;
  }
  .table-wrap {
    overflow-x: auto;
  }
  label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  label input,
  label select {
    color: var(--text);
  }
  label.inline {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 13px;
    color: var(--text);
  }
  h4 {
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
    margin: 4px 0 0;
    align-items: center;
    display: flex;
    gap: 10px;
    font-weight: normal;
  }
  .totals {
    text-transform: none;
    letter-spacing: 0;
  }
  .stack-tick {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    text-transform: none;
    letter-spacing: 0;
    cursor: pointer;
  }
  .buttons {
    display: flex;
    gap: 6px;
  }
  .small {
    font-size: 12px;
  }
  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 13px;
  }
  th {
    text-align: left;
    font-weight: normal;
    font-size: 11px;
    color: var(--muted);
    padding: 2px 4px;
  }
  td {
    padding: 2px 4px;
  }
  td input[type='number'] {
    width: 72px;
  }
  tr.off td {
    opacity: 0.45;
  }
  input.live {
    border-color: var(--accent);
  }
  .row-buttons {
    white-space: nowrap;
  }
  .icon {
    padding: 1px 6px;
    background: none;
    border: 1px solid var(--panel-border);
    color: var(--muted);
  }
  .row {
    display: flex;
    align-items: center;
    gap: 14px;
  }
  .spacer {
    flex: 1;
  }
</style>
