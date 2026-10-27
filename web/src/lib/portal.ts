/**
 * Moves an element to the end of the page's <body> while it's shown: for dialogs, menus and their backdrops. A
 * "fixed" element is placed against the screen, except inside something with a filter, backdrop-filter or transform,
 * which becomes its frame instead. Safari counts the compact layout's see-through (blurred) bars, so a dialog opened
 * from one would sit inside a 48 px bar, mostly off screen. At the top of <body> it's always against the screen.
 * Svelte still removes it when it's no longer shown.
 */
export function portal(node: HTMLElement): { destroy: () => void } {
  document.body.appendChild(node)
  return {
    destroy() {
      node.remove()
    },
  }
}
