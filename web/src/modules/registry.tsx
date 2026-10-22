import type { ReactNode } from 'react';
import PlaylistPlayOutlined from '@mui/icons-material/PlaylistPlayOutlined';

export interface NavItem { label: string; path: string; permission?: string; children?: NavItem[] }
export interface ModuleDef { key: string; label: string; description: string; icon: ReactNode; basePath: string; permissions: string[]; sidebar: NavItem[] }

/** The job tracking menu shown down the left of every page. */
export const sequfloh: ModuleDef = {
    key: 'sequfloh',
    label: 'SEQUFLOH',
    description: 'Enterprise Execution Engine: integrated execution, tracking, validation, inspection, repair, production, and controlled work instruction management',
    icon: <PlaylistPlayOutlined fontSize="small" />,
    basePath: '/sequfloh',
    permissions: ['sequfloh.access'],
    sidebar: [
      { label: 'SEQUFLOH Home', path: '/sequfloh', permission: 'sequfloh.view_dashboard' },
      { label: 'Live Execution', path: '/sequfloh/execution/dispatch', permission: 'sequfloh.view_live_execution', children: [
        { label: 'Dispatch Queue', path: '/sequfloh/execution/dispatch' },
        { label: 'Active Station', path: '/sequfloh/execution/active' },
        { label: 'Paused Tasks', path: '/sequfloh/execution/paused' },
        { label: 'Completed Tasks', path: '/sequfloh/execution/completed' },
        { label: 'My Assigned Work', path: '/sequfloh/execution/mine' },
        { label: 'Overdue Work', path: '/sequfloh/execution/overdue' },
        { label: 'Work History', path: '/sequfloh/execution/history' },
      ] },
      { label: 'Tracking', path: '/sequfloh/tracking/validation', permission: 'sequfloh.view_live_execution', children: [
        { label: 'Validation Tracking', path: '/sequfloh/tracking/validation' },
        { label: 'FAI Tracking', path: '/sequfloh/tracking/fai' },
        { label: 'Repair Tracking', path: '/sequfloh/tracking/repair' },
        { label: 'Production Tracking', path: '/sequfloh/tracking/production' },
        { label: 'Open Activities', path: '/sequfloh/tracking/open' },
        { label: 'Completed Activities', path: '/sequfloh/tracking/completed' },
        { label: 'On-Hold Activities', path: '/sequfloh/tracking/on-hold' },
      ] },
      { label: 'Work Instructions', path: '/sequfloh/instructions/all', children: [
        { label: 'All Work Instructions', path: '/sequfloh/instructions/all' },
        { label: 'Validation Work Instructions', path: '/sequfloh/instructions/validation' },
        { label: 'FAI Work Instructions', path: '/sequfloh/instructions/fai' },
        { label: 'Repair Test Instructions', path: '/sequfloh/instructions/repair-test' },
        { label: 'Repair Work Instructions', path: '/sequfloh/instructions/repair' },
        { label: 'Draft Instructions', path: '/sequfloh/instructions/draft' },
        { label: 'Released Instructions', path: '/sequfloh/instructions/released' },
        { label: 'Obsolete Instructions', path: '/sequfloh/instructions/obsolete' },
        { label: 'Templates', path: '/sequfloh/instructions/templates' },
      ] },
      { label: 'Parts and Items', path: '/sequfloh/parts', children: [
        { label: 'Part Master', path: '/sequfloh/parts' },
        { label: 'Parts Lists', path: '/sequfloh/parts-lists' },
        { label: 'Repair Parts', path: '/sequfloh/parts?view=repairable' },
        { label: 'Assemblies', path: '/sequfloh/parts?view=assemblies' },
        { label: 'Reusable Operation Steps', path: '/sequfloh/operation-steps' },
        { label: 'Revision History', path: '/sequfloh/revision-history' },
      ] },
      { label: 'Quality Personnel', path: '/sequfloh/quality', permission: 'sequfloh.view_quality_folder', children: [
        { label: 'Quality Dashboard', path: '/sequfloh/quality' },
        { label: 'Validation Work Instructions', path: '/sequfloh/quality/validation' },
        { label: 'Repair Work Instructions', path: '/sequfloh/quality/repair' },
      ] },
      { label: 'Engineering Personnel', path: '/sequfloh/engineering', permission: 'sequfloh.view_engineering_folder', children: [
        { label: 'Engineering Dashboard', path: '/sequfloh/engineering' },
        { label: 'Validation', path: '/sequfloh/engineering/validation' },
        { label: 'Development', path: '/sequfloh/engineering/development' },
      ] },
      { label: 'Reports', path: '/sequfloh/reports/task-performance', permission: 'sequfloh.view_reports', children: [
        { label: 'Task Performance', path: '/sequfloh/reports/task-performance' },
        { label: 'Validation Status', path: '/sequfloh/reports/validation-status' },
        { label: 'FAI Status', path: '/sequfloh/reports/fai-status' },
        { label: 'Repair Status', path: '/sequfloh/reports/repair-status' },
        { label: 'Work Instruction Status', path: '/sequfloh/reports/work-instruction-status' },
        { label: 'User Activity', path: '/sequfloh/reports/user-activity' },
        { label: 'Time Variance', path: '/sequfloh/reports/time-variance' },
        { label: 'Export Center', path: '/sequfloh/reports/export-center' },
      ] },
      { label: 'Audit Trail', path: '/sequfloh/audit/system', permission: 'sequfloh.view_audit_trail', children: [
        { label: 'System Audit Events', path: '/sequfloh/audit/system' },
        { label: 'Work Instruction History', path: '/sequfloh/audit/instructions' },
        { label: 'User Access History', path: '/sequfloh/audit/access' },
        { label: 'Record Change History', path: '/sequfloh/audit/changes' },
        { label: 'Export History', path: '/sequfloh/audit/exports' },
      ] },
      { label: 'SEQUFLOH Administration', path: '/sequfloh/admin/module-access', children: [
        { label: 'Module Access', path: '/sequfloh/admin/module-access', permission: 'sequfloh.manage_module_access' },
        { label: 'Roles and Permissions', path: '/sequfloh/admin/roles', permission: 'sequfloh.manage_module_access' },
        { label: 'Reference Data', path: '/sequfloh/admin/reference', permission: 'sequfloh.manage_dropdowns' },
        { label: 'Dropdown Management', path: '/sequfloh/admin/dropdowns', permission: 'sequfloh.manage_dropdowns' },
        { label: 'Application Configuration', path: '/sequfloh/admin/configuration', permission: 'sequfloh.manage_settings' },
      ] },
      { label: 'SEQUFLOH Settings', path: '/sequfloh/settings/general', permission: 'sequfloh.manage_settings', children: [
        { label: 'General Settings', path: '/sequfloh/settings/general' },
        { label: 'Work Instruction Settings', path: '/sequfloh/settings/workInstructions' },
        { label: 'Validation Settings', path: '/sequfloh/settings/validation' },
        { label: 'FAI Settings', path: '/sequfloh/settings/fai' },
        { label: 'Repair Settings', path: '/sequfloh/settings/repair' },
        { label: 'Tracking Settings', path: '/sequfloh/settings/tracking' },
        { label: 'Notification Settings', path: '/sequfloh/settings/notifications' },
        { label: 'Numbering Settings', path: '/sequfloh/settings/numbering' },
        { label: 'Display Preferences', path: '/sequfloh/settings/display' },
        { label: 'Export Settings', path: '/sequfloh/settings/export' },
        { label: 'Security Settings', path: '/sequfloh/settings/security' },
      ] },
    ],
  };

/** Administration pages: people, roles, sites and the audit trail. */
export const administration: NavItem = { label: 'Administration', path: '/admin/users', children: [
  { label: 'People', path: '/admin/users', permission: 'admin.users' },
  { label: 'Roles and Permissions', path: '/admin/roles', permission: 'admin.roles' },
  { label: 'Sites and Departments', path: '/admin/sites', permission: 'admin.organization' },
  { label: 'Audit Trail', path: '/admin/audit-trail', permission: 'audittrail.view' },
] };

export const modules = [{ ...sequfloh, sidebar: [...sequfloh.sidebar, administration] }];
