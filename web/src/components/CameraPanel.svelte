<script lang="ts">
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import { abortExposure, expose, exposure as settings, looping } from '../lib/exposure.svelte'
  import type { CameraSettingsUpdate, FrameType } from '../lib/types'
  import DevicePicker from './DevicePicker.svelte'
  import OpticsEditor from './OpticsEditor.svelte'
  import StorageEditor from './StorageEditor.svelte'
  import Panel from './Panel.svelte'

  const camera = $derived(nova.status?.camera)
  const controls = $derived(camera?.controls ?? null)
  const cooler = $derived(controls?.cooler ?? null)
  const exposure = $derived(nova.exposure)
  const ready = $derived(camera?.connected ?? false)
  const busy = $derived(exposure !== null)
  const percent = $derived(
    exposure ? (exposure.totalSeconds > 0 ? (100 * exposure.elapsedSeconds) / exposure.totalSeconds : 100) : 0,
  )
  const progressText = $derived.by(() => {
    if (!exposure) return 'Idle'
    if (exposure.state === 'Downloading') return 'Downloading…'
    return `${exposure.elapsedSeconds.toFixed(1)} / ${exposure.totalSeconds}s`
  })
  const binOptions = $derived(
    controls?.binning ? Array.from({ length: controls.binning.max - controls.binning.min + 1 }, (_, i) => controls.binning!.min + i) : [],
  )
  const frameTypes: FrameType[] = ['Light', 'Dark', 'Flat', 'Bias']

  let target = $state<number | null>(null)

  // Fill the target field once: what the camera was last asked for, else -10 °C. Then leave it to the user.
  $effect(() => {
    if (target === null && cooler) target = cooler.targetTemperature ?? -10
  })

  // Loop mode: start the next exposure as soon as a new frame arrives. Handy for framing and focusing. The settings
  // are shared with the Image tab's Expose button (lib/exposure), so a loop started there carries on here too.
  $effect(() => {
    const id = nova.image?.id
    if (id === undefined || id === looping.lastImageId) return
    const first = looping.lastImageId === undefined
    looping.lastImageId = id
    if (settings.loop && !first && ready) act(() => api.expose(settings.duration, settings.save))
  })

  const apply = (update: CameraSettingsUpdate) => act(() => api.cameraSettings(update))

  function applyNumber(event: Event, key: 'gain' | 'offset') {
    const value = (event.currentTarget as HTMLInputElement).valueAsNumber
    if (!Number.isNaN(value)) apply({ [key]: value })
  }

  // The gain and offset NOVA sets whenever this camera connects (drivers start with their own otherwise).
  const defaults = $derived(nova.status?.optics?.find((o) => o.camera === camera?.name) ?? null)
  const hasDefaults = $derived(defaults?.defaultGain != null || defaults?.defaultOffset != null)
  const matchesDefaults = $derived(
    hasDefaults && controls?.gain?.value === defaults?.defaultGain && (controls?.offset == null || controls.offset.value === defaults?.defaultOffset),
  )
  function makeDefault() {
    if (camera) act(() => api.cameraDefaults(camera.name, controls?.gain?.value ?? null, controls?.offset?.value ?? null))
  }
  function clearDefault() {
    if (camera) act(() => api.cameraDefaults(camera.name, null, null))
  }

  const temp = (t: number | null | undefined) => (t == null ? '—' : `${t.toFixed(1)} °C`)
</script>

