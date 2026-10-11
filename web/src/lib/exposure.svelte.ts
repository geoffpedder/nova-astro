import { api } from './api'
import { act, nova } from './state.svelte'

/**
 * Taking a frame by hand, from the Camera panel or the Image tab's toolbar: one set of settings for both, so the
 * toolbar's Expose takes exactly what the Camera panel shows.
 */
export const exposure = $state({ duration: 5, save: true, loop: false })

/** The image that was latest when the last exposure started (Loop starts the next one when a newer one arrives). */
export const looping = { lastImageId: undefined as number | undefined }

export function expose(): void {
  looping.lastImageId = nova.image?.id
  act(() => api.expose(exposure.duration, exposure.save))
}

export function abortExposure(): void {
  exposure.loop = false
  act(api.abortExposure)
}
