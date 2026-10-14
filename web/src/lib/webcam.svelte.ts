import { createFloatLayout } from './floats.svelte'

/** The webcam's floating view on this browser: popped out over the other tabs, where it sits and its size. */
const float = createFloatLayout('nova.webcam', { right: 16, bottom: 48, width: 280 })
export const webcamLayout = float.layout
export const saveWebcamLayout = float.save

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
