import { lazy, Suspense, useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { AppBar, Box, Button, CircularProgress, Divider, ListItemIcon, Menu, MenuItem, Stack, Toolbar, Tooltip, Typography } from '@mui/material';
import HomeOutlined from '@mui/icons-material/HomeOutlined';
import ArrowBack from '@mui/icons-material/ArrowBack';
import AccountCircleOutlined from '@mui/icons-material/AccountCircleOutlined';
import LogoutOutlined from '@mui/icons-material/LogoutOutlined';
import KeyOutlined from '@mui/icons-material/KeyOutlined';
import { useTheme } from '@mui/material/styles';
import { useAuth, useBranding } from './auth';
import { LoginPage, SetupPage, ChangePasswordPage } from './pages/SignIn';

const SqfHome = lazy(() => import('./sequfloh/Home').then((m) => ({ default: m.SequflohHome })));
const SqfSettings = lazy(() => import('./sequfloh/Admin').then((m) => ({ default: m.SettingsPage })));
const SqfReference = lazy(() => import('./sequfloh/Admin').then((m) => ({ default: m.ReferenceDataPage })));
const SqfDropdowns = lazy(() => import('./sequfloh/Admin').then((m) => ({ default: m.DropdownsPage })));
const SqfModuleAccess = lazy(() => import('./sequfloh/Admin').then((m) => ({ default: m.ModuleAccessPage })));
const SqfRoles = lazy(() => import('./sequfloh/Admin').then((m) => ({ default: m.RolesMatrixPage })));
const SqfConfiguration = lazy(() => import('./sequfloh/Admin').then((m) => ({ default: m.ConfigurationPage })));
const SqfParts = lazy(() => import('./sequfloh/Parts').then((m) => ({ default: m.PartMasterPage })));
const SqfPart = lazy(() => import('./sequfloh/Parts').then((m) => ({ default: m.PartDetailPage })));
const SqfLists = lazy(() => import('./sequfloh/Parts').then((m) => ({ default: m.PartsListsPage })));
const SqfList = lazy(() => import('./sequfloh/Parts').then((m) => ({ default: m.PartsListDetailPage })));
const SqfSteps = lazy(() => import('./sequfloh/Parts').then((m) => ({ default: m.OperationStepsPage })));
const SqfRevisions = lazy(() => import('./sequfloh/Parts').then((m) => ({ default: m.RevisionHistoryPage })));
const SqfWiFolder = lazy(() => import('./sequfloh/Instructions').then((m) => ({ default: m.InstructionsFolderPage })));
const SqfWiNew = lazy(() => import('./sequfloh/Instructions').then((m) => ({ default: m.NewInstructionPage })));
const SqfWi = lazy(() => import('./sequfloh/Instructions').then((m) => ({ default: m.InstructionPage })));
const SqfWiTemplates = lazy(() => import('./sequfloh/Instructions').then((m) => ({ default: m.TemplatesPage })));
const SqfTrFolder = lazy(() => import('./sequfloh/Tracking').then((m) => ({ default: m.TrackingFolderPage })));
const SqfTrNew = lazy(() => import('./sequfloh/Tracking').then((m) => ({ default: m.NewTrackingPage })));
const SqfTr = lazy(() => import('./sequfloh/Tracking').then((m) => ({ default: m.TrackingRecordPage })));
const SqfQDash = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: m.QualityDashboardPage })));
const SqfQVal = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: m.QualityValidationPage })));
const SqfQRep = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: m.QualityRepairPage })));
const SqfQQueue = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: () => <m.ReviewQueuePage area="quality" /> })));
const SqfEDash = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: m.EngineeringDashboardPage })));
const SqfEVal = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: m.EngineeringValidationPage })));
const SqfEDev = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: m.DevelopmentPage })));
const SqfEQueue = lazy(() => import('./sequfloh/Personnel').then((m) => ({ default: () => <m.ReviewQueuePage area="engineering" /> })));
const SqfReport = lazy(() => import('./sequfloh/Reports').then((m) => ({ default: m.ReportPage })));
const SqfExportCenter = lazy(() => import('./sequfloh/Reports').then((m) => ({ default: m.ExportCenterPage })));
const SqfAudit = lazy(() => import('./sequfloh/Reports').then((m) => ({ default: m.AuditPage })));
const SqfSearch = lazy(() => import('./sequfloh/Reports').then((m) => ({ default: m.SearchPage })));
const SqfExQueue = lazy(() => import('./sequfloh/Execution').then((m) => ({ default: m.ExecutionQueuePage })));
const SqfExStation = lazy(() => import('./sequfloh/Execution').then((m) => ({ default: m.ActiveStationPage })));
const SqfExHistory = lazy(() => import('./sequfloh/Execution').then((m) => ({ default: m.WorkHistoryPage })));
const AdminUsers = lazy(() => import('./pages/Admin').then((m) => ({ default: m.UsersPage })));
const AdminRoles = lazy(() => import('./pages/Admin').then((m) => ({ default: m.RolesPage })));
const AdminSites = lazy(() => import('./pages/Admin').then((m) => ({ default: m.SitesPage })));
const AdminAudit = lazy(() => import('./pages/Admin').then((m) => ({ default: m.AuditTrailPage })));

