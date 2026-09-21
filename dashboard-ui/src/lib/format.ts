/**
 * Small formatting helpers. Everything on the wire is microseconds since the Unix epoch
 * (local wall clock) or a microsecond duration, so all conversions funnel through here.
 */

const DASH = '—';

export function fmtBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return DASH;
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb.toFixed(kb < 10 ? 1 : 0)} KB`;
  const mb = kb / 1024;
  if (mb < 1024) return `${mb.toFixed(mb < 10 ? 1 : 0)} MB`;
  const gb = mb / 1024;
  return `${gb.toFixed(gb < 10 ? 2 : 1)} GB`;
}

/** "4m 12s", "1h 06m", "12.4s", "850ms", "0s". */
export function fmtDuration(us: number | null | undefined): string {
  if (us === null || us === undefined || !Number.isFinite(us) || us < 0) return DASH;
  const ms = us / 1000;
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const totalSeconds = ms / 1000;
  if (totalSeconds < 60) return `${totalSeconds.toFixed(totalSeconds < 10 ? 1 : 0)}s`;
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = Math.floor(totalSeconds % 60);
  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`;
  return `${minutes}m ${String(seconds).padStart(2, '0')}s`;
}

/** Compact duration for dense rows: "4:12", "1:06:33". */
export function fmtDurationClock(us: number | null | undefined): string {
  if (us === null || us === undefined || !Number.isFinite(us) || us < 0) return DASH;
  const totalSeconds = Math.floor(us / 1_000_000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) return `${hours}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

/** Microseconds since epoch -> local `HH:mm:ss.fff`. */
export function fmtClock(us: number | null | undefined): string {
  if (us === null || us === undefined || !Number.isFinite(us)) return DASH;
  const date = new Date(Math.round(us / 1000));
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}:${String(
    date.getSeconds(),
  ).padStart(2, '0')}.${String(date.getMilliseconds()).padStart(3, '0')}`;
}

/** Microseconds since epoch -> local `HH:mm:ss`. */
export function fmtWallClock(us: number | null | undefined): string {
  if (us === null || us === undefined || !Number.isFinite(us)) return DASH;
  const date = new Date(Math.round(us / 1000));
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}:${String(
    date.getSeconds(),
  ).padStart(2, '0')}`;
}

/** Microseconds since epoch -> local `HH:mm`. */
export function fmtHourMinute(us: number | null | undefined): string {
  if (us === null || us === undefined || !Number.isFinite(us)) return DASH;
  const date = new Date(Math.round(us / 1000));
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
}

/** `2026-09-21` -> `Sun 21 Sep 2026` (parsed as a local date, never as UTC). */
export function fmtDayLong(day: string | null | undefined): string {
  const date = parseDay(day);
  if (!date) return day ? day : DASH;
  const weekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  return `${weekdays[date.getDay()]} ${date.getDate()} ${months[date.getMonth()]} ${date.getFullYear()}`;
}

/** `2026-09-21` -> local `Date` at midnight, or `null` when unparsable. */
export function parseDay(day: string | null | undefined): Date | null {
  if (!day) return null;
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const dayOfMonth = Number(match[3]);
  const date = new Date(year, month - 1, dayOfMonth, 0, 0, 0, 0);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function fmtCount(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return DASH;
  return Math.round(value).toLocaleString('en-US');
}

export function fmtNumber(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return DASH;
  return value.toFixed(digits);
}

export function fmtPercent(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return DASH;
  return `${value.toFixed(digits)}%`;
}

export function fmtSpeed(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return DASH;
  return `${Number.isInteger(value) ? value : value.toFixed(2).replace(/0+$/, '').replace(/\.$/, '')}x`;
}

export function fmtText(value: string | null | undefined): string {
  if (value === null || value === undefined) return DASH;
  const trimmed = value.trim();
  return trimmed.length === 0 ? DASH : trimmed;
}

export function fmtBool(value: boolean | null | undefined): string {
  if (value === null || value === undefined) return DASH;
  return value ? 'yes' : 'no';
}

export function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min;
  return Math.min(max, Math.max(min, value));
}

/** Progress (0..1) of `value` inside `[start, start + span]`; safe for degenerate spans. */
export function progressOf(value: number, start: number, span: number): number {
  if (!Number.isFinite(start) || !Number.isFinite(span) || span <= 0) return 0;
  return clamp((value - start) / span, 0, 1);
}

export const EM_DASH = DASH;
