// The mount in 3D: a strain-wave mount (after ZWO's AM3, to its mechanical drawing) on a tripod with a pier extension,
// carrying a small refractor, its camera and a guide scope. Built from plain shapes, sizes in metres. Loaded only when
// the view is opened (it brings three.js with it).
//
// World: X east, Y up, Z south (so north is -Z).
import {
  BoxGeometry, BufferGeometry, CanvasTexture, CircleGeometry, CylinderGeometry, DirectionalLight, DoubleSide, ExtrudeGeometry,
  Group, HemisphereLight, Line, LineDashedMaterial, Mesh, MeshBasicMaterial, MeshStandardMaterial, PerspectiveCamera,
  RingGeometry, Scene, Shape, SphereGeometry, Sprite, SpriteMaterial, SRGBColorSpace, TorusGeometry, Vector3, WebGLRenderer,
  type Material, type Object3D,
} from 'three'
import type { MountAxes } from './mountAxes'

const rad = (d: number) => (d * Math.PI) / 180

export interface MountScene {
  /** Draws it at this size (CSS pixels). */
  resize(width: number, height: number): void
  /** The site's latitude (degrees, south negative): the polar axis's tilt, and which way the mount faces. */
  setLatitude(latitude: number): void
  /** Where the two axes are turned to. */
  setAxes(axes: Pick<MountAxes, 'a' | 'b'>): void
  /** Whether it's pointing below the horizon (the pointing line turns red). */
  setBelowHorizon(below: boolean): void
  /** Where it's looked at from: round it (radians, from north through east), up from level, and how far (m). */
  look: { azimuth: number; elevation: number; distance: number }
  render(): void
  dispose(): void
}

