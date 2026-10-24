# Changes

## One page layout across the app

- Every page now uses the same header: breadcrumb, then the title with its description and context chips on the left and the action bar on the right, then the page's tabs. Settings and Change password now use it too.
- Standard spacing (one set of spacing values) between the breadcrumb, the header, toolbars and tables on every page.
- Record action bar: primary and secondary actions in the fixed order, then a More (⋯) menu. Delete always lives in More (in red, still with its named confirmation); Archive and Restore for drawings and folders moved there too.
- Drawing folders get a folder header card: the folder name as the title, the linked part under it and the actions on the right (Edit, Upload drawing, Full screen, More). It wraps cleanly on narrow screens.
- List search rows (Parts Drawings, Part Master, Search, every filtered list) use one shared toolbar: search box, filters, Search button, then checkboxes on the right.
- New checks: every page in the menus renders the shared header once with one title, no destructive buttons sit in a header, every search row is the shared toolbar, and no page draws its own title. The record action check now looks for Delete in the More menu.
- Test fix: the Part Master column header check waits for the table to settle after a search.

## Parts Drawings in the sidebar, with family tabs

- Parts Drawings is now its own sidebar entry at /sequfloh/drawings instead of a page under Engineering.
- Old /sequfloh/engineering/drawings links (folders and single drawings) redirect to the new address and keep their query.
- A scrollable tab bar across the top: All first, then one tab per top-level folder (family), named without the trailing " Drawings". Opening a subfolder keeps its family tab selected.
- Search looks only inside the selected family; tick "Search all families" to search everywhere. The All tab always searches everywhere.
- The drawing search API accepts an optional family (top-level folder id) to limit results to that folder and the folders under it.

## Standard record actions, full screen, audit trail on Admin and Settings only

Standard record actions
- Work instructions, work orders, chargeback reports, drawings, drawing folders, imported files, inspection requests, parts and parts lists share one action bar. Edit, Save, Cancel, Print, Import, Delete and Full screen always come in the same order with the same labels, wherever they apply.
- Save is only active when there are unsaved changes. Cancel asks before throwing them away, and following a link away from a page with unsaved changes asks first.
- Delete names the item in its confirmation, needs the delete permission an administrator assigned, and is recorded in the audit trail. Chargeback reports now use the same Delete confirmation.
- A test opens every record page and fails if a standard action goes missing.

Full screen
- Every record page, every form dialog and the drawing viewer have a Full screen button. It fills the screen (browser full screen where allowed, otherwise the whole window) and keeps the toolbar with Save, Print and the rest visible.
- Exit full screen or Esc puts the layout and scroll position back exactly; anything typed and not yet saved stays.

Audit trail
- History tabs and buttons are gone from record pages (parts, parts lists, drawings and folders, imported files, inspection requests, work orders). On work instructions the tab is now "Revisions" and shows only lifecycle steps and revisions.
- The Admin audit trail can be filtered by record type, record, user, action and dates. Clicking a record type shows all entries for that record.
- People who are not administrators but have "View audit trail" find an Audit Trail section in their own Settings page (the user menu item is now "Settings"), limited to what that permission covers. Without the permission, the section is hidden and the server refuses the request.
- What gets recorded in the audit trail is unchanged.

## Import file and Import folder on every upload page

- Every page that takes uploads now has the same "Import file" and "Import folder" buttons: All Imported Files (blank templates too), Drawings, the work instruction lists, work instruction files, inspection request attachments, and work order tester files, photos and attachments. Import folder reads subfolders too, skips hidden and unsupported files, and ends with a summary of what was imported, skipped or failed.
- Importing on a work instruction list creates one draft work instruction per file, with the file attached. You pick the type and site first. Files imported on a work order stay on that work order; work instructions and work orders are never created from each other's imports.
- On Drawings, the chosen files go straight into the Import drawings dialog for review.
- A test fails if a page gets a file upload that doesn't use the shared buttons.

## Change product family of many parts

