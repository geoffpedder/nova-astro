<script lang="ts">
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import { guideView } from '../lib/guideView.svelte'
  import { ask } from '../lib/confirm.svelte'
  import CalibrationGraph from './CalibrationGraph.svelte'

  let showGraph = $state(false)
  import type { GuideAlgorithmSettings, NovaGuiderSettings } from '../lib/types'

  // NOVA's own guider: guide camera and output, the guide star view, Loop / Calibrate, the calibration and settings.
  // Guide, Stop, Dither, the graph and RMS are the Guider panel's own (they work the same for both guiders).
  const guider = $derived(nova.status?.guider)
  const n = $derived(guider?.nova)
  const s = $derived(n?.settings)
  const running = $derived(guider ? ['Guiding', 'Calibrating', 'Looping', 'LostLock'].includes(guider.state) : false)
  const calibration = $derived(n?.calibration)

  function save(patch: Partial<NovaGuiderSettings>) {
    if (!s) return
    act(() => api.novaGuiderSettings({ ...s, ...patch }))
  }
  function saveAlgorithms(patch: Partial<GuideAlgorithmSettings>) {
    if (!s) return
    save({ algorithms: { ...s.algorithms, ...patch } })
  }
  const num = (e: Event) => Number((e.currentTarget as HTMLInputElement).value)

  // The guide frame, swapped in only once the next has loaded, so the view doesn't flash blank every frame.
  let shown = $state<string | null>(null)
  $effect(() => {
    const frame = n?.frame ?? 0
    if (!frame) return
    const url = api.guideFrameUrl(frame, guideView.stretch)
    const img = new Image()
    img.onload = () => (shown = url)
    img.src = url
  })

  // Zoomed on the lock position (64 px square) once there's a star; the whole frame until then.
  const view = $derived.by(() => {
    const w = n?.frameWidth
    const h = n?.frameHeight
    if (!w || !h) return null
    const star = n?.star
    if (!star) return { box: `0 0 ${w} ${h}`, w, h, star: null }
    const size = 64
    return { box: `${star.lockX - size / 2} ${star.lockY - size / 2} ${size} ${size}`, w, h, star }
  })
  const lost = $derived(guider?.state === 'LostLock')

  // Dark library: which exposures to take (highlighted) and how many each. Until the user picks: all of them for a
  // first library, or the ones the library already has (taking darks again renews them), plus the guide exposure.
  const darkOptions = [0.5, 1, 1.5, 2, 2.5, 3, 4, 5]
  let darkPicked = $state<number[] | null>(null)
  const darkExposures = $derived(
    darkPicked ??
      (n?.darks
        ? [...new Set([...n.darks.exposures, s?.exposureSeconds ?? 2])].sort((a, b) => a - b)
        : [...new Set([...darkOptions, s?.exposureSeconds ?? 2])].sort((a, b) => a - b)),
  )
  let darkFrames = $state(5)
  // Opens by itself once, when the guide camera is connected and has no library yet (darks need the camera); after
  // that it stays as the user leaves it (status updates many times a second, so it mustn't be tied to the status).
  let darksOpen = $state(false)
  let darksOpenDecided = false
  $effect(() => {
    if (darksOpenDecided || !n?.cameraConnected) return
    darksOpenDecided = true
    darksOpen = !n.darks
  })

  // Profiles: pick one (the user's own, or a built-in preset), save the current settings under a name, delete.
  let profileName = $state('')
  let savingProfile = $state(false)
  const profile = $derived(n?.profile ?? null)
  const mineSelected = $derived(!!n?.profiles.find((p) => p.name === profile && !p.builtIn))
  async function saveProfile() {
    const name = profileName.trim()
    if (!name) return
    if (n?.profiles.some((p) => p.name.toLowerCase() === name.toLowerCase() && !p.builtIn) &&
        !(await ask(`Replace the profile '${name}'?`, { confirm: 'Replace' }))) return
    if (await act(() => api.guideProfileSave(name))) {
      savingProfile = false
      profileName = ''
    }
  }
  async function deleteProfile() {
    if (profile && (await ask(`Delete the profile '${profile}'?`, { confirm: 'Delete', danger: true }))) act(() => api.guideProfileDelete(profile))
  }
  const busyDarks = $derived(!!n?.darkProgress)
  function toggleDark(e: number) {
    darkPicked = darkExposures.includes(e) ? darkExposures.filter((x) => x !== e) : [...darkExposures, e].sort((a, b) => a - b)
  }
  async function buildDarks() {
    const total = Math.round(darkExposures.reduce((sum, e) => sum + e, 0) * darkFrames)
    const ok = await ask('Cover the guide scope', {
      message: `NOVA takes ${darkFrames} darks at each of ${darkExposures.join(', ')} s (about ${total} s of exposures). ` +
        'Put the cap on the guide scope, or cover it, first: any light spoils them.',
      confirm: 'Take darks',
    })
    if (ok) act(() => api.guideDarks(darkExposures, darkFrames))
  }

  const when = (utc: string) => new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  const rate = (px: number, arcsec: number | null) => (arcsec != null ? `${arcsec.toFixed(1)}″/s` : `${px.toFixed(1)} px/s`)
