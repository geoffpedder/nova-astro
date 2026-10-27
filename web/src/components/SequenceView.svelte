<script lang="ts">
  import { ask } from '../lib/confirm.svelte'
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import { sequenceInbox } from '../lib/inbox.svelte'
  import type { Sequence, SequenceRow, SequenceTargetPlan } from '../lib/types'
  import SequenceTargetCard from './SequenceTargetCard.svelte'
  import FlatsDialog from './FlatsDialog.svelte'

  let flatsOpen = $state(false)

  const DRAFT_KEY = 'nova.sequenceDraft'

  function blankRow(filter: string | null = null): SequenceRow {
    return { filter, exposureSeconds: 300, count: 25, gain: null, offset: null, binning: null, frameType: 'Light', enabled: true }
  }

  function blankTarget(rows: SequenceRow[] = [blankRow()]): SequenceTargetPlan {
    return { target: { name: '', ra: '', dec: '' }, rows, order: 'ByRow', startAfter: null, stopAt: null, minAltitude: null }
  }

  function blank(): Sequence {
    return {
      name: '',
      target: null,
      start: { coolCamera: true, coolTo: -10, centerTarget: true, autofocus: true, startGuiding: true, matchRotation: true },
      rows: [],
      order: 'ByRow',
      targets: [blankTarget()],
      // The mount's dither and settle defaults (smaller and tighter for a ZWO AM).
      dither: (() => {
        const d = nova.status?.guider?.defaults
        return d
          ? { everyFrames: d.ditherEveryFrames, pixels: d.ditherPixels, raOnly: false, settlePixels: d.settlePixels, settleSeconds: d.settleSeconds, settleTimeout: d.settleTimeout }
          : { everyFrames: 3, pixels: 5, raOnly: false, settlePixels: 1.5, settleSeconds: 10, settleTimeout: 60 }
      })(),
      autofocus: { afterFilterChange: false, temperatureChange: 2, hfrIncreasePercent: 15, everyFrames: null },
      end: { stopGuiding: true, warmCamera: false, park: false, stopAt: 'AstronomicalDawn' },
      meridianFlip: blankFlip(),
    }
  }

  function blankFlip(): Sequence['meridianFlip'] {
    return { enabled: true, minutesAfterMeridian: 5, maxMinutesAfterMeridian: 15, recenter: true, autofocusAfter: false, settleSeconds: 5 }
  }

  // The plan being edited, kept in this browser so a reload doesn't lose it.
  function loadDraft(): Sequence {
    try {
      const saved = localStorage.getItem(DRAFT_KEY)
      if (saved) return withDefaults(JSON.parse(saved))
    } catch {
      // ignore
    }
    return blank()
  }
  // Plans saved before a setting existed get its default; single-target plans become one target.
  function withDefaults(loaded: Partial<Sequence>): Sequence {
    const targets: SequenceTargetPlan[] = loaded.targets?.length
      ? loaded.targets.map((t) => ({ ...blankTarget(), ...t }))
      : [
          {
            ...blankTarget(loaded.rows?.length ? loaded.rows : [blankRow()]),
            target: loaded.target === undefined ? { name: '', ra: '', dec: '' } : loaded.target,
            order: loaded.order ?? 'ByRow',
          },
        ]
    return {
      ...blank(),
      ...loaded,
      start: { ...blank().start, ...loaded.start },
      end: { ...blank().end, ...loaded.end },
      target: null,
      rows: [],
      order: 'ByRow',
      targets,
      meridianFlip: loaded.meridianFlip ?? blankFlip(),
    }
  }
  let plan = $state<Sequence>(loadDraft())
  // The night's end: one of the named moments, or a time typed in ("05:30").
  const stopKind = $derived(
    !plan.end.stopAt ? 'AstronomicalDawn' : /^\d{1,2}:\d{2}$/.test(plan.end.stopAt) ? 'time' : plan.end.stopAt,
  )
  function setStopKind(kind: string) {
    plan.end.stopAt = kind === 'time' ? '05:00' : kind
  }
  $effect(() => {
    const json = JSON.stringify(plan)
    try {
      localStorage.setItem(DRAFT_KEY, json)
    } catch {
      // ignore
    }
  })

  const info = $derived(nova.status?.sequencer)
  const status = $derived(info?.status ?? null)
  const running = $derived(info?.running ?? false)
  const filters = $derived(nova.status?.filterWheel?.names ?? [])
  // The frame being taken right now, so it shows here as well as in the camera panel.
  const exposure = $derived(running ? nova.exposure : null)
  const exposurePercent = $derived(
    exposure ? (exposure.totalSeconds > 0 ? Math.min(100, (100 * exposure.elapsedSeconds) / exposure.totalSeconds) : 100) : 0,
  )
  // No filter wheel: every row is "no filter" (a draft can carry names over from another wheel, e.g. the simulator's).
  const noWheel = $derived(nova.status != null && !nova.status.filterWheel?.connected)
  $effect(() => {
    if (!noWheel || running) return
    for (const t of plan.targets ?? []) for (const r of t.rows) if (r.filter !== null) r.filter = null
  })
  const targets = $derived(plan.targets ?? [])
  // Progress belongs to these cards only if it's this plan's run (same number of targets).
  const progress = $derived(status?.targets && status.targets.length === targets.length ? status.targets : null)

  // Totals for the enabled rows; ~3 s per frame of download/save overhead is a fair first guess.
  const totals = $derived.by(() => {
    const rows = targets.flatMap((t) => t.rows).filter((r) => r.enabled)
    const frames = rows.reduce((n, r) => n + (r.count || 0), 0)
    const seconds = rows.reduce((n, r) => n + (r.count || 0) * (r.exposureSeconds || 0), 0)
    return { frames, seconds, estimate: seconds + frames * 3 }
  })

  function duration(seconds: number): string {
    if (seconds < 60) return `${Math.round(seconds)} s`
    const h = Math.floor(seconds / 3600)
    const m = Math.round((seconds % 3600) / 60)
    return h > 0 ? `${h} h ${String(m).padStart(2, '0')} min` : `${m} min`
  }
  // Numbers from inputs: empty means "not set" for the optional ones.
  const optional = (value: string) => (value.trim() === '' ? null : Number(value))
  const clock = (iso: string | null) => (iso ? new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '–')

  // Targets sent from the framing tool: fill an empty plan's first target, then add the rest (copying frames).
  $effect(() => {
    if (sequenceInbox.targets.length === 0 || running) return
    const incoming = sequenceInbox.targets.splice(0)
    let list = [...(plan.targets ?? [])]
    for (const target of incoming) {
      const empty = list.length === 1 && !list[0].target?.name && !list[0].target?.ra && !list[0].target?.dec
      if (empty) list = [{ ...list[0], target }]
      else {
        const last = list.at(-1)
        list = [...list, { ...blankTarget(last ? last.rows.map((r) => ({ ...r })) : [blankRow()]), target }]
      }
    }
    plan.targets = list
  })

  // Targets: a new one copies the last one's frames (most nights use the same filters), without coordinates.
  function addTarget() {
    const last = targets.at(-1)
    plan.targets = [...targets, blankTarget(last ? last.rows.map((r) => ({ ...r })) : [blankRow()])]
  }
  async function removeTarget(i: number) {
    const name = targets[i].target?.name || `target ${i + 1}`
    if (await ask(`Remove ${name} from the plan?`, { message: 'Its frames go with it.', confirm: 'Remove', danger: true }))
      plan.targets = targets.filter((_, k) => k !== i)
  }
  function moveTarget(i: number, by: -1 | 1) {
    const j = i + by
    if (j < 0 || j >= targets.length) return
    const next = [...targets]
    ;[next[i], next[j]] = [next[j], next[i]]
    plan.targets = next
  }
  // A single target may image wherever the scope points (no coordinates).
  function toggleCoordinates(on: boolean) {
    targets[0].target = on ? { name: '', ra: '', dec: '' } : null
    if (!on) plan.start.centerTarget = false
  }

  // Saved sequences
  let selected = $state('')
  async function open(name: string) {
    if (!name) return
    await act(async () => {
      plan = withDefaults(await api.sequenceLoad(name))
    })
    selected = ''
  }
  const outgoing = (): Sequence => ({
    ...$state.snapshot(plan),
    name: plan.name || targets[0]?.target?.name || 'Sequence',
    target: null,
    rows: [],
    order: 'ByRow',
  })
  const save = () => act(() => api.sequenceSave(outgoing()))
  async function remove() {
    if (plan.name && (await ask(`Delete the saved sequence '${plan.name}'?`, { confirm: 'Delete', danger: true })))
      await act(() => api.sequenceDelete(plan.name))
  }
  async function fresh() {
    if (await ask('Start a new, empty sequence?', { message: 'The current one stays saved if you saved it.', confirm: 'Start new' }))
      plan = blank()
  }

  // While it runs, the refocus triggers and the end of the night can change too: sent a moment after each edit.
  let liveSent = ''
  let liveTimer: ReturnType<typeof setTimeout> | undefined
  $effect(() => {
    const live = JSON.stringify({ autofocus: plan.autofocus, end: plan.end })
    if (!running || liveSent === '') {
      liveSent = live // what it starts with (or what this page showed when it opened): only edits after that are sent
      return
    }
    if (live === liveSent) return
    clearTimeout(liveTimer)
    liveTimer = setTimeout(async () => {
      if (await act(() => api.sequenceLive($state.snapshot(plan.autofocus), $state.snapshot(plan.end)))) liveSent = live
    }, 700)
  })

  // While running, only the counts can change: the server takes the new number straight away.
  async function changeCount(target: number, row: number, count: number) {
    if (!Number.isFinite(count)) return
    if (await act(() => api.sequenceCount(target, row, count))) targets[target].rows[row].count = count
  }

  const start = () => act(() => api.sequenceStart(outgoing()))
  async function abort() {
    if (await ask('Abort the sequence?', { message: 'The current exposure is cancelled.', confirm: 'Abort', danger: true })) act(api.sequenceAbort)
  }
