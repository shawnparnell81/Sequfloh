import { alpha, createTheme, lighten, useTheme, type PaletteMode } from '@mui/material/styles';
import '@fontsource/manrope/400.css';
import '@fontsource/manrope/500.css';
import '@fontsource/manrope/600.css';
import '@fontsource/manrope/700.css';
import '@fontsource/ibm-plex-mono/400.css';
import { brand } from './brand';
const formFamilies = ['report', 'worksheet', 'checklist', 'certificate', 'investigation', 'register', 'plan'] as const;
type FormFamily = (typeof formFamilies)[number];
interface FamilyPalette { palette: 'palette1' | 'palette2' | 'custom'; edge?: string | null; accent?: string | null }

/** Shell and form tokens: what the top bar, page headers, home page and form renderer draw with, for the current light or dark mode. */
export interface ShellTokens {
  /** Page background (CSS background shorthand: soft blue and green glows over a light or charcoal base). */
  page: string;
  /** Logo blue to green, used on badges, selected menu items, primary buttons and section rules. */
  gradient: string;
  /** Second accent beside the logo blue. */
  green: string;
  /** Coloured shadow under badges and selected items. */
  glow: string;
  /** Frosted surfaces: translucent fill for side menu, form sections and tables, their light edge and the search field. */
  glass: { side: string; section: string; table: string; edge: string; field: string };
  /** Top bar. */
  bar: { bg: string; fg: string; muted: string; activeLine: string };
  /** Page header under the top bar (sits on the page background). */
  header: { fg: string; muted: string };
  /** Module badge beside page titles and on the home page. */
  icon: { size: number; radius: number; hoverBg: string };
  /** Home greeting text fill. */
  greeting: string;
  /** Form renderer: corner radius, paper padding and callout fill. */
  form: { radius: number; paperPad: number; callout: string };
  /** Colours of each form family: edge (section bars, badges, rules, table heads) and accent (numbers, callouts). */
  families: Record<FormFamily, { edge: string; accent: string }>;
}

declare module '@mui/material/styles' {
  interface TypeBackground { sidebar: string; bar: string; band: string }
  interface Theme { tk: ShellTokens }
  interface ThemeOptions { tk?: ShellTokens }
}

/** Accent green: deep enough for white text (4.6:1) and to sit beside the logo blue. Slate is the second form palette's edge. */
export const GREEN = '#0E8550', GREEN_ON_DARK = '#4CC98A', SLATE = '#334155', SLATE_ON_DARK = '#9AA9BF';
/** Form documents keep this blue so a printed form does not pick up the shell's indigo. */
const formBlue = '#1257C4', formBlueOnDark = '#4A8FF0';

/** The two built-in form palettes: 1 green and blue (checklists, investigations, plans), 2 slate and blue (certificates, reports, worksheets, registers). */
export const formPalettes = { palette1: { edge: GREEN, accent: formBlue }, palette2: { edge: SLATE, accent: formBlue } } as const;
export const defaultPalette = (f: FormFamily): 'palette1' | 'palette2' => (f === 'checklist' || f === 'investigation' || f === 'plan' ? 'palette1' : 'palette2');

/** Options from Settings › Appearance (organization default, then the person's override). */
export interface ThemeOptionsExtra { accent?: string | null; forms?: Partial<Record<FormFamily, FamilyPalette>> }

function familyColours(dark: boolean, forms: ThemeOptionsExtra['forms']): ShellTokens['families'] {
  const lift = (c: string) => (dark ? lighten(c, 0.35) : c);
  const out = {} as ShellTokens['families'];
  for (const f of formFamilies) {
    const p = forms?.[f];
    if (p?.palette === 'custom' && valid(p.edge) && valid(p.accent)) out[f] = { edge: lift(p.edge!), accent: lift(p.accent!) };
    else if ((p?.palette ?? defaultPalette(f)) === 'palette1') out[f] = dark ? { edge: GREEN_ON_DARK, accent: formBlueOnDark } : { ...formPalettes.palette1 };
    else out[f] = dark ? { edge: SLATE_ON_DARK, accent: formBlueOnDark } : { ...formPalettes.palette2 };
  }
  return out;
}

