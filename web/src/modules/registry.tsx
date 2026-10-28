import type { ReactNode } from 'react';
import PlaylistPlayOutlined from '@mui/icons-material/PlaylistPlayOutlined';
import HomeOutlined from '@mui/icons-material/HomeOutlined';
import PlayCircleOutline from '@mui/icons-material/PlayCircleOutline';
import TimelineOutlined from '@mui/icons-material/TimelineOutlined';
import FolderSpecialOutlined from '@mui/icons-material/FolderSpecialOutlined';
import FactCheckOutlined from '@mui/icons-material/FactCheckOutlined';
import DescriptionOutlined from '@mui/icons-material/DescriptionOutlined';
import CategoryOutlined from '@mui/icons-material/CategoryOutlined';
import VerifiedOutlined from '@mui/icons-material/VerifiedOutlined';
import ArchitectureOutlined from '@mui/icons-material/ArchitectureOutlined';
import EngineeringOutlined from '@mui/icons-material/EngineeringOutlined';
import AssessmentOutlined from '@mui/icons-material/AssessmentOutlined';
import AdminPanelSettingsOutlined from '@mui/icons-material/AdminPanelSettingsOutlined';
import TuneOutlined from '@mui/icons-material/TuneOutlined';
import GroupOutlined from '@mui/icons-material/GroupOutlined';
import UploadFileOutlined from '@mui/icons-material/UploadFileOutlined';

/**
 * A menu entry. Top-level entries are the sections shown down the left; a section's children are its pages,
 * shown as a menu across the top of every page in that section. `match` lists the address prefixes that belong
 * to the section (record pages, for example) besides its pages.
 */
export interface NavItem { label: string; path: string; permission?: string; icon?: ReactNode; match?: string[]; children?: NavItem[] }
export interface ModuleDef { key: string; label: string; description: string; icon: ReactNode; basePath: string; permissions: string[]; sidebar: NavItem[] }