const Spinner = () => <Stack alignItems="center" sx={{ py: 10 }}><CircularProgress aria-label="Loading" /></Stack>;

/** Top bar on every signed-in page: name, Home and Back, and the person's menu. */
function TopBar() {
  const { me, logout } = useAuth();
  const branding = useBranding();
  const nav = useNavigate();
  const loc = useLocation();
  const tk = useTheme().tk;
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const atHome = loc.pathname === '/sequfloh';
  return (
    <AppBar position="sticky" color="inherit" className="sqf-no-print" sx={{ background: tk.bar.bg, backdropFilter: 'blur(14px)', borderBottom: 1, borderColor: 'divider', color: tk.bar.fg }}>
      <Toolbar variant="dense" sx={{ minHeight: 56, gap: 1 }}>
        <Box component={RouterLink} to="/sequfloh" sx={{ display: 'flex', alignItems: 'center', gap: 1.25, color: 'inherit', textDecoration: 'none', mr: 1 }}>
          <Box component="img" src="/favicon.svg" alt="" sx={{ width: 30, height: 30 }} />
          <Box>
            <Typography sx={{ fontWeight: 800, letterSpacing: '0.08em', fontSize: 15, lineHeight: 1.1 }}>SEQUFLOH</Typography>
            <Typography sx={{ fontSize: 10.5, color: tk.bar.muted, lineHeight: 1.1 }}>{branding.data?.organizationName || 'Job tracking'}</Typography>
          </Box>
        </Box>
        <Button startIcon={<HomeOutlined />} component={RouterLink} to="/sequfloh" variant={atHome ? 'contained' : 'text'} size="small">Home</Button>
        <Button startIcon={<ArrowBack />} onClick={() => nav(-1)} size="small" disabled={atHome && window.history.length <= 1}>Back</Button>
        <Box sx={{ flex: 1 }} />
        <Tooltip title="Your account"><Button onClick={(e) => setAnchor(e.currentTarget)} startIcon={<AccountCircleOutlined />} size="small" color="inherit">{me?.displayName}</Button></Tooltip>
        <Menu anchorEl={anchor} open={!!anchor} onClose={() => setAnchor(null)}>
          <MenuItem disabled><Typography variant="body2">{me?.email}</Typography></MenuItem>
          <Divider />
          <MenuItem onClick={() => { setAnchor(null); nav('/account/password'); }}><ListItemIcon><KeyOutlined fontSize="small" /></ListItemIcon>Change password</MenuItem>
          <MenuItem onClick={async () => { setAnchor(null); await logout(); nav('/'); }}><ListItemIcon><LogoutOutlined fontSize="small" /></ListItemIcon>Sign out</MenuItem>
        </Menu>
      </Toolbar>
    </AppBar>
  );
}

