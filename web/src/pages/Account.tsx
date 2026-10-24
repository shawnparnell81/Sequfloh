import { ModuleWorkspace } from '../layout/ModuleWorkspace';
import { Box, FormControlLabel, Paper, Switch, Typography } from '@mui/material';
import { useTabsSetting } from '../tabs/Workspace';
import { MAX_TABS } from '../tabs/model';
import { Suspense, lazy } from 'react';
import { useAuth } from '../auth';

const AuditTrailSection = lazy(() => import('../sequfloh/Reports').then((m) => ({ default: m.AuditTrailSection })));

/** Your account › Settings: settings that belong to the signed-in person, and the Audit Trail for people given the audit trail permission. */
export function PreferencesPage() {
  const [on, setOn] = useTabsSetting();
  const { can } = useAuth();
  return (
    <ModuleWorkspace crumbs={[{ label: 'Your account' }, { label: 'Settings' }]} title="Settings" subtitle="Settings that belong to you, and the audit trail if you were given access to it.">
      <Paper variant="outlined" sx={{ p: 2.5, maxWidth: 760, display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap' }}>
        <FormControlLabel control={<Switch checked={on} onChange={(e) => setOn(e.target.checked)} inputProps={{ 'aria-label': 'Use tabs' }} />} label={<Typography fontWeight={600}>Use tabs</Typography>} />
        <Typography variant="body2" color="text.secondary" sx={{ flex: 1, minWidth: 260 }}>
          Keep up to {MAX_TABS} records, forms and pages open as tabs under the top bar. Ctrl-click, middle-click or right-click a link or row to open it in a new tab.
          Drag a tab to move it. Alt+W closes the tab, Alt+[ and Alt+] move between tabs. Saved for you on this device.
        </Typography>
      </Paper>
      {can('sequfloh.view_audit_trail') && <Box sx={{ mt: 3 }}><Suspense fallback={null}><AuditTrailSection /></Suspense></Box>}
    </ModuleWorkspace>
  );
}
