<script lang="ts">
  import { onMount } from 'svelte'
  import App from './App.svelte'
  import CompactApp from './components/compact/CompactApp.svelte'
  import ConfirmDialog from './components/ConfirmDialog.svelte'
  import DriverSettings from './components/DriverSettings.svelte'
  import ErrorToast from './components/ErrorToast.svelte'
  import FlatsPrompt from './components/FlatsPrompt.svelte'
  import RotationPrompt from './components/RotationPrompt.svelte'
  import SignIn from './components/SignIn.svelte'
  import { auth } from './lib/auth.svelte'
  import { startSession } from './lib/session.svelte'
  import { nova } from './lib/state.svelte'
  import { isCompact } from './lib/uiLayout.svelte'

  // The page: one session (lib/session), shown by one of two layouts. Everything here is shared by both: signing in,
  // the dialogs and prompts NOVA brings up, and errors.
  onMount(startSession)
  const compact = $derived(isCompact())
</script>

{#if auth.checked && !auth.signedIn}
  <SignIn />
{:else}
  {#if compact}<CompactApp />{:else}<App />{/if}

  {#if nova.settingsDevice}
    <DriverSettings device={nova.settingsDevice} onclose={() => (nova.settingsDevice = null)} />
  {/if}
  <ConfirmDialog />
  <RotationPrompt />
  <FlatsPrompt />
  <ErrorToast />
{/if}
