# Changes

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
- New Time tab on each work order. Each person can start, pause, resume and stop their own timer.
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