function Signed({ children }: { children: ReactNode }) {
  return <><TopBar /><Suspense fallback={<Spinner />}>{children}</Suspense></>;
}

export function App() {
  const { me, needsSetup, loading } = useAuth();
  const loc = useLocation();
  useEffect(() => { window.scrollTo(0, 0); }, [loc.pathname]);
  if (loading) return <Spinner />;
  if (!me) return needsSetup ? <SetupPage /> : <LoginPage />;
  if (me.mustChangePassword) return <ChangePasswordPage forced />;
  return (
    <Signed>
      <Routes>
        <Route path="/" element={<Navigate to="/sequfloh" replace />} />
        <Route path="/account/password" element={<ChangePasswordPage />} />
        <Route path="/admin/users" element={<AdminUsers />} />
        <Route path="/admin/roles" element={<AdminRoles />} />
        <Route path="/admin/sites" element={<AdminSites />} />
        <Route path="/admin/audit-trail" element={<AdminAudit />} />
        <Route path="/sequfloh" element={<SqfHome />} />
        <Route path="/sequfloh/settings/:section" element={<SqfSettings />} />
        <Route path="/sequfloh/admin/reference" element={<SqfReference />} />
        <Route path="/sequfloh/admin/dropdowns" element={<SqfDropdowns />} />
        <Route path="/sequfloh/admin/module-access" element={<SqfModuleAccess />} />
        <Route path="/sequfloh/admin/roles" element={<SqfRoles />} />
        <Route path="/sequfloh/admin/configuration" element={<SqfConfiguration />} />
        <Route path="/sequfloh/parts" element={<SqfParts />} />
        <Route path="/sequfloh/parts/:id" element={<SqfPart />} />
        <Route path="/sequfloh/parts-lists" element={<SqfLists />} />
        <Route path="/sequfloh/parts-lists/:id" element={<SqfList />} />
        <Route path="/sequfloh/operation-steps" element={<SqfSteps />} />
        <Route path="/sequfloh/revision-history" element={<SqfRevisions />} />
        <Route path="/sequfloh/instructions/new" element={<SqfWiNew />} />
        <Route path="/sequfloh/instructions/templates" element={<SqfWiTemplates />} />
        <Route path="/sequfloh/instructions/:folder" element={<SqfWiFolder />} />
        <Route path="/sequfloh/instruction/:id" element={<SqfWi />} />
        <Route path="/sequfloh/tracking/new" element={<SqfTrNew />} />
        <Route path="/sequfloh/tracking/:folder" element={<SqfTrFolder />} />
        <Route path="/sequfloh/record/:id" element={<SqfTr />} />
        <Route path="/sequfloh/execution/active" element={<SqfExStation />} />
        <Route path="/sequfloh/reports/export-center" element={<SqfExportCenter />} />
        <Route path="/sequfloh/reports/:slug" element={<SqfReport />} />
        <Route path="/sequfloh/audit/:view" element={<SqfAudit />} />
        <Route path="/sequfloh/search" element={<SqfSearch />} />
        <Route path="/sequfloh/quality" element={<SqfQDash />} />
        <Route path="/sequfloh/quality/validation" element={<SqfQVal />} />
        <Route path="/sequfloh/quality/repair" element={<SqfQRep />} />
        <Route path="/sequfloh/quality/review-queue" element={<SqfQQueue />} />
        <Route path="/sequfloh/engineering" element={<SqfEDash />} />
        <Route path="/sequfloh/engineering/validation" element={<SqfEVal />} />
        <Route path="/sequfloh/engineering/development" element={<SqfEDev />} />
        <Route path="/sequfloh/engineering/review-queue" element={<SqfEQueue />} />
        <Route path="/sequfloh/execution/history" element={<SqfExHistory />} />
        <Route path="/sequfloh/execution/:view" element={<SqfExQueue />} />
        <Route path="*" element={<Navigate to="/sequfloh" replace />} />
      </Routes>
    </Signed>
  );
}
