# Changes

## Delete
- New Delete button, with a "Delete …? This can't be undone." confirm, on:
  - work instructions (removes every revision, its files and history)
  - work orders (only ones made by mistake, with no logged time)
  - parts (links to drawings are removed; the drawings stay)
  - parts lists, reusable operation steps and layout templates
  - drawings, single drawing revisions and drawing folders
  - imported files, blank forms and filled copies
  - sites and departments
- Delete needs the "Delete records" permission. Admins can give it per site and department. Administrator roles get it by default. Sites and departments use the existing organization admin permission.
- When a delete isn't allowed, the dialog explains why:
  - work orders with logged time are kept for chargebacks; cancel them instead
  - work instructions that have work orders; mark them obsolete instead
  - parts used by work instructions, work orders or parts lists; deactivate them instead
  - blank forms with filled copies; delete the copies first or archive the form
  - folders that still hold drawings or folders
  - the only revision of a drawing; delete the drawing instead
  - sites and departments in use; archive them instead
- Every delete is in the audit trail: who, when, what, and what went with it.
- Stored files are removed from storage when their record is deleted.
- People are not deleted; they are deactivated as before, because they show in history and time records.

## Several people on one work order
- A work order can have several assigned people. The first one is the lead.
- "Change" on the work order picks the people. This needs the dispatch or reassign permission, as before.
- Lists and cards show everyone assigned.
- "Assigned to me", My work and dashboard counts include work orders you are one of several on.
- Existing work orders keep their assigned person.

## Time on a job
- Every signed-in person can start and stop their own timer on any work order, assigned or not. No permission is needed.
- Several people can run timers on the same job at the same time.
- The work order shows the total time and a per-person breakdown for chargebacks.
- Timer starts and stops are in the audit trail.

## Database
- Migration AddAssigneesTimeAndDelete adds the assigned-people and time tables, copies existing assignees, and gives administrator roles the Delete records permission.
