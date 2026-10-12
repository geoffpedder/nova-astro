import type { SequenceTarget } from './types'

/**
 * Targets handed to the sequence from elsewhere (the framing tool): the sequence view adds them to its plan
 * and empties this. Each entry becomes one target, with the frames copied from the plan's last target.
 */
export const sequenceInbox = $state<{ targets: SequenceTarget[] }>({ targets: [] })

/**
 * What the target planner (framing tool) is framing right now, as sequence targets: one, or one per mosaic
 * panel. Lets a sequence target take "From target planner", and fills the Mount panel's RA/Dec (the frame centre).
 */
export const planner = $state<{ frame: { name: string; panels: SequenceTarget[]; centre: { ra: string; dec: string } } | null }>({
  frame: null,
})

/**
 * "Match framing to this image": the image viewer asks the Targets tab to frame exactly what an image shows (its
 * solved centre and rotation), once the solve of that image has arrived there.
 */
export const framing = $state<{ request: { imageId: number; name: string } | null }>({ request: null })

/**
 * An object picked in Stellarium (the Mount panel's From Stellarium): the Targets tab frames it, so the planned
 * frame follows Stellarium as well as the Mount panel's boxes. J2000.
 */
export const stellariumPick = $state<{ request: { name: string; ra: number; dec: number } | null }>({ request: null })

/** Which main view is showing; the framing tool switches to the sequence after adding to it. */
export const mainView = $state<{ view: 'image' | 'sequence' | 'targets' | 'polar' | 'phd2' | 'guiding' | 'activity' }>({ view: 'image' })
