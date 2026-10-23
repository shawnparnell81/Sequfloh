# Changes

## Inspection layout templates

- Eight standard inspection layout templates are added under Work Instructions > Templates > Layout templates: AQL Sample Result, Product Alert, Validation Result, Warranty Claim Return, Supplier Request, Customer Request / Complaint, Random Inspection, and Recall / Containment Sort.
- Each one has a purpose, required tools and gauges, numbered steps (identification, visual, dimensional and functional checks as they apply), record results, segregate counts and next step (create a repair work order for any failures).
- AQL Sample Result records the sampling plan reference, sample size and accept / reject numbers. Recall / Containment Sort is a 100% inspection of the remaining inventory.
- Quantities are job level. There are no serial numbers, approvals or signatures. Holds, quarantine and inventory moves are noted as handled in NetSuite.
- The templates are added at start-up when one with the same name is missing, so existing databases get them and edited copies are left alone. They can be used with any instruction type, so they fit Inspection work orders.
- New tests: the eight templates are present once with their common steps, and the templates page lists and opens them.

## Inspection work order type

- "Inspection" is now a work order type, alongside Production, FAI, Validation and Repair.
- Converting an inspection request to a work order starts with the type set to Inspection. It can still be changed before the work order is created.
- Inspection work orders work like production tasks: the same result choices (Pending, Pass, Fail, Conditional), default time ranges and create permission.
- Inspection shows in the work order type filters, lists, the record title, and the task performance and time variance reports.
- Existing types are unchanged. No database change is needed.
- New tests: a converted request defaults to Inspection, and the dropdown lists Inspection first.

## Report: test results Yes / No, ERP reference off the report

- The chargeback report (print page and PDF) no longer shows the ERP reference. The field stays on the Findings tab because it belongs to the work order's findings.
- The "Test results" checkbox is replaced by an explicit "Include test results report?" No / Yes choice. No is the default and the answer is remembered per work order.
- With No, the report has no test results at all: no section, file list, totals or other mention, on the print page or in the PDF.
- Full detail and Summary only leave the Yes / No answer as it is.
- New tests: no "test" wording in the report or PDF when the answer is No (including Full detail), the section comes back with Yes, and the ERP reference is never printed.

## Clock on / clock off job time
- Time buttons now read "Clock on job", "Pause job – Break", "Pause job – Lunch", "Clock back on job" and "Clock off job", with matching tooltips.
- A pause records why (Break or Lunch). The reason shows in the Time tab ("Paused for lunch since …"), the time log and the detailed time log on the job report and PDF.
- Time log wording: "Clocked on job", "Paused for break", "Paused for lunch", "Clocked back on job", "Clocked off job".
- Paused time is still left out of man hours.
- This is job time only. It is not a payroll clock in / clock out.

## Inspection requests
- New top-level "Inspection Requests" section in the menu. Status: Open, In progress, Done, Cancelled.
- "New inspection request" button opens the form in a dialog. It is on SEQUFLOH Home (quick actions), Work Instructions and Tracking (work orders).
- Fields: part number (pick from the Part Master to fill the description, or type any part number), part description, Qty (per NetSuite), reason, inspection requirement, optional linked work instruction, requested completion date, priority. Attachments can be added on the request.
- Reasons (Supplier request, Warranty claim, AQL sample result, Random inspection, Customer request, Defect driven) and priorities (Critical, High, Normal, Low) are edited in SEQUFLOH Settings › Tracking Settings.
- Anyone signed in to SEQUFLOH can submit. The list is ordered by priority, then requested date. Overdue requests are highlighted.
- Open requests are also shown at the top of the Dispatch Queue.
- Convert to work order: part, quantity, priority (Critical becomes Urgent), requested date and requirement carry over, and the part's drawings are attached. The request moves to In progress.
- The Dispatch Queue and My Tasks now sort by priority first, then due date.
- Delete follows the usual delete permission and is recorded in the audit trail. A converted work order is kept.

## Time detail per technician
- New Time tab on each work order. Each person can clock on, pause, clock back on and clock off the job.
- Every start, pause, resume and stop is logged with a timestamp ("Show time log").
- Paused time is shown separately for each person and is not counted in man hours.
- Older time entries show their start and stop in the log.

## Findings / nonconformance
- New Findings tab on each work order. Every field is optional.
- Discovery method dropdown: AQL sampling inspection, Warranty claim inspection, Product alert, Recall, Incoming inspection, In-process, Customer complaint, Other. Admins edit it in SEQUFLOH Settings › Tracking Settings.
- Fields: date discovered, discovered by, site, supplier, supplier part #, lot / date codes, PO, qty inspected / nonconforming / affected, description, warranty claim #, alert / recall #, ERP reference.
- Repair description: repair method, the work instruction and revision used, and an optional supplier root cause.
- Photos and evidence (JPG, PNG) and attachments (PDF, Word, Excel, CSV, text, images) upload separately from tester files and don't count toward test results.
- Other cost lines (description and amount), such as freight or sorting.
- Saving findings and cost lines is recorded in the History tab.

## Parts unit cost
- Parts replaced have an optional unit cost. Parts cost on the report is quantity × unit cost.

## Report options
- The job report page has a checkbox for each extra section: per-technician summary, detailed time log, findings, test results, attachments, photos / evidence, cost breakdown.
- "Full detail" ticks them all; "Summary only" clears them. The default is summary only. The choice is remembered for each work order and used by both the print page and the PDF.
- A section with nothing in it is left out, even if it is ticked.
- The summary always has: chargeback, job (with the work instruction and revision), total man hours (paused time excluded), parts replaced, the chargeback total and sign-off.
- The chargeback summary adds labor (hours × rate typed at print time), parts and other costs into a total. Without a rate, labor shows "____".
- Photos print in the PDF (JPG); PNG photos are listed by name.
- The Official status in NetSuite is not shown on the report.
