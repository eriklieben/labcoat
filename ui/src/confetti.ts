/**
 * Tiny dependency-free confetti burst for "all tests passed". One fixed, pointer-events-free canvas that removes
 * itself after ~1.8 s. Colors come from the theme tokens (--lc-confetti-*). With prefers-reduced-motion nothing
 * animates; the caller shows a static success highlight instead.
 */
const DURATION = 2200;
const COUNT = 150;

/** Reduced motion: the system setting or Settings → Motion (html[data-motion]). */
export { prefersReducedMotion } from "@eriklieben/labcoat-design-system";
import { prefersReducedMotion } from "@eriklieben/labcoat-design-system";

/** The theme's confetti colors (--lc-confetti-1..7), resolved to concrete colors for the canvas. */
function palette(): string[] {
  // Resolve through an element so var()/color-mix() references come back as rgb() strings.
  const probe = document.createElement("span");
  probe.style.display = "none";
  document.body.append(probe);
  const out: string[] = [];
  for (let i = 1; i <= 7; i++) {
    probe.style.color = `var(--lc-confetti-${i})`;
    const c = getComputedStyle(probe).color;
    if (c) out.push(c);
  }
  probe.remove();
  return out.length ? out : ["#512bd4", "#2da44e", "#e8890c"];
}

interface Particle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  rot: number;
  vr: number;
  w: number;
  h: number;
  color: string;
  wobble: number;
  shape: 0 | 1;
}

/** Burst from the middle of `origin` (the main panel); returns false when motion is reduced (nothing drawn). */
export function celebrate(origin?: DOMRect): boolean {
  if (prefersReducedMotion()) return false;
  const dpr = Math.min(2, window.devicePixelRatio || 1);
  const canvas = document.createElement("canvas");
  canvas.className = "confetti-canvas";
  canvas.setAttribute("aria-hidden", "true");
  Object.assign(canvas.style, { position: "fixed", inset: "0", width: "100vw", height: "100vh", pointerEvents: "none", zIndex: "9999" });
  const W = window.innerWidth;
  const H = window.innerHeight;
  canvas.width = W * dpr;
  canvas.height = H * dpr;
  document.body.appendChild(canvas);
  const ctx = canvas.getContext("2d");
  if (!ctx) {
    canvas.remove();
    return false;
  }
  ctx.scale(dpr, dpr);

  const colors = palette();
  // Burst from a point slightly above the center of `origin`, so the fountain arcs up and rains down over it.
  const ox = origin ? origin.left + origin.width / 2 : W / 2;
  const oy = origin ? origin.top + origin.height * 0.42 : H * 0.42;
  const reach = origin ? Math.min(origin.width, origin.height) : Math.min(W, H);
  const power = Math.max(8, Math.min(16, reach / 48));
  const parts: Particle[] = Array.from({ length: COUNT }, () => {
    // Full circle, biased upwards: most pieces shoot up and out, then gravity brings them back through the panel.
    const angle = -Math.PI / 2 + (Math.random() - 0.5) * Math.PI * 1.6;
    const speed = power * (0.45 + Math.random() * 0.75);
    return {
      x: ox + (Math.random() - 0.5) * 24,
      y: oy + (Math.random() - 0.5) * 12,
      vx: Math.cos(angle) * speed,
      vy: Math.sin(angle) * speed,
      rot: Math.random() * Math.PI * 2,
      vr: (Math.random() - 0.5) * 0.35,
      w: 6 + Math.random() * 6,
      h: 3 + Math.random() * 4,
      color: colors[Math.trunc(Math.random() * colors.length)],
      wobble: Math.random() * Math.PI * 2,
      shape: Math.random() < 0.25 ? 1 : 0,
    };
  });

  const t0 = performance.now();
  let last = t0;
  const frame = (now: number) => {
    const t = now - t0;
    const dt = Math.min(2.5, (now - last) / 16.67);
    last = now;
    ctx.clearRect(0, 0, W, H);
    const fade = t > DURATION - 450 ? Math.max(0, (DURATION - t) / 450) : 1;
    for (const p of parts) {
      p.vy += 0.15 * dt; // gravity
      p.vx *= 0.978; // air drag: pieces float down instead of dropping
      p.vy *= 0.978;
      p.wobble += 0.12 * dt;
      p.x += (p.vx + Math.sin(p.wobble) * 0.6) * dt; // drift
      p.y += p.vy * dt;
      p.rot += p.vr * dt;
      if (p.y > H + 20) continue;
      ctx.save();
      ctx.globalAlpha = fade;
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.fillStyle = p.color;
      if (p.shape) {
        ctx.beginPath();
        ctx.arc(0, 0, p.h / 1.6, 0, Math.PI * 2);
        ctx.fill();
      } else {
        // flip along one axis for a paper-like flutter
        ctx.scale(1, Math.cos(p.wobble));
        ctx.fillRect(-p.w / 2, -p.h / 2, p.w, p.h);
      }
      ctx.restore();
    }
    if (t < DURATION) requestAnimationFrame(frame);
    else canvas.remove();
  };
  requestAnimationFrame(frame);
  return true;
}
