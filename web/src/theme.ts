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

/** The two built-in form palettes: 1 green and blue (checklists, investigations, plans), 2 slate and blue (certificates, reports, worksheets, registers). */
export const formPalettes = { palette1: { edge: GREEN, accent: brand.blue }, palette2: { edge: SLATE, accent: brand.blue } } as const;
export const defaultPalette = (f: FormFamily): 'palette1' | 'palette2' => (f === 'checklist' || f === 'investigation' || f === 'plan' ? 'palette1' : 'palette2');

/** Options from Settings › Appearance (organization default, then the person's override). */
export interface ThemeOptionsExtra { accent?: string | null; forms?: Partial<Record<FormFamily, FamilyPalette>> }

function familyColours(dark: boolean, forms: ThemeOptionsExtra['forms']): ShellTokens['families'] {
  const lift = (c: string) => (dark ? lighten(c, 0.35) : c);
  const out = {} as ShellTokens['families'];
  for (const f of formFamilies) {
    const p = forms?.[f];
    if (p?.palette === 'custom' && valid(p.edge) && valid(p.accent)) out[f] = { edge: lift(p.edge!), accent: lift(p.accent!) };
    else if ((p?.palette ?? defaultPalette(f)) === 'palette1') out[f] = dark ? { edge: GREEN_ON_DARK, accent: brand.blueOnDark } : { ...formPalettes.palette1 };
    else out[f] = dark ? { edge: SLATE_ON_DARK, accent: brand.blueOnDark } : { ...formPalettes.palette2 };
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
        page: `radial-gradient(1100px 620px at 0% 0%, rgba(74,143,240,0.10), transparent 62%), radial-gradient(1000px 600px at 100% 0%, ${alpha(green, 0.08)}, transparent 58%), linear-gradient(180deg, #15181D 0%, #17191E 55%, #181A1D 100%)`,
        gradient, green, glow: 'rgba(0,0,0,0.45)',
        glass: { side: 'rgba(30,33,39,0.7)', section: 'rgba(36,40,47,0.62)', table: 'rgba(30,33,39,0.75)', edge: 'rgba(255,255,255,0.07)', field: 'rgba(255,255,255,0.06)' },
        bar: { bg: 'rgba(24,27,32,0.78)', fg: '#E6E9EE', muted: '#A7AFBB', activeLine: green },
        header: { fg: '#EEF0F3', muted: '#9AA2AE' },
        icon: { size: 52, radius: 16, hoverBg },
        greeting: `linear-gradient(90deg, #EEF0F3 0%, ${main} 60%, ${green} 100%)`,
        form: { radius: 14, paperPad: 4, callout: 'rgba(74,143,240,0.08)' }, families,
      }
    : {
        page: `radial-gradient(1100px 620px at 0% 0%, rgba(18,87,196,0.18), transparent 62%), radial-gradient(1000px 600px at 100% 0%, ${alpha(green, 0.14)}, transparent 58%), radial-gradient(900px 700px at 100% 100%, ${alpha(green, 0.07)}, transparent 60%), radial-gradient(900px 700px at 0% 100%, rgba(18,87,196,0.08), transparent 60%), linear-gradient(180deg, #EEF3FB 0%, #EFF6F3 55%, #F4F6F8 100%)`,
        gradient, green, glow: 'rgba(18,87,196,0.24)',
        glass: { side: 'rgba(255,255,255,0.55)', section: 'rgba(255,255,255,0.66)', table: 'rgba(255,255,255,0.7)', edge: 'rgba(255,255,255,0.9)', field: 'rgba(255,255,255,0.8)' },
        bar: { bg: 'rgba(255,255,255,0.7)', fg: brand.charcoal, muted: '#5E6A7A', activeLine: green },
        header: { fg: '#1B2430', muted: '#66717F' },
        icon: { size: 52, radius: 16, hoverBg },
        greeting: `linear-gradient(90deg, #1B2430 0%, ${main} 55%, ${green} 100%)`,
        form: { radius: 14, paperPad: 4, callout: 'rgba(18,87,196,0.05)' }, families,
      };
}

/**
 * Application theme: a light, airy shell (soft blue and green page glow, frosted top bar, side menu and panels with large radii, blue-to-green
 * module badges and soft status pills) in light and dark modes that share one structure. An organization colour from Settings › Branding
 * replaces the light-mode primary; dark mode keeps the lifted logo blue so text and links stay readable.
 */
