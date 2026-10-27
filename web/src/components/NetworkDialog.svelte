<script lang="ts">
  import { portal } from '../lib/portal'
  // The header's network badge → Network: how the mini PC is connected, and its Wi-Fi hotspot for when there's no
  // Wi-Fi to join (a dark site): its name and password, when it starts by itself, and starting or stopping it now.
  import { onMount } from 'svelte'
  import { api } from '../lib/api'
  import { ask } from '../lib/confirm.svelte'
  import { nova } from '../lib/state.svelte'
  import type { HotspotSettings } from '../lib/types'

  let { onclose }: { onclose: () => void } = $props()

  const network = $derived(nova.status?.network)
  const hotspot = $derived(nova.status?.hotspot ?? null)
  const on = $derived(hotspot?.state === 'On')
  const port = $derived(location.port || '80')
  const nameUrl = $derived(`http://${network?.hostname ?? 'nova'}.local${port === '80' ? '' : `:${port}`}`)
  const addressUrl = $derived(`http://${hotspot?.address ?? '10.42.0.1'}${port === '80' ? '' : `:${port}`}`)

  let settings = $state<HotspotSettings | null>(null)
  let saved = $state<HotspotSettings | null>(null)
  let showPassword = $state(false)
  let error = $state<string | null>(null)
  let note = $state<string | null>(null)
  let busy = $state(false)
  const changed = $derived(!!settings && !!saved && JSON.stringify(settings) !== JSON.stringify(saved))

  onMount(async () => {
    try {
      const view = await api.hotspot()
      settings = { ...view.settings }
      saved = { ...view.settings }
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
  })

  async function run(action: () => Promise<void>, done?: string) {
    busy = true
    error = null
    note = null
    try {
      await action()
      if (done) note = done
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
    busy = false
  }

  async function save() {
    if (!settings) return
    const next = { ...settings }
    await run(async () => {
      await api.saveHotspot(next)
      saved = { ...next, ssid: next.ssid.trim() }
      settings = { ...saved }
    }, on ? 'Saved. A new name or password takes effect the next time the hotspot starts.' : 'Saved.')
  }

  async function start() {
    if (changed) await save()
    if (error) return
    const ok = await ask('Start the hotspot now?', {
      message:
        `This browser will lose its connection to the mini PC. Join the Wi-Fi network "${saved?.ssid}" with its password, then open ` +
        `${addressUrl} (or ${nameUrl}). If nobody joins within 3 minutes, it goes back to Wi-Fi by itself.`,
      confirm: 'Start hotspot',
    })
    if (ok) await run(api.startHotspot, 'Hotspot on.')
  }

  async function stop() {
    const ok = await ask('Back to Wi-Fi?', {
      message:
        'The hotspot stops and the mini PC joins its known Wi-Fi. Anything on the hotspot (perhaps this browser) loses its connection. ' +
        (hotspot?.fallback ? 'If no Wi-Fi is found within 90 seconds, the hotspot starts again.' : ''),
      confirm: 'Back to Wi-Fi',
    })
    if (ok) await run(api.stopHotspot, 'Joining Wi-Fi.')
  }

  const describe = (i: { kind: string; mode: string | null; ssid: string | null; signalPercent: number | null }) =>
    i.mode === 'hotspot'
      ? `hotspot ${i.ssid ?? ''}`
      : i.kind === 'wifi'
        ? `Wi-Fi ${i.ssid ?? '(not joined)'}${i.signalPercent != null ? ` ${i.signalPercent}%` : ''}`
        : 'wired'
</script>

<div use:portal class="backdrop" role="presentation" onclick={() => !busy && onclose()}></div>
<div use:portal class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="network-title">
  <h3 id="network-title">Network</h3>

  {#if network}
    <div class="connections mono small">
      {#if network.hostname}<span class="muted">Mini PC</span><span>{network.hostname}</span>{/if}
      {#each network.interfaces as i (i.name)}
        <span class="muted">{i.name}</span><span>{describe(i)}, {i.addresses.join(', ')}</span>
      {/each}
    </div>
  {/if}

  <fieldset>
    <legend>Wi-Fi hotspot</legend>
    <p class="muted small">
      When the mini PC finds no Wi-Fi to join (at a dark site, say), it starts its own network so a phone or laptop can still reach NOVA:
      join it, then open <span class="mono">{addressUrl}</span> or <span class="mono">{nameUrl}</span>. Set it up here before you go.
    </p>

    {#if hotspot}
      <p class="state small" class:on class:bad={hotspot.state === 'Error'}>
        {#if on}
          <span class="emoji" aria-hidden="true">📡</span> On{hotspot.reason ? ` (${hotspot.reason.toLowerCase()})` : ''}: {hotspot.stations ?? 0} device{hotspot.stations === 1 ? '' : 's'} joined.
          {#if hotspot.manual && !hotspot.stations}Back to Wi-Fi in 3 minutes if nobody joins.{:else if hotspot.retry && !hotspot.stations}Tries Wi-Fi again every 5 minutes while nobody's on it.{/if}
        {:else if hotspot.state === 'Starting'}Starting…
        {:else if hotspot.state === 'Stopping'}Stopping…
        {:else if hotspot.state === 'Error'}{hotspot.error}
        {:else if hotspot.startsInSeconds != null}No Wi-Fi: the hotspot starts in {hotspot.startsInSeconds} s.
        {:else}Off{hotspot.configured && hotspot.fallback ? ': starts when no Wi-Fi is found' : ''}.{/if}
      </p>
      {#if hotspot.problem}<p class="bad small">{hotspot.problem}</p>{/if}
    {/if}

    {#if !settings}
      <p class="muted small">{error ?? 'Loading…'}</p>
    {:else}
      <label>Name <input bind:value={settings.ssid} maxlength="32" autocomplete="off" spellcheck="false" /></label>
      <label>Password <span class="muted">(8 to 63 characters; you'll type it on your phone)</span>
        <span class="row">
          <input type={showPassword ? 'text' : 'password'} bind:value={settings.password} maxlength="63" autocomplete="new-password" spellcheck="false" />
          <button type="button" class="secondary" onclick={() => (showPassword = !showPassword)} aria-pressed={showPassword}>{showPassword ? 'Hide' : 'Show'}</button>
        </span>
      </label>
      <label class="check"><input type="checkbox" bind:checked={settings.fallback} /> Start it when no Wi-Fi is found (after 90 seconds)</label>
      <label class="check"><input type="checkbox" bind:checked={settings.retry} /> Try my Wi-Fi again every 5 minutes while nobody's on it</label>
      {#if settings.password.length > 0 && settings.password.length < 8}<p class="bad small">The password needs at least 8 characters.</p>{/if}
    {/if}
  </fieldset>

  {#if error && settings}<p class="bad small">{error}</p>{:else if note}<p class="ok small">{note}</p>{/if}
  <div class="row">
    {#if on}
      <button type="button" class="secondary" disabled={busy} onclick={stop} title="Stop the hotspot and join Wi-Fi again">Back to Wi-Fi</button>
    {:else}
      <button type="button" class="secondary" disabled={busy || !settings || settings.password.length < 8 || !!hotspot?.problem || hotspot?.state === 'Starting'} onclick={start}
        title="Try it out now (from home: it goes back to Wi-Fi by itself if nobody joins within 3 minutes)">Start hotspot now</button>
    {/if}
    <span class="spacer"></span>
    <button type="button" class="secondary" disabled={busy} onclick={onclose}>Close</button>
    <button type="button" disabled={busy || !changed} onclick={save}>Save</button>
  </div>
</div>

<style>
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
    top: 10%;
    transform: translateX(-50%);
    width: min(500px, calc(100vw - 32px));
    max-height: 86vh;
    overflow: auto;
    display: grid;
    gap: 10px;
    padding: 16px;
  }
  h3,
  p {
    margin: 0;
  }
  .small {
    font-size: 12px;
  }
  .connections {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 2px 12px;
    overflow-wrap: anywhere;
  }
  fieldset {
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 8px 10px 10px;
    display: grid;
    gap: 8px;
  }
  legend {
    font-size: 12px;
    padding: 0 4px;
  }
  label {
    display: grid;
    gap: 4px;
    font-size: 12px;
  }
  label.check {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .row {
    display: flex;
    gap: 8px;
    align-items: center;
  }
  .row input {
    flex: 1;
    min-width: 0;
  }
  .spacer {
    flex: 1;
  }
  .state.on {
    color: var(--ok);
  }
  .bad {
    color: var(--danger);
  }
  .ok {
    color: var(--ok);
  }
</style>
