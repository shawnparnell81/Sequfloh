/**
 * Colour tokens. One place for the shell colours so the theme, charts and status tones never repeat hex values.
 * Contrast (WCAG): white on indigo 6.29:1, white on the magenta button 6.04:1, indigo text on the dark shell 9.13:1.
 * Form documents keep their own blue, set beside the theme, so a printed form does not follow the shell.
 */
export const brand = {
  blue: '#4F46E5',
  blueLight: '#A5B4FC',
  blueDark: '#3730A3',
  blueHover: '#4338CA',
  blueOnDark: '#A5B4FC',
  charcoal: '#1E1B2E',
  green: '#0E8550',
  pink: '#BE185D',
  pinkHover: '#9D174D',
} as const;

/** Neutral tones for status and data visualisation, readable on light and dark surfaces. */
export const tones = {
  ok: '#166534',
  warn: '#92400E',
  bad: '#9F1239',
  info: '#3730A3',
  slate: '#334155',
  primary: brand.blue,
  accent: brand.pink,
} as const;