export const makeTheme = (primary?: string | null, mode: PaletteMode = 'light', options: ThemeOptionsExtra = {}) => {
  const dark = mode === 'dark';
  const custom = dark ? null : valid(primary);
  const main = custom ?? (dark ? brand.blueOnDark : brand.blue);
  const focus = dark ? '#8DB8F7' : brand.blue;
  const tk = tokens(dark, main, valid(options.accent), options.forms);
  const line = dark ? 'rgba(255,255,255,0.09)' : 'rgba(18,87,196,0.12)';
  const paper = dark ? 'rgba(33,36,42,0.82)' : 'rgba(255,255,255,0.74)';
  const solid = dark ? '#202328' : '#FFFFFF';
  return createTheme({
    tk,
    palette: {
      mode,
      primary: custom
        ? { main: custom, contrastText: '#FFFFFF' }
        : dark
          ? { main: brand.blueOnDark, light: '#7AAEF5', dark: '#2F74D6', contrastText: '#0B1220' }
          : { main: brand.blue, light: brand.blueLight, dark: brand.blueDark, contrastText: '#FFFFFF' },
      secondary: valid(options.accent) ? { main: tk.green } : dark ? { main: GREEN_ON_DARK, contrastText: '#0E1A14' } : { main: GREEN, dark: '#0D7547', contrastText: '#FFFFFF' },
      background: dark
        ? { default: '#16191E', paper: solid, sidebar: tk.glass.side, bar: '#1B1E23', band: 'rgba(255,255,255,0.04)' }
        : { default: '#F3F6F8', paper: solid, sidebar: tk.glass.side, bar: '#F7F9FA', band: 'rgba(18,87,196,0.04)' },
      text: dark ? { primary: '#E6E9EE', secondary: '#A7AFBB' } : { primary: '#1B2430', secondary: '#5E6A7A' },
      divider: line,
      success: { main: dark ? '#5DCB8B' : '#0E8550' },
      warning: { main: dark ? '#F2B04A' : '#F08C00' },
      error: { main: dark ? '#F27B7E' : '#E5484D' },
      info: { main: dark ? '#7FB0FF' : '#3B82F6' },
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
          html: { background: dark ? '#16191E' : '#F1F5F6' },
          body: { background: tk.page, minHeight: '100vh' },
          // one visible keyboard focus ring everywhere (mouse clicks do not show it)
          ':focus-visible': { outline: `2px solid ${focus}`, outlineOffset: 2 },
          a: { color: main },
          '[style*="IBM Plex Mono"]': { whiteSpace: 'nowrap' },
          // phones: a wide table scrolls inside its own panel and a long chip shortens, so the page itself never scrolls sideways
          '.MuiPaper-root:has(> .MuiTable-root)': { overflowX: 'auto' },
          '.MuiChip-root': { maxWidth: '100%' },
        },
      },
      // Frosted panels with a light edge; clickable cards lift on hover with a soft blue glow.
      MuiPaper: {
        defaultProps: { elevation: 0 },
        styleOverrides: {
          root: { backgroundColor: paper, backgroundImage: 'none', backdropFilter: 'blur(16px) saturate(140%)', border: `1px solid ${tk.glass.edge}`,
            boxShadow: dark ? '0 1px 0 rgba(255,255,255,0.04) inset, 0 10px 30px rgba(0,0,0,0.35)' : '0 1px 0 rgba(255,255,255,0.8) inset, 0 10px 30px rgba(32,56,120,0.08)' },
          rounded: { borderRadius: 16 },
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
          containedPrimary: custom ? {} : {
            background: dark ? 'linear-gradient(135deg, #2F6FD0 0%, #1E8A57 100%)' : tk.gradient, color: '#FFFFFF',
            textShadow: '0 1px 1px rgba(0,0,0,0.25)', boxShadow: `0 6px 16px ${alpha(main, dark ? 0.35 : 0.3)}`,
            '&:hover': { color: '#FFFFFF', background: dark ? 'linear-gradient(135deg, #3A7DE0 0%, #24995F 100%)' : `linear-gradient(135deg, ${brand.blueHover} 0%, #0D7547 100%)` },
            '&.Mui-disabled': { color: 'rgba(255,255,255,0.92)', background: dark ? 'linear-gradient(135deg, #2F6FD0 0%, #1E8A57 100%)' : tk.gradient, opacity: 0.55, boxShadow: 'none' },
          },
          outlined: { backgroundColor: dark ? 'rgba(255,255,255,0.04)' : 'rgba(255,255,255,0.7)', borderColor: alpha(main, dark ? 0.45 : 0.3) },
        },
      },
      MuiTableContainer: { styleOverrides: { root: { borderRadius: 16 } } },
      MuiTableCell: {
        styleOverrides: {
          root: { borderColor: dark ? 'rgba(255,255,255,0.06)' : 'rgba(18,87,196,0.08)', paddingTop: 12, paddingBottom: 12 },
          head: { fontWeight: 700, color: dark ? '#9AA2AE' : '#66717F', fontSize: 11, textTransform: 'uppercase', letterSpacing: '0.08em', backgroundColor: 'transparent', borderBottom: `1px solid ${line}` },
          // sticky heads stay opaque so scrolled rows do not show through
          stickyHeader: { backgroundColor: solid },
        },
      },
      MuiTableRow: { styleOverrides: { root: { '&.MuiTableRow-hover:hover td': { backgroundColor: alpha(tk.green, dark ? 0.08 : 0.05) } } } },
      MuiChip: { styleOverrides: { root: { fontWeight: 700, borderRadius: 999 }, sizeSmall: { height: 22, fontSize: 11.5 } } },
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
