import { Box, FormControlLabel, Paper, Switch, Typography } from '@mui/material';
import { PageNav, useTabsSetting, useTabTitle } from '../tabs/Workspace';
import { MAX_TABS } from '../tabs/model';

/** Your account › Preferences: settings that belong to the signed-in person. */
export function PreferencesPage() {
  const [on, setOn] = useTabsSetting();
  useTabTitle('Preferences');
  return (
    <Box sx={{ p: { xs: 2, md: 4 } }}>
      <Box sx={{ mb: 1 }}><PageNav /></Box>
      <Typography variant="h1" sx={{ mb: 3 }}>Preferences</Typography>
      <Paper variant="outlined" sx={{ p: 2.5, maxWidth: 760, display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap' }}>
        <FormControlLabel control={<Switch checked={on} onChange={(e) => setOn(e.target.checked)} inputProps={{ 'aria-label': 'Use tabs' }} />} label={<Typography fontWeight={600}>Use tabs</Typography>} />
        <Typography variant="body2" color="text.secondary" sx={{ flex: 1, minWidth: 260 }}>
          Keep up to {MAX_TABS} records, forms and pages open as tabs under the top bar. Ctrl-click, middle-click or right-click a link or row to open it in a new tab.
          Drag a tab to move it. Alt+W closes the tab, Alt+[ and Alt+] move between tabs. Saved for you on this device.
        </Typography>
      </Paper>
    </Box>
  );
}
