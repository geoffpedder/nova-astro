<script lang="ts">
  // ⏻ → Notifications…: messages to your phone through ntfy when a sequence needs you or is done.
  import { onMount } from 'svelte'
  import { api } from '../lib/api'
  import type { NotificationSettings } from '../lib/types'

  let { onclose }: { onclose: () => void } = $props()

  let settings = $state<NotificationSettings | null>(null)
  let hasToken = $state(false)
  let lastError = $state<string | null>(null)
  let token = $state('')
  let removeToken = $state(false)
  let error = $state<string | null>(null)
  let note = $state<string | null>(null)
  let busy = $state(false)

  // A topic is like a password for the messages: a random one nobody will guess.
  function randomTopic(): string {
    const letters = 'abcdefghijkmnpqrstuvwxyz23456789'
    const bytes = crypto.getRandomValues(new Uint8Array(12))
    return 'nova-' + Array.from(bytes, (b) => letters[b % letters.length]).join('')
  }

  onMount(async () => {
    try {
      const view = await api.notifications()
      hasToken = view.hasToken
      lastError = view.lastError
      settings = {
        enabled: view.enabled,
        server: view.server,
        topic: view.topic || randomTopic(),
        sequenceDone: view.sequenceDone,
        sequenceFailed: view.sequenceFailed,
        targetSkipped: view.targetSkipped,
        needsYou: view.needsYou,
        guidingLost: view.guidingLost,
        guidingLostMinutes: view.guidingLostMinutes,
        storage: view.storage ?? true,
      }
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
  })

  async function save(): Promise<boolean> {
    if (!settings) return false
    busy = true
    error = null
    note = null
    try {
      await api.saveNotifications(settings, removeToken ? '' : token.trim() || undefined)
      hasToken = removeToken ? false : hasToken || token.trim().length > 0
      token = ''
      removeToken = false
      return true
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
      return false
    } finally {
      busy = false
    }
  }

  async function test() {
    if (!(await save())) return
    busy = true
    try {
      await api.testNotification()
      note = 'Sent. It should be on your phone now.'
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
    busy = false
  }

  async function saveAndClose() {
    if (await save()) onclose()
  }
</script>

<div class="backdrop" role="presentation" onclick={() => !busy && onclose()}></div>
<div class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="notifications-title">
  <h3 id="notifications-title">Notifications</h3>
  {#if !settings}
    <p class="muted">{error ?? 'Loading…'}</p>
  {:else}
    <p class="muted small">
      Messages to your phone through <a href="https://ntfy.sh" target="_blank" rel="noreferrer">ntfy</a>, a free app for Android
      and iPhone: install it, add a subscription to the topic below, and press Send a test. The mini PC needs internet for them
      to arrive.
    </p>
    <label class="check"><input type="checkbox" bind:checked={settings.enabled} /> Send notifications</label>

    <label>Topic <span class="muted">(anyone who knows it can read the messages, so keep it hard to guess)</span>
      <span class="row"><input class="mono" bind:value={settings.topic} /><button type="button" class="secondary" onclick={() => settings && (settings.topic = randomTopic())}>New</button></span>
    </label>
    <label>Server <input class="mono" bind:value={settings.server} /></label>
    <label>Access token <span class="muted">(only for a server or topic that needs one)</span>
      <input type="password" autocomplete="off" placeholder={hasToken ? 'set (leave empty to keep it)' : 'none'} bind:value={token} disabled={removeToken} />
    </label>
    {#if hasToken}<label class="check small"><input type="checkbox" bind:checked={removeToken} /> Remove the token</label>{/if}

    <fieldset>
      <legend>Tell me when</legend>
      <label class="check"><input type="checkbox" bind:checked={settings.sequenceDone} /> a sequence finishes (with each target's frames)</label>
      <label class="check"><input type="checkbox" bind:checked={settings.sequenceFailed} /> a sequence stops with an error</label>
      <label class="check"><input type="checkbox" bind:checked={settings.targetSkipped} /> a target is skipped because something went wrong</label>
      <label class="check"><input type="checkbox" bind:checked={settings.needsYou} /> NOVA is waiting for you (flat panel, turning the camera)</label>
      <label class="check">
        <input type="checkbox" bind:checked={settings.guidingLost} /> guiding has lost its star for
        <input class="minutes" type="number" min="1" max="60" bind:value={settings.guidingLostMinutes} disabled={!settings.guidingLost} /> min
      </label>
      <label class="check"><input type="checkbox" bind:checked={settings.storage} /> frames can't go to the USB drive (pulled out, full)</label>
    </fieldset>

    {#if error}<p class="bad">{error}</p>{:else if note}<p class="ok">{note}</p>{:else if lastError}<p class="muted small">Last problem sending: {lastError}</p>{/if}
    <div class="row">
      <button type="button" class="secondary" disabled={busy || !settings.topic} onclick={test}>Send a test</button>
      <span class="spacer"></span>
      <button type="button" class="secondary" disabled={busy} onclick={onclose}>Cancel</button>
      <button type="button" disabled={busy} onclick={saveAndClose}>Save</button>
    </div>
  {/if}
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
    top: 12%;
    transform: translateX(-50%);
    width: min(480px, calc(100vw - 32px));
    max-height: 84vh;
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
  fieldset {
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 8px 10px;
    display: grid;
    gap: 6px;
  }
  legend {
    font-size: 12px;
    padding: 0 4px;
  }
  .minutes {
    width: 4em;
  }
  .row {
    display: flex;
    gap: 8px;
    align-items: center;
  }
  .row input {
    flex: 1;
  }
  .spacer {
    flex: 1;
  }
  .bad {
    color: var(--danger);
    font-size: 12px;
  }
  .ok {
    color: var(--ok);
    font-size: 12px;
  }
</style>
