<script lang="ts">
  import { nova } from '../lib/state.svelte'

  // Errors in full (wrapped, not cut off), under the header; they clear themselves after a while (lib/session).
</script>

{#if nova.error}
  <div class="error-toast" role="alert">
    <span class="icon" aria-hidden="true">⚠</span>
    <span class="message">{nova.error}</span>
    <button class="close" title="Dismiss" aria-label="Dismiss" onclick={() => (nova.error = null)}>✕</button>
  </div>
{/if}

<style>
  .error-toast {
    position: fixed;
    top: calc(58px + env(safe-area-inset-top));
    right: 16px;
    z-index: 50;
    width: min(520px, calc(100vw - 32px));
    display: flex;
    align-items: flex-start;
    gap: 8px;
    padding: 10px 12px;
    background: var(--panel);
    color: var(--danger);
    border: 1px solid var(--danger);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
    font-size: 13px;
    line-height: 1.4;
  }
  .message {
    flex: 1;
    white-space: normal;
    overflow-wrap: anywhere;
  }
  .close {
    background: none;
    border: none;
    color: var(--danger);
    padding: 0 2px;
    cursor: pointer;
  }
</style>
