<script lang="ts">
  // Shown instead of the page while NOVA's password is set and this browser hasn't signed in.
  import { api } from '../lib/api'
  import { auth } from '../lib/auth.svelte'

  let password = $state('')
  let error = $state<string | null>(null)
  let busy = $state(false)

  async function signIn(event: SubmitEvent) {
    event.preventDefault()
    if (!password || busy) return
    busy = true
    error = null
    try {
      await api.signIn(password)
      auth.signedIn = true
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
      password = ''
    }
    busy = false
  }

  const focus = (node: HTMLInputElement) => node.focus()
</script>

<main class="sign-in">
  <form class="panel" onsubmit={signIn}>
    <div class="brand">NOVA</div>
    <label for="nova-password">Password</label>
    <input id="nova-password" type="password" autocomplete="current-password" bind:value={password} disabled={busy} use:focus />
    {#if error}<p class="bad">{error}</p>{/if}
    <button type="submit" disabled={busy || !password}>{busy ? 'Checking…' : 'Sign in'}</button>
    <p class="muted hint">This browser stays signed in for 30 days. Forgotten the password? On the mini PC: <span class="mono">nova password --clear</span></p>
  </form>
</main>

<style>
  .sign-in {
    min-height: 100vh;
    display: grid;
    place-items: center;
    padding: 16px;
    background: var(--bg);
  }
  form {
    width: min(340px, 100%);
    display: grid;
    gap: 10px;
    padding: 24px;
  }
  .brand {
    font-family: var(--font-mono);
    font-weight: 600;
    letter-spacing: 0.35em;
    color: #7fb6de;
    text-align: center;
    margin-bottom: 8px;
  }
  :global(:root[data-theme='night']) .brand {
    color: var(--accent);
  }
  label {
    font-size: 12px;
  }
  .bad {
    color: var(--danger);
    margin: 0;
    font-size: 12px;
  }
  .hint {
    font-size: 11px;
    margin: 4px 0 0;
  }
</style>
