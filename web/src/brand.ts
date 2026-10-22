/**
 * Colour tokens. One place for the brand colours so the theme, charts and status tones never repeat hex values.
 * Contrast (WCAG): white on blue 6.59:1, white on hover 8.19:1, dark-mode blue text on #121821 5.50:1.
 */
export const brand = {
  blue: '#1257C4',
  blueLight: '#4A86E0',
  blueDark: '#0B3F94',
  blueHover: '#0E4AA8',
  blueOnDark: '#4A8FF0',
  charcoal: '#29333E',
  green: '#0E8550',
} as const;

/** Neutral tones for status and data visualisation, readable on light and dark surfaces. */
export const tones = {
  ok: '#2E7D4F',
  warn: '#B4842A',
  bad: '#A4473B',
  info: '#2F6690',
  slate: '#4F6F7A',
  primary: brand.blue,
  accent: brand.green,
} as const;
