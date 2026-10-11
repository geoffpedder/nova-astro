<script lang="ts">
  import { nova } from '../lib/state.svelte'

  // How the mini PC is connected: its Wi-Fi network (and signal), the hotspot it's running, or a cable.
  const network = $derived(nova.status?.network)
  const primary = $derived(network?.primary ?? null)

  const label = $derived.by(() => {
    if (!primary) return null
    const address = primary.addresses[0] ?? ''
    if (primary.mode === 'hotspot') return { icon: '📡', text: `Hotspot ${primary.ssid ?? ''}`.trim(), address }
    if (primary.kind === 'wifi')
      return {
        icon: '📶',
        text: `${primary.ssid ?? 'Wi-Fi'}${primary.signalPercent != null ? ` ${primary.signalPercent}%` : ''}`,
        address,
      }
    return { icon: '🔌', text: 'Wired', address }
  })

  // Weak Wi-Fi is worth noticing at the scope: image downloads and the live view slow down.
  const weak = $derived(primary?.kind === 'wifi' && primary.mode !== 'hotspot' && (primary.signalPercent ?? 100) < 40)

  const details = $derived(
    network
      ? [
          network.hostname ? `Mini PC: ${network.hostname}` : null,
          ...network.interfaces.map(
            (i) =>
              `${i.name}: ${i.mode === 'hotspot' ? `hotspot ${i.ssid ?? ''}` : i.kind === 'wifi' ? `Wi-Fi ${i.ssid ?? '(not joined)'}${i.signalPercent != null ? ` ${i.signalPercent}%` : ''}` : 'wired'}, ${i.addresses.join(', ')}`,
          ),
        ]
          .filter(Boolean)
          .join('\n')
      : '',
  )
</script>

{#if label}
  <span class="network" class:weak title={details}>
    <span aria-hidden="true">{label.icon}</span>
    <span class="text">{label.text}</span>
    {#if label.address}<span class="address muted">{label.address}</span>{/if}
  </span>
{/if}

<style>
  .network {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    font-size: 12px;
    white-space: nowrap;
    min-width: 0;
    overflow: hidden;
  }
  .text {
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 180px;
  }
  .weak .text {
    color: var(--warn);
  }
  @media (max-width: 700px) {
    .address {
      display: none;
    }
  }
</style>
