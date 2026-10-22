# Changes

## Anyone can work a job
- Anyone who can see a work order (SEQUFLOH access at its site) can start, pause, resume and enter step results. Being assigned is no longer required.
- Each person's time adds to the job total.
- Complete, fail, cancel and assign keep their existing permissions.

## Job tab (work orders)
- Quantity is for the whole job: completed / repaired, plus an optional "could not repair" count and an optional target. Anyone working the job can add to it; a negative number corrects a mistake. Quantity is never recorded per technician.
- Parts replaced by part number. Quantity is worked out as completed × per assembly; you can type a number instead.
- Tester files (CSV, Excel, PDF, text) attach to the work order, with an optional batch and test date. CSV and Excel files are read for tested / passed / failed counts. The pass/fail column and pass values are picked once and remembered for the job. No serial numbers.
- Every change is in the work order's History tab.

## Chargeback job report
- "Job report" button on each work order opens a print-friendly page with a PDF download.
- Sections: chargeback (supplier and reference typed in), job, man hours per person with a total, parts replaced, cost, test results summary, sign-off.
- The rate is typed in when printing and is not saved. Without a rate the rate and cost show "____".
- No quantity per technician. The Official status in NetSuite is not shown on the report.
- Printing the PDF is recorded in the audit trail.

## Official status in NetSuite
- New informational field on parts (inline in the Part Master list and on the part page), work instructions and work orders.
- A dropdown (None, Quarantine hold (NetSuite), Repair hold, Released) plus an optional note. Admins edit the options in SEQUFLOH Settings › Tracking Settings.
- Amber badge for holds, green for released. Shown as a list column with a filter.
- Informational only: it blocks nothing. Official holds are made in NetSuite. Changes are recorded in the audit trail.

## History and audit
- History tabs now have a filter box and Export CSV.
- Work order history also shows changes to its time entries and tester files.
- Audit Trail pages have Export CSV (needs "View audit trail"; up to 50,000 rows; the export is itself recorded).

## Removed (NetSuite handles holds, quarantine, scrap, disposition and inventory)
- Hold action on work orders and tasks (Pause stays).
- Hold reasons list.
- On-Hold Activities folder and menu item.
- Hold time on the work order, station view, task events and execution lists.
- Repair final disposition and the repair dispositions list.
- Scrap quantity column on repair parts. The rule is now used + returned ≤ issued.
- "Hold tasks" in the suggested Technician and Supervisor roles. The permission itself is kept so existing roles still save, but nothing needs it.
- Existing On Hold work orders are moved to Paused, keeping the hold reason as the pause reason.

## Decisions
- Seeing a work order is the existing site-scoped SEQUFLOH access.
- FAI keeps its result, now labelled "Inspection result".
- Stored hold time is kept in the database but not shown.

## Still needed
- Real tester sample exports, to confirm the column detection and pass values on actual files.
