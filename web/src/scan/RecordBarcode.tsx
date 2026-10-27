import { useEffect, useRef } from 'react';
import JsBarcode from 'jsbarcode';
import { Box } from '@mui/material';

/** Code 128 of the exact record number with the number under it, drawn as SVG so it prints sharply; scanning it opens the record. */
export function RecordBarcode({ value }: { value: string }) {
  const ref = useRef<SVGSVGElement | null>(null);
  useEffect(() => {
    if (ref.current && value) JsBarcode(ref.current, value, { format: 'CODE128', width: 1.5, height: 36, margin: 6, fontSize: 11, font: 'monospace', displayValue: true, background: '#ffffff', lineColor: '#000000' });
  }, [value]);
  return <Box sx={{ float: 'right', ml: 2 }}><svg ref={ref} data-testid="record-barcode" data-value={value} role="img" aria-label={`Barcode ${value}`} /></Box>;
}