<Panel id="camera" title="Camera" kind="camera">
  {#snippet badge()}
    {#if camera}
      <span class="badge {busy ? 'warn' : camera.state === 'Error' ? 'danger' : 'muted'}">
        {exposure?.state ?? camera.state}
      </span>
    {/if}
  {/snippet}

  <DevicePicker kind="Camera" disabled={busy} />

  {#if !camera}
    <p class="muted">No camera.</p>
    <!-- Storage stays reachable without a camera: copying a night's frames to a USB drive needs none. -->
    <StorageEditor />
  {:else}
    {#if controls}
      <div class="settings">
        {#if controls.frameType}
          <label>
            Frame
            <select
              value={controls.frameType}
              disabled={busy}
              onchange={(e) => apply({ frameType: e.currentTarget.value as FrameType })}
            >
              {#each frameTypes as f (f)}<option value={f}>{f}</option>{/each}
            </select>
          </label>
        {/if}
        {#if controls.binning && binOptions.length > 1}
          <label>
            Binning
            <select
              value={controls.binning.value}
              disabled={busy}
              onchange={(e) => apply({ binning: Number(e.currentTarget.value) })}
            >
              {#each binOptions as b (b)}<option value={b}>{b}×{b}</option>{/each}
            </select>
          </label>
        {/if}
        {#if controls.gain}
          <label>
            Gain
            <input
              type="number"
              value={controls.gain.value}
              min={controls.gain.min}
              max={controls.gain.max}
              step={controls.gain.step || 1}
              disabled={busy}
              onchange={(e) => applyNumber(e, 'gain')}
            />
          </label>
        {/if}
        {#if controls.offset}
          <label>
            Offset
            <input
              type="number"
              value={controls.offset.value}
              min={controls.offset.min}
              max={controls.offset.max}
              step={controls.offset.step || 1}
              disabled={busy}
              onchange={(e) => applyNumber(e, 'offset')}
            />
          </label>
        {/if}
      </div>
      {#if controls.gain}
        <div class="defaults muted">
          {#if hasDefaults}
            <span title={defaults?.builtInDefaults ? "NOVA's default for ZWO cameras (the driver's own is 200): low-noise mode at about unity gain" : 'Set every time this camera connects'}>
              On connect: gain {defaults?.defaultGain ?? '—'}, offset {defaults?.defaultOffset ?? '—'}{defaults?.builtInDefaults ? ' (ZWO default)' : ''}
            </span>
            {#if !matchesDefaults}<button class="link" onclick={makeDefault}>Use current</button>{/if}
            {#if !defaults?.builtInDefaults}<button class="link" onclick={clearDefault} title="Back to the driver's own gain and offset (or NOVA's, for a ZWO camera)">Clear</button>{/if}
          {:else}
            <span>Gain and offset on connect: the driver's</span>
            <button class="link" onclick={makeDefault} title="Set this gain and offset every time this camera connects">Make these the default</button>
          {/if}
        </div>
      {/if}

      {#if cooler}
        <div class="cooling">
          <div class="readout mono">
            <span class="now">{temp(cooler.temperature)}</span>
            {#if cooler.canCool}
              {#if cooler.on && cooler.targetTemperature != null}<span class="muted">→ {temp(cooler.targetTemperature)}</span>{/if}
              {#if cooler.powerPercent != null}<span class="muted" title="Cooler power">({Math.round(cooler.powerPercent)}% power)</span>{/if}
            {/if}
          </div>
          {#if cooler.canCool}
            <div class="row">
              <input class="target" type="number" step="1" bind:value={target} aria-label="Target temperature (°C)" />
              <span class="muted">°C</span>
              <button class="secondary" disabled={target === null} onclick={() => apply({ targetTemperature: target! })}>
                Set
              </button>
              <div class="spacer"></div>
              <label class="switch" title={cooler.on ? 'The cooler is on: switch it off' : 'The cooler is off: switch it on'}>
                <input type="checkbox" role="switch" checked={cooler.on} onchange={(e) => apply({ coolerOn: e.currentTarget.checked })} />
                <span class="track" aria-hidden="true"><span class="knob"></span></span>
                Cooler
              </label>
            </div>
          {/if}
        </div>
      {/if}

      {#if controls?.dewHeater != null}
        <div class="row">
          <div class="spacer"></div>
          <label
            class="switch"
            title={controls.dewHeater ? 'The dew heater around the window is on: switch it off' : 'Warm the window to keep dew and frost off it'}
          >
            <input type="checkbox" role="switch" checked={controls.dewHeater} onchange={(e) => apply({ dewHeater: e.currentTarget.checked })} />
            <span class="track" aria-hidden="true"><span class="knob"></span></span>
            Dew heater
          </label>
        </div>
      {/if}
    {:else if !ready}
      <p class="muted">Connect to see camera settings.</p>
    {/if}

    <OpticsEditor defaultCamera={camera.name} binning={controls?.binning?.value ?? 1} />
    <StorageEditor />

    <div class="row exposure">
      <label>
        Exposure (s)
        <input type="number" min="0" max="3600" step="0.5" bind:value={settings.duration} disabled={!ready} />
      </label>
      <label class="check"><input type="checkbox" bind:checked={settings.save} /> Save FITS</label>
      <label class="check"><input type="checkbox" bind:checked={settings.loop} /> Loop</label>
    </div>

    <div class="progress" aria-label="Exposure progress">
      <div class="bar" style:width="{percent}%"></div>
      <span class="mono">{progressText}</span>
    </div>

    <div class="row">
      <button disabled={!ready || busy} onclick={expose}>Expose</button>
      <button class="danger" disabled={!busy} onclick={abortExposure}>Abort</button>
    </div>
  {/if}
</Panel>

<style>
  .defaults {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 10px;
    align-items: baseline;
    font-size: 12px;
    margin: -2px 0 8px;
  }
  .link {
    all: unset;
    cursor: pointer;
    text-decoration: underline;
    color: var(--text);
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
  .settings {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(90px, 1fr));
    gap: 8px;
    margin-bottom: 10px;
  }
  .settings input,
  .settings select {
    width: 100%;
  }
  .cooling {
    border-top: 1px solid var(--panel-border);
    border-bottom: 1px solid var(--panel-border);
    padding: 8px 0;
    margin-bottom: 10px;
    display: grid;
    gap: 6px;
  }
  .readout {
    display: flex;
    gap: 8px;
    align-items: baseline;
  }
  .now {
    font-size: 18px;
  }
  .target {
    width: 70px;
  }
  .spacer {
    flex: 1;
  }
  .exposure input[type='number'] {
    width: 90px;
  }
  label.check {
    display: flex;
    align-items: center;
    gap: 4px;
    align-self: end;
    padding-bottom: 6px;
  }
  .progress {
    position: relative;
    height: 22px;
    margin: 10px 0;
    border-radius: var(--radius);
    background: var(--input);
    border: 1px solid var(--panel-border);
    overflow: hidden;
  }
  .bar {
    position: absolute;
    inset: 0 auto 0 0;
    background: var(--accent);
    opacity: 0.35;
    transition: width 0.2s linear;
  }
  .progress span {
    position: relative;
    display: block;
    text-align: center;
    font-size: 12px;
    line-height: 20px;
  }
  .switch {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
    font-size: 13px;
  }
  .switch input {
    position: absolute;
    opacity: 0;
    width: 1px;
    height: 1px;
  }
  .track {
    width: 32px;
    height: 18px;
    border-radius: 999px;
    background: var(--input);
    border: 1px solid var(--panel-border);
    position: relative;
    transition: background 0.15s;
  }
  .knob {
    position: absolute;
    top: 2px;
    left: 2px;
    width: 12px;
    height: 12px;
    border-radius: 50%;
    background: var(--muted);
    transition: transform 0.15s, background 0.15s;
  }
  .switch input:checked + .track {
    background: color-mix(in srgb, var(--accent) 35%, var(--input));
  }
  .switch input:checked + .track .knob {
    transform: translateX(14px);
    background: var(--accent);
  }
  .switch input:focus-visible + .track {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
</style>
