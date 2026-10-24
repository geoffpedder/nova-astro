// Downloads a file NOVA makes on request (a zip of logs, a backup), named as the server names it. Throws with the
// server's reason if it couldn't be made.
export async function download(url: string, fallbackName: string): Promise<void> {
  const response = await fetch(url)
  if (!response.ok) {
    let reason = `${response.status} ${response.statusText}`
    try {
      const problem = await response.json()
      reason = problem.detail ?? problem.title ?? reason
    } catch {
      // keep the status
    }
    throw new Error(reason)
  }
  const name = /filename="?([^";]+)"?/.exec(response.headers.get('content-disposition') ?? '')?.[1] ?? fallbackName
  const link = URL.createObjectURL(await response.blob())
  const a = document.createElement('a')
  a.href = link
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(link), 10_000)
}