- The Part Master has a "Change product family" button for people who may edit parts (SEQUFLOH edit parts and Master Data edit). It moves many parts to another product family in one go.
- Parts can be chosen four ways: ticked in the parts list (tick boxes on each row, with a tick box for the whole page), all parts matching the current search and filters, the parts of a family linked to any drawing in a chosen drawing folder or its subfolders, or the parts whose number matches a pattern (starts with, contains, ends with or is exactly, capitals ignored, any of the part's numbers). A pattern can also narrow the other ways.
- Preview shows how many parts will move and a table of them with all their numbers, current family and matching drawing. It also lists the parts of the family with no linked drawing at all ("Unmatched — check these"), with Export to Excel.
- Apply only changes the product family. Numbers kept for another family stay stored and are shown on the part page as "(kept from another family)", so moving parts back loses nothing. The family cards update straight away.
- Every part change is in the Audit Trail with the old and new family, plus one entry per run saying how many parts moved, from and to which family, and how they were chosen.
- Editing a part no longer clears numbers of fields its product family does not show.
- Fuel Injector parts have the JH PN field, like Fuel Pump, and drawings link to them by it.

## Word files in Drawings; uploader names off file screens

- Drawings accept Word files (.docx and .doc) as well as PDF, images, CAD and SolidWorks files, both by Upload drawing in a folder and by folder import. The file's content is checked: a .docx must be a real Word document and a .doc must have the Word header, so a renamed file is turned away. Word files download rather than open in the browser.
- Folder imports (top level and inside a folder) now take PDF and Word files. Everything else is still skipped, and the preview shows the PDF, Word and skipped counts. Word files are linked to parts by name the same way as PDFs.
- Word files are marked with a "Word" tag in drawing lists and in revision history.
- The name of the person who uploaded a file is no longer shown on drawing lists, the drawing page, revision history, All Imported Files (list, versions and filled copies), work order test files, attachments and photos, the Job report and chargeback report (on screen, PDF and Excel). The upload date is still shown. Who uploaded what is still kept and still shown in History and the Audit Trail.

## Crew time on work orders and chargeback reports

- Every work order (all types, Inspection and Production too) has a "Crew time" panel next to the clock on panel, on the Record and Time tabs. Enter the crew size (no names, no accounts) and use Start crew, Pause crew, Continue and Stop crew. One set of buttons applies to the whole crew.
- Labor hours = (elapsed − paused) × crew size. The panel shows crew elapsed hours, crew size, crew labor hours, registered people hours, the total and total personnel. Crew size is kept with each crew session.
- Registered people still clock on, pause and clock off the job exactly as before. Their hours are shown on their own line and added into the total. Total personnel = largest crew + each registered person who clocked on.
- People with the new "Manage crew time" permission can add past crew time with start, stop and pause times, or edit it. Recorded time is never deleted, so it always appears on the chargeback report; mistakes are corrected by editing the times, and an edit cannot bring an entry to zero. A day worker can only be removed before any time is recorded for them. "Bulk entry" adds a date range in one go: working days (weekends skipped by default, any day can be unticked), hours per day, start time, unpaid lunch and crew size, with the total shown before saving (for example 3 workers × 10 days × 8 h = 240 labor hours). Negative, future or overlapping times are refused. Every add and edit goes to History and the Audit Trail with who, when and the before and after values.
- Reports has a new "Chargeback Reports" list. "Create report" there, or "Create chargeback report" on a work order, opens a form with work order # (typed), supplier, part number (search the Part Master or type one), task description, result, qty inspected/reworked, qty bad, total personnel, total hours, dates, optional hourly rate with labor cost, parts replaced, other costs, findings and notes. Linking a work order fills hours (crew and registered people), personnel, dates, part, quantities and findings; all can be changed.
- The report uses the same layout as the work order Job report (chargeback): Chargeback, Job, Parts replaced, Findings, Repair description, then worker time, Chargeback summary and Sign-off, with the same headings and styling. Worker time is added to it: crew elapsed hours, crew size, crew labor hours, registered people hours, the combined total and total personnel, a crew time sessions table (date, start, stop, paused hours, crew size, hours, labor hours) and man hours by technician.
- On Create report and Edit, "What goes in the report" has a tick box for each part of the report: chargeback details, job, part details, task description, result and quantities, total personnel and man hours, parts replaced, findings, supplier root cause, corrective action, worker time summary, crew time sessions, man hours by technician, detailed time log, attachments, photos, notes, chargeback summary and sign-off. Most are ticked by default; the detailed time log, attachments and photos are not. The choice is saved with the report, and the PDF, the Excel file and the report page show only the ticked parts. Sections with nothing in them are left out.
- The report can be printed as a PDF or exported to Excel. The work order Job report itself is unchanged. Saved reports can be edited or deleted by people with the new "Manage chargeback reports" permission; anyone with report access can view them. Changes are kept in the Audit Trail.
- The two new permissions are given to the built-in system roles and the Supervisor role. A work order with crew time cannot be deleted.

## Drawings: link by JH PN, Relink drawings

- Each part family can name the field that holds its drawing number. CSA uses the SENSEN #; Fuel Pump uses the JH PN. Other families can add theirs in one place (the part attribute list).
- Import drawings now also links a drawing named with a JH PN to every fuel pump that has that JH PN, so one drawing links to all the OR PNs that share it. Matching by OR # and SENSEN # is unchanged. Names are compared ignoring case, the file extension, spaces, dashes and underscores, so "JH 12345.pdf" matches JH PN JH-12345.
- Placeholders such as "/", "-", "N/A" or a blank never count as a number, so they never link a drawing to a part.
- The import preview shows how many parts each folder or drawing will link to, for example "7 parts", with the first part numbers.
- The Drawings page has a "Relink drawings" button for people who can manage drawings. It checks every drawing they can manage against the parts and adds the links that are missing. It never removes a link, including links made by hand, and running it again adds nothing new. It shows how many drawings were checked and how many links were added.
- Saving a part, or importing parts, links the part to any drawing already named with its OR #, SENSEN # or drawing number field.
- Drawings search, and the drawing lookup on forms and inspection requests, find a drawing by any number of a linked part: OR #, SENSEN #, NAPA, DMA, Cross, JH, Nissan and every other part number field.
- A drawing's page lists each linked part with all its numbers, for example "E16070 · DMA PN P50029 · Cross P72128 · JH PN JH-390104Q".
- A part's page shows its drawings in a box at the top, under the notes.
- Every link added by Relink or by saving parts writes an Audit Trail entry on the drawing (who, when, and how many parts), and each Relink run writes one entry with its totals.

## Drawings: import at the top level, and part pickers that find every part

- The Drawings page itself (All folders) now has "Import drawings" too. Choose a folder: its subfolders become drawing folders at the top level, with the same names and nesting, and each PDF goes into its matching folder. PDFs sitting loose in the chosen folder go into a folder named after it. Only PDF files are imported there; the preview shows how many other files are skipped. Preview, part links by OR # or SENSEN #, and carrying on after a stop work as inside a folder. Import inside a folder is unchanged.
- Every part picker now searches all parts in all families on the server as you type, by OR #, SENSEN #, name, NAPA, DMA, JH, Nissan and the other part numbers. Before, some pickers only offered the first parts in the list. This covers "Linked part" and "Link to part number(s)" in Drawings, the part fields of work instructions, tracking and inspection requests, and the part pickers on the Parts pages. Each match shows its other numbers, and when there are more matches than shown the picker says so.

## Inspection work instruction type and Remember me

- Work instructions have a new type, Inspection. It is in the work instruction type dropdown, the type filters, the Work Instructions report filter, printed labels and a new "Inspection Work Instructions" folder. Inspection instructions are numbered IWI-.
- The eight standard inspection layout templates are now filed under Inspection. Existing databases are updated at start-up; only templates with those names and no type are changed.
- A "Starter - Inspection" layout template is added, like the starter for each other type.
- Turning an inspection request into a work order lists Inspection instructions along with the types it already offered.
- The sign-in page has a "Remember me for 30 days on this device" box, unticked by default. Unticked works as before: the sign-in ends when the browser closes, and after 12 hours at most. Ticked keeps the person signed in on that device for 30 days, also across a password change. Open tabs are still cleared on sign-in and sign-out. The sign-in entry in the Audit Trail says when Remember me was used.

## Import drawings in bulk

- Drawings has an "Import drawings" button inside a folder (for people who can manage drawings). Choose a folder, with all its subfolders, or several files. The folder tree is recreated inside the current folder and each file becomes a drawing titled with its file name.
- "Revision for all" is given to every new drawing and starts as A.
- The preview shows the number of folders (and how many are new), files, total size, PDF files, files already imported and what will be imported. Files that are not PDF but are a drawing type (DWG, DXF, STEP and so on) are listed and left out unless "Import these too" is ticked. Other files are skipped.
- "Link to parts by matching name": a folder or file name equal to a part's OR # or SENSEN # links the folder (and the drawings in it) or the drawing to that part. The preview lists the matches.
- Files go from the browser straight to file storage, three at a time, in 64 MB parts above 64 MB, so the server never carries the file contents. Each file is checked (size and file type) and recorded once it has arrived. A progress bar shows files and GB, and Cancel import stops it.
- Choosing the same folder again skips files already imported to the same folder with the same name and size, so a stopped or failed import carries on where it stopped. One "Imported drawings" entry with the totals goes to the folder's History and the Audit Trail.
- Files of up to 5 GB each can be imported. Drawings larger than 50 MB open with a short-lived link straight from file storage.

### Cloudflare R2 setting needed for Import drawings

Browsers upload straight to the R2 bucket, so the bucket must allow the app's address:

1. In the Cloudflare dashboard open R2 Object Storage and choose the bucket named in STORAGE_BUCKET.
2. Open Settings, find CORS Policy and choose Add CORS policy (or Edit).
3. Paste this, with the address people use to open the app (no trailing slash), and save:

```json
[
  {
    "AllowedOrigins": ["https://your-app-address"],
    "AllowedMethods": ["PUT", "GET"],
    "AllowedHeaders": ["content-type"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }
]
```

ETag must be exposed: large files are uploaded in parts, and the app joins them using each part's ETag. Nothing else changes: the same storage keys are used. If the policy is missing, the import shows "The upload could not reach file storage".

## Closing the browser signs you out; sign-in starts with Home only

- The sign-in cookie is now a session cookie: closing the browser signs you out. A session also still ends 12 hours after sign-in, whatever the browser does.
- Browsers set to "Continue where you left off" (Chrome, Edge) or "Restore previous session" (Firefox) restore session cookies on restart, so you may still be signed in after reopening. Turn that setting off, or use Sign out, on shared computers.
- Open tabs inside the app are kept only for the current browser tab: a reload keeps them, closing the tab or browser forgets them. Signing in or out clears them, so every sign-in starts with Home only. Sign-in auditing is unchanged.

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
