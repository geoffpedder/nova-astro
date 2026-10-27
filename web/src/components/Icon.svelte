<script lang="ts" module>
  /** Line icons, the same in both layouts, drawn in the text colour (so night mode turns them red). */
  const paths = {
    mount: '<path d="M5 9l10-5 2 4-10 5z"/><path d="M11 12l-4 9M11 12l1 9M11 12l5 8"/>',
    camera: '<rect x="3" y="7" width="18" height="12" rx="2"/><circle cx="12" cy="13" r="3.5"/><path d="M8 7l1.5-3h5L16 7"/>',
    focuser: '<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3"/>',
    filterwheel: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="7.5" r="2"/><circle cx="16.5" cy="13.5" r="2"/><circle cx="7.5" cy="13.5" r="2"/>',
    guider: '<circle cx="12" cy="12" r="7"/><path d="M12 2v6M12 16v6M2 12h6M16 12h6"/>',
    plan: '<path d="M9 6h11M9 12h11M9 18h11"/><circle cx="5" cy="6" r="1.2"/><circle cx="5" cy="12" r="1.2"/><circle cx="5" cy="18" r="1.2"/>',
    polar: '<circle cx="12" cy="12" r="9"/><path d="M12 3l2 9-2 9-2-9z"/>',
    webcam: '<circle cx="12" cy="10" r="6"/><circle cx="12" cy="10" r="2"/><path d="M8 21h8M12 16v5"/>',
    minipc: '<rect x="4" y="5" width="16" height="11" rx="1.5"/><path d="M9 20h6M12 16v4"/>',
    image: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="1.6"/><path d="M3 17l5-4.5 4 3.5 3-2.5 6 5"/>',
    rotator: '<path d="M20 12a8 8 0 1 1-2.3-5.6"/><path d="M20 4v4h-4"/><path d="M12 8v4l2.5 2.5"/>',
    flatpanel: '<rect x="4" y="6" width="16" height="12" rx="1.5"/><path d="M8 10h8M8 14h8" opacity="0.6"/>',
        log: '<path d="M8 6h12M8 12h12M8 18h8"/><path d="M4 6h.01M4 12h.01M4 18h.01" stroke-width="2.4"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    file: '<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4"/>',
    trash: '<path d="M4 7h16M10 7V4h4v3M6 7l1 13h10l1-13"/><path d="M10 11v5M14 11v5"/>',
        download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
        targets: '<path d="M12 3l2.6 5.5 6 .7-4.4 4.1 1.2 5.9L12 16.3 6.6 19.2l1.2-5.9L3.4 9.2l6-.7z"/>',
    equipment: '<path d="M8 3v5M16 3v5"/><rect x="5" y="8" width="14" height="6" rx="2"/><path d="M12 14v3a4 4 0 0 1-4 4"/>',
    moon: '<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    expand: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
    shrink: '<path d="M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5"/>',
    up: '<path d="M6 15l6-6 6 6"/>',
    down: '<path d="M6 9l6 6 6-6"/>',
    right: '<path d="M9 6l6 6-6 6"/>',
    left: '<path d="M15 6l-6 6 6 6"/>',
    close: '<path d="M6 6l12 12M18 6L6 18"/>',
    solve: '<path d="M4 4h16v16H4z"/><path d="M4 12h16M12 4v16"/>',
    stars: '<circle cx="12" cy="12" r="5"/><circle cx="12" cy="12" r="1"/>',
    stack: '<path d="M12 3l9 5-9 5-9-5z"/><path d="M3 13l9 5 9-5"/>',
    one: '<rect x="4" y="4" width="16" height="16" rx="1"/><path d="M10 9l2-1v8"/>',
    power: '<path d="M12 3v8"/><path d="M7.5 6.5a7 7 0 1 0 9 0"/>',
    wifi: '<path d="M2.5 9a14 14 0 0 1 19 0"/><path d="M5.5 12.5a9.5 9.5 0 0 1 13 0"/><path d="M8.7 16a5 5 0 0 1 6.6 0"/><path d="M12 19.5h.01" stroke-width="2.6"/>',
    hotspot: '<circle cx="12" cy="11" r="1.6"/><path d="M12 12.6V21"/><path d="M8.2 7.2a5.4 5.4 0 0 0 0 7.6M15.8 7.2a5.4 5.4 0 0 1 0 7.6"/><path d="M5.3 4.3a9.5 9.5 0 0 0 0 13.4M18.7 4.3a9.5 9.5 0 0 1 0 13.4"/>',
    wired: '<rect x="6" y="3" width="12" height="10" rx="1.5"/><path d="M9 3v3M12 3v3M15 3v3"/><path d="M12 13v8"/>',
  } as const
  export type IconName = keyof typeof paths
</script>

<script lang="ts">
  let { name, size = 24 }: { name: IconName; size?: number } = $props()
</script>

<svg class="icon" width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">{@html paths[name]}</svg>

<style>
  .icon {
    fill: none;
    stroke: currentColor;
    stroke-width: 1.6;
    stroke-linecap: round;
    stroke-linejoin: round;
    flex: none;
    display: block;
  }
</style>
