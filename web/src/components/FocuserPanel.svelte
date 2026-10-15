<script lang="ts">
  import { ask } from '../lib/confirm.svelte'
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import AutofocusChart from './AutofocusChart.svelte'
  import DevicePicker from './DevicePicker.svelte'
  import FilterOffsets from './FilterOffsets.svelte'
  import type { AutofocusSettings } from '../lib/types'
  import Panel from './Panel.svelte'

  const focuser = $derived(nova.status?.focuser)
  const ready = $derived(focuser?.connected ?? false)
  const moving = $derived(focuser?.isMoving ?? false)
  const stepSizes = [10, 50, 100, 500, 1000]

  // Remember the chosen step size per browser; it depends on the user's optics.
  let step = $state(loadStep())
  let target = $state<number | null>(null)

  function loadStep(): number {
    try {
      const saved = Number(localStorage.getItem('nova.focusStep'))
      if (saved > 0) return saved
    } catch {
      // storage unavailable
    }
    return 100
  }

  function setStep(value: number) {
    step = value
    try {
      localStorage.setItem('nova.focusStep', String(value))
    } catch {
      // ignore
    }
  }

  const move = (direction: -1 | 1) => act(() => api.focuserStep(direction * step))

  // Named positions (e.g. with / without a reducer) to get roughly into focus after swapping optics.
  let presetName = $state('')
  async function savePreset() {
    if (!focuser || !presetName.trim()) return
    if (await act(() => api.saveFocusPreset(focuser!.name, presetName.trim()))) presetName = ''
  }
  async function updatePreset(name: string) {
    if (!focuser) return
    if (await ask(`Set '${name}' to the current position?`, { message: `The preset becomes ${focuser.position}.`, confirm: 'Set' }))
      act(() => api.saveFocusPreset(focuser!.name, name))
  }
  async function deletePreset(name: string) {
    if (focuser && (await ask(`Delete the focus preset '${name}'?`, { confirm: 'Delete', danger: true })))
      act(() => api.deleteFocusPreset(focuser!.name, name))
  }

  // Autofocus: V-curve run on the backend; settings live there too (they depend on the scope, not the browser).
  const af = $derived(nova.status?.autofocus)
  const run = $derived(af?.last ?? null)
  const afRunning = $derived(af?.running ?? false)
  const cameraReady = $derived(nova.status?.camera?.connected ?? false)
  let editing = $state(false)
  let draft = $state<AutofocusSettings>({ stepSize: 100, stepsPerSide: 4, exposureSeconds: 3, backlash: 0, approach: 'Inward' })
  function editSettings() {
    if (af) draft = { ...af.settings }
    editing = !editing
  }
  async function saveSettings() {
    if (await act(() => api.autofocusSettings({ ...draft }))) editing = false
  }
</script>

