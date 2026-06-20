// ---------------------------------------------------------------------------
// GrainsVisualization
//
// A reusable, framework-agnostic Canvas visualization of an Orleans cluster:
// silos rendered as large rounded cards, grains as small circular particles
// living inside the silo they're activated on, and recent grain-to-grain calls
// drawn as short-lived energy pulses (a fading line plus a dot travelling from
// source to target).
//
// Design goals baked in here:
//   * Visual nodes are LONG-LIVED. `setGrains()` reconciles against a
//     Map<grainId, GrainNode> — existing nodes are updated in place, new grains
//     get a node, vanished grains fade out and are removed. We never rebuild the
//     particle set on every poll, so positions/velocities/heat persist.
//   * When a grain changes silo we only change its attraction target; the
//     particle then physically drifts to the new card instead of teleporting.
//   * Lightweight custom physics (center attraction + local repulsion + soft
//     containment) keeps hundreds of particles smooth without a heavy dependency.
//
// The class is intentionally decoupled from the data source and from any
// framework: feed it plain records via setGrains()/emitCalls(), drive sizing via
// resize(), and start()/stop() the animation loop. Colours and labels are
// supplied through options so the same class can visualize any cluster.
// ---------------------------------------------------------------------------

export interface GrainInput {
  /** Stable identity, e.g. "presenter/alice". Used as the node map key. */
  id: string;
  /** Coarse type/category used for colouring, e.g. "presenter". */
  type: string;
  /** Silo this grain is currently activated on. Changing it makes the
   *  particle drift to the new silo card. */
  silo: string;
  /** Short name drawn under the particle, e.g. "alice". Defaults to `id`. */
  label?: string;
}

export interface CallInput {
  sourceId: string;
  targetId: string;
  /** Optional override colour for the pulse; defaults to the target's colour. */
  color?: string;
  success?: boolean;
}

export interface GrainsVizOptions {
  /** Colour for a grain/pulse given its type. */
  colorForType?: (type: string) => string;
  /** Human label for a silo, drawn on its card. */
  siloLabel?: (silo: string) => string;
  /** Background fill for the canvas. */
  background?: string;
}

// --- Tunable physics / animation constants ---------------------------------
const CENTER_PULL = 0.012; // spring strength toward the assigned silo centre
const REPEL = 1.1; // local repulsion strength between silo-mates
const REPEL_RANGE = 44; // px; repulsion only acts within this radius
const WALL_PUSH = 0.06; // soft containment force near a silo's inner edge
const DAMPING = 0.86; // velocity damping per frame
const MAX_SPEED = 6; // px/frame clamp to keep things stable
const BASE_RADIUS = 4.5; // resting grain radius
const SPAWN_JITTER = 26; // spread when a grain first appears at its centre

const HEAT_DECAY = 0.95; // per-frame multiplicative decay of "busyness"
const DOT_TRAVEL_MS = 550; // time the call dot takes to reach the target
const LINE_LIFE_MS = 1500; // total lifetime of a call line (dot is faster)
const CALL_STAGGER_MS = 90; // gap between calls in one batch so bursts play out
const FADE_OUT_MS = 350; // node removal fade

interface GrainNode {
  id: string;
  type: string;
  silo: string;
  label: string;
  x: number;
  y: number;
  vx: number;
  vy: number;
  heat: number; // 0..~1, how busy this grain is right now
  radius: number; // current rendered radius (eased)
  alpha: number; // 0..1, used for spawn-in / fade-out
  dead: boolean; // marked for removal; fades out then is deleted
}

interface SiloBox {
  silo: string;
  x: number;
  y: number;
  w: number;
  h: number;
  cx: number;
  cy: number;
  count: number;
}

interface CallAnim {
  sourceId: string;
  targetId: string;
  color: string;
  start: number; // performance.now() timestamp when the dot departs
  ignited: boolean; // whether we've already bumped endpoint heat
}

const CARD_MARGIN = 14; // gap between silo cards
const CARD_PAD = 22; // inner padding (grains kept inside this)
const HEADER_H = 30; // reserved strip at the top of a card for its label

export class GrainsVisualization {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private opts: Required<GrainsVizOptions>;

