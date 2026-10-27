<script lang="ts">
  import { portal } from '../lib/portal'
  // Set, change or remove NOVA's password (⏻ → Password…). The current password is needed while one is set.
  import { api } from '../lib/api'
  import { untrack } from 'svelte'
  import { auth, checkAuth } from '../lib/auth.svelte'

  let { onclose }: { onclose: () => void } = $props()
  // The title stays as it opened, even once the password is removed.
  const hadPassword = untrack(() => auth.passwordSet)

  const MinimumLength = 6
  let current = $state('')
  let next = $state('')
  let again = $state('')
  let error = $state<string | null>(null)
  let busy = $state(false)
  let done = $state<string | null>(null)

  const problem = $derived(
    next.length > 0 && next.length < MinimumLength
      ? `Use at least ${MinimumLength} characters`
      : again.length > 0 && again !== next
        ? "They don't match"
        : null,
  )
  const canSave = $derived(!busy && next.length >= MinimumLength && next === again && (!auth.passwordSet || current.length > 0))

  async function save(password: string) {
    busy = true
    error = null
    try {
      await api.setPassword(current, password)
      await checkAuth()
      done = password
        ? 'Password set. Other browsers will ask for it; this one stays signed in.'
        : 'Password removed. Anyone on the network can use NOVA again.'
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
    busy = false
  }

  function submit(event: SubmitEvent) {
    event.preventDefault()
    if (canSave) save(next)
  }
</script>

<div use:portal class="backdrop" role="presentation" onclick={() => !busy && onclose()}></div>
<div use:portal class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="password-title">
  <h3 id="password-title">{hadPassword ? 'Change the password' : 'Set a password'}</h3>
  {#if done}
    <p>{done}</p>
    <p class="muted small">PHD2's own page on port 6080 opens or closes to match at the next restart (or run <span class="mono">nova restart</span> on the mini PC).</p>
    <div class="row"><span class="spacer"></span><button onclick={onclose}>Close</button></div>
  {:else}
    <p class="muted small">
      {auth.passwordSet
        ? 'Changing it signs every other browser out.'
        : 'Without one, anyone on the same network can use NOVA and move the telescope.'}
      Stellarium's telescope link (port 10001) can't ask for a password; turn it off with Nova__StellariumPort=0 in /etc/nova/nova.env if that matters.
    </p>
    <form onsubmit={submit}>
      {#if auth.passwordSet}
        <label>Current password <input type="password" autocomplete="current-password" bind:value={current} disabled={busy} /></label>
      {/if}
      <label>New password <input type="password" autocomplete="new-password" bind:value={next} disabled={busy} /></label>
      <label>Again <input type="password" autocomplete="new-password" bind:value={again} disabled={busy} /></label>
      {#if problem || error}<p class="bad">{problem ?? error}</p>{/if}
      <div class="row">
        {#if auth.passwordSet}
          <button type="button" class="secondary danger" disabled={busy || !current} onclick={() => save('')}
            title="Let anyone on the network use NOVA again">Remove password</button>
        {/if}
        <span class="spacer"></span>
        <button type="button" class="secondary" disabled={busy} onclick={onclose}>Cancel</button>
        <button type="submit" disabled={!canSave}>{busy ? 'Saving…' : 'Save'}</button>
      </div>
    </form>
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
    top: 30%;
    transform: translate(-50%, -30%);
    width: min(420px, calc(100vw - 32px));
    display: grid;
    gap: 10px;
    padding: 16px;
  }
  h3 {
    margin: 0;
  }
  p {
    margin: 0;
  }
  .small {
    font-size: 12px;
  }
  form {
    display: grid;
    gap: 8px;
  }
  label {
    display: grid;
    gap: 4px;
    font-size: 12px;
  }
  .bad {
    color: var(--danger);
    font-size: 12px;
  }
  .row {
    display: flex;
    gap: 8px;
    align-items: center;
    margin-top: 4px;
  }
  .spacer {
    flex: 1;
  }
</style>
