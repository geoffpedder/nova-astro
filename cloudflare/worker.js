// nova-astro.app: static files, with www redirected to the bare domain.
export default {
  async fetch(request, env) {
    const url = new URL(request.url)
    if (url.hostname === 'www.nova-astro.app') {
      url.hostname = 'nova-astro.app'
      return Response.redirect(url.toString(), 301)
    }
    return env.ASSETS.fetch(request)
  },
}