  // Long-lived visual nodes, keyed by grain id. This is the heart of the
  // "don't recreate particles every update" requirement.
  private nodes = new Map<string, GrainNode>();
  private silos = new Map<string, SiloBox>();
  private calls: CallAnim[] = [];

  private width = 0;
  private height = 0;
  private dpr = 1;
  private raf = 0;
  private running = false;
  private lastFrame = 0;

  constructor(canvas: HTMLCanvasElement, options: GrainsVizOptions = {}) {
    this.canvas = canvas;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('2D canvas context unavailable');
    this.ctx = ctx;
    this.opts = {
      colorForType: options.colorForType ?? (() => '#64748b'),
      siloLabel: options.siloLabel ?? ((s) => s),
      background: options.background ?? '#0b1120'
    };
  }

  // --- Sizing --------------------------------------------------------------
  // Called by the host whenever the container resizes. Keeps the backing store
  // crisp on HiDPI displays and re-lays-out the silo cards for the new size.
  resize(cssWidth: number, cssHeight: number): void {
    this.dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.width = Math.max(1, cssWidth);
    this.height = Math.max(1, cssHeight);
    this.canvas.width = Math.round(this.width * this.dpr);
    this.canvas.height = Math.round(this.height * this.dpr);
    this.canvas.style.width = `${this.width}px`;
    this.canvas.style.height = `${this.height}px`;
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    this.layoutSilos();
  }

  // --- Data reconciliation -------------------------------------------------
  // Update existing nodes, add nodes for new grains, fade out grains that have
  // disappeared. Never rebuilds the whole set.
  setGrains(grains: GrainInput[]): void {
    const seen = new Set<string>();

    for (const g of grains) {
      seen.add(g.id);
      let node = this.nodes.get(g.id);
      if (!node) {
        node = this.spawn(g);
        this.nodes.set(g.id, node);
      } else {
        // Update in place. Changing `silo` retargets the spring, so the
        // particle drifts to the new card rather than jumping.
        node.type = g.type;
        node.silo = g.silo;
        node.label = g.label ?? g.id;
        node.dead = false;
      }
    }

    // Grains no longer present begin fading out (removed once invisible).
    for (const node of this.nodes.values()) {
      if (!seen.has(node.id)) node.dead = true;
    }

    this.layoutSilos();
  }

  // Queue a batch of recent calls as animated pulses, staggered so a burst from
  // one poll plays out over time instead of flashing all at once.
  emitCalls(calls: CallInput[]): void {
    const base = performance.now();
    calls.forEach((c, i) => {
      this.calls.push({
        sourceId: c.sourceId,
        targetId: c.targetId,
        color: c.color ?? this.opts.colorForType(this.nodes.get(c.targetId)?.type ?? ''),
        start: base + i * CALL_STAGGER_MS,
        ignited: false
      });
    });
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.lastFrame = performance.now();
    const loop = () => {
      if (!this.running) return;
      this.frame();
      this.raf = requestAnimationFrame(loop);
    };
    this.raf = requestAnimationFrame(loop);
  }

  stop(): void {
    this.running = false;
    if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = 0;
  }

  destroy(): void {
    this.stop();
    this.nodes.clear();
    this.silos.clear();
    this.calls = [];
  }

  /** Live counts, handy for the host to render a header. */
  stats(): { grains: number; silos: number } {
    let grains = 0;
    for (const n of this.nodes.values()) if (!n.dead) grains++;
    return { grains, silos: this.silos.size };
  }

  // --- Node creation -------------------------------------------------------
  private spawn(g: GrainInput): GrainNode {
    const box = this.silos.get(g.silo);
    const cx = box ? box.cx : this.width / 2;
    const cy = box ? box.cy : this.height / 2;
    return {
      id: g.id,
      type: g.type,
      silo: g.silo,
      label: g.label ?? g.id,
      x: cx + (Math.random() - 0.5) * SPAWN_JITTER,
      y: cy + (Math.random() - 0.5) * SPAWN_JITTER,
      vx: 0,
      vy: 0,
      heat: 0,
      radius: BASE_RADIUS,
      alpha: 0, // eases up to 1 (spawn-in)
      dead: false
    };
  }

