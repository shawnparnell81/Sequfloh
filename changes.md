# Changes

## Part types and cross-reference numbers

- New Part Types list under Parts (Setup), filled with Fuel Pump, Fuel Injector, CSA, Strut, Brake Wear Sensor, Air Strut, Gas Lift and Electric Lift. Admins can add, rename and retire types. A renamed type shows its new name on every part. A retired type stays on the parts that already have it but cannot be picked for new ones.
- A part type is required when adding a part. Parts added before this have no type. They still work, and the Parts list and the part page ask you to set one. Once set, a type can be changed but not cleared.
- New fields on a part: Nissan VA #, NAPA US #, NAPA CA # and Key Application. They show on the form, the part page and the Parts list, and search finds them.
- Part number is now labelled "OR # (Part number)" and drawing number "SENSEN # (Dwg #)" on the form, part page, list, export and parts report.
- The Parts list has a Part type filter and column, a Columns menu to show or hide columns, and an Export CSV button.
- Import recognizes OR#, SENSEN #, Dwg #, Part type and the new cross-reference headings. The old "Part type" choice (Component, Assembly and so on) is now called "Part class".

## Older Word and Excel files are converted on upload

- The server now converts older .doc and .xls files, RTF files and Word pages saved as web pages (HTML or MHT, often named .doc) into .docx or .xlsx when they are uploaded. Files no longer need to be saved again in a newer format first.
- This works for document and blank form import and for Import parts, which now also accepts .doc.
- The file is stored exactly as uploaded; the converted copy is what is read and shown. Macros never run, one file is converted at a time and a conversion that takes too long is stopped.
- If a file cannot be converted, for example because it is damaged or password protected, the message says so.

## Import parts into the Part Master

- Import parts on the Parts list reads Excel (.xlsx, .xlsm, .xls), CSV or the tables of a Word .docx (the first table, or all tables with the same headings). Files are only read; macros never run.
- Columns are matched from their headings (part number, name, description, revision, type, unit, family or category, Repairable, Repair part, lot controlled, Official status in NetSuite and others). Each match can be changed before the preview.
- The preview shows every row as Add, Update, Skip or Error with the reason. A part number repeated in the file is skipped after its first row. Parts already in the Part Master are skipped by default, or updated when Update existing is chosen; an update only writes mapped values that are not empty and never changes the revision.
- Import saves all rows in one step and adds an audit entry with the file name and the counts. Serial number columns are ignored.
- Only people who can create parts see the button.

## Repairable is separate from Repair Parts

- Repair Parts used to show every part with Repairable ticked. It now lists only parts with the new Repair part flag: components and replacement parts ordered for repairs, such as screws, nuts and top mounts.
- Repairable still only means the part itself can be repaired. Repair work orders and repair instructions still pick from Repairable parts, now under the Repairable Parts view.
- The part form and both views explain the two flags.
- No existing data is changed or deleted. Repair part starts unticked on every existing part, so Repair Parts starts empty until parts are marked.

## Starter templates for every instruction type

- Each work instruction type now has a starter layout template tied to it: Starter - Validation, Starter - Validation / FAI, Starter - FAI, Starter - Repair Test and Starter - Repair Work Instruction.
- Each one has a purpose, required tools and gauges, numbered steps for that type and a record results step. Quantities are job level. There are no serial numbers, approvals or signatures.
- The picker on New work instruction offers the starter for the chosen type, together with the templates that suit any type.
- They are added at start-up when one with the same name is missing, so existing databases get them and edited copies are left alone. Instruction types are unchanged.
- New tests: every type has exactly one starter tied to it, and the picker offers it for that type.

## Word files with the wrong extension

- Imports now check what a file really contains instead of trusting its name. A Word .docx or Excel .xlsx file renamed .doc or .xls opens normally, and a legacy binary file named .docx or .xlsx is converted like .doc or .xls.
- RTF documents and Word web pages saved with a .doc name are converted to a working Word copy. If the server cannot convert them, the message says what the file really is and to open it in Word and Save As .docx. Single-file web pages (.mht) always get that message.
- Files that are not a recognised document are still refused.
- The uploaded file is always kept exactly as it came. Macros are never run.
- The default title drops a doubled extension, so "Procedure - Rev C.doc.doc" becomes "Procedure - Rev C".
- New tests with made-up files: a .docx named .doc, RTF and web pages named .doc, a real legacy .doc, and random bytes.

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