const valid = (c?: string | null) => (c && /^#[0-9a-fA-F]{6}$/.test(c) ? c : null);

function tokens(dark: boolean, main: string, accent: string | null, forms: ThemeOptionsExtra['forms']): ShellTokens {
  const green = accent ? (dark ? lighten(accent, 0.3) : accent) : dark ? GREEN_ON_DARK : GREEN;
  const families = familyColours(dark, forms);
  const gradient = `linear-gradient(135deg, ${main} 0%, ${green} 100%)`;
  const hoverBg = `linear-gradient(135deg, ${green} 0%, ${main} 100%)`;
  return dark
    ? {
        page: `radial-gradient(900px 520px at 0% 0%, rgba(129,140,248,0.16), transparent 60%), radial-gradient(800px 480px at 100% 0%, rgba(244,114,182,0.10), transparent 55%), linear-gradient(180deg, #16141F 0%, #1A1726 100%)`,
        gradient, green, glow: 'rgba(0,0,0,0.45)',
        glass: { side: '#241F33', section: '#2A2540', table: '#241F33', edge: 'rgba(255,255,255,0.08)', field: 'rgba(255,255,255,0.06)' },
        bar: { bg: '#1E1A2C', fg: '#F4F1FB', muted: '#B7B3C9', activeLine: main },
        header: { fg: '#F4F1FB', muted: '#B7B3C9' },
        icon: { size: 48, radius: 14, hoverBg },
        greeting: `linear-gradient(90deg, #F4F1FB 0%, ${main} 70%, ${brand.pink} 100%)`,
        form: { radius: 14, paperPad: 4, callout: 'rgba(165,180,252,0.10)' }, families,
      }
    : {
        page: 'linear-gradient(180deg, #F4F1FB 0%, #EEF1F8 100%)',
        gradient, green, glow: 'rgba(79,70,229,0.22)',
        glass: { side: '#FFFFFF', section: '#FFFFFF', table: '#FFFFFF', edge: '#E6E8F2', field: '#F4F2FA' },
        bar: { bg: '#FFFFFF', fg: '#1E1B2E', muted: '#667085', activeLine: main },
        header: { fg: '#1E1B2E', muted: '#667085' },
        icon: { size: 48, radius: 14, hoverBg },
        greeting: `linear-gradient(90deg, #1E1B2E 0%, ${main} 62%, ${brand.pink} 100%)`,
        form: { radius: 14, paperPad: 4, callout: 'rgba(79,70,229,0.06)' }, families,
      };
}

/**
 * Application theme: a pale lavender page, white rounded cards, an indigo primary and a magenta button, in light and dark modes that share one
 * structure. An organization colour from Settings › Branding replaces the light-mode primary; dark mode keeps the lifted indigo so text stays readable.
 * Printed pages drop the shadows and the magenta fill so a form or job report still prints as a plain document.
 */
export const makeTheme = (primary?: string | null, mode: PaletteMode = 'light', options: ThemeOptionsExtra = {}) => {
  const dark = mode === 'dark';
  const custom = dark ? null : valid(primary);
  const main = custom ?? (dark ? brand.blueOnDark : brand.blue);
  const focus = dark ? '#8DB8F7' : brand.blue;
  const tk = tokens(dark, main, valid(options.accent), options.forms);
  const line = dark ? 'rgba(255,255,255,0.10)' : '#E6E8F2';
  const paper = dark ? '#241F33' : '#FFFFFF';
  const solid = dark ? '#241F33' : '#FFFFFF';
  const pink = brand.pink;
  const chip = dark
    ? { success: ['#064E3B', '#BBF7D0'], warning: ['#78350F', '#FDE68A'], error: ['#881337', '#FECDD3'], info: ['#312E81', '#C7D2FE'], primary: ['#4C1D95', '#E9D5FF'], secondary: ['#831843', '#FBCFE8'], default: ['#334155', '#E2E8F0'] }
    : { success: ['#DCFCE7', '#166534'], warning: ['#FEF3C7', '#92400E'], error: ['#FCE7F3', '#9F1239'], info: ['#E0E7FF', '#3730A3'], primary: ['#EDE9FE', '#5B21B6'], secondary: ['#FCE7F3', '#9D174D'], default: ['#F1F5F9', '#334155'] };
  return createTheme({
    tk,
    palette: {
      mode,
      primary: custom
        ? { main: custom, contrastText: '#FFFFFF' }
        : dark
          ? { main: brand.blueOnDark, light: '#7AAEF5', dark: '#2F74D6', contrastText: '#0B1220' }
          : { main: brand.blue, light: brand.blueLight, dark: brand.blueDark, contrastText: '#FFFFFF' },
      secondary: { main: pink, dark: brand.pinkHover, light: '#F472B6', contrastText: '#FFFFFF' },
      background: dark
        ? { default: '#16141F', paper: solid, sidebar: tk.glass.side, bar: '#1E1A2C', band: 'rgba(255,255,255,0.04)' }
        : { default: '#F4F1FB', paper: solid, sidebar: '#FFFFFF', bar: '#FFFFFF', band: 'rgba(79,70,229,0.05)' },
      text: dark ? { primary: '#F4F1FB', secondary: '#B7B3C9' } : { primary: '#1E1B2E', secondary: '#667085' },
      divider: line,
      success: { main: '#067647', light: '#BBF7D0', dark: '#14532D', contrastText: '#FFFFFF' },
      warning: { main: '#92400E', light: '#FDE68A', dark: '#78350F', contrastText: '#FFFFFF' },
      error: { main: '#9F1239', light: '#FECDD3', dark: '#881337', contrastText: '#FFFFFF' },
      info: { main: '#3730A3', light: '#C7D2FE', dark: '#312E81', contrastText: '#FFFFFF' },
      action: dark ? { hover: alpha('#FFFFFF', 0.06), selected: alpha(brand.blueOnDark, 0.16) } : { hover: alpha(brand.blue, 0.05), selected: alpha(brand.blue, 0.1) },
    },
    shape: { borderRadius: 12 },
    typography: {
      fontFamily: '"Manrope", system-ui, sans-serif',
      fontSize: 13.5,
      h1: { fontSize: '1.85rem', fontWeight: 800, letterSpacing: '-0.025em' },
      h2: { fontSize: '1.2rem', fontWeight: 700, letterSpacing: '-0.01em' },
      h3: { fontSize: '1.02rem', fontWeight: 700 },
      button: { textTransform: 'none', fontWeight: 600, letterSpacing: '0.01em' },
      overline: { fontSize: '0.68rem', letterSpacing: '0.14em', fontWeight: 700, lineHeight: 1.8 },
      caption: { letterSpacing: '0.01em' },
    },
    components: {
      MuiCssBaseline: {
        styleOverrides: {
          html: { background: dark ? '#16141F' : '#F4F1FB' },
          body: { background: tk.page, minHeight: '100vh' },
          '@media print': {
            'html, body': { background: '#fff !important', backgroundImage: 'none !important' },
            '.MuiPaper-root, .MuiCard-root, .MuiAppBar-root, .MuiTableContainer-root': { boxShadow: 'none !important', backgroundImage: 'none !important', backdropFilter: 'none !important' },
            '.MuiButton-contained': { backgroundImage: 'none !important', boxShadow: 'none !important' },
          },
          '@media (prefers-reduced-motion: reduce)': { '*, *::before, *::after': { animation: 'none !important', transition: 'none !important' } },
          // one visible keyboard focus ring everywhere (mouse clicks do not show it)
          ':focus-visible': { outline: `2px solid ${focus}`, outlineOffset: 2 },
          a: { color: main },
          '[style*="IBM Plex Mono"]': { whiteSpace: 'nowrap' },
          // phones: a wide table scrolls inside its own panel and a long chip shortens, so the page itself never scrolls sideways
          '.MuiPaper-root:has(> .MuiTable-root)': { overflowX: 'auto' },
          '.MuiChip-root': { maxWidth: '100%' },
        },
      },
      // White cards: a thin edge, a soft shadow, and a little lift when the card itself is clickable.
      MuiPaper: {
        defaultProps: { elevation: 0 },
        styleOverrides: {
          root: { backgroundColor: paper, backgroundImage: 'none', border: `1px solid ${tk.glass.edge}`,
            boxShadow: dark ? '0 8px 24px rgba(0,0,0,0.28)' : '0 1px 2px rgba(30,27,75,0.04), 0 8px 24px rgba(30,27,75,0.06)' },
          rounded: { borderRadius: 14 },
        },
      },
      MuiCard: {
        styleOverrides: {
          root: {
            borderRadius: 18, position: 'relative', overflow: 'hidden',
            transition: 'transform .18s ease, box-shadow .18s ease, border-color .18s ease',
            '&:has(.MuiCardActionArea-root):hover': { transform: 'translateY(-3px)', borderColor: dark ? 'rgba(255,255,255,0.16)' : '#FFFFFF', boxShadow: `0 18px 40px ${alpha(main, dark ? 0.22 : 0.16)}` },
          },
        },
      },
      MuiCardActionArea: { styleOverrides: { focusHighlight: { opacity: '0 !important' } } },
      MuiMenu: { styleOverrides: { paper: { backgroundColor: dark ? 'rgba(33,36,42,0.97)' : 'rgba(255,255,255,0.96)', boxShadow: dark ? '0 10px 30px rgba(0,0,0,0.5)' : '0 6px 18px rgba(20, 32, 50, 0.12)' } } },
      MuiPopover: { styleOverrides: { paper: { backgroundColor: dark ? 'rgba(33,36,42,0.97)' : 'rgba(255,255,255,0.96)' } } },
      // Dialogs: a solid surface over a dimmed backdrop, so nothing behind shows through the title or fields.
      MuiDialog: { styleOverrides: {
        paper: { borderRadius: 20, backgroundColor: dark ? '#21242A' : '#FFFFFF', backdropFilter: 'none', opacity: 1,
          boxShadow: dark ? '0 24px 60px rgba(0,0,0,0.6)' : '0 24px 60px rgba(15,23,42,0.28)' },
      } },
      MuiBackdrop: { styleOverrides: { root: { '&:not(.MuiBackdrop-invisible)': { backgroundColor: dark ? 'rgba(0,0,0,0.65)' : 'rgba(15,23,42,0.5)' } } } },
      MuiTab: { styleOverrides: { root: { textTransform: 'none', fontWeight: 600, fontSize: 13.5, minWidth: 0, paddingLeft: 14, paddingRight: 14 } } },
      MuiTabs: { styleOverrides: { indicator: { height: 3, borderRadius: 3, background: tk.gradient } } },
      MuiOutlinedInput: { styleOverrides: { root: { borderRadius: 10, backgroundColor: dark ? 'rgba(255,255,255,0.04)' : 'rgba(255,255,255,0.85)' } } },
      MuiTooltip: { styleOverrides: { tooltip: { borderRadius: 6, fontSize: 12 } } },
      MuiAppBar: { styleOverrides: { root: { border: 'none' } } },
      MuiButtonBase: { styleOverrides: { root: { '&.Mui-focusVisible': { outline: `2px solid ${focus}`, outlineOffset: 2 } } } },
      MuiButton: {
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: { borderRadius: 999, fontWeight: 700, paddingLeft: 18, paddingRight: 18 },
          containedPrimary: {
            background: pink, color: '#FFFFFF', boxShadow: '0 6px 16px rgba(190,24,93,0.28)',
            '&:hover': { color: '#FFFFFF', background: brand.pinkHover, transform: 'translateY(-1px)' },
            '&.Mui-disabled': { color: 'rgba(255,255,255,0.92)', background: pink, opacity: 0.5, boxShadow: 'none' },
          },
          outlined: { backgroundColor: dark ? 'rgba(255,255,255,0.04)' : 'rgba(255,255,255,0.7)', borderColor: alpha(main, dark ? 0.45 : 0.3) },
        },
      },
      MuiTableContainer: { styleOverrides: { root: { borderRadius: 16 } } },
      MuiTableCell: {
        styleOverrides: {
          root: { borderColor: dark ? 'rgba(255,255,255,0.06)' : '#EEF0F6', paddingTop: 12, paddingBottom: 12 },
          head: { fontWeight: 700, color: dark ? '#B7B3C9' : '#667085', fontSize: 11, textTransform: 'uppercase', letterSpacing: '0.08em', backgroundColor: dark ? 'rgba(255,255,255,0.03)' : '#F7F6FB', borderBottom: `1px solid ${line}` },
          // sticky heads stay opaque so scrolled rows do not show through
          stickyHeader: { backgroundColor: solid },
        },
      },
      MuiTableRow: { styleOverrides: { root: { transition: 'background-color .15s ease', '&.MuiTableRow-hover:hover td': { backgroundColor: alpha(main, dark ? 0.12 : 0.05) } } } },
      MuiChip: { styleOverrides: {
        root: ({ ownerState }) => {
          const tone = chip[(ownerState.color && ownerState.color !== 'default' ? ownerState.color : 'default') as keyof typeof chip] ?? chip.default;
          const colored = ownerState.color && ownerState.color !== 'default';
          return { fontWeight: 700, borderRadius: 999, ...(colored ? { backgroundColor: tone[0], color: tone[1], borderColor: 'transparent' } : { backgroundColor: tone[0], color: tone[1] }) };
        },
        sizeSmall: { height: 22, fontSize: 11.5 },
      } },
      MuiLink: { defaultProps: { underline: 'hover' } },
    },
  });
};

export const mono = '"IBM Plex Mono", ui-monospace, monospace';

/** Theme colours for charts drawn with Recharts or plain SVG (props take plain strings, not sx tokens). */
export function useChartColors() {
  const t = useTheme();
  return { grid: t.palette.divider, primary: t.palette.primary.main, accent: t.palette.secondary.main, ink: t.palette.text.primary, muted: t.palette.text.secondary };
}
