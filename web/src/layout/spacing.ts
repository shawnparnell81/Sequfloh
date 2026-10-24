/**
 * Standard spacing for every page (MUI spacing units, 1 = 8px). Page header, record header, list toolbar and tables all use these so pages
 * line up the same way everywhere.
 */
export const SPACE = {
  /** Left and right padding of the page header and body. */
  pageX: { xs: 2, md: 4 },
  /** Space between the breadcrumb and the title row. */
  crumbs: 1.5,
  /** Space under the page header before the page body. */
  header: 3,
  /** Space between blocks inside the page body (record header, toolbar, table). */
  block: 2.5,
  /** Gap between buttons and chips in a row. */
  gap: 1,
  /** Padding inside a record header card. */
  card: { xs: 2, md: 2.5 },
} as const;