/** The job tracking menu: sections down the left of every page, each section's pages across the top. */
export const sequfloh: ModuleDef = {
    key: 'sequfloh',
    label: 'SEQUFLOH',
    description: 'Enterprise Execution Engine: integrated execution, tracking, validation, inspection, repair, production, and controlled work instruction management',
    icon: <PlaylistPlayOutlined fontSize="small" />,
    basePath: '/sequfloh',
    permissions: ['sequfloh.access'],
    sidebar: [
      { label: 'SEQUFLOH Home', path: '/sequfloh', permission: 'sequfloh.view_dashboard', icon: <HomeOutlined fontSize="small" /> },
      { label: 'Live Execution', path: '/sequfloh/execution/dispatch', permission: 'sequfloh.view_live_execution', icon: <PlayCircleOutline fontSize="small" />, match: ['/sequfloh/execution'], children: [
        { label: 'Dispatch Queue', path: '/sequfloh/execution/dispatch' },
        { label: 'Active Station', path: '/sequfloh/execution/active' },
        { label: 'Paused Tasks', path: '/sequfloh/execution/paused' },
        { label: 'Completed Tasks', path: '/sequfloh/execution/completed' },
        { label: 'My Assigned Work', path: '/sequfloh/execution/mine' },
        { label: 'Overdue Work', path: '/sequfloh/execution/overdue' },
        { label: 'Work History', path: '/sequfloh/execution/history' },
      ] },
      { label: 'Work Orders', path: '/sequfloh/tracking/open', permission: 'sequfloh.view_live_execution', icon: <TimelineOutlined fontSize="small" />, match: ['/sequfloh/tracking', '/sequfloh/record/'], children: [
        { label: 'Open Work Orders', path: '/sequfloh/tracking/open' },
        { label: 'Validation / FAI', path: '/sequfloh/tracking/validation-fai' },
        { label: 'Repair / Rework', path: '/sequfloh/tracking/repair' },
        { label: 'Inspection', path: '/sequfloh/tracking/inspection' },
        { label: 'Warranty', path: '/sequfloh/tracking/warranty' },
        { label: 'Salt Spray Test', path: '/sequfloh/tracking/salt-spray' },
        { label: 'Development', path: '/sequfloh/tracking/development' },
        { label: 'Rebox', path: '/sequfloh/tracking/rebox' },
        { label: 'Test', path: '/sequfloh/tracking/test' },
        { label: 'Weight', path: '/sequfloh/tracking/weight' },
        { label: 'Research', path: '/sequfloh/tracking/research' },
        { label: 'Production', path: '/sequfloh/tracking/production' },
        { label: 'Completed Work Orders', path: '/sequfloh/tracking/completed' },
      ] },
      { label: 'Projects', path: '/sequfloh/projects', icon: <FolderSpecialOutlined fontSize="small" />, match: ['/sequfloh/project'] },
      { label: 'Inspection Requests', path: '/sequfloh/inspection-requests', icon: <FactCheckOutlined fontSize="small" />, match: ['/sequfloh/inspection-request'] },
      { label: 'WI / Procedures', path: '/sequfloh/instructions/all', icon: <DescriptionOutlined fontSize="small" />, match: ['/sequfloh/instructions', '/sequfloh/instruction/'], children: [
        { label: 'All WI / Procedures', path: '/sequfloh/instructions/all' },
        { label: 'Validation / FAI Work Instructions', path: '/sequfloh/instructions/validation' },
        { label: 'Repair / Rework Work Instructions', path: '/sequfloh/instructions/repair' },
        { label: 'Inspection Work Instructions', path: '/sequfloh/instructions/inspection' },
        { label: 'Procedure Instructions', path: '/sequfloh/instructions/procedure' },
        { label: 'Draft Instructions', path: '/sequfloh/instructions/draft' },
        { label: 'Released Instructions', path: '/sequfloh/instructions/released' },
        { label: 'Obsolete Instructions', path: '/sequfloh/instructions/obsolete' },
        { label: 'Templates', path: '/sequfloh/instructions/templates' },
      ] },
      { label: 'Parts and Items', path: '/sequfloh/parts', icon: <CategoryOutlined fontSize="small" />, match: ['/sequfloh/parts', '/sequfloh/parts-lists', '/sequfloh/operation-steps', '/sequfloh/revision-history'], children: [
        { label: 'Part Master', path: '/sequfloh/parts' },
        { label: 'Parts Lists', path: '/sequfloh/parts-lists' },
        { label: 'Repairable Parts', path: '/sequfloh/parts?view=repairable' },
        { label: 'Repair Parts', path: '/sequfloh/parts?view=repair-parts' },
        { label: 'Assemblies', path: '/sequfloh/parts?view=assemblies' },
        { label: 'Reusable Operation Steps', path: '/sequfloh/operation-steps' },
        { label: 'Product Families (Setup)', path: '/sequfloh/admin/dropdowns?list=product-families', permission: 'sequfloh.manage_dropdowns' },
        { label: 'Revision History', path: '/sequfloh/revision-history' },
      ] },
      { label: 'Imported Files', path: '/sequfloh/imported-files', permission: 'sequfloh.view_imported_files', icon: <UploadFileOutlined fontSize="small" />, match: ['/sequfloh/imported-files'], children: [
        { label: 'All Imported Files', path: '/sequfloh/imported-files' },
        { label: 'Spreadsheets', path: '/sequfloh/imported-files?kind=spreadsheet' },
        { label: 'Word Documents', path: '/sequfloh/imported-files?kind=document' },
        { label: 'Blank Forms', path: '/sequfloh/imported-files?kind=blank' },
        { label: 'Filled Forms', path: '/sequfloh/imported-files?kind=filled' },
      ] },
      { label: 'Completed Validation / FAI', path: '/sequfloh/completed-forms', permission: 'sequfloh.view_imported_files', icon: <ArchitectureOutlined fontSize="small" />, match: ['/sequfloh/completed-forms'] },
      { label: 'Parts Drawings', path: '/sequfloh/drawings', permission: 'sequfloh.view_drawings', icon: <ArchitectureOutlined fontSize="small" />, match: ['/sequfloh/drawings'] },
      { label: 'Quality', path: '/sequfloh/quality', permission: 'sequfloh.view_quality_folder', icon: <VerifiedOutlined fontSize="small" />, match: ['/sequfloh/quality'], children: [
        { label: 'Quality Dashboard', path: '/sequfloh/quality' },
        { label: 'Validation / FAI Work Instructions', path: '/sequfloh/quality/validation' },
        { label: 'Repair / Rework Work Instructions', path: '/sequfloh/quality/repair' },
      ] },
      { label: 'Engineering', path: '/sequfloh/engineering', permission: 'sequfloh.view_engineering_folder', icon: <EngineeringOutlined fontSize="small" />, match: ['/sequfloh/engineering'], children: [
        { label: 'Engineering Dashboard', path: '/sequfloh/engineering' },
        { label: 'Validation / FAI', path: '/sequfloh/engineering/validation' },
        { label: 'Development', path: '/sequfloh/engineering/development' },
      ] },
      { label: 'Reports', path: '/sequfloh/reports/task-performance', permission: 'sequfloh.view_reports', icon: <AssessmentOutlined fontSize="small" />, match: ['/sequfloh/reports', '/sequfloh/chargebacks'], children: [
        { label: 'Chargeback Reports', path: '/sequfloh/chargebacks' },
        { label: 'Job Reports', path: '/sequfloh/reports/job-reports' },
        { label: 'Task Performance', path: '/sequfloh/reports/task-performance' },
        { label: 'Validation / FAI Status', path: '/sequfloh/reports/validation-status' },
        { label: 'Repair / Rework Status', path: '/sequfloh/reports/repair-status' },
        { label: 'Work Instruction Status', path: '/sequfloh/reports/work-instruction-status' },
        { label: 'User Activity', path: '/sequfloh/reports/user-activity' },
        { label: 'Time Variance', path: '/sequfloh/reports/time-variance' },
        { label: 'Export Center', path: '/sequfloh/reports/export-center' },
      ] },
      { label: 'SEQUFLOH Administration', path: '/sequfloh/admin/module-access', icon: <AdminPanelSettingsOutlined fontSize="small" />, match: ['/sequfloh/admin'], children: [
        { label: 'Module Access', path: '/sequfloh/admin/module-access', permission: 'sequfloh.manage_module_access' },
        { label: 'Roles and Permissions', path: '/sequfloh/admin/roles', permission: 'sequfloh.manage_module_access' },
        { label: 'Reference Data', path: '/sequfloh/admin/reference', permission: 'sequfloh.manage_dropdowns' },
        { label: 'Dropdown Management', path: '/sequfloh/admin/dropdowns', permission: 'sequfloh.manage_dropdowns' },
        { label: 'Application Configuration', path: '/sequfloh/admin/configuration', permission: 'sequfloh.manage_settings' },
      ] },
      { label: 'SEQUFLOH Settings', path: '/sequfloh/settings/general', permission: 'sequfloh.manage_settings', icon: <TuneOutlined fontSize="small" />, match: ['/sequfloh/settings'], children: [
        { label: 'General Settings', path: '/sequfloh/settings/general' },
        { label: 'Work Instruction Settings', path: '/sequfloh/settings/workInstructions' },
        { label: 'Validation / FAI Settings', path: '/sequfloh/settings/validation' },
        { label: 'Repair / Rework Settings', path: '/sequfloh/settings/repair' },
        { label: 'Tracking Settings', path: '/sequfloh/settings/tracking' },
        { label: 'Notification Settings', path: '/sequfloh/settings/notifications' },
        { label: 'Revision Settings', path: '/sequfloh/settings/numbering' },
        { label: 'Display Preferences', path: '/sequfloh/settings/display' },
        { label: 'Validation forms by product and test', path: '/sequfloh/settings/validation-forms' },
        { label: 'Export Settings', path: '/sequfloh/settings/export' },
        { label: 'Security Settings', path: '/sequfloh/settings/security' },
      ] },
    ],
  };

