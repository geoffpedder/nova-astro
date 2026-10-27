<script lang="ts">
  import { nova } from '../lib/state.svelte'
  import NetworkDialog from './NetworkDialog.svelte'
  import Icon from './Icon.svelte'

  // How the mini PC is connected: its Wi-Fi network (and signal), the hotspot it's running, or a cable.
  const network = $derived(nova.status?.network)
  const primary = $derived(network?.primary ?? null)
  const hotspot = $derived(nova.status?.hotspot ?? null)
  let open = $state(false)

  const label = $derived.by(() => {
    // NOVA's own hotspot is on (also the simulator's pretend one).
    if (hotspot?.state === 'On') return { icon: 'hotspot' as const, text: `Hotspot ${hotspot.ssid}`, address: hotspot.address }
    if (!primary) return null
    const address = primary.addresses[0] ?? ''
    if (primary.mode === 'hotspot') return { icon: 'hotspot' as const, text: `Hotspot ${primary.ssid ?? ''}`.trim(), address }
    if (primary.kind === 'wifi')
      return {
        icon: 'wifi' as const,
        text: `${primary.ssid ?? 'Wi-Fi'}${primary.signalPercent != null ? ` ${primary.signalPercent}%` : ''}`,
        address,
      }
    return { icon: 'wired' as const, text: 'Wired', address }
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
  <button class="network" class:weak title={`${details}

Click for the Wi-Fi hotspot`} onclick={() => (open = true)}>
    <Icon name={label.icon} size={15} />
    <span class="text">{label.text}</span>
    {#if label.address}<span class="address muted">{label.address}</span>{/if}
  </button>
{/if}
{#if open}<NetworkDialog onclose={() => (open = false)} />{/if}

<style>
  .network {
    all: unset;
    cursor: pointer;
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
  .network:hover .text,
  .network:focus-visible .text {
    color: var(--accent);
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
