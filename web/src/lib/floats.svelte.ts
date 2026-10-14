/**
 * A floating window over the tabs (the webcam, the latest image): whether it's popped out, where it sits and how wide.
 * Remembered per browser, like the panel layout.
 */
export interface FloatLayout {
  popped: boolean
  /** Distance from the window's right and bottom edges (px), so it stays in its corner as the window resizes. */
  right: number
  bottom: number
  /** Its width (px), set by dragging its corner; the height follows the picture. */
  width: number
}

export function createFloatLayout(key: string, defaults: Omit<FloatLayout, 'popped'>): { layout: FloatLayout; save: () => void } {
  const layout = $state<FloatLayout>(load())
  function load(): FloatLayout {
    try {
      const saved = JSON.parse(localStorage.getItem(key) ?? 'null')
      if (saved && typeof saved.popped === 'boolean')
        return {
          popped: saved.popped,
          right: Number.isFinite(+saved.right) ? +saved.right : defaults.right,
          bottom: Number.isFinite(+saved.bottom) ? +saved.bottom : defaults.bottom,
          width: Math.max(200, +saved.width || defaults.width),
        }
    } catch {
      // storage unavailable or unreadable: the defaults
    }
    return { popped: false, ...defaults }
  }
  function save(): void {
    try {
      localStorage.setItem(key, JSON.stringify(layout))
    } catch {
      // just for this visit
    }
  }
  return { layout, save }
}

/** The latest image popped out; it starts beside the webcam's. */
export const imageFloat = createFloatLayout('nova.imageFloat', { right: 312, bottom: 48, width: 320 })

/** The guiding graph popped out; it starts above the webcam's. */
export const guideFloat = createFloatLayout('nova.guideFloat', { right: 16, bottom: 300, width: 360 })