/** Administration pages: people, roles, sites and the audit trail. */
export const administration: NavItem = { label: 'Administration', path: '/admin/users', icon: <GroupOutlined fontSize="small" />, match: ['/admin'], children: [
  { label: 'People', path: '/admin/users', permission: 'admin.users' },
  { label: 'Roles and Permissions', path: '/admin/roles', permission: 'admin.roles' },
  { label: 'Sites and Departments', path: '/admin/sites', permission: 'admin.organization' },
  { label: 'Audit Trail', path: '/admin/audit-trail', permission: 'audittrail.view' },
] };

export const modules = [{ ...sequfloh, sidebar: [...sequfloh.sidebar, administration] }];

/** The menu section an address belongs to (longest matching prefix wins), or undefined for pages outside the sections. */
export function sectionOf(url: string): NavItem | undefined {
  const path = url.split(/[?#]/)[0].replace(/\/$/, '') || '/';
  let best: NavItem | undefined; let len = -1;
  for (const s of modules[0].sidebar) {
    const own = s.path.split('?')[0];
    const prefixes = [...(s.match ?? []), ...(s.children ?? []).map((c) => c.path.split('?')[0])];
    if (path === own && own.length > len) { best = s; len = own.length; }
    for (const p of prefixes) if ((path === p || path.startsWith(p.endsWith('/') ? p : p + '/')) && p.length > len) { best = s; len = p.length; }
  }
  return best;
}
/** The first page of a section the person may open (the section entry itself when it has no pages). */
export function landingOf(section: NavItem, can: (p: string) => boolean): string {
  return section.children?.find((c) => !c.permission || can(c.permission))?.path ?? section.path;
}