</script>

{#if n && s}
  <div class="profiles">
    <select value={profile ?? ''} onchange={(e) => e.currentTarget.value && act(() => api.guideProfileLoad(e.currentTarget.value))}
      aria-label="Guide profile" title={n.profiles.find((p) => p.name === profile)?.description ?? 'Guide settings profile'}>
      {#if !profile}<option value="">Custom (not saved)</option>{/if}
      {#if n.profiles.some((p) => !p.builtIn)}
        <optgroup label="Saved">
          {#each n.profiles.filter((p) => !p.builtIn) as p (p.name)}<option value={p.name}>{p.name}</option>{/each}
        </optgroup>
      {/if}
      <optgroup label="Built in">
        {#each n.profiles.filter((p) => p.builtIn) as p (p.name)}<option value={p.name} title={p.description}>{p.name}</option>{/each}
      </optgroup>
    </select>
    {#if savingProfile}
      <input class="name" bind:value={profileName} placeholder="Profile name" aria-label="Profile name" maxlength="40"
        onkeydown={(e) => e.key === 'Enter' && saveProfile()} />
      <button class="secondary small-btn" disabled={!profileName.trim()} onclick={saveProfile}>Save</button>
      <button class="secondary small-btn" onclick={() => (savingProfile = false)}>Cancel</button>
    {:else}
      <button class="secondary small-btn" onclick={() => { profileName = mineSelected ? (profile ?? '') : ''; savingProfile = true }}
        title="Save the current guide settings (camera, exposure, gain, algorithms…) as a profile">Save as…</button>
      {#if mineSelected}<button class="secondary small-btn" onclick={deleteProfile} title="Delete this saved profile">Delete</button>{/if}
    {/if}
  </div>
  <div class="grid">
    <label for="ng-camera">Camera</label>
    <select id="ng-camera" value={s.camera ?? ''} disabled={running} onchange={(e) => save({ camera: e.currentTarget.value || null })}>
      <option value="">Automatic{n.camera && !s.camera ? ` (${n.camera})` : ''}</option>
      {#each n.cameras as c (c)}<option value={c}>{c}</option>{/each}
    </select>
    <label for="ng-output">Pulses to</label>
    <select id="ng-output" value={s.output ?? ''} disabled={running} onchange={(e) => save({ output: e.currentTarget.value || null })}
      title="Where guide corrections go: the mount (pulse guiding), or a guide camera's ST4 port">
      {#each n.outputs as o (o.name ?? '')}<option value={o.name ?? ''}>{o.label}</option>{/each}
      {#if !n.outputs.length}<option value="">Mount (not connected)</option>{/if}
    </select>
    <label for="ng-exposure">Exposure</label>
    <div class="pair">
      <input id="ng-exposure" class="mono" type="number" min="0.05" max="30" step="0.5" value={s.exposureSeconds}
        onchange={(e) => save({ exposureSeconds: num(e) })} /> <span class="muted">s</span>
      <select value={s.binning} disabled={running} onchange={(e) => save({ binning: num(e) })} aria-label="Guide camera binning">
        {#each [1, 2, 3, 4] as b (b)}<option value={b}>{b}×{b}</option>{/each}
      </select>
      <label class="small inline" title="Choose the exposure by itself, to keep the guide star near the target SNR (longer when it fades or is lost)">
        <input type="checkbox" checked={s.autoExposure} onchange={(e) => save({ autoExposure: e.currentTarget.checked })} /> Auto
      </label>
    </div>
    {#if s.autoExposure}
      <span class="muted small-label">Auto</span>
      <div class="pair">
        <input class="mono short" type="number" min="0.05" max="30" step="0.5" value={s.minExposureSeconds} aria-label="Shortest exposure"
          onchange={(e) => save({ minExposureSeconds: num(e) })} />
        <span class="muted">to</span>
        <input class="mono short" type="number" min="0.05" max="30" step="0.5" value={s.maxExposureSeconds} aria-label="Longest exposure"
          onchange={(e) => save({ maxExposureSeconds: num(e) })} />
        <span class="muted">s, SNR</span>
        <input class="mono short" type="number" min="3" max="200" step="5" value={s.targetSnr} aria-label="Target SNR"
          onchange={(e) => save({ targetSnr: num(e) })} title="The guide star's SNR to aim for" />
        {#if n.exposure != null && running}<span class="muted small-label">now {n.exposure} s</span>{/if}
      </div>
    {/if}
    <label for="ng-gain">Gain</label>
    <div class="pair">
      <input id="ng-gain" class="mono" type="number" step="1" min={n.cameraGain?.min} max={n.cameraGain?.max}
        value={s.gain ?? ''} placeholder={n.cameraGain ? String(Math.round(n.cameraGain.value)) : 'driver'}
        onchange={(e) => save({ gain: e.currentTarget.value === '' ? null : num(e) })}
        title="Set before every guide frame (and for darks). Empty: leave it as the driver has it" />
      <span class="muted small">{#if n.cameraGain}{Math.round(n.cameraGain.min)}–{Math.round(n.cameraGain.max)}{#if s.gain == null}, as the driver has it{/if}{:else}connect to see its range{/if}</span>
    </div>
  </div>

  {#if view}
    <svg class="star" class:lost viewBox={view.box} role="img" aria-label="Guide star">
      {#if shown}<image class="frame" href={shown} x="0" y="0" width={view.w} height={view.h} preserveAspectRatio="none" />{/if}
      {#if view.star}
        <line class="cross" x1={view.star.lockX - 40} y1={view.star.lockY} x2={view.star.lockX + 40} y2={view.star.lockY} />
        <line class="cross" x1={view.star.lockX} y1={view.star.lockY - 40} x2={view.star.lockX} y2={view.star.lockY + 40} />
        <circle class="ring" cx={view.star.x} cy={view.star.y} r="5" />
      {/if}
    </svg>
  {/if}
  <p class="activity muted">
    {n.darkProgress ? `Taking darks: ${n.darkProgress}` : (n.activity ?? 'Stopped')}{n.star ? `, ${n.star.found} of ${n.star.tracked} stars` : ''}
  </p>

  <div class="row">
    <button class="secondary" disabled={guider?.state === 'Looping' || busyDarks} onclick={() => act(api.guiderLoop)}
      title="Expose the guide camera in a loop and pick a guide star, without correcting">Loop</button>
    <button class="secondary" disabled={guider?.state === 'Calibrating' || busyDarks} onclick={() => act(api.guiderCalibrate)}
      title="Measure how guide pulses move the star (near the meridian and Dec 0 is best)">Calibrate</button>
  </div>

  {#if calibration}
    <div class="calibration mono">
      <span class="muted">Calibrated {when(calibration.utc)}, Dec {calibration.decDegrees.toFixed(0)}°{calibration.pierSide ? `, pier ${calibration.pierSide.toLowerCase()}` : ''}</span>
      <span>RA {calibration.raAngle.toFixed(0)}° at {rate(calibration.raRate, calibration.raArcsecPerSecond)}</span>
      <span>Dec {calibration.decAngle.toFixed(0)}° at {rate(calibration.decRate, calibration.decArcsecPerSecond)}</span>
      <span class:warn={calibration.orthogonality > 10}>{calibration.orthogonality.toFixed(1)}° from square</span>
      {#if calibration.cameraTurn != null && Math.abs(calibration.cameraTurn) >= 0.5}
        <span title="An off-axis guider: the calibration turns with the main camera">turned {calibration.cameraTurn.toFixed(0)}° with the camera</span>
      {/if}
      <button class="secondary clear" onclick={() => (showGraph = !showGraph)} aria-pressed={showGraph}
        title="Where the star went during calibration, as PHD2 shows it">{showGraph ? 'Hide graph' : 'Graph'}</button>
      <button class="secondary clear" onclick={() => act(api.guiderClearCalibration)} title="Forget it: the next Guide calibrates again">Clear</button>
    </div>
    {#if showGraph}<div class="graph"><CalibrationGraph {calibration} /></div>{/if}
    {#if calibration.warning}<p class="warn small">{calibration.warning}</p>{/if}
  {:else}
    <p class="muted small">Not calibrated yet: Guide calibrates first (or press Calibrate).</p>
  {/if}

  <details class="settings" bind:open={darksOpen}>
    <summary>Dark library{n.darks ? `: ${n.darks.exposures.join(', ')} s` : ''}</summary>
    <p class="small">
      {#if n.darks}
        <span class="mono">{n.darks.exposures.join(', ')} s, {n.darks.frames} each, {when(n.darks.utc)}{n.darks.gain != null ? `, gain ${n.darks.gain}` : ''}, {n.darks.hotPixels} hot pixels</span>
        {#if !n.darks.gainMatches}<br /><span class="warn">Taken at another gain: not used until you take them again.</span>{/if}
      {:else}
        <span class="muted">None yet for {n.camera ?? 'the guide camera'}{s.binning > 1 ? ` at ${s.binning}×${s.binning}` : ''}: hot pixels can be taken for stars.
          Take darks once (scope covered); they're subtracted from every guide frame.</span>
      {/if}
    </p>
    <div class="chips" role="group" aria-label="Dark exposures">
      {#each darkOptions as e (e)}
        <button class="chip" class:on={darkExposures.includes(e)} onclick={() => toggleDark(e)}>{e} s</button>
      {/each}
    </div>
    <div class="row darks-row">
      <input class="mono frames" type="number" min="1" max="50" bind:value={darkFrames} aria-label="Darks per exposure" />
      <span class="muted small">each</span>
      <button class="secondary" disabled={running || busyDarks || !darkExposures.length} onclick={buildDarks}
        title={running ? 'Stop the guide camera first' : 'Take the darks (with the guide scope covered)'}>Take darks</button>
      <span class="spacer"></span>
      <label class="small"><input type="checkbox" checked={s.useDarks} onchange={(e) => save({ useDarks: e.currentTarget.checked })} /> Use</label>
      <button class="secondary clear" disabled={!n.darks || busyDarks} onclick={() => act(api.clearGuideDarks)}>Clear</button>
    </div>
    {#if running && !busyDarks}<p class="muted small">Stop the guide camera (Stop) to take darks.</p>{/if}
  </details>

  <details class="settings">
    <summary>Guiding settings</summary>
    <div class="grid">
      <span class="head">RA</span><span></span>
      <label for="ng-ra-alg">Algorithm</label>
      <select id="ng-ra-alg" value={s.algorithms.raAlgorithm}
        onchange={(e) => saveAlgorithms({ raAlgorithm: e.currentTarget.value as GuideAlgorithmSettings['raAlgorithm'] })}
        title="Predictive PEC learns the mount's periodic error while guiding and corrects it ahead of time">
        <option value="Hysteresis">Hysteresis</option>
        <option value="PredictivePec">Predictive PEC</option>
      </select>
      {#if s.algorithms.raAlgorithm === 'PredictivePec'}
        <label for="ng-pec-period">PE period</label>
        <div class="pair"><input id="ng-pec-period" class="mono" type="number" min="0" max="1500" step="1" value={s.algorithms.pecPeriodSeconds}
          onchange={(e) => saveAlgorithms({ pecPeriodSeconds: num(e) })} title="0: found while guiding (the guiding assistant reports it too)" /> <span class="muted">s (0 = find)</span></div>
        <label for="ng-pec-gain">Prediction gain</label>
        <div class="pair"><input id="ng-pec-gain" class="mono" type="number" min="0" max="150" step="5" value={Math.round(s.algorithms.pecPredictionGain * 100)}
          onchange={(e) => saveAlgorithms({ pecPredictionGain: num(e) / 100 })} title="How much of the predicted periodic error to correct ahead of time" /> <span class="muted">%</span></div>
        <label for="ng-pec-react">Reactive</label>
        <div class="pair"><input id="ng-pec-react" class="mono" type="number" min="0" max="200" step="5" value={Math.round(s.algorithms.pecReactiveAggressiveness * 100)}
          onchange={(e) => saveAlgorithms({ pecReactiveAggressiveness: num(e) / 100 })} title="The aggressiveness of the reactive part while predicting (the next line is used while it learns)" /> <span class="muted">%</span></div>
      {/if}
      <label for="ng-ra-agg">Aggressiveness</label>
      <div class="pair"><input id="ng-ra-agg" class="mono" type="number" min="0" max="200" step="5" value={Math.round(s.algorithms.raAggressiveness * 100)}
        onchange={(e) => saveAlgorithms({ raAggressiveness: num(e) / 100 })} /> <span class="muted">%</span></div>
      <label for="ng-ra-hys">Hysteresis</label>
      <div class="pair"><input id="ng-ra-hys" class="mono" type="number" min="0" max="90" step="5" value={Math.round(s.algorithms.raHysteresis * 100)}
        onchange={(e) => saveAlgorithms({ raHysteresis: num(e) / 100 })} /> <span class="muted">%</span></div>
      <label for="ng-ra-min">Min move</label>
      <div class="pair"><input id="ng-ra-min" class="mono" type="number" min="0" max="5" step="0.05" value={s.algorithms.raMinMove}
        onchange={(e) => saveAlgorithms({ raMinMove: num(e) })} /> <span class="muted">px</span></div>
      <label for="ng-ra-max">Max pulse</label>
      <div class="pair"><input id="ng-ra-max" class="mono" type="number" min="50" max="10000" step="100" value={s.algorithms.maxRaPulseMs}
        onchange={(e) => saveAlgorithms({ maxRaPulseMs: num(e) })} /> <span class="muted">ms</span></div>

      <span class="head">Dec</span><span></span>
      <label for="ng-dec-alg">Algorithm</label>
      <select id="ng-dec-alg" value={s.algorithms.decAlgorithm ?? 'ResistSwitch'}
        onchange={(e) => saveAlgorithms({ decAlgorithm: e.currentTarget.value as GuideAlgorithmSettings['decAlgorithm'] })}
        title="Resist Switch is slow to reverse (Dec mostly drifts one way); Lowpass2 follows a line fitted through the recent errors, smoothing out seeing">
        <option value="ResistSwitch">Resist switch</option>
        <option value="Lowpass2">Lowpass2</option>
      </select>
      <label for="ng-dec-mode">Corrections</label>
      <select id="ng-dec-mode" value={s.algorithms.decMode} onchange={(e) => saveAlgorithms({ decMode: e.currentTarget.value as GuideAlgorithmSettings['decMode'] })}>
        <option value="Auto">Both ways</option>
        <option value="North">North only</option>
        <option value="South">South only</option>
        <option value="Off">Off</option>
      </select>
      <label for="ng-dec-agg">Aggressiveness</label>
      <div class="pair"><input id="ng-dec-agg" class="mono" type="number" min="0" max="200" step="5" value={Math.round(s.algorithms.decAggressiveness * 100)}
        onchange={(e) => saveAlgorithms({ decAggressiveness: num(e) / 100 })} /> <span class="muted">%</span></div>
      <label for="ng-dec-min">Min move</label>
      <div class="pair"><input id="ng-dec-min" class="mono" type="number" min="0" max="5" step="0.05" value={s.algorithms.decMinMove}
        onchange={(e) => saveAlgorithms({ decMinMove: num(e) })} /> <span class="muted">px</span></div>
      <label for="ng-dec-max">Max pulse</label>
      <div class="pair"><input id="ng-dec-max" class="mono" type="number" min="50" max="10000" step="100" value={s.algorithms.maxDecPulseMs}
        onchange={(e) => saveAlgorithms({ maxDecPulseMs: num(e) })} /> <span class="muted">ms</span></div>
      {#if s.algorithms.decAlgorithm !== 'Lowpass2'}
        <label for="ng-fast">Fast switch</label>
        <input id="ng-fast" type="checkbox" checked={s.algorithms.decFastSwitch} onchange={(e) => saveAlgorithms({ decFastSwitch: e.currentTarget.checked })}
          title="Reverse Dec at once for a large error, instead of waiting a few frames" />
      {/if}
      <label for="ng-backlash">Backlash comp</label>
      <input id="ng-backlash" type="checkbox" checked={s.algorithms.decBacklashCompensation}
        onchange={(e) => saveAlgorithms({ decBacklashCompensation: e.currentTarget.checked })}
        title="When Dec reverses, add a pulse to take up the gears' slack first: measured at calibration, then adjusted as it guides" />

      <span class="head">Guide star</span><span></span>
      <label for="ng-mass">Brightness jump</label>
      <div class="pair"><input id="ng-mass" class="mono" type="number" min="0" max="200" step="10" value={Math.round(s.starMassTolerance * 100)}
        onchange={(e) => save({ starMassTolerance: num(e) / 100 })}
        title="A frame whose guide star suddenly brightens or fades by more than this (a cloud, a satellite, another star) isn't corrected. 0 turns it off" />
        <span class="muted">% (0 = off)</span></div>

      <span class="head">Calibration</span><span></span>
      <label for="ng-step">Step</label>
      <div class="pair"><input id="ng-step" class="mono" type="number" min="0" max="10000" step="100" value={s.calibrationStepMs}
        onchange={(e) => save({ calibrationStepMs: num(e) })} title="0: worked out from the guide scope and guide rate" /> <span class="muted">ms (0 = auto)</span></div>
      <label for="ng-flip">Reverse Dec after flip</label>
      <input id="ng-flip" type="checkbox" checked={s.reverseDecAfterFlip} onchange={(e) => save({ reverseDecAfterFlip: e.currentTarget.checked })}
        title="Only for mounts whose Dec guide pulses change direction after a meridian flip" />
      <label for="ng-oag">Off-axis guider</label>
      <div class="pair">
        <input id="ng-oag" type="checkbox" checked={s.rotateCalibration} onchange={(e) => save({ rotateCalibration: e.currentTarget.checked })}
          title="The guide camera turns with the main camera: when that turns (its rotator, or a plate solve after turning it by hand), the calibration turns with it, so there's no need to calibrate again" />
        {#if s.rotateCalibration}
          <label class="small inline" title="Tick if guiding goes wrong after turning the camera: the guide image turns the other way">
            <input type="checkbox" checked={s.rotationReversed} onchange={(e) => save({ rotationReversed: e.currentTarget.checked })} /> Reversed
          </label>
        {/if}
      </div>

      <span class="head">Comet</span><span></span>
      <label for="ng-comet">Follow</label>
      <input id="ng-comet" type="checkbox" checked={s.cometTracking} onchange={(e) => save({ cometTracking: e.currentTarget.checked })}
        title="Guide on a comet (or asteroid): the lock position moves with it at these rates, so it stays put while the stars trail" />
      {#if s.cometTracking}
        <label for="ng-comet-ra">RA rate</label>
        <div class="pair"><input id="ng-comet-ra" class="mono" type="number" step="1" value={s.cometRaRate}
          onchange={(e) => save({ cometRaRate: num(e) })} title="On the sky, east positive (an ephemeris's dRA×cos Dec)" /> <span class="muted">″/h</span></div>
        <label for="ng-comet-dec">Dec rate</label>
        <div class="pair"><input id="ng-comet-dec" class="mono" type="number" step="1" value={s.cometDecRate}
          onchange={(e) => save({ cometDecRate: num(e) })} title="North positive" /> <span class="muted">″/h</span></div>
      {/if}

      <span class="head">Log</span><span></span>
      <label for="ng-log">Guide log</label>
      <input id="ng-log" type="checkbox" checked={s.guideLog} onchange={(e) => save({ guideLog: e.currentTarget.checked })}
        title="A log of every calibration and guide frame (PHD2's format, for PHD2 log viewers), one per night in the night's image folder" />
    </div>
  </details>
{/if}

<style>
  .grid {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 4px 8px;
    align-items: center;
    font-size: 13px;
    margin-bottom: 8px;
  }
  .grid select {
    min-width: 0;
  }
  .pair {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .pair input {
    width: 70px;
  }
  .pair input.short {
    width: 52px;
  }
  .pair input[type='checkbox'] {
    width: auto;
  }
  .pair {
    flex-wrap: wrap;
  }
  .pair .inline {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    margin: 0;
  }
  .small-label {
    font-size: 12px;
  }
  .head {
    font-weight: 600;
    margin-top: 4px;
  }
  .star {
    display: block;
    width: 128px;
    height: 128px;
    margin: 0 auto 4px;
    background: #000;
    border: 1px solid var(--panel-border);
  }
  .star.lost {
    border-color: var(--danger);
  }
  .cross {
    stroke: var(--ok);
    stroke-width: 0.4;
  }
  .ring {
    fill: none;
    stroke: var(--warn);
    stroke-width: 0.5;
  }
  .activity {
    margin: 0 0 8px;
    font-size: 12px;
    text-align: center;
  }
  .row {
    margin-bottom: 8px;
  }
  .calibration {
    display: flex;
    flex-wrap: wrap;
    gap: 2px 10px;
    font-size: 12px;
    margin-bottom: 6px;
  }
  .calibration .muted {
    flex-basis: 100%;
  }
  .clear {
    font-size: 11px;
    padding: 1px 8px;
  }
  .profiles {
    display: flex;
    gap: 4px;
    align-items: center;
    margin-bottom: 8px;
  }
  .profiles select {
    flex: 1;
    min-width: 0;
  }
  .profiles .name {
    flex: 1;
    min-width: 0;
  }
  .small-btn {
    font-size: 12px;
    padding: 3px 8px;
    flex-shrink: 0;
  }
  .graph {
    max-width: 260px;
    margin: 0 auto 8px;
  }
  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    margin-bottom: 6px;
  }
  .chip {
    font-size: 11px;
    padding: 2px 8px;
    color: var(--muted);
    background: transparent;
    border: 1px solid var(--panel-border);
  }
  .chip.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .darks-row {
    align-items: center;
    gap: 6px;
  }
  .frames {
    width: 52px;
  }
  .spacer {
    flex: 1;
  }
  .warn {
    color: var(--warn);
  }
  .small {
    font-size: 12px;
    margin: 0 0 8px;
  }
  .settings summary {
    cursor: pointer;
    font-size: 13px;
    margin-bottom: 6px;
  }
</style>
