<script lang="ts" module>
  import type { IconName } from './Icon.svelte'
  import type { PanelKind } from './Panel.svelte'

  const icons: Record<PanelKind, IconName> = {
    mount: 'mount',
    camera: 'camera',
    focuser: 'focuser',
    filterwheel: 'filterwheel',
    rotator: 'rotator',
    guider: 'guider',
    flatpanel: 'flatpanel',
  }
</script>

<script lang="ts">
  import Icon from './Icon.svelte'

  // A kind of equipment as a small round badge in its colour, with its icon: before a panel's title, and by each
  // device in the Equipment list. The same icons as the compact layout's.
  let { kind, size = 22 }: { kind: PanelKind; size?: number } = $props()
</script>

<span class="badge-kind" style:--kind="var(--kind-{kind})" style:width="{size}px" style:height="{size}px" aria-hidden="true">
  <Icon name={icons[kind]} size={Math.round(size * 0.64)} />
</span>

<style>
  .badge-kind {
    display: inline-grid;
    place-items: center;
    flex: none;
    border-radius: 50%;
    background: color-mix(in srgb, var(--kind) 22%, transparent);
    color: var(--kind);
  }
</style>