</script>

<div class="panel sequence">
  <div class="top">
    <h2>Plan</h2>
    <input class="name" placeholder="Sequence name" bind:value={plan.name} aria-label="Sequence name" />
    <select bind:value={selected} onchange={() => open(selected)} aria-label="Open a saved sequence" disabled={running}>
      <option value="">Open…</option>
      {#each info?.saved ?? [] as name (name)}<option>{name}</option>{/each}
    </select>
    <button class="secondary" disabled={!plan.name} onclick={save}>Save</button>
    <button class="secondary" disabled={!plan.name || !info?.saved.includes(plan.name)} onclick={remove}>Delete</button>
    <button class="secondary" onclick={fresh} disabled={running}>New</button>
    <button
      class="secondary"
      onclick={() => (flatsOpen = true)}
      disabled={running || !nova.status?.camera?.connected || !!nova.status?.flats?.running}
      title={running ? 'The sequence takes its own flats (At the end)' : 'Take flats now with your panel'}>Flats…</button
    >
  </div>
  {#if flatsOpen}
    <FlatsDialog
      name={targets
        .map((t) => t.target?.name)
        .filter(Boolean)
        .join(' + ')}
      onclose={() => (flatsOpen = false)}
    />
  {/if}

  {#if status}
    <section class="progress {status.state.toLowerCase()}">
      <div class="progress-head">
        <span class="badge {status.state === 'Running' ? 'warn' : status.state === 'Done' ? 'ok' : status.state === 'Paused' ? 'muted' : 'danger'}">
          {status.state}{status.pauseRequested && status.state === 'Running' ? ' (pausing after this frame)' : ''}
        </span>
        <b>{status.sequenceName}</b>
        {#if status.target}
          <span class="muted"
            >on {status.target}{#if (status.targets?.length ?? 0) > 1}{' '}(target {status.currentTarget + 1} of {status.targets?.length}){/if}</span
          >
        {/if}
        <span class="spacer"></span>
        {#if running}
          {#if status.state === 'Paused'}
            <button onclick={() => act(api.sequenceResume)}>Resume</button>
          {:else}
            <button class="secondary" disabled={status.pauseRequested} onclick={() => act(api.sequencePause)}>Pause</button>
          {/if}
          <button class="secondary" title="Skip the rest of the current row" onclick={() => act(api.sequenceSkip)}>Skip row</button>
          {#if (status.targets?.length ?? 0) > 1}
            <button class="secondary" title="Move on to the next target" onclick={() => act(api.sequenceSkipTarget)}>Skip target</button>
          {/if}
          <button class="danger" onclick={abort}>Abort</button>
        {/if}
      </div>
      <div class="step"><span class="muted">{status.step}:</span> {status.message}</div>
      <!-- Kept while running, between frames too (autofocus, slews, dithers), so the box doesn't change height. -->
      {#if running}
        <div class="exposure">
          <div class="bar exposing" class:downloading={exposure?.state === 'Downloading'} title="This exposure">
            <div style:width="{!exposure ? 0 : exposure.state === 'Downloading' ? 100 : exposurePercent}%"></div>
          </div>
          <span class="mono small" class:muted={!exposure}>
            {!exposure
              ? status.step
              : exposure.state === 'Downloading'
                ? 'Downloading…'
                : `${exposure.elapsedSeconds.toFixed(0)} / ${exposure.totalSeconds}s`}
          </span>
        </div>
      {/if}
      <div class="bar" title="{status.framesDone} of {status.framesTotal} frames">
        <div style:width="{status.framesTotal ? (status.framesDone / status.framesTotal) * 100 : 0}%"></div>
      </div>
      <div class="facts mono">
        <span>{status.framesDone}/{status.framesTotal} frames</span>
        {#if running && status.etaUtc}<span>done ≈ {clock(status.etaUtc)}</span>{/if}
        {#if running && status.nightEndUtc}<span title="The sequence stops then: {status.nightEnd}">stops ≈ {clock(status.nightEndUtc)}</span>{/if}
        {#if status.finishedUtc}<span>ended {clock(status.finishedUtc)}</span>{/if}
        {#if status.lastHfr != null}<span>HFR {status.lastHfr.toFixed(2)}</span>{/if}
        <span>{status.autofocuses} autofocus</span>
        <span>{status.dithers} dithers</span>
        {#if status.flips}<span>{status.flips} meridian flip{status.flips > 1 ? 's' : ''}</span>{/if}
        {#if running && status.flipDueUtc}<span class="flip-due"
            title="Meridian flips happen between frames: {status.flipNote ?? 'when the target is far enough past the meridian'}. The log warns about 5 minutes before."
            >flip ≈ {clock(status.flipDueUtc)}{status.flipNote?.startsWith('after this frame') ? ', after this frame' : status.flipNote?.startsWith('waiting') ? ', waiting for it' : ''}{status.flipLatestUtc
              ? ` (latest ${clock(status.flipLatestUtc)})`
              : ''}</span
          >{/if}
        {#if status.failures}<span class="bad">{status.failures} failed</span>{/if}
      </div>
    </section>
  {/if}

  <div class="editor" class:locked={running}>
    <fieldset disabled={running}>
      <section>
        <h3>Start</h3>
        <div class="checks">
          <label class="check">
            <input type="checkbox" bind:checked={plan.start.coolCamera} /> Cool to
            <input class="tiny" type="number" step="1" bind:value={plan.start.coolTo} disabled={!plan.start.coolCamera} /> °C
          </label>
          <label class="check" title="Each target: slew there and plate solve until it's centred">
            <input type="checkbox" bind:checked={plan.start.centerTarget} disabled={targets.length === 1 && !targets[0].target} /> Slew & centre
            on each target
          </label>
          <label
            class="check"
            title="After centring a target that has a rotation (from the target planner), turn the camera to it: with the rotator if one is connected, otherwise by telling you which way to turn it (carrying on as it is if nobody does within 20 minutes)."
          >
            <input type="checkbox" bind:checked={plan.start.matchRotation} disabled={!plan.start.centerTarget} /> Match camera rotation
          </label>
          <label class="check"><input type="checkbox" bind:checked={plan.start.autofocus} /> Autofocus at each target</label>
          <label class="check"><input type="checkbox" bind:checked={plan.start.startGuiding} /> Guide</label>
        </div>
      </section>
    </fieldset>

    <section>
      <h3>
        Targets
        <span class="muted totals">
          {targets.length} target{targets.length === 1 ? '' : 's'}, {totals.frames} frames, {duration(totals.seconds)} of exposure. About
          {duration(totals.estimate)} in all.
        </span>
      </h3>
      {#if targets.length === 1}
        <label class="check coords">
          <input
            type="checkbox"
            checked={!!targets[0].target}
            disabled={running}
            onchange={(e) => toggleCoordinates(e.currentTarget.checked)}
          /> Go to a target (untick to image wherever the scope points)
        </label>
      {/if}
      <div class="targets">
        {#each targets as _, i (i)}
          <SequenceTargetCard
            bind:plan={plan.targets![i]}
            index={i}
            count={targets.length}
            {running}
            progress={progress?.[i] ?? null}
            current={running && status?.currentTarget === i}
            {filters}
            onremove={() => removeTarget(i)}
            onmove={(by) => moveTarget(i, by)}
            oncount={(row, n) => changeCount(i, row, n)}
          />
        {/each}
      </div>
      <div class="row">
        <button class="secondary small" disabled={running || (targets.length === 1 && !targets[0].target)} onclick={addTarget}
          >+ Target</button
        >
        <span class="muted small">Targets run in order. Each can wait for a start time or altitude, and stop at a time.</span>
      </div>
      {#if running}<p class="muted small">While it runs, you can change the counts, when to refocus and the end of the night; the rest is locked.</p>{/if}
    </section>

    <fieldset>
      <div class="two">
        <!-- What happens between frames, as one group. -->
        <div class="group">
        <fieldset class="lock" disabled={running}>
        <section>
          <h3>Dither</h3>
          <div class="checks">
            <label class="check">
              Every <input class="tiny" type="number" min="0" bind:value={plan.dither.everyFrames} /> frames
              <span class="muted">(0 = off)</span>
            </label>
            <label class="check">By <input class="tiny" type="number" min="0.5" step="0.5" bind:value={plan.dither.pixels} /> px</label>
            <label class="check"><input type="checkbox" bind:checked={plan.dither.raOnly} /> RA only</label>
          </div>
        </section>
        </fieldset>

        <section>
          <h3>Refocus when</h3>
          <div class="checks">
            <label class="check" title="Not needed with filter offsets on (Focuser panel): each filter change then moves the focuser by the known difference"><input type="checkbox" bind:checked={plan.autofocus.afterFilterChange} /> the filter changes</label>
            <label class="check">
              temperature moves
              <input
                class="tiny"
                type="number"
                step="0.5"
                value={plan.autofocus.temperatureChange ?? ''}
                oninput={(e) => (plan.autofocus.temperatureChange = optional(e.currentTarget.value))}
              /> °C
            </label>
            <label class="check">
              HFR gets
              <input
                class="tiny"
                type="number"
                step="5"
                value={plan.autofocus.hfrIncreasePercent ?? ''}
                oninput={(e) => (plan.autofocus.hfrIncreasePercent = optional(e.currentTarget.value))}
              /> % worse
            </label>
            <label class="check">
              every
              <input
                class="tiny"
                type="number"
                min="1"
                value={plan.autofocus.everyFrames ?? ''}
                oninput={(e) => (plan.autofocus.everyFrames = optional(e.currentTarget.value))}
              /> frames
            </label>
            <span class="muted small">Leave a box empty to turn that trigger off.</span>
          </div>
        </section>

        <fieldset class="lock" disabled={running}>
        <section>
          <h3>
            <label class="check"><input type="checkbox" bind:checked={plan.meridianFlip.enabled} /> Meridian flip</label>
          </h3>
          {#if plan.meridianFlip.enabled}
            <div class="checks">
              <label class="check" title="Flip once the target is this far past the meridian">
                Flip <input class="tiny" type="number" min="0" step="1" bind:value={plan.meridianFlip.minutesAfterMeridian} /> min after
                the meridian
              </label>
              <label class="check" title="Don't start a frame that would still be running this long after the meridian: wait for the flip instead. Keep it within what your mount can track past the meridian.">
                Frames may run until <input class="tiny" type="number" min="0" step="1" bind:value={plan.meridianFlip.maxMinutesAfterMeridian} /> min
                after
              </label>
              <label class="check"><input type="checkbox" bind:checked={plan.meridianFlip.recenter} /> Re-centre after the flip</label>
              <label class="check"><input type="checkbox" bind:checked={plan.meridianFlip.autofocusAfter} /> Autofocus after the flip</label>
            </div>
          {:else}
            <p class="muted small">Off: for fork or alt-az mounts, or targets that won't reach the meridian.</p>
          {/if}
        </section>
        </fieldset>
        </div>

        <section class="end">
          <h3>At the end</h3>
          <label class="night-end" title="Every target stops then (no frame is started that would run past it), and the steps below run. Dawn needs your site, in the Mount panel.">
            Stop the night at
            <select value={stopKind} onchange={(e) => setStopKind(e.currentTarget.value)}>
              <option value="AstronomicalDawn">astronomical dawn</option>
              <option value="NauticalDawn">nautical dawn</option>
              <option value="Sunrise">sunrise</option>
              <option value="time">a set time</option>
              <option value="Never">never</option>
            </select>
            {#if stopKind === 'time'}
              <input type="time" value={plan.end.stopAt} onchange={(e) => (plan.end.stopAt = e.currentTarget.value || '05:00')} />
            {/if}
          </label>
          <div class="checks">
            <label class="check"><input type="checkbox" bind:checked={plan.end.stopGuiding} /> Stop guiding</label>
            <label class="check"><input type="checkbox" bind:checked={plan.end.warmCamera} /> Warm the camera</label>
            <label class="check"><input type="checkbox" bind:checked={plan.end.park} /> Park the mount</label>
            <label
              class="check"
              title="With your flat panel, put on by hand when NOVA asks: before the camera is turned for a target with a different rotation (skipped if nobody comes within 20 minutes), and at the end, after parking and before warming the camera (NOVA waits for you). Uses the Flats… settings."
            >
              <input
                type="checkbox"
                checked={plan.flats?.enabled ?? false}
                onchange={(e) => (plan.flats = { enabled: e.currentTarget.checked })}
              /> Flats (before turning the camera, and at the end)
            </label>
          </div>
        </section>
      </div>
    </fieldset>
  </div>

  <div class="run">
    {#if running}
      <span class="muted">While it runs: frame counts, refocus and the end of the night can change.</span>
    {:else}
      <button class="start" disabled={!nova.status?.camera?.connected || totals.frames === 0} onclick={start}>▶ Start sequence</button>
      {#if !nova.status?.camera?.connected}<span class="muted">Connect a camera first.</span>{/if}
    {/if}
  </div>
</div>

<style>
  .sequence {
    display: flex;
    flex-direction: column;
    gap: 12px;
    min-height: 0;
    /* Up and down only: nothing in it is wider (the frames table scrolls sideways in its own box), and a width that
       works out to a fraction of a pixel would otherwise give a scroll bar that moves 1 px. */
    overflow: hidden auto;
  }
  .top {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .top h2 {
    margin: 0 8px 0 0;
  }
  .name {
    flex: 1;
    min-width: 160px;
  }
  .progress {
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 10px 12px;
    display: grid;
    gap: 8px;
  }
  .progress.running {
    border-color: var(--warn);
  }
  .progress-head {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .step {
    font-size: 13px;
  }
  .bar {
    height: 6px;
    background: var(--input);
    border-radius: 3px;
    overflow: hidden;
  }
  .exposure {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .exposure .bar {
    flex: 1;
    height: 10px;
  }
  .exposure .mono {
    min-width: 7em;
    max-width: 45%;
    text-align: right;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .bar.exposing div {
    background: var(--warn);
    transition: width 1s linear;
  }
  .bar.downloading div {
    animation: pulse 1s ease-in-out infinite alternate;
  }
  @keyframes pulse {
    from {
      opacity: 1;
    }
    to {
      opacity: 0.4;
    }
  }
  .bar div {
    height: 100%;
    background: var(--accent);
    transition: width 0.5s;
  }
  .facts {
    display: flex;
    gap: 14px;
    flex-wrap: wrap;
    font-size: 12px;
  }
  .bad {
    color: var(--danger);
  }
  .flip-due {
    color: var(--warn);
  }
  .editor {
    display: grid;
    gap: 14px;
  }
  fieldset {
    border: 0;
    padding: 0;
    margin: 0;
    display: grid;
    gap: 14px;
  }
  /* Locks its section while the sequence runs, without being a box of its own in the layout. */
  fieldset.lock {
    display: contents;
  }
  section h3 {
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
    margin: 0 0 8px;
    display: flex;
    align-items: center;
    gap: 10px;
  }
  .totals {
    text-transform: none;
    letter-spacing: 0;
    font-weight: normal;
  }
  .grid.target {
    display: grid;
    grid-template-columns: 1.4fr 1fr 1fr auto;
    gap: 8px;
    align-items: end;
  }
  label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  label input {
    color: var(--text);
  }
  label.check {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 13px;
    color: var(--text);
  }
  .night-end {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    margin-bottom: 8px;
  }
  .night-end input {
    width: 7em;
  }
  .checks {
    display: flex;
    flex-wrap: wrap;
    gap: 8px 18px;
  }
  .two {
    display: flex;
    flex-wrap: wrap;
    gap: 14px;
    align-items: start;
  }
  /* Dither, refocus and the meridian flip: a shaded box, so they read as one group of between-frames settings. */
  .group {
    flex: 3 1 520px;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
    gap: 14px;
    padding: 10px 12px;
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--text) 4%, transparent);
    border: 1px solid color-mix(in srgb, var(--text) 7%, transparent);
  }
  .end {
    flex: 1 1 200px;
    padding: 10px 0;
  }
  .two .checks {
    flex-direction: column;
  }
  .tiny {
    width: 64px;
  }
  .buttons {
    display: flex;
    gap: 6px;
  }
  .small {
    font-size: 12px;
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
  .targets {
    display: grid;
    gap: 10px;
  }
  .coords {
    margin-bottom: 8px;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 14px;
    margin-top: 8px;
  }
  .run {
    display: flex;
    align-items: center;
    gap: 10px;
  }
  .start {
    font-size: 15px;
    padding: 8px 18px;
  }
  .spacer {
    flex: 1;
  }
  .locked {
    opacity: 0.85;
  }
</style>
