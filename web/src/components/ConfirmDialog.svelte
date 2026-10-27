<script lang="ts">
  import { portal } from '../lib/portal'
  import { answer, confirmation } from '../lib/confirm.svelte'

  const request = $derived(confirmation.request)
  let confirmButton = $state<HTMLButtonElement | null>(null)

  // Focus the confirm button so Enter confirms; Escape cancels.
  $effect(() => {
    if (request) confirmButton?.focus()
  })
  function keydown(e: KeyboardEvent) {
    if (!request) return
    if (e.key === 'Escape') {
      e.preventDefault()
      answer(false)
    }
  }
</script>

<svelte:window onkeydown={keydown} />

{#if request}
  <div use:portal class="backdrop" role="presentation" onclick={() => answer(false)}></div>
  <div use:portal class="dialog panel" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-message">
    <h3 id="confirm-title">{request.title}</h3>
    {#if request.message}<p id="confirm-message" class="muted">{request.message}</p>{/if}
    <div class="actions">
      <button class="secondary" onclick={() => answer(false)}>Cancel</button>
      <button bind:this={confirmButton} class:danger={request.danger} onclick={() => answer(true)}>{request.confirm}</button>
    </div>
  </div>
{/if}

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.5);
    z-index: 60;
  }
  .dialog {
    position: fixed;
    z-index: 61;
    left: 50%;
    top: 30%;
    transform: translate(-50%, -30%);
    width: min(420px, calc(100vw - 32px));
    display: grid;
    gap: 10px;
    padding: 16px;
    box-shadow: 0 12px 32px rgb(0 0 0 / 0.45);
  }
  h3 {
    margin: 0;
    font-size: 16px;
  }
  p {
    margin: 0;
    font-size: 13px;
  }
  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
  }
</style>