export function createMountScene(canvas: HTMLCanvasElement): MountScene {
  const renderer = new WebGLRenderer({ canvas, antialias: true, alpha: true })
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
  const scene = new Scene()
  const camera = new PerspectiveCamera(32, 1, 0.05, 30)

  scene.add(new HemisphereLight(0xdfe6f0, 0x2a2622, 2.4))
  const sun = new DirectionalLight(0xffffff, 2.6)
  sun.position.set(1.5, 3, 2)
  scene.add(sun)
  const fill = new DirectionalLight(0xaab4c8, 1)
  fill.position.set(-2, 1.2, -1.5)
  scene.add(fill)

  const mat = (color: number, o: { roughness?: number; metalness?: number } = {}) =>
    new MeshStandardMaterial({ color, roughness: 0.6, metalness: 0.15, ...o })
  const M = {
    black: mat(0x1b1b1d, { roughness: 0.5, metalness: 0.4 }),
    carbon: mat(0x26272b, { roughness: 0.35, metalness: 0.3 }),
    grey: mat(0x4a4c52, { metalness: 0.5 }),
    red: mat(0xc0161d, { roughness: 0.38, metalness: 0.5 }),
    steel: mat(0xb9bcc2, { roughness: 0.25, metalness: 0.9 }),
    white: mat(0xe9e9e6, { roughness: 0.45, metalness: 0.05 }),
    glass: mat(0x1d3f52, { roughness: 0.08, metalness: 0.7 }),
    rubber: mat(0x101010, { roughness: 0.9, metalness: 0 }),
  }

  /** A cylinder along Y from y0 to y1. */
  function cyl(parent: Object3D, r: number, y0: number, y1: number, material: Material, r2 = r) {
    const m = new Mesh(new CylinderGeometry(r2, r, Math.abs(y1 - y0), 40), material)
    m.position.y = (y0 + y1) / 2
    parent.add(m)
    return m
  }
  function box(parent: Object3D, w: number, h: number, d: number, x: number, y: number, z: number, material: Material) {
    const m = new Mesh(new BoxGeometry(w, h, d), material)
    m.position.set(x, y, z)
    parent.add(m)
    return m
  }
  /** A block with rounded edges: w along X, d along Z, from y0 to y1. */
  function rbox(parent: Object3D, w: number, d: number, y0: number, y1: number, r: number, material: Material) {
    const bevel = 0.006
    const hw = w / 2 - bevel, hd = d / 2 - bevel, c = Math.max(0.001, r - bevel)
    const shape = new Shape()
    shape.moveTo(-hw + c, -hd)
    shape.lineTo(hw - c, -hd)
    shape.quadraticCurveTo(hw, -hd, hw, -hd + c)
    shape.lineTo(hw, hd - c)
    shape.quadraticCurveTo(hw, hd, hw - c, hd)
    shape.lineTo(-hw + c, hd)
    shape.quadraticCurveTo(-hw, hd, -hw, hd - c)
    shape.lineTo(-hw, -hd + c)
    shape.quadraticCurveTo(-hw, -hd, -hw + c, -hd)
    const g = new ExtrudeGeometry(shape, {
      depth: y1 - y0 - 2 * bevel, bevelEnabled: true, bevelSize: bevel, bevelThickness: bevel, bevelSegments: 4, curveSegments: 10 })
    g.translate(0, 0, bevel)
    g.rotateX(-Math.PI / 2)
    const m = new Mesh(g, material)
    m.position.y = y0
    parent.add(m)
    return m
  }
  /** A cylinder between two points. */
  function rod(parent: Object3D, a: Vector3, b: Vector3, r: number, material: Material) {
    const dir = new Vector3().subVectors(b, a)
    const m = new Mesh(new CylinderGeometry(r, r, dir.length(), 24), material)
    m.position.copy(a).addScaledVector(dir, 0.5)
    m.quaternion.setFromUnitVectors(new Vector3(0, 1, 0), dir.clone().normalize())
    parent.add(m)
    return m
  }
  /** A group whose own Y axis lies along the parent's Z (for parts on the Dec axis). */
  function alongZ(parent: Object3D) {
    const g = new Group()
    g.rotation.x = Math.PI / 2
    parent.add(g)
    return g
  }
  /** A group whose own Y axis lies along the parent's X (towards +X, or -X). */
  function alongX(parent: Object3D, x: number, y: number, z: number, outwards = 1) {
    const g = new Group()
    g.rotation.z = outwards > 0 ? -Math.PI / 2 : Math.PI / 2
    g.position.set(x, y, z)
    parent.add(g)
    return g
  }
  function label(text: string, color: string, size = 0.09) {
    const c = document.createElement('canvas')
    c.width = c.height = 128
    const g = c.getContext('2d')!
    g.font = "600 76px 'JetBrains Mono Variable', 'JetBrains Mono', Consolas, monospace"
    g.textAlign = 'center'
    g.textBaseline = 'middle'
    g.fillStyle = color
    g.fillText(text, 64, 68)
    const texture = new CanvasTexture(c)
    texture.colorSpace = SRGBColorSpace
    const s = new Sprite(new SpriteMaterial({ map: texture, depthTest: false, transparent: true }))
    s.scale.set(size, size, 1)
    return s
  }

  // ---- The ground and the compass ----
  const ground = new Mesh(new CircleGeometry(0.78, 72), new MeshBasicMaterial({ color: 0x1b1b1b }))
  ground.rotation.x = -Math.PI / 2
  scene.add(ground)
  const rim = new Mesh(new RingGeometry(0.775, 0.785, 96), new MeshBasicMaterial({ color: 0x555555, side: DoubleSide }))
  rim.rotation.x = -Math.PI / 2
  rim.position.y = 0.001
  scene.add(rim)
  for (const [text, x, z, color] of [['N', 0, -0.88, '#c9a45c'], ['E', 0.88, 0, '#a2a2a2'], ['S', 0, 0.88, '#a2a2a2'], ['W', -0.88, 0, '#a2a2a2']] as const) {
    const s = label(text, color)
    s.position.set(x, 0.02, z)
    scene.add(s)
  }
  box(scene, 0.012, 0.002, 0.1, 0, 0.001, -0.72, new MeshBasicMaterial({ color: 0xc9a45c }))

  // ---- The tripod (carbon, three legs, one to the north) and the pier extension, as wide as the mount's base ----
  const HUB = 0.78
  for (let i = 0; i < 3; i++) {
    const a = rad(i * 120) // from north, clockwise seen from above
    const out = new Vector3(Math.sin(a), 0, -Math.cos(a))
    const top = out.clone().multiplyScalar(0.07).setY(HUB - 0.03)
    const foot = out.clone().multiplyScalar(0.46).setY(0.012)
    const knee = top.clone().lerp(foot, 0.52)
    rod(scene, top, knee, 0.021, M.carbon)
    rod(scene, knee, foot, 0.016, M.carbon)
    rod(scene, knee.clone().lerp(top, 0.06), knee.clone().lerp(foot, 0.05), 0.025, M.black) // the leg clamp
    const tip = new Mesh(new SphereGeometry(0.02, 16, 12), M.rubber)
    tip.position.copy(foot)
    scene.add(tip)
    box(scene, 0.05, 0.05, 0.05, top.x, top.y + 0.005, top.z, M.black).rotation.y = -a
  }
  cyl(scene, 0.075, HUB - 0.05, HUB, M.black, 0.07)
  cyl(scene, 0.064, HUB, HUB + 0.01, M.black)
  cyl(scene, 0.058, HUB + 0.01, HUB + 0.19, M.black)
  cyl(scene, 0.064, HUB + 0.19, HUB + 0.2, M.black)
  const TOP = HUB + 0.2

  // ---- The mount, to the AM3's mechanical drawing (mm): base disc 122 across; altitude pivot 64 above the bottom;
  // the box 94 wide, 88 deep (along the Dec axis) and 91.5 long (along the polar axis), starting 40 above the pivot;
  // the Dec axis 74 above the pivot, its flange 80.5 from the polar axis; the saddle 90 x 100 x 27.8, its knobs 59
  // apart. It faces north (the adjusters to the south); in the southern hemisphere the whole head faces south. ----
  const head = new Group()
  scene.add(head)
  cyl(head, 0.061, TOP, TOP + 0.008, M.black) // the base disc
  cyl(head, 0.054, TOP + 0.008, TOP + 0.022, M.red) // the base
  const PIVOT = TOP + 0.064
  for (const x of [-0.046, 0.046]) {
    box(head, 0.008, PIVOT - TOP - 0.022, 0.06, x, (PIVOT + TOP + 0.022) / 2, 0, M.red)
    const side = alongX(head, x, PIVOT, 0, x)
    cyl(side, 0.03, -0.004, 0.004, M.red) // the cheek's arched top
    cyl(side, 0.008, 0.004, 0.014, M.black) // the altitude lock: a hub and a lever
    cyl(side, 0.004, 0.014, 0.016, M.steel)
    box(side, 0.011, 0.006, 0.056, 0, 0.011, 0, M.black).rotation.y = rad(x > 0 ? 40 : -40)
  }
  box(head, 0.03, 0.02, 0.022, 0, TOP + 0.034, 0.05, M.red) // the azimuth block, on the far side from the pole
  const bolts = alongX(head, 0, TOP + 0.036, 0.053, -1)
  cyl(bolts, 0.0035, -0.056, 0.056, M.steel) // its two bolts, with T-handles
  for (const y of [-0.056, 0.056]) {
    box(bolts, 0.026, 0.004, 0.004, 0, y, 0, M.steel)
    for (const x of [-0.013, 0.013]) {
      const ball = new Mesh(new SphereGeometry(0.0045, 12, 10), M.steel)
      ball.position.set(x, y, 0)
      bolts.add(ball)
    }
  }
  const altitude = alongZ(head) // the altitude adjuster's knob, beside it
  altitude.position.set(0.03, TOP + 0.02, 0)
  cyl(altitude, 0.005, 0.04, 0.056, M.steel)
  cyl(altitude, 0.012, 0.056, 0.07, M.black)

  const polar = new Group() // its Y axis points at the pole
  polar.position.set(0, PIVOT, 0)
  head.add(polar)
  polar.add(new Mesh(new SphereGeometry(0.04, 40, 24), M.black)) // the RA drum, round where it pivots
  cyl(polar, 0.04, 0, 0.04, M.black)

  const ra = new Group() // turns about the polar axis
  ra.position.y = 0.04
  polar.add(ra)
  const BOX = 0.0915, DEC = 0.034
  rbox(ra, 0.094, 0.088, 0, BOX - 0.01, 0.008, M.red) // the box
  rbox(ra, 0.094, 0.088, BOX - 0.01, BOX, 0.008, M.black) // its black end, with the sockets
  for (const [x, w] of [[-0.022, 0.013], [-0.002, 0.013], [0.018, 0.009]]) box(ra, w, 0.002, 0.011, x, BOX + 0.0005, -0.018, M.steel)
  box(ra, 0.007, 0.002, 0.007, 0.03, BOX + 0.0005, 0.018, M.red) // and the power light
  const decDrum = alongZ(ra) // the Dec drum, out of the box's side
  decDrum.position.y = DEC
  cyl(decDrum, 0.036, 0.043, 0.0805, M.black)

  const dec = new Group() // turns about the Dec axis (its Z)
  dec.position.set(0, DEC, 0.0805)
  ra.add(dec)
  box(dec, 0.09, 0.1, 0.016, 0, 0, 0.008, M.red) // the saddle: a slab with a channel
  box(dec, 0.016, 0.1, 0.0118, -0.037, 0, 0.0219, M.red)
  box(dec, 0.016, 0.1, 0.0118, 0.037, 0, 0.0219, M.red)
  for (const y of [-0.0295, 0.0295]) {
    const knob = alongX(dec, 0.045, y, 0.02) // its two clamp knobs
    cyl(knob, 0.005, 0, 0.012, M.steel)
    cyl(knob, 0.015, 0.012, 0.03, M.black)
    cyl(knob, 0.006, 0.03, 0.032, M.steel)
  }
  box(dec, 0.044, 0.26, 0.014, 0, 0.06, 0.023, M.grey) // the dovetail bar

  // ---- The telescope: a 71 mm refractor (620 mm from the dew shield's lip to the camera's back), its rings, focuser
  // and focus motor, a cooled camera 90 across and 97 long, and a guide scope ----
  const TUBE = 0.0445
  const ota = new Group() // its Y axis is where it points
  ota.position.set(0, 0.07, 0.03 + 0.02 + TUBE)
  dec.add(ota)
  for (const y of [-0.075, 0.075]) {
    const ring = new Mesh(new TorusGeometry(TUBE + 0.004, 0.0065, 12, 48), M.black)
    ring.rotation.x = Math.PI / 2
    ring.position.y = y
    ota.add(ring)
    box(ota, 0.04, 0.022, 0.022, 0, y, -(TUBE + 0.012), M.black)
    box(ota, 0.03, 0.022, 0.012, 0, y, TUBE + 0.01, M.black)
  }
  box(ota, 0.016, 0.172, 0.01, 0, 0, TUBE + 0.02, M.black) // the handle bar over the rings
  cyl(ota, TUBE, -0.17, 0.13, M.white)
  cyl(ota, 0.052, 0.12, 0.216, M.white) // the dew shield
  cyl(ota, 0.0528, 0.216, 0.224, M.black)
  cyl(ota, 0.0525, 0.117, 0.122, M.red)
  const cell = new Mesh(new CircleGeometry(0.05, 48), M.rubber)
  cell.rotation.x = -Math.PI / 2
  cell.position.y = 0.185
  ota.add(cell)
  const lens = new Mesh(new CircleGeometry(0.0355, 48), M.glass)
  lens.rotation.x = -Math.PI / 2
  lens.position.y = 0.1855
  ota.add(lens)
  cyl(ota, 0.046, -0.185, -0.17, M.black)
  cyl(ota, 0.036, -0.25, -0.185, M.black) // the focuser
  const knobs = alongX(ota, 0, -0.215, 0, -1)
  cyl(knobs, 0.012, -0.055, 0.06, M.grey)
  box(ota, 0.035, 0.05, 0.04, -0.078, -0.215, 0, M.black) // the focus motor
  box(ota, 0.004, 0.04, 0.03, -0.096, -0.215, 0, M.red)
  cyl(ota, 0.027, -0.285, -0.25, M.grey) // the drawtube
  cyl(ota, 0.04, -0.295, -0.285, M.black)
  cyl(ota, 0.045, -0.375, -0.295, M.red) // the camera
  cyl(ota, 0.0452, -0.392, -0.375, M.black)
  cyl(ota, 0.03, -0.396, -0.392, M.grey)
  const guide = new Group()
  guide.position.set(0, -0.01, TUBE + 0.055)
  ota.add(guide)
  box(guide, 0.02, 0.06, 0.03, 0, 0, -0.022, M.black)
  cyl(guide, 0.017, -0.055, 0.06, M.white)
  cyl(guide, 0.0195, 0.06, 0.085, M.black)
  cyl(guide, 0.014, -0.11, -0.055, M.red)

  // Where it points, and the polar axis.
  const rayMaterial = new LineDashedMaterial({ color: 0xc9a45c, dashSize: 0.03, gapSize: 0.02, transparent: true, opacity: 0.85 })
  const ray = new Line(new BufferGeometry().setFromPoints([new Vector3(0, 0.226, 0), new Vector3(0, 0.95, 0)]), rayMaterial)
  ray.computeLineDistances()
  ota.add(ray)
  const axis = new Line(
    new BufferGeometry().setFromPoints([new Vector3(0, 0.15, 0), new Vector3(0, 1.0, 0)]),
    new LineDashedMaterial({ color: 0x8a8a8a, dashSize: 0.012, gapSize: 0.02, transparent: true, opacity: 0.6 }))
  axis.computeLineDistances()
  polar.add(axis)
  const pole = label('✦', '#a2a2a2', 0.07)
  pole.position.set(0, 1.04, 0)
  polar.add(pole)

  const look = { azimuth: rad(215), elevation: rad(14), distance: 3.1 }
  const centre = new Vector3(0, 0.92, 0)

  return {
    look,
    resize(width, height) {
      if (width <= 0 || height <= 0) return
      renderer.setSize(width, height, false)
      camera.aspect = width / height
      camera.updateProjectionMatrix()
    },
    setLatitude(latitude) {
      head.rotation.y = latitude < 0 ? Math.PI : 0
      polar.rotation.x = -(Math.PI / 2 - rad(Math.abs(latitude)))
    },
    setAxes({ a, b }) {
      ra.rotation.y = rad(a)
      dec.rotation.z = rad(b)
    },
    setBelowHorizon(below) {
      rayMaterial.color.set(below ? 0xd06a5a : 0xc9a45c)
    },
    render() {
      camera.position.set(
        centre.x + look.distance * Math.cos(look.elevation) * Math.sin(look.azimuth),
        centre.y + look.distance * Math.sin(look.elevation),
        centre.z - look.distance * Math.cos(look.elevation) * Math.cos(look.azimuth))
      camera.lookAt(centre)
      renderer.render(scene, camera)
    },
    dispose() {
      scene.traverse((o) => {
        const mesh = o as Mesh
        mesh.geometry?.dispose()
        const material = mesh.material as Material | Material[] | undefined
        for (const m of Array.isArray(material) ? material : material ? [material] : []) {
          ;(m as SpriteMaterial).map?.dispose()
          m.dispose()
        }
      })
      renderer.dispose()
    },
  }
}