  // --- Silo layout ---------------------------------------------------------
  // Arrange the distinct silos as a responsive grid of cards filling the canvas.
  // Recomputed on data change and on resize; node physics read the boxes each
  // frame, so retargeting on relayout is automatically smooth.
  private layoutSilos(): void {
    const counts = new Map<string, number>();
    for (const n of this.nodes.values()) {
      if (n.dead) continue;
      counts.set(n.silo, (counts.get(n.silo) ?? 0) + 1);
    }
    const ids = [...counts.keys()].sort();
    const n = ids.length;

    const next = new Map<string, SiloBox>();
    if (n === 0 || this.width <= 0 || this.height <= 0) {
      this.silos = next;
      return;
    }

    // Choose a column count that keeps cards close to the canvas aspect ratio.
    const aspect = this.width / this.height;
    let cols = Math.max(1, Math.round(Math.sqrt(n * aspect)));
    cols = Math.min(cols, n);
    const rows = Math.ceil(n / cols);

    const cellW = this.width / cols;
    const cellH = this.height / rows;

    ids.forEach((silo, i) => {
      const col = i % cols;
      const row = Math.floor(i / cols);
      const x = col * cellW + CARD_MARGIN;
      const y = row * cellH + CARD_MARGIN;
      const w = cellW - CARD_MARGIN * 2;
      const h = cellH - CARD_MARGIN * 2;
      next.set(silo, {
        silo,
        x,
        y,
        w,
        h,
        cx: x + w / 2,
        cy: y + HEADER_H + (h - HEADER_H) / 2,
        count: counts.get(silo) ?? 0
      });
    });
    this.silos = next;
  }

  // Inner rectangle a grain is gently kept within (below the header strip).
  private innerRect(box: SiloBox) {
    return {
      left: box.x + CARD_PAD,
      right: box.x + box.w - CARD_PAD,
      top: box.y + HEADER_H + CARD_PAD * 0.5,
      bottom: box.y + box.h - CARD_PAD
    };
  }

  // --- Simulation + render per frame --------------------------------------
  private frame(): void {
    const now = performance.now();
    this.lastFrame = now;

    this.simulate();
    this.render(now);
  }

  private simulate(): void {
    // Bucket live nodes by silo so repulsion only considers silo-mates — this
    // keeps the pairwise cost low enough for hundreds of grains.
    const buckets = new Map<string, GrainNode[]>();
    for (const node of this.nodes.values()) {
      // Ease spawn-in / fade-out alpha; delete fully-faded dead nodes.
      if (node.dead) {
        node.alpha -= 1 / (FADE_OUT_MS / 16);
        node.radius *= 0.94;
        if (node.alpha <= 0) this.nodes.delete(node.id);
      } else if (node.alpha < 1) {
        node.alpha = Math.min(1, node.alpha + 0.08);
      }
      node.heat *= HEAT_DECAY;

      let arr = buckets.get(node.silo);
      if (!arr) buckets.set(node.silo, (arr = []));
      arr.push(node);
    }

    for (const [silo, arr] of buckets) {
      const box = this.silos.get(silo);
      for (let i = 0; i < arr.length; i++) {
        const a = arr[i];
        let fx = 0;
        let fy = 0;

        // Attraction toward the assigned silo centre (the drift spring).
        if (box) {
          fx += (box.cx - a.x) * CENTER_PULL;
          fy += (box.cy - a.y) * CENTER_PULL;
        }

        // Local repulsion: short-range, only against silo-mates.
        for (let j = i + 1; j < arr.length; j++) {
          const b = arr[j];
          let dx = a.x - b.x;
          let dy = a.y - b.y;
          let d2 = dx * dx + dy * dy;
          if (d2 > REPEL_RANGE * REPEL_RANGE) continue;
          if (d2 < 0.01) {
            // Coincident: nudge apart deterministically-ish.
            dx = (Math.random() - 0.5) * 0.5;
            dy = (Math.random() - 0.5) * 0.5;
            d2 = dx * dx + dy * dy + 0.01;
          }
          const d = Math.sqrt(d2);
          const f = (REPEL * (1 - d / REPEL_RANGE)) / d;
          const fxr = dx * f;
          const fyr = dy * f;
          fx += fxr;
          fy += fyr;
          b.vx -= fxr;
          b.vy -= fyr;
        }

        // Soft containment near the card's inner walls (keeps grains inside
        // without hard snapping, so a cross-silo drift stays smooth).
        if (box) {
          const r = this.innerRect(box);
          const pad = a.radius + 2;
          if (a.x < r.left + pad) fx += (r.left + pad - a.x) * WALL_PUSH;
          if (a.x > r.right - pad) fx += (r.right - pad - a.x) * WALL_PUSH;
          if (a.y < r.top + pad) fy += (r.top + pad - a.y) * WALL_PUSH;
          if (a.y > r.bottom - pad) fy += (r.bottom - pad - a.y) * WALL_PUSH;
        }

        a.vx = (a.vx + fx) * DAMPING;
        a.vy = (a.vy + fy) * DAMPING;

        // Clamp speed for stability.
        const sp = Math.hypot(a.vx, a.vy);
        if (sp > MAX_SPEED) {
          a.vx = (a.vx / sp) * MAX_SPEED;
          a.vy = (a.vy / sp) * MAX_SPEED;
        }
        a.x += a.vx;
        a.y += a.vy;

        // Busy grains swell, idle grains shrink — eased toward the target size.
        const target = BASE_RADIUS * (1 + a.heat * 0.9) * (a.dead ? 1 : 1);
        a.radius += (target - a.radius) * 0.2;
      }
    }

    // Keep everything on-screen as a final safety net.
    for (const node of this.nodes.values()) {
      node.x = Math.max(2, Math.min(this.width - 2, node.x));
      node.y = Math.max(2, Math.min(this.height - 2, node.y));
    }
  }

