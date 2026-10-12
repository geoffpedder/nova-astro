/**
 * How the guide camera's frames are shown on this browser: guide frames are short and faint, so a full auto stretch
 * mostly shows noise. Low: black background, just the stars; medium (default): a hint of background; high: the full
 * auto stretch, for finding faint stars. Remembered per browser.
 */
export type GuideStretch = 'low' | 'medium' | 'high'

function load(): GuideStretch {
  try {
    const value = localStorage.getItem('nova.guide.stretch')
    return value === 'low' || value === 'high' ? value : 'medium'
  } catch {
    return 'medium'
  }
}

export const guideView = $state({ stretch: load() })

export function setGuideStretch(stretch: GuideStretch): void {
  guideView.stretch = stretch
  try {
    localStorage.setItem('nova.guide.stretch', stretch)
  } catch {
    // storage unavailable: just for this visit
  }
}
