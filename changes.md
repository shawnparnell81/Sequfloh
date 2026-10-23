# Changes

## Parts have no revision; notes, editable OR # and fuel pump fields

- Parts no longer show or ask for a revision: it is gone from the part form, part details (including the Revisions tab), the parts list and Columns menu, Export CSV, search results, the parts report and the Import parts mapping. Existing revision records are kept in the database. Work instructions, parts lists and drawings still have revisions.
- Every field of a part, including the OR #, can be changed with Edit after import. A new OR # may match another part only when the Positions differ. Each change is in the part's History and the Audit Trail with who, when and old and new values.
- Each part has one Notes box, edited in the part form and shown at the top of the part page. Changes to notes are kept in History.
- Family cards count distinct OR # values, for example "1,180 part numbers". Stock stays in NetSuite.
- Fuel Pump parts use the fuel pump sheet titles: OR PN (the OR #), NAPA PN, DMA PN, Cross, JH PN and Individual Box Size. The Fuel Pump list shows those six columns in that order (others can be added with Columns). SENSEN #, Nissan VA #, NAPA US #, NAPA CA #, Key Application and Position are not shown for Fuel Pump. Import parts matches these headers by name.

## Part Master home page

- The Part Master home page no longer lists every part. It has a search box and one card per product family with its number of parts. Every family has a card, even with no parts yet, and parts with no product family get their own card.
- The search covers OR #, SENSEN #, NAPA and Nissan numbers and key application. Click a result to open the part. "Show all matches" opens the full list when there are more than 10.
- Click a family card to open that family's parts list. It keeps the search, filters, Columns menu and Export CSV, and "Back to Part Master home" returns to the cards.
- New part and Import parts stay on the home page. Import parts on a family's page sets that family as both the Name and the Product family for all rows. Both can still be changed before you preview.

## Name is always chosen in Parts import

- In Import parts, Name is never taken from the file. Pick it from the product families list under "Same for all rows". It has no "From file column" or "Not imported" option.
- Preview and Import stay off until a Name is chosen, with the message "Choose the Name for all rows before you preview or import."
- There is no automatic name. Every added part gets the chosen Name, and "Update existing" sets it on updated parts too.

## Set choices on parts and in Parts import

- Name, Product family and Part class are dropdowns with the same list: Complete Strut Assembly (CSA), Fuel Pump, Fuel Injector, Brake Wear Sensor, Gas Lift, Electric Lift, Coil Spring, Struts and Air Struts. Strut and Air Strut were renamed Struts and Air Struts, and Coil Spring was added. Parts that had them keep their family under the new name.
- Unit of measure is a dropdown: Each, Lot, Batch or Skid. Parts that had EA now show Each. Any other older unit stays on the part until it is changed.
- Assembly type is replaced by Assembly, Yes or No. Parts whose class was Assembly or Subassembly, or that had an assembly type typed in, are set to Yes. The Assemblies view lists the parts set to Yes.
- Lot controlled is a dropdown: Yes, No or Not applicable.
- Repairable is a dropdown: Yes means the part can be repaired if required. It still does not add the part to Repair Parts.
- Material, Specification, OR #, SENSEN #, Nissan VA #, NAPA US #, NAPA CA #, Key Application and Position stay as typed fields.
- Import parts: each dropdown with set choices offers Not imported, "Same for all rows" with the choices, and "From file column" with the file's columns. A value from a column must match a choice (capital letters don't matter, and EA counts as Each). A row that doesn't match is shown as an error in the preview. Typed fields still take a column.
- Import parts: the separate product family setting at the bottom of the dialog is gone. Use "Same for all rows" in the Product family dropdown instead.

## Parts that share an OR #

- Several parts can now have the same OR # (Part number), for example one per Position. The OR # is kept exactly as entered and nothing is added to it.
- Import:
  - Rows without an OR # are skipped and not counted as errors. A missing SENSEN # is fine.
  - A row that is an exact copy of an earlier row is skipped.
  - A row with the same OR # as another row but different values (for example another Position) is skipped unless you tick "Keep rows that repeat an OR # but differ". When ticked, it is added as its own part and the preview shows "Add (repeat OR #, different values)".
  - Existing parts are matched by OR # and Position, or by OR # alone when only one part has it. Importing the same file again skips or updates each part and adds no copies.
- Part pickers for work orders, instructions, repair parts, parts lists, drawings and inspection requests show the Position beside the OR #, so parts that share an OR # can be told apart.
- An inspection request with a typed OR # is linked to a part only when one part has that number.
- Editing a part that shares its OR # works as before. A new part, or a changed number, still can't use an OR # that is already taken.

## Product families and cross-reference numbers

- Product family is now a dropdown from a new Product Families list under Parts (Setup). It starts with Complete Strut Assembly (CSA), Fuel Pump, Fuel Injector, Strut, Brake Wear Sensor, Air Strut, Gas Lift and Electric Lift. Admins can add, rename and retire families. A renamed family shows its new name on every part. A retired family stays on the parts that already have it but cannot be picked for new ones.
- Product families that were typed on existing parts are linked to the matching entry. A typed family with no match is added to the list, so nothing is lost.
- A product family is required when adding a part. Parts without one still work, and the Parts list and the part page ask you to set one.
- New free-text fields on a part: Nissan VA #, NAPA US #, NAPA CA #, Key Application and Position. They show on the form, the part page and the Parts list, and search finds them.
- Part number is now labelled "OR # (Part number)" and drawing number "SENSEN # (Dwg #)" on the form, part page, list, export and parts report.
- The Parts list has a Product family filter and column, a Columns menu to show or hide columns, and an Export CSV button.
- Import:
  - It finds the header row in the first ten rows, skipping a title line above it, and you can pick a different row.
  - It recognizes OR#, SENSEN #, Dwg #, Product family and the cross-reference headings. "CSA" and "Complete Strut Assembly" both match Complete Strut Assembly (CSA).
  - A product family can be set for all rows when the file has no such column.
  - A name column is no longer needed. The name comes from the description, then Key Application and Position, then the OR #.
  - Values are kept exactly as the sheet shows them, with spaces trimmed. Numbers keep leading zeros, and formula results are taken as saved in the file.
  - Rows without an OR # are listed as errors. A repeated OR # is skipped, with the row number that was used.
- The old "Part type" choice (Component, Assembly and so on) is now called "Part class".
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
