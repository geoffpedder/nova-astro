<script lang="ts">
  // PHD2's own window (its virtual screen on the mini PC), shown through NOVA's port with noVNC. Connected only
  // while this tab is open; red-filtered in night mode like everything else.
  let { active }: { active: boolean } = $props()

  let status = $state<{ available: boolean; problem: string | null } | null>(null)
  let attempt = $state(0)

  $effect(() => {
    if (!active) return
    void attempt
    status = null
    fetch('/api/phd2/screen')
      .then((r) => r.json())
      .then((s) => (status = s))
      .catch(() => (status = { available: false, problem: "Couldn't ask the mini PC about PHD2's screen" }))
  })

  // noVNC's default path ("websockify", next to its page or at the root, depending on its version): NOVA relays both.
  const src = '/phd2/novnc/vnc.html?autoconnect=true&resize=scale&reconnect=true&reconnect_delay=2000'
</script>

<div class="phd2">
  {#if active && status?.available}
    {#key attempt}
      <iframe {src} title="PHD2" allow="clipboard-read; clipboard-write"></iframe>
    {/key}
  {:else if active && status}
    <div class="message">
      <p>{status.problem}</p>
      <button class="secondary" onclick={() => attempt++}>Try again</button>
    </div>
  {:else if active}
    <p class="muted message">Connecting to PHD2's screen…</p>
  {/if}
</div>

<style>
  .phd2 {
    display: grid;
    min-height: 0;
    background: #000;
    border-radius: var(--radius);
    overflow: hidden;
  }
  iframe {
    width: 100%;
    height: 100%;
    border: 0;
    display: block;
  }
  :global(:root[data-theme='night']) iframe {
    filter: grayscale(1) sepia(1) hue-rotate(-50deg) saturate(6) brightness(0.6);
  }
  .message {
    margin: auto;
    padding: 24px;
    text-align: center;
    color: var(--muted);
  }
</style>