  private render(now: number): void {
    const ctx = this.ctx;
    ctx.clearRect(0, 0, this.width, this.height);
    ctx.fillStyle = this.opts.background;
    ctx.fillRect(0, 0, this.width, this.height);

    this.drawSilos(ctx);
    this.drawCalls(ctx, now);
    this.drawGrains(ctx, now);
  }

  private drawSilos(ctx: CanvasRenderingContext2D): void {
    ctx.save();
    ctx.font = '600 13px ui-sans-serif, system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    for (const box of this.silos.values()) {
      // Card background + border.
      this.roundRect(ctx, box.x, box.y, box.w, box.h, 18);
      ctx.fillStyle = 'rgba(148, 163, 184, 0.06)';
      ctx.fill();
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = 'rgba(148, 163, 184, 0.22)';
      ctx.stroke();

      // Header: silo name + grain count badge.
      ctx.fillStyle = 'rgba(226, 232, 240, 0.92)';
      ctx.textAlign = 'left';
      ctx.fillText(this.opts.siloLabel(box.silo), box.x + 16, box.y + HEADER_H / 2 + 4);

      const badge = `${box.count}`;
      ctx.textAlign = 'right';
      ctx.fillStyle = 'rgba(148, 163, 184, 0.7)';
      ctx.font = '600 11px ui-sans-serif, system-ui, sans-serif';
      ctx.fillText(`${badge} grain${box.count === 1 ? '' : 's'}`, box.x + box.w - 14, box.y + HEADER_H / 2 + 4);
      ctx.font = '600 13px ui-sans-serif, system-ui, sans-serif';
    }
    ctx.restore();
  }

  // Recent calls: a fading line from source to target, plus a dot travelling
  // along it from source -> target. The dot reaches the target well before the
  // line finishes fading, so direction reads clearly.
  private drawCalls(ctx: CanvasRenderingContext2D, now: number): void {
    ctx.save();
    ctx.lineCap = 'round';
    const survivors: CallAnim[] = [];

    for (const call of this.calls) {
      const t = now - call.start;
      if (t < 0) {
        survivors.push(call); // scheduled but not yet started
        continue;
      }
      if (t >= LINE_LIFE_MS) continue; // expired — drops out

      const from = this.nodes.get(call.sourceId);
      const to = this.nodes.get(call.targetId);
      if (!from || !to) {
        survivors.push(call);
        continue;
      }

      // Ignite endpoint heat exactly when the pulse departs.
      if (!call.ignited) {
        from.heat = Math.min(1.2, from.heat + 0.8);
        to.heat = Math.min(1.2, to.heat + 1);
        call.ignited = true;
      }
      survivors.push(call);

      const lineAlpha = Math.max(0, 1 - t / LINE_LIFE_MS);
      const p = Math.min(1, t / DOT_TRAVEL_MS); // dot progress (faster)
      const dotX = from.x + (to.x - from.x) * p;
      const dotY = from.y + (to.y - from.y) * p;

      // Fading full line source -> target.
      ctx.globalAlpha = lineAlpha * 0.5;
      ctx.strokeStyle = call.color;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(from.x, from.y);
      ctx.lineTo(to.x, to.y);
      ctx.stroke();

      // Brighter comet trail from source up to the dot.
      ctx.globalAlpha = lineAlpha;
      ctx.lineWidth = 2.5;
      ctx.beginPath();
      ctx.moveTo(from.x, from.y);
      ctx.lineTo(dotX, dotY);
      ctx.stroke();

      // The travelling direction dot, with a soft glow.
      ctx.globalAlpha = lineAlpha;
      ctx.fillStyle = call.color;
      ctx.beginPath();
      ctx.arc(dotX, dotY, 3.5, 0, Math.PI * 2);
      ctx.fill();
      ctx.globalAlpha = lineAlpha * 0.25;
      ctx.beginPath();
      ctx.arc(dotX, dotY, 8, 0, Math.PI * 2);
      ctx.fill();
    }

    this.calls = survivors;
    ctx.restore();
  }

  private drawGrains(ctx: CanvasRenderingContext2D, now: number): void {
    ctx.save();
    const pulse = (Math.sin(now / 220) + 1) / 2; // 0..1, shared pulse phase
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.font = '600 8px ui-sans-serif, system-ui, sans-serif';
    ctx.lineJoin = 'round';

    for (const node of this.nodes.values()) {
      const color = this.opts.colorForType(node.type);
      const a = node.alpha;
      const r = node.radius;

      // Hot grains (busy) get an expanding pulse ring.
      if (node.heat > 0.35) {
        const ringR = r + 4 + node.heat * 6 + pulse * 3;
        ctx.globalAlpha = a * node.heat * 0.4;
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(node.x, node.y, ringR, 0, Math.PI * 2);
        ctx.stroke();
      }

      // Soft halo — stronger for busier grains.
      ctx.globalAlpha = a * (0.15 + node.heat * 0.35);
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(node.x, node.y, r + 3, 0, Math.PI * 2);
      ctx.fill();

      // Core. Idle grains are dimmer; active/busy grains read at full strength.
      ctx.globalAlpha = a * (0.55 + Math.min(0.45, node.heat * 0.6));
      ctx.beginPath();
      ctx.arc(node.x, node.y, r, 0, Math.PI * 2);
      ctx.fill();

      // Crisp rim for readability against the dark card.
      ctx.globalAlpha = a * 0.9;
      ctx.lineWidth = 1;
      ctx.strokeStyle = 'rgba(255,255,255,0.55)';
      ctx.beginPath();
      ctx.arc(node.x, node.y, r, 0, Math.PI * 2);
      ctx.stroke();

      // Name label under the particle. Drawn with a dark outline (stroke behind
      // fill) so it stays legible over neighbouring grains and card fills.
      const text = node.label.length > 14 ? `${node.label.slice(0, 13)}…` : node.label;
      const ty = node.y + r + 3;
      ctx.globalAlpha = a * 0.45;
      ctx.lineWidth = 2;
      ctx.strokeStyle = this.opts.background;
      ctx.strokeText(text, node.x, ty);
      ctx.fillStyle = 'rgba(226, 232, 240, 0.85)';
      ctx.fillText(text, node.x, ty);
    }
    ctx.restore();
  }

  private roundRect(
    ctx: CanvasRenderingContext2D,
    x: number,
    y: number,
    w: number,
    h: number,
    r: number
  ): void {
    const rr = Math.min(r, w / 2, h / 2);
    ctx.beginPath();
    ctx.moveTo(x + rr, y);
    ctx.arcTo(x + w, y, x + w, y + h, rr);
    ctx.arcTo(x + w, y + h, x, y + h, rr);
    ctx.arcTo(x, y + h, x, y, rr);
    ctx.arcTo(x, y, x + w, y, rr);
    ctx.closePath();
  }
}
