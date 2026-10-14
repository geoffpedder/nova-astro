/**
 * The webcam's floating view on this browser: whether it's popped out over the other tabs, and where it sits.
 * Remembered per browser, like the panel layout.
 */
interface WebcamLayout {
  popped: boolean
  /** Distance from the window's right and bottom edges (px), so it stays in its corner as the window resizes. */
  right: number
  bottom: number
  /** Its width (px), set by dragging its corner; the height follows the picture. */
  width: number
}

function load(): WebcamLayout {
  try {
    const saved = JSON.parse(localStorage.getItem('nova.webcam') ?? 'null')
    if (saved && typeof saved.popped === 'boolean') return {
        popped: saved.popped,
        right: +saved.right || 16,
        bottom: +saved.bottom || 48,
        width: Math.max(200, +saved.width || 280),
      }
  } catch {
    // storage unavailable or unreadable: the defaults
  }
  return { popped: false, right: 16, bottom: 48, width: 280 }
}

export const webcamLayout = $state<WebcamLayout>(load())

export function saveWebcamLayout(): void {
  try {
    localStorage.setItem('nova.webcam', JSON.stringify(webcamLayout))
  } catch {
    // just for this visit
  }
}

/**
 * The live stream's address. Each view adds its own number so two <img>s never share a connection, and a new number
 * reconnects (after a settings change, say).
 */
let streams = 0
export const webcamStreamUrl = () => `/api/webcam/stream?v=${++streams}`

/**
 * For the live <img>: browsers keep an MJPEG connection open after the image leaves the page, which would keep the
 * webcam running for nobody. Pointing it at a blank picture first closes the connection.
 */
export function liveStream(node: HTMLImageElement) {
  return {
    destroy() {
      node.src = 'data:image/gif;base64,R0lGODlhAQABAAAAACw='
    },
  }
}