<Panel id="focuser" title="Focuser" kind="focuser">
  {#snippet badge()}
    {#if focuser}
      <span class="badge {moving ? 'warn' : ready ? 'ok' : 'muted'}">
        {moving ? 'Moving' : ready ? 'Ready' : 'Not connected'}
      </span>
    {/if}
  {/snippet}

  <DevicePicker kind="Focuser" disabled={moving} />

  {#if !focuser}
    <p class="muted">No focuser found. Is its driver in /etc/nova/indi.env?</p>
  {:else}
    <div class="readout mono">
      {#if focuser.canMoveAbsolute}
        <span class="position">{focuser.position.toLocaleString()}</span>
        <span class="muted">/ {focuser.maxPosition.toLocaleString()}</span>
      {:else}
        <span class="muted">Relative focuser (no position)</span>
      {/if}
      {#if focuser.temperature != null}<span class="muted temp">{focuser.temperature.toFixed(1)} °C</span>{/if}
    </div>

    <div class="row steps">
      <button disabled={!ready || moving} onclick={() => move(-1)} title="Move in by {step} steps">◀ In</button>
      <select
        value={step}
        onchange={(e) => setStep(Number(e.currentTarget.value))}
        aria-label="Step size"
        disabled={!ready}
      >
        {#each stepSizes as s (s)}<option value={s}>{s} steps</option>{/each}
      </select>
      <button disabled={!ready || moving} onclick={() => move(1)} title="Move out by {step} steps">Out ▶</button>
    </div>

    <div class="row">
      {#if focuser.canMoveAbsolute}
        <input
          class="mono target"
          type="number"
          min={focuser.minPosition}
          max={focuser.maxPosition}
          placeholder={String(focuser.position)}
          bind:value={target}
          disabled={!ready}
          aria-label="Target position"
          onkeydown={(e) => e.key === 'Enter' && target !== null && act(() => api.focuserMove(target!))}
        />
        <button
          class="secondary"
          disabled={!ready || moving || target === null}
          onclick={() => act(() => api.focuserMove(target!))}>Go to</button
        >
      {/if}
      <div class="spacer"></div>
      <button class="danger" disabled={!ready} onclick={() => act(api.focuserHalt)}>Halt</button>
    </div>

    {#if focuser.canMoveAbsolute}
      <div class="presets">
        {#each focuser.presets as p (p.name)}
          <div class="preset" class:here={p.position === focuser.position}>
            <span class="preset-name" title={p.name}>{p.name}</span>
            <span class="mono muted">{p.position.toLocaleString()}</span>
            <button
              class="secondary small"
              disabled={!ready || moving || p.position === focuser.position}
              title="Move the focuser to {p.position}"
              onclick={() => act(() => api.focuserMove(p.position))}>Go</button
            >
            <button
              class="secondary small icon"
              disabled={!ready || moving || p.position === focuser.position}
              title="Set this preset to the current position (e.g. after autofocus)"
              onclick={() => updatePreset(p.name)}>⟳</button
            >
            <button class="secondary small icon" title="Delete this preset" onclick={() => deletePreset(p.name)}>✕</button>
          </div>
        {/each}
        <div class="row">
          <input
            class="preset-input"
            placeholder="Name, e.g. Askar 71F + reducer"
            maxlength="60"
            bind:value={presetName}
            disabled={!ready}
            aria-label="Preset name"
            onkeydown={(e) => e.key === 'Enter' && savePreset()}
          />
          <button
            class="secondary"
            disabled={!ready || moving || !presetName.trim()}
            title="Remember the current position ({focuser.position}) under this name"
            onclick={savePreset}>Save position</button
          >
        </div>
      </div>
    {/if}

    {#if focuser.canMoveAbsolute && af}
      <div class="autofocus">
        <div class="row">
          {#if afRunning}
            <button class="danger" onclick={() => act(api.autofocusCancel)}>Cancel autofocus</button>
          {:else}
            <button
              disabled={!ready || moving || !cameraReady}
              title={cameraReady
                ? `Measure HFR at ${2 * af.settings.stepsPerSide + 1} positions ${af.settings.stepSize} steps apart and move to the best`
                : 'Connect a camera first'}
              onclick={() => act(api.autofocus)}>Autofocus</button
            >
          {/if}
          <button
            class="secondary gear"
            disabled={afRunning}
            aria-expanded={editing}
            title="Autofocus settings"
            onclick={editSettings}>⚙</button
          >
          <span class="muted summary">
            {af.settings.stepSize} steps × {af.settings.stepsPerSide} each side, {af.settings.exposureSeconds} s exposures{af.settings.backlash
              ? `, backlash ${af.settings.backlash} (${af.settings.approach.toLowerCase()})`
              : ''}
          </span>
        </div>

        {#if editing}
          <div class="af-settings">
            <label title="Focuser steps between measurements: the outer points should be clearly blurred">
              Step size
              <input type="number" min="1" bind:value={draft.stepSize} />
            </label>
            <label title="Measurements either side of the current position">
              Steps each side
              <input type="number" min="2" max="10" bind:value={draft.stepsPerSide} />
            </label>
            <label title="Exposure per measurement">
              Exposure (s)
              <input type="number" min="0" step="0.5" bind:value={draft.exposureSeconds} />
            </label>
            <label title="Every focuser move (autofocus, filter offsets, In/Out, Go to) arrives from the same direction: one that would arrive the other way goes this many steps past and comes back, taking up the gears' slack. 0 = off (the EAF rarely needs it)">
              Backlash (all moves)
              <input type="number" min="0" bind:value={draft.backlash} />
            </label>
            <label title="The direction every move finishes in: inward usually; outward if the focuser's load (a heavy camera, the scope pointing high) pushes it in">
              Final approach
              <select bind:value={draft.approach} disabled={!draft.backlash}>
                <option value="Inward">Inward</option>
                <option value="Outward">Outward</option>
              </select>
            </label>
          </div>
          <div class="row">
            <div class="spacer"></div>
            <button class="secondary" onclick={() => (editing = false)}>Cancel</button>
            <button onclick={saveSettings}>Save</button>
          </div>
        {/if}

        {#if run}
          <p class="af-message" class:ok={run.succeeded === true} class:bad={run.succeeded === false}>
            {run.running ? '⏳ ' : run.succeeded ? '✓ ' : '✗ '}{run.message}
          </p>
          <AutofocusChart points={run.points} fit={run.fit} best={run.bestPosition} />
          {#if run.fit}
            <p class="muted fit">
              {#if run.startHfr != null}Started at {run.startPosition} (HFR {run.startHfr.toFixed(2)}).{/if}
              Curve minimum at {run.fit.bestPosition}, HFR {run.fit.bestHfr.toFixed(2)} px (fit R² {run.fit.rSquared.toFixed(3)}){#if run.temperature != null || run.filter}, with
                {[run.temperature != null ? `${run.temperature.toFixed(1)} °C` : null, run.filter ? `the ${run.filter} filter` : null].filter(Boolean).join(' and ')}{/if}.
            </p>
          {/if}
        {/if}
        {#if focuser?.canMoveAbsolute && nova.status?.filterWheel}<FilterOffsets focuser={focuser} />{/if}
      </div>
    {/if}
  {/if}
</Panel>

<style>
  .readout {
    display: flex;
    align-items: baseline;
    gap: 8px;
    margin-bottom: 10px;
  }
  .position {
    font-size: 22px;
  }
  .temp {
    margin-left: auto;
  }
  .steps {
    margin-bottom: 8px;
  }
  .steps button {
    flex: 1;
  }
  .steps select {
    flex: 1;
    min-width: 0;
  }
  .target {
    width: 110px;
  }
  .spacer {
    flex: 1;
  }
  .presets {
    margin-top: 12px;
    padding-top: 10px;
    border-top: 1px solid var(--panel-border);
    display: grid;
    gap: 6px;
  }
  .preset {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 2px 4px;
    border-radius: 4px;
    font-size: 13px;
  }
  .preset.here {
    background: var(--input);
    box-shadow: inset 2px 0 0 var(--kind-focuser);
  }
  .preset-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .small {
    font-size: 12px;
    padding: 2px 8px;
  }
  .icon {
    padding: 2px 6px;
  }
  .preset-input {
    flex: 1;
    min-width: 0;
  }
  .autofocus {
    margin-top: 12px;
    padding-top: 10px;
    border-top: 1px solid var(--panel-border);
    display: grid;
    gap: 8px;
  }
  .gear {
    padding: 4px 8px;
  }
  .summary {
    font-size: 11px;
  }
  .af-settings {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
  }
  .af-settings label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  .af-settings input {
    color: var(--text);
    min-width: 0;
  }
  .af-message {
    margin: 0;
    font-size: 13px;
  }
  .af-message.ok {
    color: var(--ok);
  }
  .af-message.bad {
    color: var(--danger);
  }
  .fit {
    margin: 0;
    font-size: 11px;
  }
</style>
