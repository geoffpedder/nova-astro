<script lang="ts" module>
  let count = 0
</script>

<script lang="ts">
  // NOVA's star (the favicon), beside the NOVA name: a four-pointed star with a bright core. Drawn here rather than
  // loaded, so night mode can turn it red like the rest of the page.
  let { size = 16 }: { size?: number } = $props()
  const id = `nova-star-${++count}`
</script>

<svg class="star" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true">
  <defs>
    <radialGradient {id} cx="50%" cy="50%" r="50%">
      <stop offset="0" class="core" />
      <stop offset="0.35" class="mid" />
      <stop offset="1" class="edge" />
    </radialGradient>
  </defs>
  <path d="M16 1 C17 10 22 15 31 16 C22 17 17 22 16 31 C15 22 10 17 1 16 C10 15 15 10 16 1 Z" fill="url(#{id})" />
</svg>

<style>
  .star {
    display: block;
    flex: none;
  }
  .core {
    stop-color: #ffffff;
  }
  .mid {
    stop-color: #cfe6f7;
  }
  .edge {
    stop-color: #5e9fcf;
  }
  :global(:root[data-theme='night']) .core {
    stop-color: #ff5050;
  }
  :global(:root[data-theme='night']) .mid {
    stop-color: #c02020;
  }
  :global(:root[data-theme='night']) .edge {
    stop-color: #6a0000;
  }
</style>
